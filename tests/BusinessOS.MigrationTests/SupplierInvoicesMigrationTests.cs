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

public sealed class SupplierInvoicesMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "supplier-invoice-migration-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(root, "businessos.db");
    public SupplierInvoicesMigrationTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Fresh_database_contains_exact_supplier_invoice_columns_and_indexes()
    {
        await using var db = Context(); await db.Database.MigrateAsync();
        Assert.Equal(new[] { "amount", "archived_at_utc", "business_project_id", "created_at_utc", "currency", "due_date", "id", "invoice_date", "invoice_number", "invoice_number_key", "note", "supplier_key", "supplier_name", "updated_at_utc", "version" }, (await Strings("SELECT name FROM pragma_table_info('supplier_invoices') ORDER BY name")));
        var indexes = await Indexes(); Assert.Contains(indexes, x => x.Name == "IX_supplier_invoices_business_project_id" && !x.Unique); Assert.Contains(indexes, x => x.Name == "IX_supplier_invoices_business_project_id_due_date" && !x.Unique); Assert.Contains(indexes, x => x.Name == "IX_supplier_invoices_business_project_id_supplier_key_invoice_number_key" && x.Unique);
    }

    [Fact]
    public async Task Upgrade_from_forecast_costs_preserves_all_old_data_and_creates_empty_invoice_schema()
    {
        await using var db = Context(); var migrator = db.Database.GetService<IMigrator>(); await migrator.MigrateAsync("20260811102427_AddForecastCosts");
        var project = BusinessProjectId.New(); var budget = Budget.Create(project, "Plan", DateTimeOffset.UtcNow); var version = BudgetVersion.Create(budget.Id, 1, DateTimeOffset.UtcNow, "v1"); var line = BudgetLine.Create(version.Id, BudgetLineKind.Capex, "Line", new(10, new("PLN")), 0, null); var actual = ActualCost.Create(project, ActualCostKind.Opex, "Actual", new(5, new("PLN")), new(2026, 1, 1), null, DateTimeOffset.UtcNow); var forecast = ForecastCost.Create(project, ForecastCostKind.Capex, "Forecast", new(6, new("PLN")), new(2026, 2, 1), null, DateTimeOffset.UtcNow);
        db.AddRange(budget, version, line, actual, forecast); await db.SaveChangesAsync(); await migrator.MigrateAsync("20260811173437_AddSupplierInvoices");
        foreach (var table in new[] { "budgets", "budget_versions", "budget_lines", "actual_costs", "forecast_costs" }) Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM {table}")); Assert.Equal(0, await Scalar("SELECT COUNT(*) FROM supplier_invoices")); Assert.Equal(4, await Scalar("SELECT COUNT(*) FROM __EFMigrationsHistory_Budgeting")); Assert.Equal(3, (await Indexes()).Count);
    }

    [Fact]
    public async Task Database_unique_constraint_reserves_archived_normalized_identity()
    {
        await using var db = Context(); await db.Database.MigrateAsync(); var project = BusinessProjectId.New(); var first = SupplierInvoice.Create(project, "Acme", "INV-1", new(1, new("PLN")), new(2026, 1, 1), new(2026, 1, 1), null, DateTimeOffset.UtcNow); first.Archive(DateTimeOffset.UtcNow); db.SupplierInvoices.Add(first); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        db.SupplierInvoices.Add(SupplierInvoice.Create(project, "ACME", "INV-1", new(2, new("PLN")), new(2026, 1, 2), new(2026, 1, 2), null, DateTimeOffset.UtcNow)); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private BudgetingDbContext Context() => new(new DbContextOptionsBuilder<BudgetingDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = PathName, Pooling = false }.ToString(), x => x.MigrationsHistoryTable("__EFMigrationsHistory_Budgeting")).Options);
    private async Task<long> Scalar(string sql) { await using var c = new SqliteConnection($"Data Source={PathName};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(await q.ExecuteScalarAsync()); }
    private async Task<List<string>> Strings(string sql) { var values = new List<string>(); await using var c = new SqliteConnection($"Data Source={PathName};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = sql; await using var r = await q.ExecuteReaderAsync(); while (await r.ReadAsync()) values.Add(r.GetString(0)); return values; }
    private async Task<List<(string Name, bool Unique)>> Indexes() { var rows = new List<(string, bool)>(); await using var c = new SqliteConnection($"Data Source={PathName};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "SELECT name, [unique] FROM pragma_index_list('supplier_invoices') WHERE origin='c' ORDER BY name"; await using var r = await q.ExecuteReaderAsync(); while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetInt64(1) == 1)); return rows; }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
