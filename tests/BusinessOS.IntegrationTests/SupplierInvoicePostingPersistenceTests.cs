using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class SupplierInvoicePostingPersistenceTests
{
    [Fact]
    public async Task Real_sqlite_posts_exactly_once_and_link_survives_actual_archive_while_forecast_is_untouched()
    {
        await using var f = await Fixture.Create(); var before = await f.Forecast(); var first = await f.Posting.PostAsync(f.Invoice.Id.Value, 1, ActualCostKind.Capex); Assert.Equal(SupplierInvoicePostingStatus.Success, first.Status); var linked = first.Value!.ActualCost;
        await using (var db = f.Context()) { var invoice = await db.SupplierInvoices.AsNoTracking().SingleAsync(); Assert.Null(invoice.ArchivedAtUtc); Assert.Equal(2, invoice.Version); Assert.Equal(linked.Id, invoice.PostedActualCostId!.Value.Value); Assert.Equal(Fixture.Now, invoice.PostedAtUtc); var cost = await db.ActualCosts.AsNoTracking().SingleAsync(); Assert.Equal(ActualCostKind.Capex, cost.Kind); Assert.Equal("Faktura INV-POST-001", cost.Name); Assert.Equal(new Money(120, new("PLN")), cost.Amount); Assert.Equal(new DateOnly(2026, 1, 10), cost.IncurredOn); Assert.Equal("posting note", cost.Note); Assert.Equal(1, cost.Version); Assert.Null(cost.ArchivedAtUtc); }
        var second = await f.Posting.PostAsync(f.Invoice.Id.Value, 1, ActualCostKind.Opex); Assert.Equal(SupplierInvoicePostingStatus.AlreadyPosted, second.Status); await using (var db = f.Context()) { Assert.Equal(1, await db.ActualCosts.CountAsync()); var invoice = await db.SupplierInvoices.AsNoTracking().SingleAsync(); Assert.Equal(2, invoice.Version); Assert.Equal(linked.Id, invoice.PostedActualCostId!.Value.Value); }
        var archive = await f.Actuals.ArchiveAsync(linked.Id, 1, default); Assert.Equal(ActualCostOperationStatus.Success, archive.Status); var again = await f.Posting.PostAsync(f.Invoice.Id.Value, 2, ActualCostKind.Capex); Assert.Equal(SupplierInvoicePostingStatus.AlreadyPosted, again.Status); await using (var db = f.Context()) { var invoice = await db.SupplierInvoices.AsNoTracking().SingleAsync(); Assert.True(invoice.IsPosted); Assert.Null(invoice.ArchivedAtUtc); var forecast = await db.ForecastCosts.AsNoTracking().SingleAsync(); Assert.Equal(before.Version, forecast.Version); Assert.Equal(before.ArchivedAtUtc, forecast.ArchivedAtUtc); Assert.Equal(before.Name, forecast.Name); }
    }
    [Fact]
    public async Task Real_sqlite_concurrency_conflict_rolls_back_insert_and_posting_update()
    {
        await using var f = await Fixture.Create(); var store = f.Provider.GetRequiredService<ISupplierInvoicePostingStore>(); var tracked = await store.GetInvoiceAsync(f.Invoice.Id, default); Assert.NotNull(tracked); await using (var c = new SqliteConnection($"Data Source={f.Path};Pooling=False")) { await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "UPDATE supplier_invoices SET version=version+1 WHERE invoice_number='INV-POST-001'"; Assert.Equal(1, await q.ExecuteNonQueryAsync()); }
        var cost = ActualCost.Create(f.Invoice.ProjectId, ActualCostKind.Capex, "Faktura INV-POST-001", f.Invoice.Amount, f.Invoice.InvoiceDate, f.Invoice.Note, Fixture.Now); await store.AddActualCostAsync(cost, default); tracked!.MarkPosted(cost.Id, Fixture.Now); Assert.Equal(SupplierInvoicePostingStatus.ConcurrencyConflict, await store.SaveAsync(default)); await store.ResetTrackingAsync(); await using var db = f.Context(); Assert.Empty(await db.ActualCosts.AsNoTracking().ToArrayAsync()); var invoice = await db.SupplierInvoices.AsNoTracking().SingleAsync(); Assert.Equal(2, invoice.Version); Assert.False(invoice.IsPosted); Assert.Null(invoice.PostedAtUtc);
    }
    private sealed class Fixture(ServiceProvider provider, string root, SupplierInvoice invoice) : IAsyncDisposable
    {
        public static readonly DateTimeOffset Now = new(2026, 1, 20, 12, 0, 0, TimeSpan.Zero); public ServiceProvider Provider => provider; public string Path => System.IO.Path.Combine(root, "businessos.db"); public SupplierInvoice Invoice => invoice; public ISupplierInvoicePostingService Posting => provider.GetRequiredService<ISupplierInvoicePostingService>(); public IActualCostsCrudService Actuals => provider.GetRequiredService<IActualCostsCrudService>(); public BudgetingDbContext Context() => new(new DbContextOptionsBuilder<BudgetingDbContext>().UseSqlite($"Data Source={Path};Pooling=False", x => x.MigrationsHistoryTable("__EFMigrationsHistory_Budgeting")).Options);
        public static async Task<Fixture> Create() { var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "posting-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(new Clock()); services.AddBudgetingPersistence(System.IO.Path.Combine(root, "businessos.db")); services.AddBudgetingModule(); var lookup = new Lookup(); services.AddSingleton<IBudgetingProjectLookup>(lookup); var provider = services.BuildServiceProvider(); await provider.GetRequiredService<IBudgetingDatabaseLifecycle>().InitializeAsync(default); var project = BusinessProjectId.New(); lookup.Project = new(project.Value, "Project1", "PLN", true); var invoice = SupplierInvoice.Create(project, "Smoke Equipment Vendor", "INV-POST-001", new(120, new("PLN")), new(2026, 1, 10), new(2026, 2, 10), "posting note", DateTimeOffset.UnixEpoch); var forecast = ForecastCost.Create(project, ForecastCostKind.Opex, "Forecast", new(20, new("PLN")), new(2026, 3, 1), "untouched", DateTimeOffset.UnixEpoch); await using (var db = new Fixture(provider, root, invoice).Context()) { db.AddRange(invoice, forecast); await db.SaveChangesAsync(); } return new(provider, root, invoice); }
        public async Task<ForecastCost> Forecast() { await using var db = Context(); return await db.ForecastCosts.AsNoTracking().SingleAsync(); }
        public async ValueTask DisposeAsync() { await provider.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Fixture.Now; }
    private sealed class Lookup : IBudgetingProjectLookup { public BudgetProjectInfo? Project; public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Project); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)(Project is null ? [] : [Project])); }
}
