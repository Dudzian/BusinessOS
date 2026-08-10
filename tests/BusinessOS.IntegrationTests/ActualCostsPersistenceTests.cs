using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class ActualCostsPersistenceTests
{
    [Fact]
    public async Task Real_sqlite_round_trips_scopes_all_fields_allows_same_names_and_filters_archive()
    {
        await using var f = await Fixture.Create(); var p = BusinessProjectId.New(); var other = BusinessProjectId.New();
        var capex = Cost(p, ActualCostKind.Capex, " Same ", 100, new(2026, 2, 2), " note ", new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero));
        var opex = Cost(p, ActualCostKind.Opex, "Same", 40, new(2026, 1, 2), null, new(2026, 2, 1, 11, 0, 0, TimeSpan.Zero));
        var foreign = Cost(other, ActualCostKind.Opex, "Foreign", 1, new(2026, 3, 1), null, DateTimeOffset.UtcNow);
        foreach (var cost in new[] { capex, opex, foreign }) { await f.Store.AddAsync(cost, default); Assert.Equal(ActualCostOperationStatus.Success, await f.Store.SaveAsync(default)); }
        var rows = await f.Store.ListAsync(p, default);
        Assert.Equal([capex.Id, opex.Id], rows.Select(x => x.Id)); Assert.Equal(ActualCostKind.Capex, rows[0].Kind); Assert.Equal(100, rows[0].Amount.Amount); Assert.Equal("PLN", rows[0].Amount.Currency.Value); Assert.Equal(new DateOnly(2026, 2, 2), rows[0].IncurredOn); Assert.Equal("note", rows[0].Note);
        var tracked = await f.Store.GetAsync(opex.Id, true, default); tracked!.Archive(DateTimeOffset.UtcNow); await f.Store.SaveAsync(default); Assert.Single(await f.Store.ListAsync(p, default)); Assert.NotNull((await f.Store.GetAsync(opex.Id, false, default))!.ArchivedAtUtc);
    }
    [Fact]
    public async Task Ordering_is_incurred_then_updated_then_id_after_materialization()
    {
        await using var f = await Fixture.Create(); var p = BusinessProjectId.New(); var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var olderDate = Cost(p, ActualCostKind.Capex, "old", 1, new(2026, 1, 1), null, now);
        var olderUpdate = Cost(p, ActualCostKind.Capex, "middle", 1, new(2026, 2, 1), null, now);
        var newerUpdate = Cost(p, ActualCostKind.Capex, "first", 1, new(2026, 2, 1), null, now.AddHours(1));
        foreach (var c in new[] { olderDate, olderUpdate, newerUpdate }) { await f.Store.AddAsync(c, default); await f.Store.SaveAsync(default); }
        Assert.Equal([newerUpdate.Id, olderUpdate.Id, olderDate.Id], (await f.Store.ListAsync(p, default)).Select(x => x.Id));
    }
    [Fact]
    public async Task Update_preserves_created_and_concurrency_conflict_has_no_partial_mutation()
    {
        await using var f = await Fixture.Create(); var p = BusinessProjectId.New(); f.Projects.Item = new(p.Value, "Gym", "PLN", true);
        var created = await f.Service.CreateAsync(p.Value, ActualCostKind.Capex, "Original", 10, "PLN", new(2026, 1, 1), null, default); var originalCreated = created.Value!.CreatedAtUtc;
        var conflict = await f.Service.UpdateAsync(created.Value.Id, 99, ActualCostKind.Opex, "Wrong", 20, "PLN", new(2026, 2, 1), null, default); Assert.Equal(ActualCostOperationStatus.ConcurrencyConflict, conflict.Status); Assert.Equal("Original", (await f.Service.GetAsync(created.Value.Id, default))!.Name);
        var updated = await f.Service.UpdateAsync(created.Value.Id, 1, ActualCostKind.Opex, "Updated", 20, "PLN", new(2026, 2, 1), "note", default); Assert.Equal(2, updated.Value!.Version); Assert.Equal(originalCreated, updated.Value.CreatedAtUtc); Assert.True(updated.Value.UpdatedAtUtc >= originalCreated);
    }
    [Fact]
    public async Task Archive_is_soft_second_archive_rejected_and_unavailable_project_does_not_mutate()
    {
        await using var f = await Fixture.Create(); var p = Guid.NewGuid(); f.Projects.Item = new(p, "Gym", "PLN", true); var made = await f.Service.CreateAsync(p, ActualCostKind.Opex, "Rent", 40, "PLN", new(2026, 1, 1), null, default);
        f.Projects.Item = f.Projects.Item with { Available = false }; Assert.Equal(ActualCostOperationStatus.ProjectUnavailable, (await f.Service.UpdateAsync(made.Value!.Id, 1, ActualCostKind.Opex, "No", 50, "PLN", new(2026, 1, 2), null, default)).Status); Assert.Equal(1, (await f.Service.GetAsync(made.Value.Id, default))!.Version); Assert.Equal(ActualCostOperationStatus.ProjectUnavailable, (await f.Service.ArchiveAsync(made.Value.Id, 1, default)).Status);
        f.Projects.Item = f.Projects.Item with { Available = true }; Assert.Equal(ActualCostOperationStatus.Success, (await f.Service.ArchiveAsync(made.Value.Id, 1, default)).Status); Assert.Empty(await f.Service.ListAsync(p, default)); Assert.Equal(ActualCostOperationStatus.Archived, (await f.Service.ArchiveAsync(made.Value.Id, 2, default)).Status);
    }
    [Fact]
    public async Task Currency_mismatch_does_not_persist()
    {
        await using var f = await Fixture.Create(); var p = Guid.NewGuid(); f.Projects.Item = new(p, "Gym", "PLN", true); Assert.Equal(ActualCostOperationStatus.ValidationFailure, (await f.Service.CreateAsync(p, ActualCostKind.Capex, "x", 1, "EUR", new(2026, 1, 1), null, default)).Status); Assert.Empty(await f.Service.ListAsync(p, default));
    }
    private static ActualCost Cost(BusinessProjectId p, ActualCostKind k, string n, decimal a, DateOnly d, string? note, DateTimeOffset now) => ActualCost.Create(p, k, n, new(a, new CurrencyCode("PLN")), d, note, now);
    private sealed class Fixture(ServiceProvider provider, string root, IActualCostsStore store, ProjectLookup projects, IActualCostsCrudService service) : IAsyncDisposable
    {
        public IActualCostsStore Store => store; public ProjectLookup Projects => projects; public IActualCostsCrudService Service => service;
        public static async Task<Fixture> Create() { var root = Path.Combine(Path.GetTempPath(), "actual-costs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var services = new ServiceCollection(); services.AddSingleton(TimeProvider.System); services.AddBudgetingPersistence(Path.Combine(root, "businessos.db")); services.AddBudgetingModule(); var projects = new ProjectLookup(); services.AddSingleton<IBudgetingProjectLookup>(projects); var provider = services.BuildServiceProvider(); await provider.GetRequiredService<IBudgetingDatabaseLifecycle>().InitializeAsync(default); return new(provider, root, provider.GetRequiredService<IActualCostsStore>(), projects, provider.GetRequiredService<IActualCostsCrudService>()); }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    public sealed class ProjectLookup : IBudgetingProjectLookup { public BudgetProjectInfo? Item; public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Item?.Id == id ? Item : null); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)(Item is null ? [] : [Item])); }
}
