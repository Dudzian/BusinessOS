using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class BudgetVarianceTests
{
    private readonly Guid projectId = Guid.NewGuid();
    private readonly Guid budgetId = Guid.NewGuid();
    private readonly Guid versionId = Guid.NewGuid();

    [Fact]
    public async Task Snapshot_aggregates_expenses_actuals_variance_utilization_and_total()
    {
        var store = Store(
            [new(BudgetLineKind.Capex, 100, "PLN"), new(BudgetLineKind.Capex, 50, "PLN"), new(BudgetLineKind.Opex, 40, "PLN"), new(BudgetLineKind.Revenue, 250, "PLN"), new(BudgetLineKind.Financing, 500, "PLN")],
            [new(ActualCostKind.Capex, 120, "PLN"), new(ActualCostKind.Opex, 60, "PLN")]);
        var result = await Service(store).GetSnapshotAsync(projectId, budgetId, versionId, default);
        result!.Capex.Should().Be(new BudgetVarianceMetric(150, 120, 30, 80, BudgetVarianceState.UnderBudget));
        result.Opex.Should().Be(new BudgetVarianceMetric(40, 60, -20, 150, BudgetVarianceState.OverBudget));
        result.Total.Should().Be(new BudgetVarianceMetric(190, 180, 10, 180m / 190m * 100, BudgetVarianceState.UnderBudget));
    }

    [Fact] public async Task Zero_plan_with_actual_is_unplanned_and_has_null_utilization() { var result = await Service(Store([], [new(ActualCostKind.Opex, 25, "PLN")])).GetSnapshotAsync(projectId, budgetId, versionId, default); result!.Opex.Should().Be(new BudgetVarianceMetric(0, 25, -25, null, BudgetVarianceState.UnplannedSpend)); }
    [Fact] public async Task Equal_plan_and_actual_is_on_budget() { var result = await Service(Store([new(BudgetLineKind.Capex, 100, "PLN")], [new(ActualCostKind.Capex, 100, "PLN")])).GetSnapshotAsync(projectId, budgetId, versionId, default); result!.Capex.State.Should().Be(BudgetVarianceState.OnBudget); result.Capex.UtilizationPercent.Should().Be(100); }
    [Fact] public async Task Archived_budget_is_accepted() { var store = Store([], []); store.Source = store.Source! with { BudgetStatus = BudgetStatus.Archived }; (await Service(store).GetSnapshotAsync(projectId, budgetId, versionId, default))!.BudgetStatus.Should().Be(BudgetStatus.Archived); }
    [Fact] public async Task Mismatch_returns_not_found() { var store = Store([], []); store.Source = null; (await Service(store).GetSnapshotAsync(projectId, budgetId, versionId, default)).Should().BeNull(); }
    [Fact] public async Task Currency_corruption_is_safe_read_failure() { var action = () => Service(Store([new(BudgetLineKind.Capex, 1, "EUR")], [])).GetSnapshotAsync(projectId, budgetId, versionId, default); var error = await action.Should().ThrowAsync<BudgetVarianceReadException>(); error.Which.Message.Should().NotContain("EUR"); }
    [Fact] public async Task Persistence_failure_is_safe_and_does_not_leak_details() { var action = () => Service(new FakeStore { Failure = new InvalidOperationException("secret sqlite detail") }).ListBudgetsAsync(projectId, default); var error = await action.Should().ThrowAsync<BudgetVarianceReadException>(); error.Which.Message.Should().NotContain("secret"); }
    [Fact] public async Task Cancellation_is_preserved() { using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(Store([], [])).GetSnapshotAsync(projectId, budgetId, versionId, cts.Token)); }

    private BudgetVarianceQueryService Service(FakeStore store) => new(store, new FakeProjects(projectId));
    private FakeStore Store(IReadOnlyList<BudgetVarianceLineSource> lines, IReadOnlyList<BudgetVarianceActualSource> actuals) => new() { Source = new(projectId, budgetId, "Plan", BudgetStatus.Draft, versionId, 1, lines, actuals) };
    private sealed class FakeProjects(Guid id) : IBudgetingProjectLookup { public Task<BudgetProjectInfo?> GetAsync(Guid projectId, CancellationToken ct) => Task.FromResult<BudgetProjectInfo?>(projectId == id ? new(id, "Project", "PLN", true) : null); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)[]); }
    private sealed class FakeStore : IBudgetVarianceReadStore
    {
        public BudgetVarianceSnapshotSource? Source { get; set; }
        public Exception? Failure { get; set; }
        public Task<IReadOnlyList<BudgetVarianceBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct) => Failure is null ? Task.FromResult((IReadOnlyList<BudgetVarianceBudgetItem>)[]) : Task.FromException<IReadOnlyList<BudgetVarianceBudgetItem>>(Failure);
        public Task<IReadOnlyList<BudgetVarianceVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetVarianceVersionItem>)[]);
        public Task<BudgetVarianceSnapshotSource?> GetSnapshotSourceAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct) => Task.FromResult(Source);
    }
}
