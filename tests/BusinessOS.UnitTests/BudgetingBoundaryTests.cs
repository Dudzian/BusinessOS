using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class BudgetingBoundaryTests
{
    [Fact]
    public async Task Read_persistence_failure_is_translated()
    {
        var store = new FakeStore { FailReads = true }; var service = Create(store, new FakeProjects(true));
        await Assert.ThrowsAsync<BudgetingReadException>(() => service.ListBudgetsAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<BudgetingReadException>(() => service.GetBudgetAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<BudgetingReadException>(() => service.GetVersionAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<BudgetingReadException>(() => service.ListVersionsAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Unavailable_project_prevents_next_version_store_call()
    {
        var store = new FakeStore { Budget = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow) };
        var result = await Create(store, new FakeProjects(false)).CreateNextVersionAsync(store.Budget.Id.Value, 1, null, default);
        Assert.Equal(BudgetingOperationStatus.ProjectUnavailable, result.Status); Assert.Equal(0, store.VersionCalls);
    }

    [Fact]
    public async Task Created_version_identity_comes_from_atomic_result()
    {
        var store = new FakeStore { Budget = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow) };
        var expected = BudgetVersion.Create(store.Budget.Id, 1, DateTimeOffset.UtcNow, null); store.Created = expected;
        var result = await Create(store, new FakeProjects(true)).CreateInitialVersionAsync(store.Budget.Id.Value, 1, null, default);
        Assert.Equal(expected.Id.Value, result.Value!.Id);
    }


    [Fact] public async Task Rename_draft_available_succeeds() { var f = Fixture(); var r = await f.Service.RenameBudgetAsync(f.Store.Budget!.Id.Value, 1, "New", default); Assert.Equal(BudgetingOperationStatus.Success, r.Status); Assert.Equal(1, f.Store.SaveCalls); }
    [Fact] public async Task Rename_unavailable_stops_before_name_and_save() { var f = Fixture(false); var r = await f.Service.RenameBudgetAsync(f.Store.Budget!.Id.Value, 1, "New", default); Assert.Equal(BudgetingOperationStatus.ProjectUnavailable, r.Status); Assert.Equal(0, f.Store.NameCalls); Assert.Equal(0, f.Store.SaveCalls); }
    [Fact] public async Task Rename_active_is_validation_failure() { var f = Fixture(); f.Store.Budget!.Activate(true, DateTimeOffset.UtcNow); var r = await f.Service.RenameBudgetAsync(f.Store.Budget.Id.Value, 2, "New", default); Assert.Equal(BudgetingOperationStatus.ValidationFailure, r.Status); }
    [Fact] public async Task Rename_archived_is_archived() { var f = Fixture(); f.Store.Budget!.Archive(DateTimeOffset.UtcNow); var r = await f.Service.RenameBudgetAsync(f.Store.Budget.Id.Value, 2, "New", default); Assert.Equal(BudgetingOperationStatus.Archived, r.Status); }
    [Fact] public async Task Activate_populated_draft_succeeds() { var f = Fixture(); f.Store.History.Add(BudgetVersion.Create(f.Store.Budget!.Id, 1, DateTimeOffset.UtcNow, null)); f.Store.Lines.Add(BudgetLine.Create(f.Store.History[0].Id, BudgetLineKind.Capex, "X", new(1, BusinessOS.BuildingBlocks.Domain.Primitives.CurrencyCode.Pln), 0, null)); var r = await f.Service.ActivateBudgetAsync(f.Store.Budget.Id.Value, 1, default); Assert.Equal(BudgetingOperationStatus.Success, r.Status); }
    [Fact] public async Task Activate_unavailable_does_not_read_versions_or_save() { var f = Fixture(false); var r = await f.Service.ActivateBudgetAsync(f.Store.Budget!.Id.Value, 1, default); Assert.Equal(BudgetingOperationStatus.ProjectUnavailable, r.Status); Assert.Equal(0, f.Store.VersionReads); Assert.Equal(0, f.Store.SaveCalls); }
    [Fact] public async Task Activate_active_is_validation_failure() { var f = Fixture(); f.Store.Budget!.Activate(true, DateTimeOffset.UtcNow); var r = await f.Service.ActivateBudgetAsync(f.Store.Budget.Id.Value, 2, default); Assert.Equal(BudgetingOperationStatus.ValidationFailure, r.Status); }
    [Fact] public async Task Initial_unavailable_does_not_call_atomic_store() { var f = Fixture(false); var r = await f.Service.CreateInitialVersionAsync(f.Store.Budget!.Id.Value, 1, null, default); Assert.Equal(BudgetingOperationStatus.ProjectUnavailable, r.Status); Assert.Equal(0, f.Store.VersionCalls); }
    [Fact] public async Task Next_identity_ignores_later_history() { var f = Fixture(); var created = BudgetVersion.Create(f.Store.Budget!.Id, 2, DateTimeOffset.UtcNow, null); f.Store.Created = created; f.Store.History.Add(BudgetVersion.Create(f.Store.Budget.Id, 99, DateTimeOffset.UtcNow, null)); var r = await f.Service.CreateNextVersionAsync(f.Store.Budget.Id.Value, 1, null, default); Assert.Equal(created.Id.Value, r.Value!.Id); }
    [Fact] public async Task Archive_draft_unavailable_does_not_lookup_project() { var f = Fixture(false); var r = await f.Service.ArchiveBudgetAsync(f.Store.Budget!.Id.Value, 1, default); Assert.Equal(BudgetingOperationStatus.Success, r.Status); Assert.Equal(0, f.Projects.GetCalls); }
    [Fact] public async Task Archive_active_unavailable_succeeds_without_lookup() { var f = Fixture(false); f.Store.Budget!.Activate(true, DateTimeOffset.UtcNow); var r = await f.Service.ArchiveBudgetAsync(f.Store.Budget.Id.Value, 2, default); Assert.Equal(BudgetingOperationStatus.Success, r.Status); Assert.Equal(0, f.Projects.GetCalls); }
    [Fact] public async Task Archive_stale_is_conflict() { var f = Fixture(false); var r = await f.Service.ArchiveBudgetAsync(f.Store.Budget!.Id.Value, 9, default); Assert.Equal(BudgetingOperationStatus.ConcurrencyConflict, r.Status); }
    [Fact] public async Task Archive_archived_is_archived() { var f = Fixture(false); f.Store.Budget!.Archive(DateTimeOffset.UtcNow); var r = await f.Service.ArchiveBudgetAsync(f.Store.Budget.Id.Value, 2, default); Assert.Equal(BudgetingOperationStatus.Archived, r.Status); }
    [Fact] public async Task Lookup_failure_maps_create_to_persistence_failure() { var f = Fixture(); f.Projects.Fail = true; var r = await f.Service.CreateBudgetAsync(Guid.NewGuid(), "X", default); Assert.Equal(BudgetingOperationStatus.PersistenceFailure, r.Status); Assert.Equal(0, f.Store.SaveCalls); }
    [Fact] public async Task Lookup_failure_maps_rename_to_persistence_failure() { var f = Fixture(); f.Projects.Fail = true; Assert.Equal(BudgetingOperationStatus.PersistenceFailure, (await f.Service.RenameBudgetAsync(f.Store.Budget!.Id.Value, 1, "X", default)).Status); }
    [Fact] public async Task Lookup_failure_maps_activate_to_persistence_failure() { var f = Fixture(); f.Projects.Fail = true; Assert.Equal(BudgetingOperationStatus.PersistenceFailure, (await f.Service.ActivateBudgetAsync(f.Store.Budget!.Id.Value, 1, default)).Status); }
    [Fact] public async Task Lookup_failure_maps_initial_to_persistence_failure() { var f = Fixture(); f.Projects.Fail = true; Assert.Equal(BudgetingOperationStatus.PersistenceFailure, (await f.Service.CreateInitialVersionAsync(f.Store.Budget!.Id.Value, 1, null, default)).Status); }
    [Fact] public async Task Lookup_failure_maps_next_to_persistence_failure() { var f = Fixture(); f.Projects.Fail = true; Assert.Equal(BudgetingOperationStatus.PersistenceFailure, (await f.Service.CreateNextVersionAsync(f.Store.Budget!.Id.Value, 1, null, default)).Status); }
    [Fact] public async Task Mutation_cancellation_returns_cancelled() { var f = Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Equal(BudgetingOperationStatus.Cancelled, (await f.Service.CreateBudgetAsync(Guid.NewGuid(), "X", cts.Token)).Status); }
    [Fact] public async Task Read_cancellation_is_propagated() { var f = Fixture(); f.Store.CancelReads = true; using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ListBudgetsAsync(Guid.NewGuid(), cts.Token)); }

    private static (IBudgetingCrudService Service, FakeStore Store, FakeProjects Projects) Fixture(bool available = true) { var store = new FakeStore { Budget = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow) }; var projects = new FakeProjects(available); return (Create(store, projects), store, projects); }

    private static IBudgetingCrudService Create(IBudgetingStore store, IBudgetingProjectLookup projects)
    { var services = new ServiceCollection().AddSingleton(store).AddSingleton(projects).AddSingleton(TimeProvider.System).AddBudgetingModule().BuildServiceProvider(); return services.GetRequiredService<IBudgetingCrudService>(); }

    private sealed class FakeProjects(bool available) : IBudgetingProjectLookup
    { public int GetCalls; public bool Fail; public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) { GetCalls++; return Fail ? Task.FromException<BudgetProjectInfo?>(new BudgetingProjectLookupException("failure", new InvalidOperationException())) : Task.FromResult<BudgetProjectInfo?>(new(id, "Project", "PLN", available)); } public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BudgetProjectInfo>>([]); }
    private sealed class FakeStore : IBudgetingStore
    {
        public Budget? Budget; public BudgetVersion? Created; public bool FailReads; public bool CancelReads; public int VersionCalls; public int SaveCalls; public int NameCalls; public int VersionReads; public List<BudgetVersion> History { get; } = []; public List<BudgetLine> Lines { get; } = [];
        private Exception Failure() => new BudgetingPersistenceException("failure", new InvalidOperationException());
        public Task<IReadOnlyList<Budget>> ListBudgetsAsync(BusinessProjectId id, CancellationToken ct) => CancelReads ? Task.FromCanceled<IReadOnlyList<Budget>>(ct) : FailReads ? Task.FromException<IReadOnlyList<Budget>>(Failure()) : Task.FromResult<IReadOnlyList<Budget>>([]);
        public Task<Budget?> GetBudgetAsync(BudgetId id, bool tracked, CancellationToken ct) => FailReads ? Task.FromException<Budget?>(Failure()) : Task.FromResult(Budget);
        public Task<IReadOnlyList<BudgetVersion>> ListVersionsAsync(BudgetId id, CancellationToken ct) { VersionReads++; return FailReads ? Task.FromException<IReadOnlyList<BudgetVersion>>(Failure()) : Task.FromResult<IReadOnlyList<BudgetVersion>>(History); }
        public Task<BudgetVersion?> GetVersionAsync(BudgetVersionId id, CancellationToken ct) => FailReads ? Task.FromException<BudgetVersion?>(Failure()) : Task.FromResult<BudgetVersion?>(null);
        public Task<IReadOnlyList<BudgetLine>> ListLinesAsync(BudgetVersionId id, CancellationToken ct) => Task.FromResult<IReadOnlyList<BudgetLine>>(Lines.Where(x => x.VersionId == id).ToArray());
        public Task<BudgetVersionCreationResult> CreateInitialVersionAsync(BudgetId id, long version, string? note, DateTimeOffset now, CancellationToken ct) { VersionCalls++; return Task.FromResult(new BudgetVersionCreationResult(BudgetingOperationStatus.Success, Created)); }
        public Task<BudgetVersionCreationResult> CreateNextVersionAsync(Budget budget, long version, string? note, DateTimeOffset now, CancellationToken ct) { VersionCalls++; return Task.FromResult(new BudgetVersionCreationResult(BudgetingOperationStatus.Success, Created)); }
        public Task<bool> NameExistsAsync(BusinessProjectId p, string n, BudgetId? e, CancellationToken ct) { NameCalls++; return Task.FromResult(false); }
        public Task AddBudgetAsync(Budget b, CancellationToken ct) => Task.CompletedTask; public Task<BudgetingOperationStatus> SaveAsync(CancellationToken ct) { SaveCalls++; return Task.FromResult(BudgetingOperationStatus.Success); }
        public Task AddVersionAsync(BudgetVersion v, CancellationToken ct) => Task.CompletedTask; public Task AddLineAsync(BudgetLine l, CancellationToken ct) => Task.CompletedTask; public Task<BudgetLine?> GetLineAsync(Guid id, bool tracked, CancellationToken ct) => Task.FromResult<BudgetLine?>(null); public Task RemoveLineAsync(BudgetLine l, CancellationToken ct) => Task.CompletedTask; public Task ResetTrackingAsync() => Task.CompletedTask;
    }
}
