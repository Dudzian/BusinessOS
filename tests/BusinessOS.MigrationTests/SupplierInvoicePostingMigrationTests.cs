using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Domain;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace BusinessOS.MigrationTests;

public sealed class SupplierInvoicePostingMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "posting-migration-" + Guid.NewGuid().ToString("N")); private string Db => Path.Combine(root, "businessos.db");
    public SupplierInvoicePostingMigrationTests() => Directory.CreateDirectory(root);
    [Fact]
    public async Task Fresh_latest_has_columns_filtered_unique_index_foreign_key_and_check()
    {
        await using var db = Context(); await db.Database.MigrateAsync();
        Assert.Equal(["posted_actual_cost_id", "posted_at_utc"], await Strings("SELECT name FROM pragma_table_info('supplier_invoices') WHERE name LIKE 'posted_%' ORDER BY name"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM pragma_index_list('supplier_invoices') WHERE name='IX_supplier_invoices_posted_actual_cost_id' AND [unique]=1 AND partial=1"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM pragma_foreign_key_list('supplier_invoices') WHERE [table]='actual_costs' AND [from]='posted_actual_cost_id' AND [to]='id' AND on_delete='RESTRICT'"));
        var sql = (await Strings("SELECT sql FROM sqlite_master WHERE type='table' AND name='supplier_invoices'")).Single(); Assert.Contains("CK_supplier_invoices_posting_pair", sql); Assert.Contains("posted_actual_cost_id IS NULL AND posted_at_utc IS NULL", sql);
    }
    [Fact]
    public async Task Upgrade_preserves_all_3b7_data_as_unposted_and_history_is_five()
    {
        await using var db = Context(); var migrator = db.Database.GetService<IMigrator>(); await migrator.MigrateAsync("20260811173437_AddSupplierInvoices");
        var project = BusinessProjectId.New(); var budget = Budget.Create(project, "Plan", DateTimeOffset.UtcNow); var version = BudgetVersion.Create(budget.Id, 1, DateTimeOffset.UtcNow, null); var line = BudgetLine.Create(version.Id, BudgetLineKind.Capex, "Line", new(10, new("PLN")), 0, null); var actual = ActualCost.Create(project, ActualCostKind.Capex, "Actual", new(2, new("PLN")), new(2026, 1, 1), null, DateTimeOffset.UtcNow); var forecast = ForecastCost.Create(project, ForecastCostKind.Opex, "Forecast", new(3, new("PLN")), new(2026, 2, 1), null, DateTimeOffset.UtcNow); var invoice = SupplierInvoice.Create(project, "Acme", "INV", new(4, new("PLN")), new(2026, 1, 2), new(2026, 1, 3), null, DateTimeOffset.UtcNow); db.AddRange(budget, version, line, actual, forecast); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); await Execute($"INSERT INTO supplier_invoices(id,business_project_id,supplier_name,supplier_key,invoice_number,invoice_number_key,amount,currency,invoice_date,due_date,note,created_at_utc,updated_at_utc,version,archived_at_utc) VALUES('{invoice.Id.Value}','{project.Value}','Acme','ACME','INV','INV','4','PLN','2026-01-02','2026-01-03',NULL,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z',1,NULL)");
        await migrator.MigrateAsync("20260816175745_AddSupplierInvoicePosting"); var loaded = await db.SupplierInvoices.AsNoTracking().SingleAsync(); Assert.False(loaded.IsPosted); Assert.Null(loaded.PostedActualCostId); Assert.Null(loaded.PostedAtUtc); foreach (var table in new[] { "budgets", "budget_versions", "budget_lines", "actual_costs", "forecast_costs", "supplier_invoices" }) Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM {table}")); Assert.Equal(5, await Scalar("SELECT COUNT(*) FROM __EFMigrationsHistory_Budgeting"));
    }
    [Fact]
    public async Task Database_rejects_half_pairs_duplicate_links_and_missing_foreign_keys()
    {
        await using var db = Context(); await db.Database.MigrateAsync(); var p = BusinessProjectId.New(); var cost = ActualCost.Create(p, ActualCostKind.Capex, "A", new(1, new("PLN")), new(2026, 1, 1), null, DateTimeOffset.UtcNow); var a = SupplierInvoice.Create(p, "A", "1", new(1, new("PLN")), new(2026, 1, 1), new(2026, 1, 1), null, DateTimeOffset.UtcNow); var b = SupplierInvoice.Create(p, "B", "2", new(1, new("PLN")), new(2026, 1, 1), new(2026, 1, 1), null, DateTimeOffset.UtcNow); db.AddRange(cost, a, b); await db.SaveChangesAsync();
        await AssertRejected($"UPDATE supplier_invoices SET posted_actual_cost_id=(SELECT id FROM actual_costs LIMIT 1) WHERE invoice_number='1'"); await AssertRejected($"UPDATE supplier_invoices SET posted_at_utc='2026-01-01T00:00:00Z' WHERE invoice_number='1'"); await Execute($"UPDATE supplier_invoices SET posted_actual_cost_id=(SELECT id FROM actual_costs LIMIT 1), posted_at_utc='2026-01-01T00:00:00Z' WHERE invoice_number='1'"); await AssertRejected($"UPDATE supplier_invoices SET posted_actual_cost_id=(SELECT id FROM actual_costs LIMIT 1), posted_at_utc='2026-01-01T00:00:00Z' WHERE invoice_number='2'"); await AssertRejected($"UPDATE supplier_invoices SET posted_actual_cost_id='{Guid.NewGuid()}', posted_at_utc='2026-01-01T00:00:00Z' WHERE invoice_number='2'");
    }
    private BudgetingDbContext Context() => new(new DbContextOptionsBuilder<BudgetingDbContext>().UseSqlite($"Data Source={Db};Pooling=False", x => x.MigrationsHistoryTable("__EFMigrationsHistory_Budgeting")).Options);
    private async Task Execute(string sql) { await using var c = new SqliteConnection($"Data Source={Db};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "PRAGMA foreign_keys=ON;" + sql; await q.ExecuteNonQueryAsync(); }
    private async Task AssertRejected(string sql) => await Assert.ThrowsAsync<SqliteException>(() => Execute(sql));
    private async Task<long> Scalar(string sql) { await using var c = new SqliteConnection($"Data Source={Db};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(await q.ExecuteScalarAsync()); }
    private async Task<List<string>> Strings(string sql) { var x = new List<string>(); await using var c = new SqliteConnection($"Data Source={Db};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = sql; await using var r = await q.ExecuteReaderAsync(); while (await r.ReadAsync()) x.Add(r.GetString(0)); return x; }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
