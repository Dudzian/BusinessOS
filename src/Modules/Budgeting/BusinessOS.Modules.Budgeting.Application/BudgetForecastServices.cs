using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Modules.Budgeting.Application;

public enum BudgetForecastState { UnderBudget, OnBudget, OverBudget, UnplannedSpend }
public sealed record BudgetForecastMetric(decimal Planned, decimal Actual, decimal EstimateToComplete, decimal EstimateAtCompletion, decimal VarianceAtCompletion, decimal? EacUtilizationPercent, BudgetForecastState State);
public sealed record BudgetForecastBudgetItem(Guid Id, string Name, BudgetStatus Status, DateTimeOffset UpdatedAtUtc);
public sealed record BudgetForecastVersionItem(Guid Id, Guid BudgetId, int Number, DateTimeOffset CreatedAtUtc) { public string Label => $"Version {Number}"; }
public sealed record BudgetForecastSnapshot(Guid ProjectId, string ProjectName, string Currency, Guid BudgetId, string BudgetName, BudgetStatus BudgetStatus, Guid VersionId, int VersionNumber, BudgetForecastMetric Capex, BudgetForecastMetric Opex, BudgetForecastMetric Total);
public sealed record BudgetForecastLineSource(BudgetLineKind Kind, decimal Amount, string Currency);
public sealed record BudgetForecastActualSource(ActualCostKind Kind, decimal Amount, string Currency);
public sealed record BudgetForecastCostSource(ForecastCostKind Kind, decimal Amount, string Currency);
public sealed record BudgetForecastSnapshotSource(Guid ProjectId, Guid BudgetId, string BudgetName, BudgetStatus BudgetStatus, Guid VersionId, int VersionNumber, IReadOnlyList<BudgetForecastLineSource> Lines, IReadOnlyList<BudgetForecastActualSource> Actuals, IReadOnlyList<BudgetForecastCostSource> Forecasts);
public sealed class BudgetForecastReadException(Exception inner) : Exception("Nie udało się odczytać analizy planu i prognozy.", inner);

public interface IBudgetForecastReadStore
{
    Task<IReadOnlyList<BudgetForecastBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<BudgetForecastVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct);
    Task<BudgetForecastSnapshotSource?> GetSnapshotSourceAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct);
}
public interface IBudgetForecastQueryService
{
    Task<IReadOnlyList<BudgetForecastBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<BudgetForecastVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct);
    Task<BudgetForecastSnapshot?> GetSnapshotAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct);
}

internal sealed class BudgetForecastQueryService(IBudgetForecastReadStore store, IBudgetingProjectLookup projects) : IBudgetForecastQueryService
{
    public Task<IReadOnlyList<BudgetForecastBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct) => ReadAsync(() => store.ListBudgetsAsync(projectId, ct), ct);
    public Task<IReadOnlyList<BudgetForecastVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct) => ReadAsync(() => store.ListVersionsAsync(budgetId, ct), ct);
    public Task<BudgetForecastSnapshot?> GetSnapshotAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct) => ReadAsync<BudgetForecastSnapshot?>(async () =>
    {
        var project = await projects.GetAsync(projectId, ct);
        if (project is not { Available: true }) return null;
        var source = await store.GetSnapshotSourceAsync(projectId, budgetId, versionId, ct);
        if (source is null) return null;
        var currencies = source.Lines.Select(x => x.Currency).Concat(source.Actuals.Select(x => x.Currency)).Concat(source.Forecasts.Select(x => x.Currency));
        if (currencies.Any(x => !string.Equals(x, project.BaseCurrency, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Currency consistency check failed.");
        var capex = Metric(source.Lines.Where(x => x.Kind == BudgetLineKind.Capex).Sum(x => x.Amount), source.Actuals.Where(x => x.Kind == ActualCostKind.Capex).Sum(x => x.Amount), source.Forecasts.Where(x => x.Kind == ForecastCostKind.Capex).Sum(x => x.Amount));
        var opex = Metric(source.Lines.Where(x => x.Kind == BudgetLineKind.Opex).Sum(x => x.Amount), source.Actuals.Where(x => x.Kind == ActualCostKind.Opex).Sum(x => x.Amount), source.Forecasts.Where(x => x.Kind == ForecastCostKind.Opex).Sum(x => x.Amount));
        return new(source.ProjectId, project.Name, project.BaseCurrency, source.BudgetId, source.BudgetName, source.BudgetStatus, source.VersionId, source.VersionNumber, capex, opex, Metric(capex.Planned + opex.Planned, capex.Actual + opex.Actual, capex.EstimateToComplete + opex.EstimateToComplete));
    }, ct);
    private static BudgetForecastMetric Metric(decimal planned, decimal actual, decimal etc)
    {
        var eac = actual + etc; var vac = planned - eac;
        var state = planned == 0 && eac > 0 ? BudgetForecastState.UnplannedSpend : vac > 0 ? BudgetForecastState.UnderBudget : vac < 0 ? BudgetForecastState.OverBudget : BudgetForecastState.OnBudget;
        return new(planned, actual, etc, eac, vac, planned > 0 ? eac / planned * 100 : null, state);
    }
    private static async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try { ct.ThrowIfCancellationRequested(); return await read(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BudgetForecastReadException) { throw; }
        catch (Exception exception) { throw new BudgetForecastReadException(exception); }
    }
}
