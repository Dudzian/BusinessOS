using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class BudgetForecastPersistenceTests
{
    [Fact]
    public async Task Archived_budget_and_both_immutable_versions_remain_visible()
    {
        await using var f = await Fixture.Create(); var seed = await f.SeedPlan();
        var budgets = await f.Store.ListBudgetsAsync(seed.Project1.Value, default); var versions = await f.Store.ListVersionsAsync(seed.Budget1.Id.Value, default);
        Assert.Contains(budgets, x => x.Id == seed.Budget1.Id.Value && x.Status == BudgetStatus.Archived); Assert.Equal([1, 2], versions.Select(x => x.Number));
    }

    [Fact]
    public async Task Snapshot_counts_active_actuals_only_and_is_project_scoped()
    {
        await using var f = await Fixture.Create(); var s = await f.SeedPlan();
        await f.AddCost(s.Project1, ActualCostKind.Capex, 150); await f.AddCost(s.Project1, ActualCostKind.Opex, 40); await f.AddCost(s.Project1, ActualCostKind.Opex, 25, archived: true); await f.AddCost(s.Project2, ActualCostKind.Capex, 999); await f.AddForecast(s.Project1, ForecastCostKind.Capex, 75, new(2027, 1, 1)); await f.AddForecast(s.Project1, ForecastCostKind.Opex, 20, new(2020, 1, 1)); await f.AddForecast(s.Project1, ForecastCostKind.Capex, 300, new(2027, 1, 1), archived: true); await f.AddForecast(s.Project2, ForecastCostKind.Capex, 888, new(2027, 1, 1));
        var snapshot = await f.Query.GetSnapshotAsync(s.Project1.Value, s.Budget1.Id.Value, s.Version1.Id.Value, default);
        Assert.Equal(new BudgetForecastMetric(100, 150, 75, 225, -125, 225, BudgetForecastState.OverBudget), snapshot!.Capex); Assert.Equal(new BudgetForecastMetric(0, 40, 20, 60, -60, null, BudgetForecastState.UnplannedSpend), snapshot.Opex); Assert.Equal(new BudgetForecastMetric(100, 190, 95, 285, -185, 285, BudgetForecastState.OverBudget), snapshot.Total);
    }

    [Fact]
    public async Task Versions_are_isolated_and_revenue_and_financing_are_excluded()
    {
        await using var f = await Fixture.Create(); var s = await f.SeedPlan(includeFinancing: true); await f.AddCost(s.Project1, ActualCostKind.Capex, 150); await f.AddForecast(s.Project1, ForecastCostKind.Capex, 75, new(2027, 1, 1));
        var v1 = await f.Query.GetSnapshotAsync(s.Project1.Value, s.Budget1.Id.Value, s.Version1.Id.Value, default); var v2 = await f.Query.GetSnapshotAsync(s.Project1.Value, s.Budget1.Id.Value, s.Version2.Id.Value, default);
        Assert.Equal(100, v1!.Capex.Planned); Assert.Equal(100, v1.Total.Planned); Assert.Equal(new BudgetForecastMetric(150, 150, 75, 225, -75, 150, BudgetForecastState.OverBudget), v2!.Capex); Assert.Equal(new BudgetForecastMetric(150, 150, 75, 225, -75, 150, BudgetForecastState.OverBudget), v2.Total);
    }

    [Fact]
    public async Task Budget_project_mismatch_returns_null()
    {
        await using var f = await Fixture.Create(); var s = await f.SeedPlan(); Assert.Null(await f.Store.GetSnapshotSourceAsync(s.Project2.Value, s.Budget1.Id.Value, s.Version1.Id.Value, default));
    }

    [Fact]
    public async Task Version_budget_mismatch_returns_null()
    {
        await using var f = await Fixture.Create(); var s = await f.SeedPlan(); Assert.Null(await f.Store.GetSnapshotSourceAsync(s.Project1.Value, s.Budget1.Id.Value, s.OtherVersion.Id.Value, default));
    }

    [Theory]
    [InlineData("budget_lines")]
    [InlineData("actual_costs")]
    [InlineData("forecast_costs")]
    public async Task Corrupted_currency_from_each_source_is_safe(string table)
    {
        await using var f = await Fixture.Create(); var s = await f.SeedPlan(); await f.AddCost(s.Project1, ActualCostKind.Capex, 1); await f.AddForecast(s.Project1, ForecastCostKind.Capex, 1, new(2020, 1, 1)); await using var db = await f.Factory.CreateDbContextAsync();
        if (table == "budget_lines") await db.Database.ExecuteSqlRawAsync("UPDATE budget_lines SET currency = 'EUR' WHERE budget_version_id = {0}", s.Version1.Id.Value); else if (table == "actual_costs") await db.Database.ExecuteSqlRawAsync("UPDATE actual_costs SET currency = 'EUR' WHERE business_project_id = {0}", s.Project1.Value); else await db.Database.ExecuteSqlRawAsync("UPDATE forecast_costs SET currency = 'EUR' WHERE business_project_id = {0}", s.Project1.Value);
        var error = await Assert.ThrowsAsync<BudgetForecastReadException>(() => f.Query.GetSnapshotAsync(s.Project1.Value, s.Budget1.Id.Value, s.Version1.Id.Value, default)); Assert.DoesNotContain("EUR", error.Message); Assert.DoesNotContain("SQLite", error.Message);
    }

    private sealed class Fixture(ServiceProvider provider, string directory, ProjectLookup projects) : IAsyncDisposable
    {
        public IBudgetForecastReadStore Store => provider.GetRequiredService<IBudgetForecastReadStore>(); public IBudgetForecastQueryService Query => provider.GetRequiredService<IBudgetForecastQueryService>(); public IDbContextFactory<BudgetingDbContext> Factory => provider.GetRequiredService<IDbContextFactory<BudgetingDbContext>>();
        public static async Task<Fixture> Create() { var directory = Path.Combine(Path.GetTempPath(), "businessos-forecast-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var projects = new ProjectLookup(); var services = new ServiceCollection(); services.AddSingleton<IBudgetingProjectLookup>(projects); services.AddBudgetingModule(); services.AddBudgetingPersistence(Path.Combine(directory, "businessos.db")); var provider = services.BuildServiceProvider(); await provider.GetRequiredService<IBudgetingDatabaseLifecycle>().InitializeAsync(default); return new(provider, directory, projects); }
        public async Task<Seed> SeedPlan(bool includeFinancing = false)
        {
            var p1 = BusinessProjectId.New(); var p2 = BusinessProjectId.New(); projects.Items[p1.Value] = new(p1.Value, "P1", "PLN", true); projects.Items[p2.Value] = new(p2.Value, "P2", "PLN", true);
            var b1 = Budget.Create(p1, "Plan", DateTimeOffset.UtcNow); var b2 = Budget.Create(p2, "Other", DateTimeOffset.UtcNow); var v1 = BudgetVersion.Create(b1.Id, 1, DateTimeOffset.UtcNow, null); var v2 = BudgetVersion.Create(b1.Id, 2, DateTimeOffset.UtcNow, null); var ov = BudgetVersion.Create(b2.Id, 1, DateTimeOffset.UtcNow, null);
            await using var db = await Factory.CreateDbContextAsync(); db.Budgets.AddRange(b1, b2); db.BudgetVersions.AddRange(v1, v2, ov); db.BudgetLines.AddRange(BudgetLine.Create(v1.Id, BudgetLineKind.Capex, "Capex", new(100, new("PLN")), 0, null), BudgetLine.Create(v1.Id, BudgetLineKind.Revenue, "Revenue", new(250, new("PLN")), 1, null), BudgetLine.Create(v2.Id, BudgetLineKind.Capex, "Capex", new(150, new("PLN")), 0, null), BudgetLine.Create(v2.Id, BudgetLineKind.Revenue, "Revenue", new(250, new("PLN")), 1, null)); if (includeFinancing) db.BudgetLines.Add(BudgetLine.Create(v1.Id, BudgetLineKind.Financing, "Financing", new(900, new("PLN")), 2, null)); await db.SaveChangesAsync();
            b1.Archive(DateTimeOffset.UtcNow); await db.SaveChangesAsync(); return new(p1, p2, b1, v1, v2, ov);
        }
        public async Task AddCost(BusinessProjectId project, ActualCostKind kind, decimal amount, bool archived = false) { var cost = ActualCost.Create(project, kind, "Cost", new(amount, new("PLN")), new(2026, 1, 1), null, DateTimeOffset.UtcNow); if (archived) cost.Archive(DateTimeOffset.UtcNow); await using var db = await Factory.CreateDbContextAsync(); db.ActualCosts.Add(cost); await db.SaveChangesAsync(); }
        public async Task AddForecast(BusinessProjectId project, ForecastCostKind kind, decimal amount, DateOnly expected, bool archived = false) { var cost = ForecastCost.Create(project, kind, "Forecast", new(amount, new("PLN")), expected, null, DateTimeOffset.UtcNow); if (archived) cost.Archive(DateTimeOffset.UtcNow); await using var db = await Factory.CreateDbContextAsync(); db.ForecastCosts.Add(cost); await db.SaveChangesAsync(); }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); Directory.Delete(directory, true); }
    }
    private sealed record Seed(BusinessProjectId Project1, BusinessProjectId Project2, Budget Budget1, BudgetVersion Version1, BudgetVersion Version2, BudgetVersion OtherVersion);
    private sealed class ProjectLookup : IBudgetingProjectLookup { public Dictionary<Guid, BudgetProjectInfo> Items { get; } = []; public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(id)); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)Items.Values.ToArray()); }
}
