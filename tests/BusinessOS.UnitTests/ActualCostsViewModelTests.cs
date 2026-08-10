using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class ActualCostsViewModelTests
{
    private readonly FakeService service = new(); private readonly FakeProjects projects = new(); private readonly ActualCostsViewModel vm;
    private readonly BudgetProjectInfo first = new(Guid.NewGuid(), "First", "PLN", true); private readonly BudgetProjectInfo second = new(Guid.NewGuid(), "Second", "EUR", true);
    public ActualCostsViewModelTests() { projects.Items = [first, second]; vm = new(service, projects, TimeProvider.System); }
    private static ActualCostItem Cost(Guid project, ActualCostKind kind = ActualCostKind.Capex, decimal amount = 10, Guid? id = null) => new(id ?? Guid.NewGuid(), project, kind, kind.ToString(), amount, "PLN", new(2026, 1, 1), "note", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
    private async Task Select(BudgetProjectInfo project, params ActualCostItem[] costs) { service.ByProject[project.Id] = costs; await vm.ReloadProjectsAsync(); await vm.SelectProjectAsync(project); }

    [Fact] public async Task Reload_and_select_load_scoped_costs_currency_and_totals() { var a = Cost(first.Id, amount: 10); var b = Cost(first.Id, ActualCostKind.Opex, 4); await Select(first, a, b); vm.SelectedProject.Should().Be(first); vm.Costs.Should().Equal(a, b); vm.ProjectCurrency.Should().Be("PLN"); vm.CapexTotal.Should().Be(10); vm.OpexTotal.Should().Be(4); vm.TotalCost.Should().Be(14); vm.LastProjectsReloadSucceeded.Should().BeTrue(); }
    [Fact] public async Task SelectProject_failure_does_not_mix_new_project_with_previous_costs() { var old = Cost(first.Id); await Select(first, old); service.FailProject = second.Id; await vm.SelectProjectAsync(second); vm.SelectedProject.Should().Be(first); vm.Costs.Should().Equal(old); vm.ProjectCurrency.Should().Be("PLN"); vm.OperationMessage.Should().NotBeEmpty(); }
    [Fact] public async Task Reload_failure_preserves_previous_snapshot_and_resets_success() { var old = Cost(first.Id); await Select(first, old); projects.Failure = true; await vm.ReloadProjectsAsync(); vm.LastProjectsReloadSucceeded.Should().BeFalse(); vm.SelectedProject.Should().Be(first); vm.Costs.Should().Equal(old); }
    [Fact] public async Task Edit_notifies_every_field_and_preserves_opex() { var cost = Cost(first.Id, ActualCostKind.Opex); await Select(first, cost); await vm.SelectCostAsync(cost); var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName); vm.BeginEditCost(); names.Should().Contain([nameof(vm.CostKind), nameof(vm.CostName), nameof(vm.CostAmount), nameof(vm.CostDate), nameof(vm.CostNote)]); vm.CostKind.Should().Be(ActualCostKind.Opex); vm.CostName.Should().Be("Opex"); }
    [Fact] public async Task Stale_selection_is_canonicalized_and_foreign_is_rejected() { var id = Guid.NewGuid(); var canonical = Cost(first.Id, id: id); await Select(first, canonical); await vm.SelectCostAsync(canonical with { Name = "stale" }); vm.SelectedCost.Should().BeSameAs(canonical); await vm.SelectCostAsync(Cost(second.Id)); vm.SelectedCost.Should().BeNull(); }
    [Fact] public async Task Create_edit_and_archive_use_service() { await Select(first); vm.BeginAddCost(); vm.CostName = "New"; vm.CostAmount = "12"; await vm.SaveCostAsync(); service.Created.Should().BeTrue(); var created = vm.Costs.Single(); await vm.SelectCostAsync(created); vm.BeginEditCost(); vm.CostAmount = "15"; await vm.SaveCostAsync(); service.Updated.Should().BeTrue(); vm.OpenArchiveDialog(); await vm.ConfirmArchiveAsync(); service.Archived.Should().BeTrue(); vm.Costs.Should().BeEmpty(); }
    [Fact] public async Task Invalid_amount_is_safe() { await Select(first); vm.BeginAddCost(); vm.CostAmount = "not-number"; await vm.SaveCostAsync(); service.Created.Should().BeFalse(); vm.OperationMessage.Should().NotBeEmpty(); }
    [Fact] public async Task Editor_and_archive_dialog_block_navigation_and_cancel_restores_it() { var cost = Cost(first.Id); await Select(first, cost); vm.BeginAddCost(); vm.CanNavigate.Should().BeFalse(); vm.CancelEditor(); await vm.SelectCostAsync(cost); vm.OpenArchiveDialog(); vm.CanNavigate.Should().BeFalse(); vm.CancelArchive(); vm.CanNavigate.Should().BeTrue(); }
    [Fact] public async Task Reload_preserves_valid_project_selection() { await Select(first, Cost(first.Id)); await vm.ReloadProjectsAsync(); vm.SelectedProject.Should().Be(first); }
    [Fact] public async Task Archive_cancel_does_not_mutate() { var cost = Cost(first.Id); await Select(first, cost); await vm.SelectCostAsync(cost); vm.OpenArchiveDialog(); vm.CancelArchive(); service.Archived.Should().BeFalse(); vm.Costs.Should().ContainSingle(); }

    private sealed class FakeProjects : IBudgetingProjectLookup { public IReadOnlyList<BudgetProjectInfo> Items { get; set; } = []; public bool Failure; public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id)); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Failure ? throw new BudgetingProjectLookupException("secret", new Exception()) : Task.FromResult(Items); }
    private sealed class FakeService : IActualCostsCrudService
    {
        public Dictionary<Guid, IReadOnlyList<ActualCostItem>> ByProject { get; } = []; public Guid? FailProject; public bool Created, Updated, Archived;
        public Task<IReadOnlyList<ActualCostItem>> ListAsync(Guid id, CancellationToken ct) => FailProject == id ? throw new ActualCostsReadException(new Exception("secret")) : Task.FromResult(ByProject.GetValueOrDefault(id, []));
        public Task<ActualCostItem?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(ByProject.Values.SelectMany(x => x).SingleOrDefault(x => x.Id == id));
        public Task<ActualCostResult<ActualCostItem>> CreateAsync(Guid p, ActualCostKind k, string n, decimal a, string c, DateOnly d, string? note, CancellationToken ct) { Created = true; var item = Cost(p, k, a); ByProject[p] = [.. ByProject.GetValueOrDefault(p, []), item]; return Task.FromResult(new ActualCostResult<ActualCostItem>(ActualCostOperationStatus.Success, "ok", item)); }
        public Task<ActualCostResult<ActualCostItem>> UpdateAsync(Guid id, long v, ActualCostKind k, string n, decimal a, string c, DateOnly d, string? note, CancellationToken ct) { Updated = true; var pair = ByProject.Single(x => x.Value.Any(y => y.Id == id)); var item = pair.Value.Single(x => x.Id == id) with { Kind = k, Name = n, Amount = a, IncurredOn = d, Note = note, Version = v + 1 }; ByProject[pair.Key] = pair.Value.Select(x => x.Id == id ? item : x).ToArray(); return Task.FromResult(new ActualCostResult<ActualCostItem>(ActualCostOperationStatus.Success, "ok", item)); }
        public Task<ActualCostResult> ArchiveAsync(Guid id, long v, CancellationToken ct) { Archived = true; var pair = ByProject.Single(x => x.Value.Any(y => y.Id == id)); ByProject[pair.Key] = pair.Value.Where(x => x.Id != id).ToArray(); return Task.FromResult(new ActualCostResult(ActualCostOperationStatus.Success, "ok")); }
    }
}
