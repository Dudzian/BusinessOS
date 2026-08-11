using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class CostCashFlowPersistenceTests
{
    [Fact]
    public async Task Real_sqlite_store_returns_sparse_empty_snapshot_for_project_without_costs()
    {
        await using var fixture = await Fixture.Create();
        var source = await fixture.Provider.GetRequiredService<ICostCashFlowReadStore>().GetSnapshotSourceAsync(fixture.Project.Id, default);
        Assert.Empty(source.Actuals); Assert.Empty(source.Forecasts);
        var snapshot = await fixture.Provider.GetRequiredService<ICostCashFlowQueryService>().GetSnapshotAsync(fixture.Project.Id, default);
        Assert.NotNull(snapshot); Assert.Empty(snapshot.Months);
    }

    [Fact]
    public async Task Real_sqlite_snapshot_proves_months_scoping_archives_and_totals()
    {
        await using var f = await Fixture.Create(); await f.Seed(); var s = await f.Query.GetSnapshotAsync(f.Project.Id, default);
        Assert.Equal([new(2026, 7, 1), new(2026, 8, 1), new(2026, 9, 1), new(2026, 10, 1)], s!.Months.Select(x => x.Month));
        Assert.Equal(new CostCashFlowMetric(0, 20, 20), s.Months[0].Opex); Assert.Equal(new(100, 50, 150), s.Months[1].Total);
        Assert.Equal(new(40, 0, 40), s.Months[2].Opex); Assert.Equal(new(0, 75, 75), s.Months[3].Capex);
        Assert.Equal(new(100, 75, 175), s.Capex); Assert.Equal(new(40, 70, 110), s.Opex); Assert.Equal(new(140, 145, 285), s.Total);
    }

    [Theory]
    [InlineData("actual_costs")]
    [InlineData("forecast_costs")]
    public async Task Real_sqlite_currency_corruption_is_safe(string table)
    {
        await using var f = await Fixture.Create(); await f.Seed(); await using var db = await f.Factory.CreateDbContextAsync();
        if (table == "actual_costs") await db.Database.ExecuteSqlRawAsync("UPDATE actual_costs SET currency = 'EUR' WHERE business_project_id = {0} AND archived_at_utc IS NULL", f.Project.Id); else await db.Database.ExecuteSqlRawAsync("UPDATE forecast_costs SET currency = 'EUR' WHERE business_project_id = {0} AND archived_at_utc IS NULL", f.Project.Id);
        var error = await Assert.ThrowsAsync<CostCashFlowReadException>(() => f.Query.GetSnapshotAsync(f.Project.Id, default));
        foreach (var detail in new[] { "EUR", "SQLite", "SQL", "provider" }) Assert.DoesNotContain(detail, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture(ServiceProvider provider, string directory, BudgetProjectInfo project) : IAsyncDisposable
    {
        public ServiceProvider Provider => provider;
        public BudgetProjectInfo Project => project;
        public ICostCashFlowQueryService Query => provider.GetRequiredService<ICostCashFlowQueryService>();
        public IDbContextFactory<BudgetingDbContext> Factory => provider.GetRequiredService<IDbContextFactory<BudgetingDbContext>>();
        public static async Task<Fixture> Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "businessos-cash-flow-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(directory);
            var project = new BudgetProjectInfo(Guid.NewGuid(), "Project", "PLN", true); var services = new ServiceCollection();
            var other = new BudgetProjectInfo(Guid.NewGuid(), "Other", "PLN", true);
            services.AddSingleton<IBudgetingProjectLookup>(new Lookup([project, other])); services.AddBudgetingModule(); services.AddBudgetingPersistence(Path.Combine(directory, "businessos.db"));
            var provider = services.BuildServiceProvider(); await provider.GetRequiredService<IBudgetingDatabaseLifecycle>().InitializeAsync(default); return new(provider, directory, project);
        }
        public async Task Seed()
        {
            var p1 = new BusinessProjectId(project.Id); var p2 = BusinessProjectId.New(); var now = DateTimeOffset.UtcNow;
            ActualCost A(BusinessProjectId p, ActualCostKind k, decimal amount, DateOnly date, bool archived = false) { var x = ActualCost.Create(p, k, "Actual", new(amount, new("PLN")), date, null, now); if (archived) x.Archive(now); return x; }
            ForecastCost F(BusinessProjectId p, ForecastCostKind k, decimal amount, DateOnly date, bool archived = false) { var x = ForecastCost.Create(p, k, "Forecast", new(amount, new("PLN")), date, null, now); if (archived) x.Archive(now); return x; }
            await using var db = await Factory.CreateDbContextAsync(); db.ActualCosts.AddRange(A(p1, ActualCostKind.Capex, 100, new(2026, 8, 15)), A(p1, ActualCostKind.Opex, 40, new(2026, 9, 1)), A(p1, ActualCostKind.Capex, 999, new(2026, 8, 20), true), A(p2, ActualCostKind.Capex, 888, new(2026, 8, 1)));
            db.ForecastCosts.AddRange(F(p1, ForecastCostKind.Opex, 20, new(2026, 7, 5)), F(p1, ForecastCostKind.Opex, 50, new(2026, 8, 20)), F(p1, ForecastCostKind.Capex, 75, new(2026, 10, 10)), F(p1, ForecastCostKind.Capex, 777, new(2026, 8, 25), true), F(p2, ForecastCostKind.Capex, 999, new(2026, 8, 1))); await db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); System.IO.Directory.Delete(directory, true); }
    }
    private sealed record Lookup(IReadOnlyList<BudgetProjectInfo> Projects) : IBudgetingProjectLookup
    {
        public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Projects.SingleOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult(Projects);
    }
}
