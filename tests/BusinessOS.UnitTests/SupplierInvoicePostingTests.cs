using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class SupplierInvoicePostingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 20, 12, 30, 0, TimeSpan.Zero);
    private static SupplierInvoice Invoice() => SupplierInvoice.Create(new(Guid.Parse("10000000-0000-0000-0000-000000000001")), "Acme", "INV-001", new(120, new("PLN")), new(2026, 1, 10), new(2026, 2, 10), "note", DateTimeOffset.UnixEpoch);
    private static Fixture Make() => new(Invoice());
    private static Fixture Make(SupplierInvoice? invoice) => new(invoice);

    [Fact]
    public async Task Success_maps_invoice_atomically_with_one_timestamp()
    {
        var f = Make(); var result = await f.Service.PostAsync(f.Invoice!.Id.Value, 1, ActualCostKind.Capex);
        Assert.Equal(SupplierInvoicePostingStatus.Success, result.Status); var receipt = Assert.IsType<SupplierInvoicePostingReceipt>(result.Value); var cost = Assert.Single(f.Store.Added);
        Assert.Equal(2, f.Invoice.Version); Assert.True(f.Invoice.IsPosted); Assert.Equal(cost.Id, f.Invoice.PostedActualCostId); Assert.Equal(Now, f.Invoice.PostedAtUtc); Assert.Equal(Now, f.Invoice.UpdatedAtUtc);
        Assert.Equal(f.Invoice.ProjectId.Value, receipt.ActualCost.ProjectId); Assert.Equal(ActualCostKind.Capex, cost.Kind); Assert.Equal("Faktura INV-001", cost.Name); Assert.Equal(f.Invoice.Amount, cost.Amount); Assert.Equal(f.Invoice.InvoiceDate, cost.IncurredOn); Assert.Equal(f.Invoice.Note, cost.Note); Assert.Equal(1, cost.Version); Assert.Null(cost.ArchivedAtUtc); Assert.Equal(Now, cost.CreatedAtUtc); Assert.Equal(1, f.Store.SaveCalls);
    }

    [Fact] public async Task Invalid_kind_is_validation_failure_before_store() { var f = Make(); var r = await f.Service.PostAsync(Guid.NewGuid(), 1, (ActualCostKind)99); Assert.Equal(SupplierInvoicePostingStatus.ValidationFailure, r.Status); Assert.Equal(0, f.Store.GetCalls); }
    [Fact] public async Task Missing_is_not_found() { var f = Make(null); Assert.Equal(SupplierInvoicePostingStatus.NotFound, (await f.Service.PostAsync(Guid.NewGuid(), 1, ActualCostKind.Capex)).Status); }
    [Fact] public async Task Archived_is_rejected() { var i = Invoice(); i.Archive(Now); var f = Make(i); Assert.Equal(SupplierInvoicePostingStatus.Archived, (await f.Service.PostAsync(i.Id.Value, i.Version, ActualCostKind.Capex)).Status); }
    [Fact] public async Task Already_posted_precedes_stale_version_and_does_not_add_second_cost() { var i = Invoice(); i.MarkPosted(ActualCostId.New(), Now); var f = Make(i); Assert.Equal(SupplierInvoicePostingStatus.AlreadyPosted, (await f.Service.PostAsync(i.Id.Value, 1, ActualCostKind.Capex)).Status); Assert.Empty(f.Store.Added); Assert.Equal(0, f.Store.SaveCalls); }
    [Fact] public async Task Stale_version_conflicts() { var f = Make(); Assert.Equal(SupplierInvoicePostingStatus.ConcurrencyConflict, (await f.Service.PostAsync(f.Invoice!.Id.Value, 2, ActualCostKind.Capex)).Status); }
    [Fact] public async Task Unavailable_project_is_rejected() { var f = Make(); f.Lookup.Project = f.Lookup.Project! with { Available = false }; Assert.Equal(SupplierInvoicePostingStatus.ProjectUnavailable, (await f.Service.PostAsync(f.Invoice!.Id.Value, 1, ActualCostKind.Capex)).Status); }
    [Fact] public async Task Persisted_currency_mismatch_is_safe_persistence_failure() { var f = Make(); f.Lookup.Project = f.Lookup.Project! with { BaseCurrency = "EUR" }; var r = await f.Service.PostAsync(f.Invoice!.Id.Value, 1, ActualCostKind.Capex); Assert.Equal(SupplierInvoicePostingStatus.PersistenceFailure, r.Status); Assert.DoesNotContain("EUR", r.SafeMessage); }
    [Theory]
    [InlineData("get")]
    [InlineData("add")]
    [InlineData("save")]
    public async Task Dependency_failures_are_safe(string phase) { var f = Make(); f.Store.Failure = phase; var r = await f.Service.PostAsync(f.Invoice!.Id.Value, 1, ActualCostKind.Capex); Assert.Equal(SupplierInvoicePostingStatus.PersistenceFailure, r.Status); Assert.DoesNotContain("technical", r.SafeMessage, StringComparison.OrdinalIgnoreCase); }
    [Fact] public async Task Cancellation_before_work_is_cancelled() { var f = Make(); using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Equal(SupplierInvoicePostingStatus.Cancelled, (await f.Service.PostAsync(Guid.NewGuid(), 1, ActualCostKind.Capex, cts.Token)).Status); Assert.Equal(0, f.Store.GetCalls); }
    [Fact] public async Task Cancellation_during_dependency_is_cancelled() { var f = Make(); using var cts = new CancellationTokenSource(); f.Store.CancelOnGet = cts; var r = await f.Service.PostAsync(f.Invoice!.Id.Value, 1, ActualCostKind.Capex, cts.Token); Assert.Equal(SupplierInvoicePostingStatus.Cancelled, r.Status); }

    private sealed class Fixture
    {
        public SupplierInvoice? Invoice { get; }
        public Store Store { get; }
        public Lookup Lookup { get; }
        public ISupplierInvoicePostingService Service { get; }
        public Fixture(SupplierInvoice? invoice) { Invoice = invoice; Store = new() { Invoice = invoice }; Lookup = new() { Project = invoice is null ? null : new(invoice.ProjectId.Value, "Project", "PLN", true) }; Service = new SupplierInvoicePostingService(Store, Lookup, new Clock()); }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Lookup : IBudgetingProjectLookup { public BudgetProjectInfo? Project; public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Project); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)[]); }
    private sealed class Store : ISupplierInvoicePostingStore
    {
        public SupplierInvoice? Invoice; public List<ActualCost> Added { get; } = []; public int GetCalls; public int SaveCalls; public string? Failure; public CancellationTokenSource? CancelOnGet;
        public Task<SupplierInvoice?> GetInvoiceAsync(SupplierInvoiceId id, CancellationToken ct) { GetCalls++; if (CancelOnGet is not null) { CancelOnGet.Cancel(); throw new OperationCanceledException(ct); } if (Failure == "get") throw new SupplierInvoicePostingPersistenceException(new Exception("technical")); return Task.FromResult(Invoice); }
        public Task AddActualCostAsync(ActualCost cost, CancellationToken ct) { if (Failure == "add") throw new SupplierInvoicePostingPersistenceException(new Exception("technical")); Added.Add(cost); return Task.CompletedTask; }
        public Task<SupplierInvoicePostingStatus> SaveAsync(CancellationToken ct) { SaveCalls++; if (Failure == "save") throw new SupplierInvoicePostingPersistenceException(new Exception("technical")); return Task.FromResult(SupplierInvoicePostingStatus.Success); }
        public Task ResetTrackingAsync() => Task.CompletedTask;
    }
}
