using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class CostCashFlowTests
{
    private readonly Guid projectId = Guid.NewGuid();

    [Fact]
    public async Task Groups_active_costs_by_their_literal_dates_into_sparse_ordered_months()
    {
        var actuals = new[] { new CostCashFlowActualSource(ActualCostKind.Capex, 100, "pln", new(2026, 8, 15)), new(ActualCostKind.Opex, 20, "PLN", new(2026, 8, 31)) };
        var forecasts = new[] { new CostCashFlowForecastSource(ForecastCostKind.Capex, 50, "PLN", new(2026, 8, 1)), new(ForecastCostKind.Opex, 30, "PLN", new(2027, 1, 10)) };
        var snapshot = await Service(actuals, forecasts).GetSnapshotAsync(projectId, default);
        snapshot!.Months.Select(x => x.Month).Should().Equal(new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 1));
        snapshot.Months[0].Capex.Should().Be(new CostCashFlowMetric(100, 50, 150));
        snapshot.Total.Should().Be(new CostCashFlowMetric(120, 80, 200));
    }

    [Fact] public async Task Empty_sources_return_empty_snapshot() => (await Service([], []).GetSnapshotAsync(projectId, default))!.Months.Should().BeEmpty();
    [Fact] public async Task Past_forecast_stays_in_expected_month() => (await Service([], [new(ForecastCostKind.Opex, 5, "PLN", new(2020, 2, 20))]).GetSnapshotAsync(projectId, default))!.Months.Single().Month.Should().Be(new DateOnly(2020, 2, 1));
    [Theory][InlineData(true)][InlineData(false)] public async Task Currency_corruption_is_safe(bool actual) { var service = actual ? Service([new(ActualCostKind.Capex, 1, "EUR", new(2026, 1, 1))], []) : Service([], [new(ForecastCostKind.Capex, 1, "EUR", new(2026, 1, 1))]); var action = () => service.GetSnapshotAsync(projectId, default); (await action.Should().ThrowAsync<CostCashFlowReadException>()).Which.Message.Should().NotContain("EUR").And.NotContain("SQL"); }
    [Theory][InlineData(true)][InlineData(false)] public async Task Unknown_kind_is_safe(bool actual) { var service = actual ? Service([new((ActualCostKind)99, 1, "PLN", new(2026, 1, 1))], []) : Service([], [new((ForecastCostKind)99, 1, "PLN", new(2026, 1, 1))]); await ((Func<Task>)(() => service.GetSnapshotAsync(projectId, default))).Should().ThrowAsync<CostCashFlowReadException>(); }
    [Theory][InlineData(false)][InlineData(true)] public async Task Missing_or_unavailable_project_returns_null(bool missing) { var services = new ServiceCollection().AddBudgetingModule().AddSingleton<ICostCashFlowReadStore>(new Store(projectId, [], [])).AddSingleton<IBudgetingProjectLookup>(new Lookup(missing ? null : new(projectId, "P", "PLN", false))).BuildServiceProvider(); (await services.GetRequiredService<ICostCashFlowQueryService>().GetSnapshotAsync(projectId, default)).Should().BeNull(); }
    [Fact] public async Task Cancellation_is_not_wrapped() { using var cts = new CancellationTokenSource(); cts.Cancel(); await ((Func<Task>)(() => Service([], []).GetSnapshotAsync(projectId, cts.Token))).Should().ThrowAsync<OperationCanceledException>(); }
    [Fact] public async Task Store_failure_is_wrapped() { var services = new ServiceCollection().AddBudgetingModule().AddSingleton<ICostCashFlowReadStore>(new FailingStore()).AddSingleton<IBudgetingProjectLookup>(new Lookup(new(projectId, "P", "PLN", true))).BuildServiceProvider(); await ((Func<Task>)(() => services.GetRequiredService<ICostCashFlowQueryService>().GetSnapshotAsync(projectId, default))).Should().ThrowAsync<CostCashFlowReadException>(); }
    [Fact] public async Task Actual_only_aggregates_multiple_records() { var s = await Service([new(ActualCostKind.Capex, 2, "PLN", new(2026, 1, 2)), new(ActualCostKind.Capex, 3, "PLN", new(2026, 1, 30))], []).GetSnapshotAsync(projectId, default); s!.Capex.Should().Be(new CostCashFlowMetric(5, 0, 5)); }
    [Fact] public async Task Forecast_only_aggregates_multiple_records() { var s = await Service([], [new(ForecastCostKind.Opex, 2, "pln", new(2026, 1, 2)), new(ForecastCostKind.Opex, 3, "PLN", new(2026, 1, 30))]).GetSnapshotAsync(projectId, default); s!.Opex.Should().Be(new CostCashFlowMetric(0, 5, 5)); }
    [Fact] public async Task Same_month_mixed_categories_have_correct_total() { var s = await Service([new(ActualCostKind.Capex, 100, "PLN", new(2026, 8, 1)), new(ActualCostKind.Opex, 20, "PLN", new(2026, 8, 2))], [new(ForecastCostKind.Capex, 50, "PLN", new(2026, 8, 3)), new(ForecastCostKind.Opex, 30, "PLN", new(2026, 8, 4))]).GetSnapshotAsync(projectId, default); s!.Months.Single().Total.Should().Be(new CostCashFlowMetric(120, 80, 200)); }
    [Fact] public async Task Sparse_months_and_year_boundary_are_literal() { var s = await Service([new(ActualCostKind.Capex, 1, "PLN", new(2026, 1, 15)), new(ActualCostKind.Capex, 1, "PLN", new(2026, 12, 15))], [new(ForecastCostKind.Capex, 1, "PLN", new(2026, 4, 10)), new(ForecastCostKind.Capex, 1, "PLN", new(2027, 1, 1))]).GetSnapshotAsync(projectId, default); s!.Months.Select(x => x.Month).Should().Equal(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1)); }
    [Fact] public async Task Lookup_failure_is_wrapped_without_details() { var services = new ServiceCollection().AddBudgetingModule().AddSingleton<ICostCashFlowReadStore>(new Store(projectId, [], [])).AddSingleton<IBudgetingProjectLookup>(new FailingLookup()).BuildServiceProvider(); var error = await ((Func<Task>)(() => services.GetRequiredService<ICostCashFlowQueryService>().GetSnapshotAsync(projectId, default))).Should().ThrowAsync<CostCashFlowReadException>(); error.Which.Message.Should().NotContain("database path").And.NotContain("provider"); }

    private ICostCashFlowQueryService Service(IReadOnlyList<CostCashFlowActualSource> actuals, IReadOnlyList<CostCashFlowForecastSource> forecasts) => new ServiceCollection().AddBudgetingModule().AddSingleton<ICostCashFlowReadStore>(new Store(projectId, actuals, forecasts)).AddSingleton<IBudgetingProjectLookup>(new Lookup(new(projectId, "Project", "PLN", true))).BuildServiceProvider().GetRequiredService<ICostCashFlowQueryService>();
    private sealed record Store(Guid Id, IReadOnlyList<CostCashFlowActualSource> Actuals, IReadOnlyList<CostCashFlowForecastSource> Forecasts) : ICostCashFlowReadStore { public Task<CostCashFlowSnapshotSource> GetSnapshotSourceAsync(Guid id, CancellationToken ct) => Task.FromResult(new CostCashFlowSnapshotSource(Id, Actuals, Forecasts)); }
    private sealed class FailingStore : ICostCashFlowReadStore { public Task<CostCashFlowSnapshotSource> GetSnapshotSourceAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("SQLite provider"); }
    private sealed class FailingLookup : IBudgetingProjectLookup { public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("database path SQL provider"); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => throw new NotSupportedException(); }
    private sealed record Lookup(BudgetProjectInfo? Project) : IBudgetingProjectLookup { public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Project); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)(Project is null ? [] : [Project])); }
}
