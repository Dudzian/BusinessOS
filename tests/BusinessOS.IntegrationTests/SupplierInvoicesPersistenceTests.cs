using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class SupplierInvoicesPersistenceTests
{
    [Fact]
    public async Task Real_sqlite_scopes_orders_filters_archived_and_totals_active_project_rows()
    {
        await using var f = await Fixture.Create(); var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid(); f.Projects.Add(p1, "PLN"); f.Projects.Add(p2, "PLN");
        var equipment = await f.Create(p1, "Equipment", "INV-001", 120, new(2026, 1, 10), new(2026, 2, 10));
        var utilities = await f.Create(p1, "Utilities", "UTIL-001", 30, new(2026, 1, 15), new(2026, 1, 31));
        var old = await f.Create(p1, "Old", "OLD-001", 999, new(2025, 1, 1), new(2025, 1, 2)); await f.Service.ArchiveAsync(old.Id, old.Version);
        await f.Create(p2, "Other", "OTHER-001", 888, new(2026, 1, 1), new(2026, 1, 2));
        var rows = await f.Service.ListAsync(p1); Assert.Equal(["UTIL-001", "INV-001"], rows.Select(x => x.InvoiceNumber)); Assert.Equal(150, rows.Sum(x => x.Amount)); Assert.DoesNotContain(rows, x => x.Id == old.Id || x.ProjectId == p2); Assert.Equal(equipment.Id, rows[1].Id); Assert.Equal(utilities.Id, rows[0].Id);
    }

    [Fact]
    public async Task Duplicate_identity_remains_reserved_after_archive()
    {
        await using var f = await Fixture.Create(); var p = Guid.NewGuid(); f.Projects.Add(p, "PLN"); var first = await f.Create(p, "Acme Sp. z o.o.", "INV-123", 1, new(2026, 1, 1), new(2026, 1, 1));
        Assert.Equal(SupplierInvoiceOperationStatus.DuplicateInvoice, (await f.Service.CreateAsync(p, " acme sp. z o.o. ", " inv-123 ", 1, "pln", new(2026, 1, 1), new(2026, 1, 1), null)).Status);
        await f.Service.ArchiveAsync(first.Id, first.Version); Assert.Equal(SupplierInvoiceOperationStatus.DuplicateInvoice, (await f.Service.CreateAsync(p, "ACME SP. Z O.O.", "INV-123", 1, "PLN", new(2026, 1, 1), new(2026, 1, 1), null)).Status);
    }

    [Fact]
    public async Task Update_increments_version_and_stale_version_conflicts()
    {
        await using var f = await Fixture.Create(); var p = Guid.NewGuid(); f.Projects.Add(p, "PLN"); var made = await f.Create(p, "Equipment", "INV-001", 120, new(2026, 1, 10), new(2026, 2, 10));
        var updated = await f.Service.UpdateAsync(made.Id, 1, "Equipment", "INV-001", 135, "pln", made.InvoiceDate, made.DueDate, null); Assert.Equal(SupplierInvoiceOperationStatus.Success, updated.Status); Assert.Equal((135m, 2L), (updated.Value!.Amount, updated.Value.Version));
        Assert.Equal(SupplierInvoiceOperationStatus.ConcurrencyConflict, (await f.Service.UpdateAsync(made.Id, 1, "Equipment", "INV-001", 140, "PLN", made.InvoiceDate, made.DueDate, null)).Status);
    }

    [Fact]
    public async Task Currency_corruption_fails_list_and_get_all_or_nothing_with_safe_message()
    {
        await using var f = await Fixture.Create(); var p = Guid.NewGuid(); f.Projects.Add(p, "PLN"); await f.Create(p, "Utilities", "UTIL-001", 30, new(2026, 1, 15), new(2026, 1, 31)); var invoice = await f.Create(p, "Equipment", "INV-001", 120, new(2026, 1, 10), new(2026, 2, 10));
        await using (var connection = new SqliteConnection($"Data Source={f.Path};Pooling=False")) { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "UPDATE supplier_invoices SET currency='EUR' WHERE invoice_number='INV-001'"; Assert.Equal(1, await command.ExecuteNonQueryAsync()); }
        await AssertSafe(() => f.Service.ListAsync(p)); await AssertSafe(() => f.Service.GetAsync(invoice.Id));
    }

    private static async Task AssertSafe(Func<Task> read) { var ex = await Assert.ThrowsAsync<SupplierInvoicesReadException>(read); foreach (var value in new[] { "EUR", "SQLite", "SQL", "provider", "database", "connection" }) Assert.DoesNotContain(value, ex.Message, StringComparison.OrdinalIgnoreCase); }

    private sealed class Fixture(ServiceProvider provider, string root, ProjectLookup projects) : IAsyncDisposable
    {
        public ISupplierInvoicesCrudService Service => provider.GetRequiredService<ISupplierInvoicesCrudService>(); public ProjectLookup Projects => projects; public string Path => System.IO.Path.Combine(root, "businessos.db");
        public static async Task<Fixture> Create() { var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "supplier-invoices-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var services = new ServiceCollection(); services.AddSingleton(TimeProvider.System); services.AddBudgetingPersistence(System.IO.Path.Combine(root, "businessos.db")); services.AddBudgetingModule(); var lookup = new ProjectLookup(); services.AddSingleton<IBudgetingProjectLookup>(lookup); var provider = services.BuildServiceProvider(); await provider.GetRequiredService<IBudgetingDatabaseLifecycle>().InitializeAsync(default); return new(provider, root, lookup); }
        public async Task<SupplierInvoiceItem> Create(Guid project, string supplier, string number, decimal amount, DateOnly invoiceDate, DateOnly dueDate) { var result = await Service.CreateAsync(project, supplier, number, amount, Projects.Values[project].BaseCurrency, invoiceDate, dueDate, null); Assert.Equal(SupplierInvoiceOperationStatus.Success, result.Status); return result.Value!; }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    public sealed class ProjectLookup : IBudgetingProjectLookup
    {
        public Dictionary<Guid, BudgetProjectInfo> Values { get; } = []; public void Add(Guid id, string currency) => Values[id] = new(id, "Project", currency, true);
        public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Values.GetValueOrDefault(id)); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)Values.Values.ToArray());
    }
}
