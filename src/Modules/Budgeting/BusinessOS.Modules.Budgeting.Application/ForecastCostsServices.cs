using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Modules.Budgeting.Application;

public enum ForecastCostOperationStatus { Success, ValidationFailure, NotFound, ConcurrencyConflict, ProjectUnavailable, Archived, PersistenceFailure, Cancelled }
public sealed record ForecastCostResult(ForecastCostOperationStatus Status, string SafeMessage);
public sealed record ForecastCostResult<T>(ForecastCostOperationStatus Status, string SafeMessage, T? Value);
public sealed record ForecastCostItem(Guid Id, Guid ProjectId, ForecastCostKind Kind, string Name, decimal Amount, string Currency,
    DateOnly ExpectedOn, string? Note, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Version);
public sealed class ForecastCostsReadException(Exception inner) : Exception("Forecast costs could not be read.", inner);
public sealed class ForecastCostsPersistenceException(string message, Exception inner) : Exception(message, inner);

public interface IForecastCostsStore
{
    Task<IReadOnlyList<ForecastCost>> ListAsync(BusinessProjectId projectId, CancellationToken ct);
    Task<ForecastCost?> GetAsync(ForecastCostId id, bool tracked, CancellationToken ct);
    Task AddAsync(ForecastCost cost, CancellationToken ct);
    Task<ForecastCostOperationStatus> SaveAsync(CancellationToken ct);
    Task ResetTrackingAsync();
}

public interface IForecastCostsCrudService
{
    Task<IReadOnlyList<ForecastCostItem>> ListAsync(Guid projectId, CancellationToken ct);
    Task<ForecastCostItem?> GetAsync(Guid id, CancellationToken ct);
    Task<ForecastCostResult<ForecastCostItem>> CreateAsync(Guid projectId, ForecastCostKind kind, string name, decimal amount,
        string currency, DateOnly expectedOn, string? note, CancellationToken ct);
    Task<ForecastCostResult<ForecastCostItem>> UpdateAsync(Guid id, long expectedVersion, ForecastCostKind kind, string name,
        decimal amount, string currency, DateOnly expectedOn, string? note, CancellationToken ct);
    Task<ForecastCostResult> ArchiveAsync(Guid id, long expectedVersion, CancellationToken ct);
}

internal sealed class ForecastCostsCrudService(IForecastCostsStore store, IBudgetingProjectLookup projects, TimeProvider clock) : IForecastCostsCrudService
{
    public Task<IReadOnlyList<ForecastCostItem>> ListAsync(Guid projectId, CancellationToken ct) => Read(async () =>
        (IReadOnlyList<ForecastCostItem>)(await store.ListAsync(new(projectId), ct)).Select(Map).ToArray(), ct);

    public Task<ForecastCostItem?> GetAsync(Guid id, CancellationToken ct) => Read(async () =>
    {
        var cost = await store.GetAsync(new(id), false, ct);
        return cost is null ? null : Map(cost);
    }, ct);

    public Task<ForecastCostResult<ForecastCostItem>> CreateAsync(Guid projectId, ForecastCostKind kind, string name, decimal amount,
        string currency, DateOnly expectedOn, string? note, CancellationToken ct) => Guard(async () =>
    {
        var project = await projects.GetAsync(projectId, ct);
        var validation = ValidateProject(project, currency);
        if (validation is not null) return validation;
        var cost = ForecastCost.Create(new(projectId), kind, name, new(amount, new(currency.Trim().ToUpperInvariant())), expectedOn, note, clock.GetUtcNow());
        await store.AddAsync(cost, ct);
        return await Save(cost, "Koszt został dodany.", ct);
    }, ct);

    public Task<ForecastCostResult<ForecastCostItem>> UpdateAsync(Guid id, long expectedVersion, ForecastCostKind kind, string name,
        decimal amount, string currency, DateOnly expectedOn, string? note, CancellationToken ct) => Guard(async () =>
    {
        var cost = await store.GetAsync(new(id), true, ct);
        if (cost is null) return Fail(ForecastCostOperationStatus.NotFound, "Nie znaleziono kosztu.");
        if (cost.ArchivedAtUtc is not null) return Fail(ForecastCostOperationStatus.Archived, "Koszt jest zarchiwizowany.");
        if (cost.Version != expectedVersion) return Fail(ForecastCostOperationStatus.ConcurrencyConflict, "Koszt został zmieniony.");
        var validation = ValidateProject(await projects.GetAsync(cost.ProjectId.Value, ct), currency);
        if (validation is not null) return validation;
        cost.Update(kind, name, new(amount, new(currency.Trim().ToUpperInvariant())), expectedOn, note, clock.GetUtcNow());
        return await Save(cost, "Koszt został zmieniony.", ct);
    }, ct);

    public async Task<ForecastCostResult> ArchiveAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var result = await Guard(async () =>
        {
            var cost = await store.GetAsync(new(id), true, ct);
            if (cost is null) return Fail(ForecastCostOperationStatus.NotFound, "Nie znaleziono kosztu.");
            if (cost.ArchivedAtUtc is not null) return Fail(ForecastCostOperationStatus.Archived, "Koszt jest zarchiwizowany.");
            if (cost.Version != expectedVersion) return Fail(ForecastCostOperationStatus.ConcurrencyConflict, "Koszt został zmieniony.");
            var project = await projects.GetAsync(cost.ProjectId.Value, ct);
            if (project is not { Available: true }) return Fail(ForecastCostOperationStatus.ProjectUnavailable, "Projekt nie jest dostępny.");
            cost.Archive(clock.GetUtcNow());
            return await Save(cost, "Koszt został zarchiwizowany.", ct);
        }, ct);
        return new(result.Status, result.SafeMessage);
    }

    private static ForecastCostResult<ForecastCostItem>? ValidateProject(BudgetProjectInfo? project, string currency)
    {
        if (project is not { Available: true }) return Fail(ForecastCostOperationStatus.ProjectUnavailable, "Projekt nie jest dostępny.");
        if (string.IsNullOrWhiteSpace(currency) || !string.Equals(project.BaseCurrency, currency.Trim(), StringComparison.OrdinalIgnoreCase))
            return Fail(ForecastCostOperationStatus.ValidationFailure, "Waluta kosztu musi być walutą bazową projektu.");
        return null;
    }

    private async Task<ForecastCostResult<ForecastCostItem>> Save(ForecastCost cost, string message, CancellationToken ct)
    {
        var status = await store.SaveAsync(ct);
        return status == ForecastCostOperationStatus.Success ? new(status, message, Map(cost)) : Fail(status, "Nie udało się zapisać kosztu.");
    }

    private async Task<ForecastCostResult<ForecastCostItem>> Guard(Func<Task<ForecastCostResult<ForecastCostItem>>> action, CancellationToken ct)
    {
        try { ct.ThrowIfCancellationRequested(); return await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail(ForecastCostOperationStatus.Cancelled, "Operacja została anulowana."); }
        catch (ArgumentException) { return Fail(ForecastCostOperationStatus.ValidationFailure, "Popraw wskazane dane."); }
        catch (InvalidOperationException) { return Fail(ForecastCostOperationStatus.ValidationFailure, "Operacja jest niedozwolona."); }
        catch (Exception e) when (e is ForecastCostsPersistenceException or BudgetingProjectLookupException) { return Fail(ForecastCostOperationStatus.PersistenceFailure, "Nie udało się wykonać operacji."); }
        finally { await store.ResetTrackingAsync(); }
    }

    private static async Task<T> Read<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is ForecastCostsPersistenceException or BudgetingProjectLookupException) { throw new ForecastCostsReadException(e); }
    }

    private static ForecastCostResult<ForecastCostItem> Fail(ForecastCostOperationStatus status, string message) => new(status, message, null);
    private static ForecastCostItem Map(ForecastCost cost) => new(cost.Id.Value, cost.ProjectId.Value, cost.Kind, cost.Name,
        cost.Money.Amount, cost.Money.Currency.Value, cost.ExpectedOn, cost.Note, cost.CreatedAtUtc, cost.UpdatedAtUtc, cost.Version);
}
