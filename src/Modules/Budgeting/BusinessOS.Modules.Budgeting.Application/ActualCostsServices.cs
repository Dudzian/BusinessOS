using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Modules.Budgeting.Application;

public enum ActualCostOperationStatus { Success, ValidationFailure, NotFound, ConcurrencyConflict, ProjectUnavailable, Archived, PersistenceFailure, Cancelled }
public sealed record ActualCostResult(ActualCostOperationStatus Status, string SafeMessage);
public sealed record ActualCostResult<T>(ActualCostOperationStatus Status, string SafeMessage, T? Value);
public sealed record ActualCostItem(Guid Id, Guid ProjectId, ActualCostKind Kind, string Name, decimal Amount, string Currency,
    DateOnly IncurredOn, string? Note, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Version);
public sealed class ActualCostsReadException(Exception inner) : Exception("Actual costs could not be read.", inner);
public sealed class ActualCostsPersistenceException(string message, Exception inner) : Exception(message, inner);

public interface IActualCostsStore
{
    Task<IReadOnlyList<ActualCost>> ListAsync(BusinessProjectId projectId, CancellationToken ct);
    Task<ActualCost?> GetAsync(ActualCostId id, bool tracked, CancellationToken ct);
    Task AddAsync(ActualCost cost, CancellationToken ct);
    Task<ActualCostOperationStatus> SaveAsync(CancellationToken ct);
    Task ResetTrackingAsync();
}

public interface IActualCostsCrudService
{
    Task<IReadOnlyList<ActualCostItem>> ListAsync(Guid projectId, CancellationToken ct);
    Task<ActualCostItem?> GetAsync(Guid id, CancellationToken ct);
    Task<ActualCostResult<ActualCostItem>> CreateAsync(Guid projectId, ActualCostKind kind, string name, decimal amount,
        string currency, DateOnly incurredOn, string? note, CancellationToken ct);
    Task<ActualCostResult<ActualCostItem>> UpdateAsync(Guid id, long expectedVersion, ActualCostKind kind, string name,
        decimal amount, string currency, DateOnly incurredOn, string? note, CancellationToken ct);
    Task<ActualCostResult> ArchiveAsync(Guid id, long expectedVersion, CancellationToken ct);
}

internal sealed class ActualCostsCrudService(IActualCostsStore store, IBudgetingProjectLookup projects, TimeProvider clock) : IActualCostsCrudService
{
    public Task<IReadOnlyList<ActualCostItem>> ListAsync(Guid projectId, CancellationToken ct) => Read(async () =>
        (IReadOnlyList<ActualCostItem>)(await store.ListAsync(new(projectId), ct)).Select(Map).ToArray(), ct);

    public Task<ActualCostItem?> GetAsync(Guid id, CancellationToken ct) => Read(async () =>
    {
        var cost = await store.GetAsync(new(id), false, ct);
        return cost is null ? null : Map(cost);
    }, ct);

    public Task<ActualCostResult<ActualCostItem>> CreateAsync(Guid projectId, ActualCostKind kind, string name, decimal amount,
        string currency, DateOnly incurredOn, string? note, CancellationToken ct) => Guard(async () =>
    {
        var project = await projects.GetAsync(projectId, ct);
        var validation = ValidateProject(project, currency);
        if (validation is not null) return validation;
        var cost = ActualCost.Create(new(projectId), kind, name, new(amount, new(currency.Trim().ToUpperInvariant())), incurredOn, note, clock.GetUtcNow());
        await store.AddAsync(cost, ct);
        return await Save(cost, "Koszt został dodany.", ct);
    }, ct);

    public Task<ActualCostResult<ActualCostItem>> UpdateAsync(Guid id, long expectedVersion, ActualCostKind kind, string name,
        decimal amount, string currency, DateOnly incurredOn, string? note, CancellationToken ct) => Guard(async () =>
    {
        var cost = await store.GetAsync(new(id), true, ct);
        if (cost is null) return Fail(ActualCostOperationStatus.NotFound, "Nie znaleziono kosztu.");
        if (cost.ArchivedAtUtc is not null) return Fail(ActualCostOperationStatus.Archived, "Koszt jest zarchiwizowany.");
        if (cost.Version != expectedVersion) return Fail(ActualCostOperationStatus.ConcurrencyConflict, "Koszt został zmieniony.");
        var validation = ValidateProject(await projects.GetAsync(cost.ProjectId.Value, ct), currency);
        if (validation is not null) return validation;
        cost.Update(kind, name, new(amount, new(currency.Trim().ToUpperInvariant())), incurredOn, note, clock.GetUtcNow());
        return await Save(cost, "Koszt został zmieniony.", ct);
    }, ct);

    public async Task<ActualCostResult> ArchiveAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var result = await Guard(async () =>
        {
            var cost = await store.GetAsync(new(id), true, ct);
            if (cost is null) return Fail(ActualCostOperationStatus.NotFound, "Nie znaleziono kosztu.");
            if (cost.ArchivedAtUtc is not null) return Fail(ActualCostOperationStatus.Archived, "Koszt jest zarchiwizowany.");
            if (cost.Version != expectedVersion) return Fail(ActualCostOperationStatus.ConcurrencyConflict, "Koszt został zmieniony.");
            var project = await projects.GetAsync(cost.ProjectId.Value, ct);
            if (project is not { Available: true }) return Fail(ActualCostOperationStatus.ProjectUnavailable, "Projekt nie jest dostępny.");
            cost.Archive(clock.GetUtcNow());
            return await Save(cost, "Koszt został zarchiwizowany.", ct);
        }, ct);
        return new(result.Status, result.SafeMessage);
    }

    private static ActualCostResult<ActualCostItem>? ValidateProject(BudgetProjectInfo? project, string currency)
    {
        if (project is not { Available: true }) return Fail(ActualCostOperationStatus.ProjectUnavailable, "Projekt nie jest dostępny.");
        if (string.IsNullOrWhiteSpace(currency) || !string.Equals(project.BaseCurrency, currency.Trim(), StringComparison.OrdinalIgnoreCase))
            return Fail(ActualCostOperationStatus.ValidationFailure, "Waluta kosztu musi być walutą bazową projektu.");
        return null;
    }

    private async Task<ActualCostResult<ActualCostItem>> Save(ActualCost cost, string message, CancellationToken ct)
    {
        var status = await store.SaveAsync(ct);
        return status == ActualCostOperationStatus.Success ? new(status, message, Map(cost)) : Fail(status, "Nie udało się zapisać kosztu.");
    }

    private async Task<ActualCostResult<ActualCostItem>> Guard(Func<Task<ActualCostResult<ActualCostItem>>> action, CancellationToken ct)
    {
        try { ct.ThrowIfCancellationRequested(); return await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail(ActualCostOperationStatus.Cancelled, "Operacja została anulowana."); }
        catch (ArgumentException) { return Fail(ActualCostOperationStatus.ValidationFailure, "Popraw wskazane dane."); }
        catch (InvalidOperationException) { return Fail(ActualCostOperationStatus.ValidationFailure, "Operacja jest niedozwolona."); }
        catch (Exception e) when (e is ActualCostsPersistenceException or BudgetingProjectLookupException) { return Fail(ActualCostOperationStatus.PersistenceFailure, "Nie udało się wykonać operacji."); }
        finally { await store.ResetTrackingAsync(); }
    }

    private static async Task<T> Read<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is ActualCostsPersistenceException or BudgetingProjectLookupException) { throw new ActualCostsReadException(e); }
    }

    private static ActualCostResult<ActualCostItem> Fail(ActualCostOperationStatus status, string message) => new(status, message, null);
    private static ActualCostItem Map(ActualCost cost) => new(cost.Id.Value, cost.ProjectId.Value, cost.Kind, cost.Name,
        cost.Amount.Amount, cost.Amount.Currency.Value, cost.IncurredOn, cost.Note, cost.CreatedAtUtc, cost.UpdatedAtUtc, cost.Version);
}
