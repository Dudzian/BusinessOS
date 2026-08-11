using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class ForecastCostsViewModelTests
{
    private readonly FakeService service = new(); private readonly FakeProjects projects = new(); private readonly ForecastCostsViewModel vm;
    private readonly BudgetProjectInfo first = new(Guid.NewGuid(), "First", "PLN", true); private readonly BudgetProjectInfo second = new(Guid.NewGuid(), "Second", "EUR", true);
    public ForecastCostsViewModelTests() { projects.Items = [first, second]; vm = new(service, projects, TimeProvider.System); }
    private static ForecastCostItem Forecast(Guid project, ForecastCostKind kind = ForecastCostKind.Capex, decimal amount = 10, Guid? id = null) => new(id ?? Guid.NewGuid(), project, kind, kind.ToString(), amount, "PLN", new(2026, 1, 1), "note", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
    private async Task Select(BudgetProjectInfo project, params ForecastCostItem[] forecasts) { service.ByProject[project.Id] = forecasts; await vm.ReloadProjectsAsync(); await vm.SelectProjectAsync(project); }

    [Fact] public async Task Reload_and_select_load_scoped_costs_currency_and_totals() { var a = Forecast(first.Id, amount: 10); var b = Forecast(first.Id, ForecastCostKind.Opex, 4); await Select(first, a, b); vm.SelectedProject.Should().Be(first); vm.ForecastCosts.Should().Equal(a, b); vm.ProjectCurrency.Should().Be("PLN"); vm.ForecastCapexTotal.Should().Be(10); vm.ForecastOpexTotal.Should().Be(4); vm.ForecastTotal.Should().Be(14); vm.LastProjectsReloadSucceeded.Should().BeTrue(); }
    [Fact] public async Task SelectProject_failure_does_not_mix_new_project_with_previous_costs() { var old = Forecast(first.Id); await Select(first, old); service.FailProject = second.Id; await vm.SelectProjectAsync(second); vm.SelectedProject.Should().Be(first); vm.ForecastCosts.Should().Equal(old); vm.ProjectCurrency.Should().Be("PLN"); vm.OperationMessage.Should().NotBeEmpty(); }
    [Fact] public async Task Reload_failure_preserves_previous_snapshot_and_resets_success() { var old = Forecast(first.Id); await Select(first, old); projects.Failure = true; await vm.ReloadProjectsAsync(); vm.LastProjectsReloadSucceeded.Should().BeFalse(); vm.SelectedProject.Should().Be(first); vm.ForecastCosts.Should().Equal(old); }
    [Fact] public async Task Edit_notifies_every_field_and_preserves_opex() { var forecast = Forecast(first.Id, ForecastCostKind.Opex); await Select(first, forecast); await vm.SelectForecastAsync(forecast); var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName); vm.BeginEditForecast(); names.Should().Contain([nameof(vm.ForecastKind), nameof(vm.ForecastName), nameof(vm.ForecastAmount), nameof(vm.ForecastExpectedOn), nameof(vm.ForecastNote)]); vm.ForecastKind.Should().Be(ForecastCostKind.Opex); vm.ForecastName.Should().Be("Opex"); }
    [Fact] public async Task Stale_selection_is_canonicalized_and_foreign_is_rejected() { var id = Guid.NewGuid(); var canonical = Forecast(first.Id, id: id); await Select(first, canonical); await vm.SelectForecastAsync(canonical with { Name = "stale" }); vm.SelectedForecastCost.Should().BeSameAs(canonical); await vm.SelectForecastAsync(Forecast(second.Id)); vm.SelectedForecastCost.Should().BeNull(); }
    [Fact] public async Task Create_edit_and_archive_use_service() { await Select(first); vm.BeginAddForecast(); vm.ForecastName = "New"; vm.ForecastAmount = "12"; await vm.SaveForecastAsync(); service.Created.Should().BeTrue(); var created = vm.ForecastCosts.Single(); await vm.SelectForecastAsync(created); vm.BeginEditForecast(); vm.ForecastAmount = "15"; await vm.SaveForecastAsync(); service.Updated.Should().BeTrue(); vm.OpenArchiveDialog(); await vm.ConfirmArchiveAsync(); service.Archived.Should().BeTrue(); vm.ForecastCosts.Should().BeEmpty(); vm.LastProjectsReloadSucceeded.Should().BeTrue(); }
    [Fact] public async Task Invalid_amount_is_safe() { await Select(first); vm.BeginAddForecast(); vm.ForecastAmount = "not-number"; await vm.SaveForecastAsync(); service.Created.Should().BeFalse(); vm.OperationMessage.Should().NotBeEmpty(); }
    [Fact] public async Task Editor_and_archive_dialog_block_navigation_and_cancel_restores_it() { var forecast = Forecast(first.Id); await Select(first, forecast); vm.BeginAddForecast(); vm.CanNavigate.Should().BeFalse(); vm.CancelEditor(); await vm.SelectForecastAsync(forecast); vm.OpenArchiveDialog(); vm.CanNavigate.Should().BeFalse(); vm.CancelArchive(); vm.CanNavigate.Should().BeTrue(); }
    [Fact] public async Task Reload_preserves_valid_project_selection() { await Select(first, Forecast(first.Id)); await vm.ReloadProjectsAsync(); vm.SelectedProject.Should().Be(first); }
    [Fact] public async Task Archive_cancel_does_not_mutate() { var forecast = Forecast(first.Id); await Select(first, forecast); await vm.SelectForecastAsync(forecast); vm.OpenArchiveDialog(); vm.CancelArchive(); service.Archived.Should().BeFalse(); vm.ForecastCosts.Should().ContainSingle(); vm.ArchivingForecastName.Should().BeNull(); vm.IsArchiveDialogOpen.Should().BeFalse(); vm.CanNavigate.Should().BeTrue(); }
    [Fact]
    public async Task Add_uses_controlled_local_today_plus_thirty_and_republishes_editor_fields()
    {
        var local = new DateTimeOffset(2026, 4, 5, 8, 0, 0, TimeSpan.FromHours(2));
        var subject = new ForecastCostsViewModel(service, projects, new FixedTimeProvider(local));
        await subject.ReloadProjectsAsync(); await subject.SelectProjectAsync(first);
        var names = new List<string?>(); subject.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        subject.BeginAddForecast();
        subject.ForecastKind.Should().Be(ForecastCostKind.Capex); subject.ForecastName.Should().BeEmpty(); subject.ForecastAmount.Should().BeEmpty(); subject.ForecastNote.Should().BeEmpty(); subject.ForecastExpectedOn.Should().Be(new DateOnly(2026, 5, 5));
        names.Should().Contain([nameof(subject.ForecastKind), nameof(subject.ForecastName), nameof(subject.ForecastAmount), nameof(subject.ForecastExpectedOn), nameof(subject.ForecastNote)]);
    }
    [Fact]
    public async Task Project_failure_republishes_complete_snapshot_and_does_not_change_reload_flag()
    {
        var old = Forecast(first.Id, amount: 7); await Select(first, old); await vm.SelectForecastAsync(old);
        var names = new List<string?>(); vm.PropertyChanged += (_, e) => names.Add(e.PropertyName); service.FailProject = second.Id;
        await vm.SelectProjectAsync(second);
        vm.LastProjectsReloadSucceeded.Should().BeTrue(); vm.SelectedProject.Should().Be(first); vm.SelectedForecastCost.Should().Be(old); vm.ForecastCosts.Should().Equal(old); vm.ForecastTotal.Should().Be(7);
        names.Should().Contain([nameof(vm.SelectedProject), nameof(vm.ProjectCurrency), nameof(vm.SelectedForecastCost), nameof(vm.ForecastCapexTotal), nameof(vm.ForecastOpexTotal), nameof(vm.ForecastTotal), nameof(vm.CanNavigate)]);
    }
    [Fact]
    public async Task Refresh_failure_is_atomic_and_success_clears_stale_message()
    {
        var old = Forecast(first.Id, amount: 9); await Select(first, old); await vm.SelectForecastAsync(old); service.FailProject = first.Id;
        await vm.RefreshAsync(); vm.ForecastCosts.Should().Equal(old); vm.SelectedForecastCost.Should().Be(old); vm.ForecastTotal.Should().Be(9); vm.LastProjectsReloadSucceeded.Should().BeTrue(); vm.OperationMessage.Should().NotBeEmpty();
        service.FailProject = null; service.ByProject[first.Id] = [old with { Amount = 11 }]; await vm.RefreshAsync(); vm.ForecastTotal.Should().Be(11); vm.OperationMessage.Should().BeEmpty();
    }
    [Fact]
    public async Task Archive_uses_identity_captured_when_opened_and_always_clears_it()
    {
        var original = Forecast(first.Id, id: Guid.NewGuid()); var other = Forecast(first.Id, id: Guid.NewGuid()); await Select(first, original, other); await vm.SelectForecastAsync(original);
        vm.OpenArchiveDialog(); vm.ArchivingForecastName.Should().Be(original.Name); await vm.SelectForecastAsync(other); await vm.ConfirmArchiveAsync();
        service.ArchivedRequest.Should().Be((original.Id, original.Version)); vm.ArchivingForecastName.Should().BeNull(); vm.IsArchiveDialogOpen.Should().BeFalse();
        vm.LastProjectsReloadSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Pending_refresh_disables_every_navigation_and_forecast_capability_deterministically()
    {
        var forecast = Forecast(first.Id); await Select(first, forecast); await vm.SelectForecastAsync(forecast);
        service.ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = vm.RefreshAsync();
        vm.IsBusy.Should().BeTrue(); vm.CanNavigate.Should().BeFalse(); vm.CanSelectProject.Should().BeFalse(); vm.CanSelectForecast.Should().BeFalse(); vm.CanRefresh.Should().BeFalse(); vm.CanAddForecast.Should().BeFalse(); vm.CanEditForecast.Should().BeFalse(); vm.CanArchiveForecast.Should().BeFalse();
        service.ListGate.SetResult([forecast]); await refresh;
        vm.IsBusy.Should().BeFalse(); vm.CanNavigate.Should().BeTrue(); vm.CanSelectProject.Should().BeTrue(); vm.CanSelectForecast.Should().BeTrue(); vm.CanRefresh.Should().BeTrue(); vm.CanAddForecast.Should().BeTrue(); vm.LastProjectsReloadSucceeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(ForecastCostOperationStatus.ConcurrencyConflict)]
    [InlineData(ForecastCostOperationStatus.PersistenceFailure)]
    [InlineData(ForecastCostOperationStatus.Cancelled)]
    [InlineData(ForecastCostOperationStatus.ProjectUnavailable)]
    [InlineData(ForecastCostOperationStatus.Archived)]
    [InlineData(ForecastCostOperationStatus.NotFound)]
    [InlineData(ForecastCostOperationStatus.ValidationFailure)]
    public async Task Every_controlled_archive_failure_closes_dialog_clears_capture_and_preserves_snapshot(ForecastCostOperationStatus status)
    {
        var forecast = Forecast(first.Id); await Select(first, forecast); await vm.SelectForecastAsync(forecast); service.ArchiveStatus = status;
        vm.OpenArchiveDialog(); await vm.ConfirmArchiveAsync();
        vm.IsArchiveDialogOpen.Should().BeFalse(); vm.ArchivingForecastName.Should().BeNull(); vm.CanNavigate.Should().BeTrue(); vm.ForecastCosts.Should().Equal(forecast); vm.SelectedForecastCost.Should().Be(forecast); vm.LastProjectsReloadSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Successful_select_and_reload_clear_stale_read_messages_without_leaking_reload_ownership()
    {
        var old = Forecast(first.Id, ForecastCostKind.Capex, 8); await Select(first, old); await vm.SelectForecastAsync(old);
        service.FailProject = second.Id; await vm.SelectProjectAsync(second); vm.OperationMessage.Should().NotBeEmpty(); vm.LastProjectsReloadSucceeded.Should().BeTrue();
        service.FailProject = null; service.ByProject[second.Id] = [Forecast(second.Id, ForecastCostKind.Opex, 3)]; await vm.SelectProjectAsync(second); vm.OperationMessage.Should().BeEmpty(); vm.LastProjectsReloadSucceeded.Should().BeTrue();
        await vm.SelectProjectAsync(first); await vm.SelectForecastAsync(old); projects.Failure = true; await vm.ReloadProjectsAsync();
        vm.OperationMessage.Should().NotBeEmpty(); vm.LastProjectsReloadSucceeded.Should().BeFalse(); vm.SelectedProject.Should().Be(first); vm.SelectedForecastCost.Should().Be(old); vm.ForecastCosts.Should().Equal(old); vm.ProjectCurrency.Should().Be("PLN"); vm.ForecastCapexTotal.Should().Be(8); vm.ForecastOpexTotal.Should().Be(0); vm.ForecastTotal.Should().Be(8);
        projects.Failure = false; await vm.ReloadProjectsAsync(); vm.OperationMessage.Should().BeEmpty(); vm.LastProjectsReloadSucceeded.Should().BeTrue();
    }

    private sealed class FakeProjects : IBudgetingProjectLookup { public IReadOnlyList<BudgetProjectInfo> Items { get; set; } = []; public bool Failure; public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id)); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Failure ? throw new BudgetingProjectLookupException("secret", new Exception()) : Task.FromResult(Items); }
    private sealed class FakeService : IForecastCostsCrudService
    {
        public Dictionary<Guid, IReadOnlyList<ForecastCostItem>> ByProject { get; } = []; public Guid? FailProject; public bool Created, Updated, Archived; public (Guid Id, long Version)? ArchivedRequest; public TaskCompletionSource<IReadOnlyList<ForecastCostItem>>? ListGate; public ForecastCostOperationStatus ArchiveStatus = ForecastCostOperationStatus.Success;
        public Task<IReadOnlyList<ForecastCostItem>> ListAsync(Guid id, CancellationToken ct) => FailProject == id ? throw new ForecastCostsReadException(new Exception("secret")) : ListGate?.Task ?? Task.FromResult(ByProject.GetValueOrDefault(id, []));
        public Task<ForecastCostItem?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(ByProject.Values.SelectMany(x => x).SingleOrDefault(x => x.Id == id));
        public Task<ForecastCostResult<ForecastCostItem>> CreateAsync(Guid p, ForecastCostKind k, string n, decimal a, string c, DateOnly d, string? note, CancellationToken ct) { Created = true; var item = Forecast(p, k, a); ByProject[p] = [.. ByProject.GetValueOrDefault(p, []), item]; return Task.FromResult(new ForecastCostResult<ForecastCostItem>(ForecastCostOperationStatus.Success, "ok", item)); }
        public Task<ForecastCostResult<ForecastCostItem>> UpdateAsync(Guid id, long v, ForecastCostKind k, string n, decimal a, string c, DateOnly d, string? note, CancellationToken ct) { Updated = true; var pair = ByProject.Single(x => x.Value.Any(y => y.Id == id)); var item = pair.Value.Single(x => x.Id == id) with { Kind = k, Name = n, Amount = a, ExpectedOn = d, Note = note, Version = v + 1 }; ByProject[pair.Key] = pair.Value.Select(x => x.Id == id ? item : x).ToArray(); return Task.FromResult(new ForecastCostResult<ForecastCostItem>(ForecastCostOperationStatus.Success, "ok", item)); }
        public Task<ForecastCostResult> ArchiveAsync(Guid id, long v, CancellationToken ct) { Archived = true; ArchivedRequest = (id, v); if (ArchiveStatus == ForecastCostOperationStatus.Success) { var pair = ByProject.Single(x => x.Value.Any(y => y.Id == id)); ByProject[pair.Key] = pair.Value.Where(x => x.Id != id).ToArray(); } return Task.FromResult(new ForecastCostResult(ArchiveStatus, "ok")); }
    }
    private sealed class FixedTimeProvider(DateTimeOffset local) : TimeProvider { public override DateTimeOffset GetUtcNow() => local.ToUniversalTime(); public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", local.Offset, "test", "test"); }
}
