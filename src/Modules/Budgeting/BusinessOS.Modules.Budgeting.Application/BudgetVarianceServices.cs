using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Modules.Budgeting.Application;

public enum BudgetVarianceState { UnderBudget, OnBudget, OverBudget, UnplannedSpend }
public sealed record BudgetVarianceMetric(decimal Planned, decimal Actual, decimal Variance, decimal? UtilizationPercent, BudgetVarianceState State);
public sealed record BudgetVarianceBudgetItem(Guid Id, string Name, BudgetStatus Status, DateTimeOffset UpdatedAtUtc);
public sealed record BudgetVarianceVersionItem(Guid Id, Guid BudgetId, int Number, DateTimeOffset CreatedAtUtc)
{
    public string Label => $"Version {Number}";
}
public sealed record BudgetVarianceSnapshot(Guid ProjectId, string ProjectName, string Currency, Guid BudgetId, string BudgetName,
    BudgetStatus BudgetStatus, Guid VersionId, int VersionNumber, BudgetVarianceMetric Capex, BudgetVarianceMetric Opex, BudgetVarianceMetric Total);
public sealed record BudgetVarianceLineSource(BudgetLineKind Kind, decimal Amount, string Currency);
public sealed record BudgetVarianceActualSource(ActualCostKind Kind, decimal Amount, string Currency);
public sealed record BudgetVarianceSnapshotSource(Guid ProjectId, Guid BudgetId, string BudgetName, BudgetStatus BudgetStatus,
    Guid VersionId, int VersionNumber, IReadOnlyList<BudgetVarianceLineSource> Lines, IReadOnlyList<BudgetVarianceActualSource> Actuals);

public sealed class BudgetVarianceReadException(Exception inner) : Exception("Plan vs actual data could not be read.", inner);

public interface IBudgetVarianceReadStore
{
    Task<IReadOnlyList<BudgetVarianceBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<BudgetVarianceVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct);
    Task<BudgetVarianceSnapshotSource?> GetSnapshotSourceAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct);
}

public interface IBudgetVarianceQueryService
{
    Task<IReadOnlyList<BudgetVarianceBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<BudgetVarianceVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct);
    Task<BudgetVarianceSnapshot?> GetSnapshotAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct);
}

internal sealed class BudgetVarianceQueryService(IBudgetVarianceReadStore store, IBudgetingProjectLookup projects) : IBudgetVarianceQueryService
{
    public Task<IReadOnlyList<BudgetVarianceBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct) =>
        ReadAsync(() => store.ListBudgetsAsync(projectId, ct), ct);

    public Task<IReadOnlyList<BudgetVarianceVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct) =>
        ReadAsync(() => store.ListVersionsAsync(budgetId, ct), ct);

    public Task<BudgetVarianceSnapshot?> GetSnapshotAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct) => ReadAsync<BudgetVarianceSnapshot?>(async () =>
    {
        var project = await projects.GetAsync(projectId, ct);
        if (project is null) return null;
        var source = await store.GetSnapshotSourceAsync(projectId, budgetId, versionId, ct);
        if (source is null) return null;
        var currencies = source.Lines.Select(x => x.Currency).Concat(source.Actuals.Select(x => x.Currency));
        if (currencies.Any(x => !string.Equals(x, project.BaseCurrency, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Currency consistency check failed.");

        var capex = Metric(source.Lines.Where(x => x.Kind == BudgetLineKind.Capex).Sum(x => x.Amount), source.Actuals.Where(x => x.Kind == ActualCostKind.Capex).Sum(x => x.Amount));
        var opex = Metric(source.Lines.Where(x => x.Kind == BudgetLineKind.Opex).Sum(x => x.Amount), source.Actuals.Where(x => x.Kind == ActualCostKind.Opex).Sum(x => x.Amount));
        return new(source.ProjectId, project.Name, project.BaseCurrency, source.BudgetId, source.BudgetName, source.BudgetStatus,
            source.VersionId, source.VersionNumber, capex, opex, Metric(capex.Planned + opex.Planned, capex.Actual + opex.Actual));
    }, ct);

    private static BudgetVarianceMetric Metric(decimal planned, decimal actual)
    {
        var variance = planned - actual;
        var state = planned == 0 && actual > 0 ? BudgetVarianceState.UnplannedSpend
            : variance > 0 ? BudgetVarianceState.UnderBudget
            : variance < 0 ? BudgetVarianceState.OverBudget : BudgetVarianceState.OnBudget;
        return new(planned, actual, variance, planned > 0 ? actual / planned * 100 : null, state);
    }

    private static async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try { ct.ThrowIfCancellationRequested(); return await read(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BudgetVarianceReadException) { throw; }
        catch (Exception exception) { throw new BudgetVarianceReadException(exception); }
    }
}
