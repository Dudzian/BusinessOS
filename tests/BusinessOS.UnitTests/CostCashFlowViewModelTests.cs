using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Budgeting.Application;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class CostCashFlowViewModelTests
{
    private static readonly BudgetProjectInfo A = new(Guid.NewGuid(), "A", "PLN", true);
    private static readonly BudgetProjectInfo B = new(Guid.NewGuid(), "B", "PLN", true);

    [Fact]
    public async Task Reload_success_failure_rollback_and_recovery_are_atomic()
    {
        var lookup = new Lookup { Items = [A] }; var query = new Query { Values = { [A.Id] = Snapshot(A, 100) } }; var vm = new CostCashFlowViewModel(query, lookup);
        await vm.ReloadProjectsAsync(); await vm.SelectProjectAsync(A); var oldSnapshot = vm.Snapshot; var oldMonths = vm.Months.ToArray();
        lookup.Failure = true; await vm.ReloadProjectsAsync();
        vm.LastProjectsReloadSucceeded.Should().BeFalse(); vm.OperationMessage.Should().NotBeEmpty(); vm.Projects.Should().Equal(A); vm.SelectedProject.Should().BeSameAs(A); vm.Snapshot.Should().BeSameAs(oldSnapshot); vm.Months.Should().Equal(oldMonths); vm.CapexActualTotal.Should().Be("100");
        lookup.Failure = false; await vm.ReloadProjectsAsync(); vm.LastProjectsReloadSucceeded.Should().BeTrue(); vm.OperationMessage.Should().BeEmpty(); vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Selection_is_canonical_foreign_is_ignored_and_failure_rolls_back_deeply()
    {
        var lookup = new Lookup { Items = [A, B] }; var query = new Query { Values = { [A.Id] = Snapshot(A, 100), [B.Id] = Snapshot(B, 200) } }; var vm = new CostCashFlowViewModel(query, lookup); await vm.ReloadProjectsAsync();
        await vm.SelectProjectAsync(new(A.Id, "foreign name", "EUR", true)); vm.SelectedProject.Should().BeSameAs(vm.Projects.Single(x => x.Id == A.Id));
        var old = vm.Snapshot; var notifications = new List<string?>(); vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName); query.Failure = true; await vm.SelectProjectAsync(B);
        vm.SelectedProject!.Id.Should().Be(A.Id); vm.Snapshot.Should().BeSameAs(old); vm.CapexActualTotal.Should().Be("100"); notifications.Should().Contain(nameof(vm.SelectedProject)).And.Contain(nameof(vm.Snapshot));
        var calls = query.Calls; await vm.SelectProjectAsync(new(Guid.NewGuid(), "X", "PLN", true)); query.Calls.Should().Be(calls); vm.SelectedProject!.Id.Should().Be(A.Id);
    }

    [Fact]
    public async Task Refresh_success_failure_and_null_preserve_or_replace_as_required()
    {
        var lookup = new Lookup { Items = [A] }; var query = new Query { Values = { [A.Id] = Snapshot(A, 100) } }; var vm = new CostCashFlowViewModel(query, lookup); await vm.ReloadProjectsAsync(); await vm.SelectProjectAsync(A);
        query.Values[A.Id] = Snapshot(A, 250); await vm.RefreshAsync(); vm.CapexActualTotal.Should().Be("250"); vm.OperationMessage.Should().BeEmpty(); vm.LastProjectsReloadSucceeded.Should().BeTrue(); var old = vm.Snapshot;
        query.Failure = true; await vm.RefreshAsync(); vm.Snapshot.Should().BeSameAs(old); vm.LastProjectsReloadSucceeded.Should().BeTrue();
        query.Failure = false; query.Values[A.Id] = null; await vm.RefreshAsync(); vm.Snapshot.Should().BeSameAs(old); vm.OperationMessage.Should().NotBeEmpty(); vm.LastProjectsReloadSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Busy_gate_disables_all_capabilities_deterministically()
    {
        var lookup = new Lookup { Items = [A] }; var query = new Query(); var vm = new CostCashFlowViewModel(query, lookup); await vm.ReloadProjectsAsync(); query.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var selecting = vm.SelectProjectAsync(A); vm.IsBusy.Should().BeTrue(); vm.CanNavigate.Should().BeFalse(); vm.CanSelectProject.Should().BeFalse(); vm.CanRefresh.Should().BeFalse();
        query.Gate.SetResult(Snapshot(A, 1)); await selecting; vm.IsBusy.Should().BeFalse(); vm.CanNavigate.Should().BeTrue(); vm.CanSelectProject.Should().BeTrue(); vm.CanRefresh.Should().BeTrue();
    }

    [Fact]
    public async Task Presentation_and_empty_state_are_invariant()
    {
        var month = new CostCashFlowMonth(new(2026, 8, 1), new(100, 0, 100), new(0, 50, 50), new(100, 50, 150)); var item = new CostCashFlowMonthItem(month);
        item.MonthLabel.Should().Be("2026-08"); item.SemanticName.Should().Be("2026-08 | CAPEX A=100 F=0 E=100 | OPEX A=0 F=50 E=50 | TOTAL A=100 F=50 E=150");
        var lookup = new Lookup { Items = [A] }; var query = new Query { Values = { [A.Id] = Snapshot(A, 0, []) } }; var vm = new CostCashFlowViewModel(query, lookup); vm.HasEmptySnapshot.Should().BeFalse(); await vm.ReloadProjectsAsync(); await vm.SelectProjectAsync(A); vm.HasEmptySnapshot.Should().BeTrue();
    }

    private static CostCashFlowSnapshot Snapshot(BudgetProjectInfo p, decimal actual, IReadOnlyList<CostCashFlowMonth>? months = null) { var metric = new CostCashFlowMetric(actual, 0, actual); return new(p.Id, p.Name, p.BaseCurrency, months ?? [new(new(2026, 1, 1), metric, new(0, 0, 0), metric)], metric, new(0, 0, 0), metric); }
    private sealed class Query : ICostCashFlowQueryService { public Dictionary<Guid, CostCashFlowSnapshot?> Values { get; } = []; public bool Failure { get; set; } public int Calls { get; private set; } public TaskCompletionSource<CostCashFlowSnapshot?>? Gate { get; set; } public Task<CostCashFlowSnapshot?> GetSnapshotAsync(Guid id, CancellationToken ct) { Calls++; if (Failure) throw new CostCashFlowReadException(new Exception()); return Gate?.Task ?? Task.FromResult(Values.GetValueOrDefault(id)); } }
    private sealed class Lookup : IBudgetingProjectLookup { public IReadOnlyList<BudgetProjectInfo> Items { get; set; } = []; public bool Failure { get; set; } public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id)); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Failure ? Task.FromException<IReadOnlyList<BudgetProjectInfo>>(new BudgetingProjectLookupException("technical", new Exception())) : Task.FromResult(Items); }
}
