using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Modules.Budgeting.Application;

public sealed record CostCashFlowActualSource(ActualCostKind Kind, decimal Amount, string Currency, DateOnly IncurredOn);
public sealed record CostCashFlowForecastSource(ForecastCostKind Kind, decimal Amount, string Currency, DateOnly ExpectedOn);
public sealed record CostCashFlowSnapshotSource(Guid ProjectId, IReadOnlyList<CostCashFlowActualSource> Actuals, IReadOnlyList<CostCashFlowForecastSource> Forecasts);
public sealed record CostCashFlowMetric(decimal Actual, decimal Forecast, decimal Expected);
public sealed record CostCashFlowMonth(DateOnly Month, CostCashFlowMetric Capex, CostCashFlowMetric Opex, CostCashFlowMetric Total);
public sealed record CostCashFlowSnapshot(Guid ProjectId, string ProjectName, string Currency, IReadOnlyList<CostCashFlowMonth> Months, CostCashFlowMetric Capex, CostCashFlowMetric Opex, CostCashFlowMetric Total);
public sealed class CostCashFlowReadException(Exception inner) : Exception("Nie udało się odczytać cash flow kosztów.", inner);

public interface ICostCashFlowReadStore
{
    Task<CostCashFlowSnapshotSource> GetSnapshotSourceAsync(Guid projectId, CancellationToken ct);
}

public interface ICostCashFlowQueryService
{
    Task<CostCashFlowSnapshot?> GetSnapshotAsync(Guid projectId, CancellationToken ct);
}

internal sealed class CostCashFlowQueryService(ICostCashFlowReadStore store, IBudgetingProjectLookup projects) : ICostCashFlowQueryService
{
    public async Task<CostCashFlowSnapshot?> GetSnapshotAsync(Guid projectId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var project = await projects.GetAsync(projectId, ct);
            if (project is not { Available: true }) return null;
            var source = await store.GetSnapshotSourceAsync(projectId, ct);
            if (source.Actuals.Any(x => !Valid(x.Kind)) || source.Forecasts.Any(x => !Valid(x.Kind)))
                throw new InvalidDataException("Invalid cost kind.");
            if (source.Actuals.Any(x => !Currency(x.Currency, project.BaseCurrency)) || source.Forecasts.Any(x => !Currency(x.Currency, project.BaseCurrency)))
                throw new InvalidDataException("Currency consistency check failed.");

            var keys = source.Actuals.Select(x => Month(x.IncurredOn)).Concat(source.Forecasts.Select(x => Month(x.ExpectedOn))).Distinct().Order().ToArray();
            var months = keys.Select(month =>
            {
                var capex = Metric(source.Actuals.Where(x => x.Kind == ActualCostKind.Capex && Month(x.IncurredOn) == month).Sum(x => x.Amount), source.Forecasts.Where(x => x.Kind == ForecastCostKind.Capex && Month(x.ExpectedOn) == month).Sum(x => x.Amount));
                var opex = Metric(source.Actuals.Where(x => x.Kind == ActualCostKind.Opex && Month(x.IncurredOn) == month).Sum(x => x.Amount), source.Forecasts.Where(x => x.Kind == ForecastCostKind.Opex && Month(x.ExpectedOn) == month).Sum(x => x.Amount));
                return new CostCashFlowMonth(month, capex, opex, Add(capex, opex));
            }).ToArray();
            var allCapex = Metric(months.Sum(x => x.Capex.Actual), months.Sum(x => x.Capex.Forecast));
            var allOpex = Metric(months.Sum(x => x.Opex.Actual), months.Sum(x => x.Opex.Forecast));
            return new(projectId, project.Name, project.BaseCurrency, months, allCapex, allOpex, Add(allCapex, allOpex));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CostCashFlowReadException) { throw; }
        catch (Exception exception) { throw new CostCashFlowReadException(exception); }
    }

    private static DateOnly Month(DateOnly value) => new(value.Year, value.Month, 1);
    private static CostCashFlowMetric Metric(decimal actual, decimal forecast) => new(actual, forecast, actual + forecast);
    private static CostCashFlowMetric Add(CostCashFlowMetric left, CostCashFlowMetric right) => Metric(left.Actual + right.Actual, left.Forecast + right.Forecast);
    private static bool Currency(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Valid(ActualCostKind kind) => kind is ActualCostKind.Capex or ActualCostKind.Opex;
    private static bool Valid(ForecastCostKind kind) => kind is ForecastCostKind.Capex or ForecastCostKind.Opex;
}
