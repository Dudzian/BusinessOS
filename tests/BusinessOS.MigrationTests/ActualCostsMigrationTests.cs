using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Domain;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace BusinessOS.MigrationTests;

public sealed class ActualCostsMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "actual-cost-migration-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(root, "businessos.db");
    public ActualCostsMigrationTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Fresh_database_applies_all_budgeting_migrations_and_creates_cost_indexes()
    {
        await using var db = Context(); await db.Database.MigrateAsync();
        (await Strings("SELECT MigrationId FROM __EFMigrationsHistory_Budgeting ORDER BY MigrationId")).Should().Equal("20260808231124_InitialBudgetingPersistence", "20260810122833_AddActualCosts", "20260811102427_AddForecastCosts", "20260811173437_AddSupplierInvoices", "20260816175745_AddSupplierInvoicePosting");
        (await Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='actual_costs'")).Should().Be(1);
        var indexes = await Strings("SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='actual_costs'"); indexes.Should().Contain("IX_actual_costs_business_project_id").And.Contain("IX_actual_costs_business_project_id_incurred_on");
        (await Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='forecast_costs'")).Should().Be(1);
        var forecastIndexes = await Strings("SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='forecast_costs'"); forecastIndexes.Should().Contain("IX_forecast_costs_business_project_id").And.Contain("IX_forecast_costs_business_project_id_expected_on");
    }
    [Fact]
    public async Task Add_actual_costs_schema_and_data_upgrade_safely_to_add_forecast_costs()
    {
        await using var db = Context(); await db.Database.GetService<IMigrator>().MigrateAsync("20260810122833_AddActualCosts");
        var project = BusinessProjectId.New(); var budget = Budget.Create(project, "Plan", DateTimeOffset.UtcNow); var version = BudgetVersion.Create(budget.Id, 1, DateTimeOffset.UtcNow, "v1"); var line = BudgetLine.Create(version.Id, BudgetLineKind.Capex, "Line", new(10, new CurrencyCode("PLN")), 0, null); var actual = ActualCost.Create(project, ActualCostKind.Opex, "Actual", new(5, new CurrencyCode("PLN")), new(2026, 1, 2), null, DateTimeOffset.UtcNow);
        db.Budgets.Add(budget); db.BudgetVersions.Add(version); db.BudgetLines.Add(line); db.ActualCosts.Add(actual); await db.SaveChangesAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync("20260811102427_AddForecastCosts");
        (await Scalar("SELECT COUNT(*) FROM budgets")).Should().Be(1); (await Scalar("SELECT COUNT(*) FROM budget_versions")).Should().Be(1); (await Scalar("SELECT COUNT(*) FROM budget_lines")).Should().Be(1); (await Scalar("SELECT COUNT(*) FROM actual_costs")).Should().Be(1); (await Scalar("SELECT COUNT(*) FROM forecast_costs")).Should().Be(0);
        var indexes = await Strings("SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='forecast_costs'"); indexes.Should().Contain("IX_forecast_costs_business_project_id").And.Contain("IX_forecast_costs_business_project_id_expected_on"); (await Scalar("SELECT COUNT(*) FROM __EFMigrationsHistory_Budgeting")).Should().Be(3);
    }

    [Fact]
    public async Task Three_b_one_data_is_preserved_when_actual_costs_migration_is_applied()
    {
        await using var db = Context(); await db.Database.GetService<IMigrator>().MigrateAsync("20260808231124_InitialBudgetingPersistence");
        var budget = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow); var version = BudgetVersion.Create(budget.Id, 1, DateTimeOffset.UtcNow, "v1"); var line = BudgetLine.Create(version.Id, BudgetLineKind.Capex, "Line", new(10, new CurrencyCode("PLN")), 0, null);
        db.Budgets.Add(budget); db.BudgetVersions.Add(version); db.BudgetLines.Add(line); await db.SaveChangesAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync("20260810122833_AddActualCosts");
        (await Scalar("SELECT COUNT(*) FROM budgets")).Should().Be(1); (await Scalar("SELECT COUNT(*) FROM budget_versions")).Should().Be(1); (await Scalar("SELECT COUNT(*) FROM budget_lines")).Should().Be(1); (await Scalar("SELECT COUNT(*) FROM actual_costs")).Should().Be(0); (await Scalar("SELECT COUNT(*) FROM __EFMigrationsHistory_Budgeting")).Should().Be(2);
    }
    private BudgetingDbContext Context() => new(new DbContextOptionsBuilder<BudgetingDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = PathName, Pooling = false }.ToString(), x => x.MigrationsHistoryTable("__EFMigrationsHistory_Budgeting")).Options);
    private async Task<long> Scalar(string sql) { await using var c = new SqliteConnection($"Data Source={PathName};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(await q.ExecuteScalarAsync()); }
    private async Task<List<string>> Strings(string sql) { var result = new List<string>(); await using var c = new SqliteConnection($"Data Source={PathName};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = sql; await using var r = await q.ExecuteReaderAsync(); while (await r.ReadAsync()) result.Add(r.GetString(0)); return result; }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
