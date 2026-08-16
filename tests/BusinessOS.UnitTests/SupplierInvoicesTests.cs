using System.Globalization;
using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class SupplierInvoicesTests
{
    private static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateOnly InvoiceDate = new(2026, 1, 10);
    private static readonly DateOnly DueDate = new(2026, 2, 10);

    [Fact]
    public async Task Create_trims_fields_uses_canonical_currency_and_normalizes_note()
    {
        var f = new Fixture();
        var result = await f.Service.CreateAsync(ProjectId, " Acme ", " INV-1 ", 120.5m, "pln", InvoiceDate, DueDate, " note ");
        Assert.Equal(SupplierInvoiceOperationStatus.Success, result.Status);
        Assert.Equal(("Acme", "INV-1", "PLN", "note", 1L), (result.Value!.SupplierName, result.Value.InvoiceNumber, result.Value.Currency, result.Value.Note, result.Value.Version));
        Assert.Equal("Supplier=Acme | Invoice=INV-1 | Amount=120.5 PLN | InvoiceDate=2026-01-10 | DueDate=2026-02-10", result.Value.SemanticName);
    }

    [Theory]
    [MemberData(nameof(InvalidCreateData))]
    public async Task Create_rejects_invalid_domain_input(string supplier, string number, decimal amount, DateOnly invoiceDate, DateOnly dueDate, string? note)
    {
        var result = await new Fixture().Service.CreateAsync(ProjectId, supplier, number, amount, "PLN", invoiceDate, dueDate, note);
        Assert.Equal(SupplierInvoiceOperationStatus.ValidationFailure, result.Status);
    }

    public static TheoryData<string, string, decimal, DateOnly, DateOnly, string?> InvalidCreateData => new()
    {
        { " ", "I", 1, InvoiceDate, DueDate, null }, { new('s', 257), "I", 1, InvoiceDate, DueDate, null },
        { "S", " ", 1, InvoiceDate, DueDate, null }, { "S", new('i', 101), 1, InvoiceDate, DueDate, null },
        { "S", "I", 0, InvoiceDate, DueDate, null }, { "S", "I", -1, InvoiceDate, DueDate, null },
        { "S", "I", 1, default, DueDate, null }, { "S", "I", 1, InvoiceDate, default, null },
        { "S", "I", 1, DueDate, InvoiceDate, null }, { "S", "I", 1, InvoiceDate, DueDate, new('n', 1001) }
    };

    [Fact]
    public async Task Create_handles_project_currency_duplicates_and_whitespace_note()
    {
        var f = new Fixture(); f.Lookup.Project = null;
        Assert.Equal(SupplierInvoiceOperationStatus.ProjectUnavailable, (await f.Create()).Status);
        f.Lookup.Project = Project(false); Assert.Equal(SupplierInvoiceOperationStatus.ProjectUnavailable, (await f.Create()).Status);
        f.Lookup.Project = Project(); Assert.Equal(SupplierInvoiceOperationStatus.ValidationFailure, (await f.Service.CreateAsync(ProjectId, "S", "I", 1, "EUR", InvoiceDate, DueDate, null)).Status);
        var ok = await f.Service.CreateAsync(ProjectId, " Acme ", " INV ", 1, "pln", InvoiceDate, DueDate, " "); Assert.Null(ok.Value!.Note);
        f.Store.IdentityExists = true;
        Assert.Equal(SupplierInvoiceOperationStatus.DuplicateInvoice, (await f.Service.CreateAsync(ProjectId, " acme ", " inv ", 1, "PLN", InvoiceDate, DueDate, null)).Status);
    }

    [Fact]
    public async Task Update_covers_success_not_found_archived_conflict_project_duplicate_and_version()
    {
        var f = new Fixture(); Assert.Equal(SupplierInvoiceOperationStatus.NotFound, (await f.Update()).Status);
        f.Store.Entity = Invoice(); Assert.Equal(SupplierInvoiceOperationStatus.ConcurrencyConflict, (await f.Update(version: 9)).Status);
        f.Store.Entity.Archive(DateTimeOffset.UtcNow); Assert.Equal(SupplierInvoiceOperationStatus.Archived, (await f.Update(version: 2)).Status);
        f.Store.Entity = Invoice(); f.Lookup.Project = Project(false); Assert.Equal(SupplierInvoiceOperationStatus.ProjectUnavailable, (await f.Update()).Status);
        f.Lookup.Project = Project(); f.Store.IdentityExists = true; Assert.Equal(SupplierInvoiceOperationStatus.DuplicateInvoice, (await f.Update(supplier: " acme ", number: " inv-1 ")).Status);
        f.Store.Entity = Invoice(); f.Store.IdentityExists = false; var success = await f.Update(); Assert.Equal(SupplierInvoiceOperationStatus.Success, success.Status); Assert.Equal(2, success.Value!.Version);
    }

    [Fact]
    public async Task Archive_covers_success_not_found_archived_conflict_and_project()
    {
        var f = new Fixture(); Assert.Equal(SupplierInvoiceOperationStatus.NotFound, (await f.Service.ArchiveAsync(Guid.NewGuid(), 1)).Status);
        f.Store.Entity = Invoice(); Assert.Equal(SupplierInvoiceOperationStatus.ConcurrencyConflict, (await f.Service.ArchiveAsync(f.Store.Entity.Id.Value, 9)).Status);
        f.Lookup.Project = Project(false); Assert.Equal(SupplierInvoiceOperationStatus.ProjectUnavailable, (await f.Service.ArchiveAsync(f.Store.Entity.Id.Value, 1)).Status);
        f.Lookup.Project = Project(); var success = await f.Service.ArchiveAsync(f.Store.Entity.Id.Value, 1); Assert.Equal(SupplierInvoiceOperationStatus.Success, success.Status); Assert.Equal(2, f.Store.Entity.Version);
        Assert.Equal(SupplierInvoiceOperationStatus.Archived, (await f.Service.ArchiveAsync(f.Store.Entity.Id.Value, 2)).Status);
    }

    [Fact]
    public async Task Mutations_map_persistence_failure_and_cancellation_safely()
    {
        var f = new Fixture(); f.Store.ThrowOnSave = true; var failed = await f.Create(); Assert.Equal(SupplierInvoiceOperationStatus.PersistenceFailure, failed.Status); Assert.DoesNotContain("database", failed.SafeMessage, StringComparison.OrdinalIgnoreCase);
        f = new Fixture(); f.Store.Entity = Invoice(); f.Store.ThrowOnSave = true; Assert.Equal(SupplierInvoiceOperationStatus.PersistenceFailure, (await f.Update()).Status);
        f = new Fixture(); f.Store.Entity = Invoice(); f.Store.ThrowOnSave = true; Assert.Equal(SupplierInvoiceOperationStatus.PersistenceFailure, (await f.Service.ArchiveAsync(f.Store.Entity.Id.Value, 1)).Status);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        f = new Fixture(); Assert.Equal(SupplierInvoiceOperationStatus.Cancelled, (await f.Create(cts.Token)).Status);
        f.Store.Entity = Invoice(); Assert.Equal(SupplierInvoiceOperationStatus.Cancelled, (await f.Update(ct: cts.Token)).Status); Assert.Equal(SupplierInvoiceOperationStatus.Cancelled, (await f.Service.ArchiveAsync(f.Store.Entity.Id.Value, 1, cts.Token)).Status);
    }

    [Fact]
    public async Task Reads_enforce_project_availability_scope_and_currency_integrity()
    {
        var f = new Fixture(); var invoice = Invoice(); f.Store.Rows = [invoice]; f.Store.Entity = invoice;
        Assert.Single(await f.Service.ListAsync(ProjectId)); Assert.NotNull(await f.Service.GetAsync(invoice.Id.Value));
        f.Lookup.Project = Project(false); Assert.Empty(await f.Service.ListAsync(ProjectId)); Assert.Null(await f.Service.GetAsync(invoice.Id.Value)); Assert.Equal(1, f.Store.ListCalls);
        f.Lookup.Project = Project(); var corrupt = Invoice("EUR"); f.Store.Rows = [invoice, corrupt]; f.Store.Entity = corrupt;
        await AssertSafeRead(() => f.Service.ListAsync(ProjectId)); await AssertSafeRead(() => f.Service.GetAsync(corrupt.Id.Value));
        f.Store.Rows = [SupplierInvoice.Create(BusinessProjectId.New(), "X", "X", new(1, new("PLN")), InvoiceDate, DueDate, null, DateTimeOffset.UtcNow)]; await AssertSafeRead(() => f.Service.ListAsync(ProjectId));
    }

    [Fact]
    public async Task Read_failures_are_safe_and_cancellation_propagates()
    {
        var f = new Fixture(); f.Store.ThrowOnRead = true; await AssertSafeRead(() => f.Service.ListAsync(ProjectId));
        f = new Fixture(); f.Lookup.Throw = true; await AssertSafeRead(() => f.Service.ListAsync(ProjectId));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new Fixture().Service.ListAsync(ProjectId, cts.Token));
    }

    private static async Task AssertSafeRead(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<SupplierInvoicesReadException>(action);
        foreach (var forbidden in new[] { "EUR", "SQLite", "SQL", "provider", "database", "connection" }) Assert.DoesNotContain(forbidden, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
    private static BudgetProjectInfo Project(bool available = true) => new(ProjectId, "Gym", "PLN", available);
    private static SupplierInvoice Invoice(string currency = "PLN") => SupplierInvoice.Create(new(ProjectId), "Acme", "INV-1", new(1, new(currency)), InvoiceDate, DueDate, null, DateTimeOffset.UtcNow);

    private sealed class Fixture
    {
        public Store Store { get; } = new(); public Lookup Lookup { get; } = new() { Project = Project() }; public ISupplierInvoicesCrudService Service { get; }
        public Fixture() => Service = new SupplierInvoicesCrudService(Store, Lookup, TimeProvider.System);
        public Task<SupplierInvoiceResult<SupplierInvoiceItem>> Create(CancellationToken ct = default) => Service.CreateAsync(ProjectId, "S", "I", 1, "PLN", InvoiceDate, DueDate, null, ct);
        public Task<SupplierInvoiceResult<SupplierInvoiceItem>> Update(long version = 1, string supplier = "S2", string number = "I2", CancellationToken ct = default) => Service.UpdateAsync(Store.Entity?.Id.Value ?? Guid.NewGuid(), version, supplier, number, 2, "PLN", InvoiceDate, DueDate, null, ct);
    }
    private sealed class Lookup : IBudgetingProjectLookup
    {
        public BudgetProjectInfo? Project; public bool Throw;
        public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Throw ? Task.FromException<BudgetProjectInfo?>(new BudgetingProjectLookupException("technical", new Exception())) : Task.FromResult(Project?.Id == id ? Project : null); }
        public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)[]);
    }
    private sealed class Store : ISupplierInvoicesStore
    {
        public SupplierInvoice? Entity; public IReadOnlyList<SupplierInvoice> Rows = []; public bool IdentityExists; public bool ThrowOnRead; public bool ThrowOnSave; public int ListCalls;
        public Task<IReadOnlyList<SupplierInvoice>> ListAsync(BusinessProjectId id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); ListCalls++; return ThrowOnRead ? Task.FromException<IReadOnlyList<SupplierInvoice>>(new SupplierInvoicesPersistenceException("technical", new Exception())) : Task.FromResult(Rows); }
        public Task<SupplierInvoice?> GetAsync(SupplierInvoiceId id, bool tracked, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ThrowOnRead ? Task.FromException<SupplierInvoice?>(new SupplierInvoicesPersistenceException("technical", new Exception())) : Task.FromResult(Entity?.Id == id ? Entity : null); }
        public Task<bool> IdentityExistsAsync(BusinessProjectId p, string s, string n, SupplierInvoiceId? except, CancellationToken ct) => Task.FromResult(IdentityExists);
        public Task AddAsync(SupplierInvoice invoice, CancellationToken ct) { Entity = invoice; Rows = [invoice]; return Task.CompletedTask; }
        public Task<SupplierInvoiceOperationStatus> SaveAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ThrowOnSave ? Task.FromException<SupplierInvoiceOperationStatus>(new SupplierInvoicesPersistenceException("technical", new Exception())) : Task.FromResult(SupplierInvoiceOperationStatus.Success); }
        public Task ResetTrackingAsync() => Task.CompletedTask;
    }
}
