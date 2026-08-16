using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Modules.Budgeting.Application;

public enum SupplierInvoiceOperationStatus { Success, ValidationFailure, NotFound, ConcurrencyConflict, ProjectUnavailable, Archived, Posted, DuplicateInvoice, PersistenceFailure, Cancelled }
public sealed record SupplierInvoiceResult(SupplierInvoiceOperationStatus Status, string SafeMessage);
public sealed record SupplierInvoiceResult<T>(SupplierInvoiceOperationStatus Status, string SafeMessage, T? Value);
public sealed record SupplierInvoiceItem(Guid Id, Guid ProjectId, string SupplierName, string InvoiceNumber, decimal Amount, string Currency, DateOnly InvoiceDate, DateOnly DueDate, string? Note, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Version, Guid? PostedActualCostId = null, DateTimeOffset? PostedAtUtc = null)
{
    public bool IsPosted => PostedActualCostId is not null;
    public string PostingStatusText => IsPosted ? "Zaksięgowana" : "Nie zaksięgowana";
    public string SemanticName => FormattableString.Invariant($"Supplier={SupplierName} | Invoice={InvoiceNumber} | Amount={Amount:0.##} {Currency} | InvoiceDate={InvoiceDate:yyyy-MM-dd} | DueDate={DueDate:yyyy-MM-dd}");
}
public sealed class SupplierInvoicesReadException(Exception inner) : Exception("Supplier invoices could not be read.", inner);
public sealed class SupplierInvoicesPersistenceException(string message, Exception inner) : Exception(message, inner);
public interface ISupplierInvoicesStore
{
    Task<IReadOnlyList<SupplierInvoice>> ListAsync(BusinessProjectId projectId, CancellationToken ct);
    Task<SupplierInvoice?> GetAsync(SupplierInvoiceId id, bool tracked, CancellationToken ct);
    Task<bool> IdentityExistsAsync(BusinessProjectId projectId, string supplierKey, string invoiceNumberKey, SupplierInvoiceId? except, CancellationToken ct);
    Task AddAsync(SupplierInvoice invoice, CancellationToken ct);
    Task<SupplierInvoiceOperationStatus> SaveAsync(CancellationToken ct);
    Task ResetTrackingAsync();
}
public interface ISupplierInvoicesCrudService
{
    Task<IReadOnlyList<SupplierInvoiceItem>> ListAsync(Guid projectId, CancellationToken ct = default);
    Task<SupplierInvoiceItem?> GetAsync(Guid invoiceId, CancellationToken ct = default);
    Task<SupplierInvoiceResult<SupplierInvoiceItem>> CreateAsync(Guid projectId, string supplierName, string invoiceNumber, decimal amount, string currency, DateOnly invoiceDate, DateOnly dueDate, string? note, CancellationToken ct = default);
    Task<SupplierInvoiceResult<SupplierInvoiceItem>> UpdateAsync(Guid invoiceId, long expectedVersion, string supplierName, string invoiceNumber, decimal amount, string currency, DateOnly invoiceDate, DateOnly dueDate, string? note, CancellationToken ct = default);
    Task<SupplierInvoiceResult> ArchiveAsync(Guid invoiceId, long expectedVersion, CancellationToken ct = default);
}
internal sealed class SupplierInvoicesCrudService(ISupplierInvoicesStore store, IBudgetingProjectLookup projects, TimeProvider clock) : ISupplierInvoicesCrudService
{
    public Task<IReadOnlyList<SupplierInvoiceItem>> ListAsync(Guid projectId, CancellationToken ct = default) => Read(async () =>
    {
        var project = await projects.GetAsync(projectId, ct);
        if (project is not { Available: true }) return [];
        var invoices = await store.ListAsync(new(projectId), ct);
        if (invoices.Any(x => x.ProjectId.Value != projectId || !string.Equals(x.Amount.Currency.Value, project.BaseCurrency, StringComparison.OrdinalIgnoreCase)))
            throw new SupplierInvoicesReadException(new InvalidOperationException("Persisted invoice integrity check failed."));
        return (IReadOnlyList<SupplierInvoiceItem>)invoices.Select(Map).ToArray();
    }, ct);
    public Task<SupplierInvoiceItem?> GetAsync(Guid id, CancellationToken ct = default) => Read(async () =>
    {
        var invoice = await store.GetAsync(new(id), false, ct);
        if (invoice is null) return null;
        var project = await projects.GetAsync(invoice.ProjectId.Value, ct);
        if (project is not { Available: true }) return null;
        if (!string.Equals(invoice.Amount.Currency.Value, project.BaseCurrency, StringComparison.OrdinalIgnoreCase))
            throw new SupplierInvoicesReadException(new InvalidOperationException("Persisted invoice integrity check failed."));
        return Map(invoice);
    }, ct);
    public Task<SupplierInvoiceResult<SupplierInvoiceItem>> CreateAsync(Guid projectId, string supplier, string number, decimal amount, string currency, DateOnly invoiceDate, DateOnly dueDate, string? note, CancellationToken ct = default) => Guard(async () =>
    {
        var project = await projects.GetAsync(projectId, ct); var invalid = ValidateProject(project, currency); if (invalid is not null) return invalid;
        var invoice = SupplierInvoice.Create(new(projectId), supplier, number, new(amount, new(project!.BaseCurrency)), invoiceDate, dueDate, note, clock.GetUtcNow());
        if (await store.IdentityExistsAsync(invoice.ProjectId, invoice.SupplierKey, invoice.InvoiceNumberKey, null, ct)) return Fail(SupplierInvoiceOperationStatus.DuplicateInvoice, "Faktura jest już zarejestrowana.");
        await store.AddAsync(invoice, ct); return await Save(invoice, "Faktura została dodana.", ct);
    }, ct);
    public Task<SupplierInvoiceResult<SupplierInvoiceItem>> UpdateAsync(Guid id, long version, string supplier, string number, decimal amount, string currency, DateOnly invoiceDate, DateOnly dueDate, string? note, CancellationToken ct = default) => Guard(async () =>
    {
        var invoice = await store.GetAsync(new(id), true, ct); if (invoice is null) return Fail(SupplierInvoiceOperationStatus.NotFound, "Nie znaleziono faktury.");
        if (invoice.ArchivedAtUtc is not null) return Fail(SupplierInvoiceOperationStatus.Archived, "Faktura jest zarchiwizowana."); if (invoice.IsPosted) return Fail(SupplierInvoiceOperationStatus.Posted, "Zaksięgowanej faktury nie można zmienić."); if (invoice.Version != version) return Fail(SupplierInvoiceOperationStatus.ConcurrencyConflict, "Faktura została zmieniona.");
        var project = await projects.GetAsync(invoice.ProjectId.Value, ct); var invalid = ValidateProject(project, currency); if (invalid is not null) return invalid;
        invoice.Update(supplier, number, new(amount, new(project!.BaseCurrency)), invoiceDate, dueDate, note, clock.GetUtcNow());
        if (await store.IdentityExistsAsync(invoice.ProjectId, invoice.SupplierKey, invoice.InvoiceNumberKey, invoice.Id, ct)) return Fail(SupplierInvoiceOperationStatus.DuplicateInvoice, "Faktura jest już zarejestrowana.");
        return await Save(invoice, "Faktura została zmieniona.", ct);
    }, ct);
    public async Task<SupplierInvoiceResult> ArchiveAsync(Guid id, long version, CancellationToken ct = default)
    {
        var r = await Guard(async () => { var x = await store.GetAsync(new(id), true, ct); if (x is null) return Fail(SupplierInvoiceOperationStatus.NotFound, "Nie znaleziono faktury."); if (x.ArchivedAtUtc is not null) return Fail(SupplierInvoiceOperationStatus.Archived, "Faktura jest zarchiwizowana."); if (x.IsPosted) return Fail(SupplierInvoiceOperationStatus.Posted, "Zaksięgowanej faktury nie można zarchiwizować."); if (x.Version != version) return Fail(SupplierInvoiceOperationStatus.ConcurrencyConflict, "Faktura została zmieniona."); if (await projects.GetAsync(x.ProjectId.Value, ct) is not { Available: true }) return Fail(SupplierInvoiceOperationStatus.ProjectUnavailable, "Projekt nie jest dostępny."); x.Archive(clock.GetUtcNow()); return await Save(x, "Faktura została zarchiwizowana.", ct); }, ct); return new(r.Status, r.SafeMessage);
    }
    private static SupplierInvoiceResult<SupplierInvoiceItem>? ValidateProject(BudgetProjectInfo? p, string currency) => p is not { Available: true } ? Fail(SupplierInvoiceOperationStatus.ProjectUnavailable, "Projekt nie jest dostępny.") : string.IsNullOrWhiteSpace(currency) || !string.Equals(p.BaseCurrency, currency.Trim(), StringComparison.OrdinalIgnoreCase) ? Fail(SupplierInvoiceOperationStatus.ValidationFailure, "Waluta faktury musi być walutą bazową projektu.") : null;
    private async Task<SupplierInvoiceResult<SupplierInvoiceItem>> Save(SupplierInvoice x, string message, CancellationToken ct) { var s = await store.SaveAsync(ct); return s == SupplierInvoiceOperationStatus.Success ? new(s, message, Map(x)) : Fail(s, "Nie udało się zapisać faktury."); }
    private async Task<SupplierInvoiceResult<SupplierInvoiceItem>> Guard(Func<Task<SupplierInvoiceResult<SupplierInvoiceItem>>> action, CancellationToken ct) { try { ct.ThrowIfCancellationRequested(); return await action(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail(SupplierInvoiceOperationStatus.Cancelled, "Operacja została anulowana."); } catch (ArgumentException) { return Fail(SupplierInvoiceOperationStatus.ValidationFailure, "Popraw wskazane dane."); } catch (Exception e) when (e is SupplierInvoicesPersistenceException or BudgetingProjectLookupException) { return Fail(SupplierInvoiceOperationStatus.PersistenceFailure, "Nie udało się wykonać operacji."); } finally { await store.ResetTrackingAsync(); } }
    private static async Task<T> Read<T>(Func<Task<T>> read, CancellationToken ct) { try { return await read(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) when (e is SupplierInvoicesPersistenceException or BudgetingProjectLookupException) { throw new SupplierInvoicesReadException(e); } }
    private static SupplierInvoiceResult<SupplierInvoiceItem> Fail(SupplierInvoiceOperationStatus s, string m) => new(s, m, null);
    internal static SupplierInvoiceItem Map(SupplierInvoice x) => new(x.Id.Value, x.ProjectId.Value, x.SupplierName, x.InvoiceNumber, x.Amount.Amount, x.Amount.Currency.Value, x.InvoiceDate, x.DueDate, x.Note, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version, x.PostedActualCostId?.Value, x.PostedAtUtc);
}
