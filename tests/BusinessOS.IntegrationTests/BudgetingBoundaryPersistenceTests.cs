using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class BudgetingBoundaryPersistenceTests
{
    [Fact]
    public async Task Initial_and_next_results_identify_persisted_versions_and_advance_budget()
    {
        await using var fixture = await Fixture.Create(); var store = fixture.Store; var budget = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow);
        await store.AddBudgetAsync(budget, default); Assert.Equal(BudgetingOperationStatus.Success, await store.SaveAsync(default));
        var initial = await store.CreateInitialVersionAsync(budget.Id, 1, null, DateTimeOffset.UtcNow, default);
        Assert.Equal(BudgetingOperationStatus.Success, initial.Status); Assert.NotNull(initial.Version); Assert.Equal(1, initial.Version.Number);
        var persistedInitial = await store.GetVersionAsync(initial.Version.Id, default); Assert.Equal(initial.Version.Id, persistedInitial!.Id);
        var afterInitial = await store.GetBudgetAsync(budget.Id, false, default); Assert.Equal(2, afterInitial!.Version);
        var next = await store.CreateNextVersionAsync(afterInitial, 2, null, DateTimeOffset.UtcNow, default);
        Assert.Equal(BudgetingOperationStatus.Success, next.Status); Assert.NotNull(next.Version); Assert.Equal(2, next.Version.Number);
        Assert.Equal(next.Version.Id, (await store.GetVersionAsync(next.Version.Id, default))!.Id); Assert.Equal(3, (await store.GetBudgetAsync(budget.Id, false, default))!.Version);
    }

    [Fact]
    public async Task Atomic_result_failures_have_no_version()
    {
        await using var f = await Fixture.Create();
        var missing = await f.Store.CreateInitialVersionAsync(BudgetId.New(), 1, null, DateTimeOffset.UtcNow, default); Assert.Equal(BudgetingOperationStatus.NotFound, missing.Status); Assert.Null(missing.Version);
        var budget = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow); await f.Store.AddBudgetAsync(budget, default); await f.Store.SaveAsync(default);
        var conflict = await f.Store.CreateInitialVersionAsync(budget.Id, 9, null, DateTimeOffset.UtcNow, default); Assert.Equal(BudgetingOperationStatus.ConcurrencyConflict, conflict.Status); Assert.Null(conflict.Version);
        var invalid = await f.Store.CreateNextVersionAsync(budget, 1, null, DateTimeOffset.UtcNow, default); Assert.Equal(BudgetingOperationStatus.ValidationFailure, invalid.Status); Assert.Null(invalid.Version);
    }

    [Fact]
    public async Task Cancelled_atomic_operations_leave_budget_unchanged()
    {
        await using var f = await Fixture.Create(); var budget = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow); await f.Store.AddBudgetAsync(budget, default); await f.Store.SaveAsync(default);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.CreateInitialVersionAsync(budget.Id, 1, null, DateTimeOffset.UtcNow, cts.Token));
        Assert.Empty(await f.Store.ListVersionsAsync(budget.Id, default)); Assert.Equal(1, (await f.Store.GetBudgetAsync(budget.Id, false, default))!.Version);
    }

    [Fact]
    public async Task Duplicate_active_name_is_classified_precisely()
    {
        await using var f = await Fixture.Create(); var project = BusinessProjectId.New();
        await f.Store.AddBudgetAsync(Budget.Create(project, "Plan", DateTimeOffset.UtcNow), default); await f.Store.SaveAsync(default);
        await f.Store.AddBudgetAsync(Budget.Create(project, " plan ", DateTimeOffset.UtcNow), default);
        Assert.Equal(BudgetingOperationStatus.DuplicateName, await f.Store.SaveAsync(default));
    }

    private sealed class Fixture(ServiceProvider provider, string directory, IBudgetingStore store)
        : IAsyncDisposable
    {
        public IBudgetingStore Store => store;
        public static async Task<Fixture> Create() { var directory = Path.Combine(Path.GetTempPath(), "businessos-budgeting-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var services = new ServiceCollection(); services.AddBudgetingPersistence(Path.Combine(directory, "businessos.db")); var provider = services.BuildServiceProvider(); await provider.GetRequiredService<IBudgetingDatabaseLifecycle>().InitializeAsync(default); return new(provider, directory, provider.GetRequiredService<IBudgetingStore>()); }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
