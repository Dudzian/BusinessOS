using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class BudgetForecastViewModelTests
{
    private readonly BudgetProjectInfo p1 = new(Guid.NewGuid(), "Gym", "PLN", true);
    private readonly BudgetProjectInfo p2 = new(Guid.NewGuid(), "Hotel", "EUR", true);
    private readonly BudgetForecastBudgetItem b1 = new(Guid.NewGuid(), "Plan", BudgetStatus.Archived, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Reload_success_sets_flag_and_failure_preserves_state_then_success_clears_error()
    {
        var lookup = new Projects { Items = [p1] }; var service = new Query { Budgets = [b1] }; var vm = new BudgetForecastViewModel(service, lookup);
        await vm.ReloadProjectsAsync(); await vm.SelectProjectAsync(p1);
        vm.LastProjectsReloadSucceeded.Should().BeTrue(); vm.SelectedProject.Should().BeSameAs(p1); vm.Budgets.Should().Equal(b1);
        lookup.Failure = true; await vm.ReloadProjectsAsync();
        vm.LastProjectsReloadSucceeded.Should().BeFalse(); vm.SelectedProject.Should().BeSameAs(p1); vm.Budgets.Should().Equal(b1); vm.OperationMessage.Should().NotBeEmpty();
        lookup.Failure = false; await vm.ReloadProjectsAsync();
        vm.LastProjectsReloadSucceeded.Should().BeTrue(); vm.OperationMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task Project_selection_is_atomic_and_reasserts_canonical_selection_on_failure()
    {
        var service = new Query { Budgets = [b1] }; var vm = await Ready(service);
        await vm.SelectProjectAsync(p1); var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        service.Failure = true; await vm.SelectProjectAsync(p2);
        vm.SelectedProject.Should().BeSameAs(p1); vm.Budgets.Should().Equal(b1); names.Should().Contain(nameof(vm.SelectedProject)).And.Contain(nameof(vm.Snapshot)); vm.LastProjectsReloadSucceeded.Should().BeTrue();
        await vm.SelectProjectAsync(new(Guid.NewGuid(), "Foreign", "PLN", true)); vm.SelectedProject.Should().BeSameAs(p1);
    }

    [Fact]
    public async Task Budget_selection_is_atomic_and_reasserts_canonical_selection_on_failure()
    {
        var v1 = Version(b1, 1); var b2 = new BudgetForecastBudgetItem(Guid.NewGuid(), "Second", BudgetStatus.Draft, DateTimeOffset.UtcNow);
        var service = new Query { Budgets = [b1, b2], Versions = [v1] }; var vm = await Ready(service); await vm.SelectProjectAsync(p1); await vm.SelectBudgetAsync(b1);
        var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName); service.Failure = true; await vm.SelectBudgetAsync(b2);
        vm.SelectedBudget.Should().BeSameAs(b1); vm.Versions.Should().Equal(v1); names.Should().Contain(nameof(vm.SelectedBudget));
        await vm.SelectBudgetAsync(new(Guid.NewGuid(), "Foreign", BudgetStatus.Draft, DateTimeOffset.UtcNow)); vm.SelectedBudget.Should().BeSameAs(b1);
    }

    [Fact]
    public async Task Version_failure_and_null_snapshot_roll_back_selection_and_snapshot()
    {
        var v1 = Version(b1, 1); var v2 = Version(b1, 2); var service = new Query { Budgets = [b1], Versions = [v1, v2], Snapshot = Snapshot(v1, 100, 150, 75) };
        var vm = await Selected(service, v1); var old = vm.Snapshot; var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        service.Failure = true; await vm.SelectVersionAsync(v2); vm.SelectedVersion.Should().BeSameAs(v1); vm.Snapshot.Should().BeSameAs(old); names.Should().Contain(nameof(vm.SelectedVersion));
        service.Failure = false; service.Snapshot = null; names.Clear(); await vm.SelectVersionAsync(v2); vm.SelectedVersion.Should().BeSameAs(v1); vm.Snapshot.Should().BeSameAs(old); vm.OperationMessage.Should().NotBeEmpty(); names.Should().Contain(nameof(vm.SelectedVersion));
        await vm.SelectVersionAsync(new(Guid.NewGuid(), Guid.NewGuid(), 9, DateTimeOffset.UtcNow)); vm.SelectedVersion.Should().BeSameAs(v1);
    }

    [Fact]
    public async Task Failed_project_selection_preserves_complete_deep_state_and_reasserts_bindings()
    {
        var version = Version(b1, 1); var service = new Query { Budgets = [b1], Versions = [version], Snapshot = Snapshot(version, 100, 150, 75) }; var vm = await Selected(service, version); var oldSnapshot = vm.Snapshot; var oldVersions = vm.Versions.ToArray(); var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName); service.Failure = true; await vm.SelectProjectAsync(p2);
        vm.SelectedProject.Should().BeSameAs(p1); vm.Budgets.Should().Equal(b1); vm.SelectedBudget.Should().BeSameAs(b1); vm.Versions.Should().Equal(oldVersions); vm.SelectedVersion.Should().BeSameAs(version); vm.Snapshot.Should().BeSameAs(oldSnapshot); vm.ProjectCurrency.Should().Be("PLN"); vm.BudgetStatus.Should().Be("Archived"); vm.VersionLabel.Should().Be("Version 1"); names.Should().Contain([nameof(vm.SelectedProject), nameof(vm.SelectedBudget), nameof(vm.SelectedVersion), nameof(vm.Snapshot)]);
    }
    [Fact]
    public async Task Failed_budget_selection_preserves_complete_deep_state_and_reasserts_bindings()
    {
        var version = Version(b1, 1); var b2 = new BudgetForecastBudgetItem(Guid.NewGuid(), "Second", BudgetStatus.Draft, DateTimeOffset.UtcNow); var service = new Query { Budgets = [b1, b2], Versions = [version], Snapshot = Snapshot(version, 100, 150, 75) }; var vm = await Selected(service, version); var oldSnapshot = vm.Snapshot; var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName); service.Failure = true; await vm.SelectBudgetAsync(b2);
        vm.SelectedBudget.Should().BeSameAs(b1); vm.Versions.Should().Equal(version); vm.SelectedVersion.Should().BeSameAs(version); vm.Snapshot.Should().BeSameAs(oldSnapshot); names.Should().Contain(nameof(vm.SelectedBudget)).And.Contain(nameof(vm.SelectedVersion)).And.Contain(nameof(vm.Snapshot));
    }

    [Theory]
    [InlineData(100, 150, "100", "150", "75", "225", "-125", "225%", "Powyżej budżetu")]
    [InlineData(150, 150, "150", "150", "75", "225", "-75", "150%", "Powyżej budżetu")]
    public async Task Snapshot_metrics_are_formatted_for_versions(decimal planned, decimal actual, string planText, string actualText, string etc, string eac, string variance, string utilization, string state)
    {
        var version = Version(b1, planned == 100 ? 1 : 2); var service = new Query { Budgets = [b1], Versions = [version], Snapshot = Snapshot(version, planned, actual, planned == 100 ? 75 : 75) }; var vm = await Selected(service, version);
        vm.BudgetStatus.Should().Be("Archived"); vm.CapexPlanned.Should().Be(planText); vm.CapexActual.Should().Be(actualText); vm.CapexEtc.Should().Be(etc); vm.CapexEac.Should().Be(eac); vm.CapexVac.Should().Be(variance); vm.CapexUtilization.Should().Be(utilization); vm.CapexState.Should().Be(state);
        vm.OpexUtilization.Should().Be("—"); vm.OpexState.Should().Be("Zgodnie z budżetem");
    }

    [Fact]
    public async Task Refresh_updates_snapshot_and_failure_or_null_preserves_it()
    {
        var v1 = Version(b1, 1); var service = new Query { Budgets = [b1], Versions = [v1], Snapshot = Snapshot(v1, 100, 50, 25) }; var vm = await Selected(service, v1);
        vm.ReportPresentationFailure(); vm.OperationMessage.Should().NotBeEmpty(); service.Snapshot = Snapshot(v1, 100, 75, 25); await vm.RefreshAsync(); vm.CapexActual.Should().Be("75"); vm.OperationMessage.Should().BeEmpty();
        var old = vm.Snapshot; service.Failure = true; await vm.RefreshAsync(); vm.Snapshot.Should().BeSameAs(old); vm.LastProjectsReloadSucceeded.Should().BeTrue();
        service.Failure = false; service.Snapshot = null; await vm.RefreshAsync(); vm.Snapshot.Should().BeSameAs(old); vm.OperationMessage.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Busy_state_disables_all_capabilities_and_success_clears_error()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<BudgetForecastBudgetItem>>(); var service = new Query { BudgetGate = gate }; var vm = await Ready(service);
        vm.ReportPresentationFailure(); var selecting = vm.SelectProjectAsync(p1); vm.IsBusy.Should().BeTrue(); vm.CanNavigate.Should().BeFalse(); vm.CanSelectProject.Should().BeFalse(); vm.CanSelectBudget.Should().BeFalse(); vm.CanSelectVersion.Should().BeFalse(); vm.CanRefresh.Should().BeFalse();
        gate.SetResult([b1]); await selecting; vm.OperationMessage.Should().BeEmpty(); vm.IsBusy.Should().BeFalse(); vm.CanNavigate.Should().BeTrue(); vm.CanSelectProject.Should().BeTrue(); vm.CanSelectBudget.Should().BeTrue(); vm.CanSelectVersion.Should().BeFalse(); vm.CanRefresh.Should().BeFalse();
    }

    private async Task<BudgetForecastViewModel> Ready(Query service) { var vm = new BudgetForecastViewModel(service, new Projects { Items = [p1, p2] }); await vm.ReloadProjectsAsync(); return vm; }
    private async Task<BudgetForecastViewModel> Selected(Query service, BudgetForecastVersionItem version) { var vm = await Ready(service); await vm.SelectProjectAsync(p1); await vm.SelectBudgetAsync(b1); await vm.SelectVersionAsync(version); return vm; }
    private static BudgetForecastVersionItem Version(BudgetForecastBudgetItem budget, int number) => new(Guid.NewGuid(), budget.Id, number, DateTimeOffset.UtcNow);
    private BudgetForecastSnapshot Snapshot(BudgetForecastVersionItem version, decimal planned, decimal actual, decimal etc) => new(p1.Id, p1.Name, p1.BaseCurrency, b1.Id, b1.Name, b1.Status, version.Id, version.Number, Metric(planned, actual, etc), Metric(0, 0, 0), Metric(planned, actual, etc));
    private static BudgetForecastMetric Metric(decimal planned, decimal actual, decimal etc) { var eac = actual + etc; var vac = planned - eac; return new(planned, actual, etc, eac, vac, planned == 0 ? null : eac / planned * 100, planned == 0 && eac > 0 ? BudgetForecastState.UnplannedSpend : vac > 0 ? BudgetForecastState.UnderBudget : vac < 0 ? BudgetForecastState.OverBudget : BudgetForecastState.OnBudget); }
    private sealed class Projects : IBudgetingProjectLookup { public IReadOnlyList<BudgetProjectInfo> Items { get; set; } = []; public bool Failure { get; set; } public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id)); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Failure ? Task.FromException<IReadOnlyList<BudgetProjectInfo>>(new BudgetingProjectLookupException("lookup", new Exception())) : Task.FromResult(Items); }
    private sealed class Query : IBudgetForecastQueryService
    {
        public IReadOnlyList<BudgetForecastBudgetItem> Budgets { get; set; } = []; public IReadOnlyList<BudgetForecastVersionItem> Versions { get; set; } = []; public BudgetForecastSnapshot? Snapshot { get; set; }
        public bool Failure { get; set; }
        public TaskCompletionSource<IReadOnlyList<BudgetForecastBudgetItem>>? BudgetGate { get; set; }
        public Task<IReadOnlyList<BudgetForecastBudgetItem>> ListBudgetsAsync(Guid id, CancellationToken ct) => Failure ? Fail<IReadOnlyList<BudgetForecastBudgetItem>>() : BudgetGate?.Task ?? Task.FromResult(Budgets);
        public Task<IReadOnlyList<BudgetForecastVersionItem>> ListVersionsAsync(Guid id, CancellationToken ct) => Failure ? Fail<IReadOnlyList<BudgetForecastVersionItem>>() : Task.FromResult(Versions);
        public Task<BudgetForecastSnapshot?> GetSnapshotAsync(Guid p, Guid b, Guid v, CancellationToken ct) => Failure ? Fail<BudgetForecastSnapshot?>() : Task.FromResult(Snapshot);
        private static Task<T> Fail<T>() => Task.FromException<T>(new BudgetForecastReadException(new Exception("technical")));
    }
}
