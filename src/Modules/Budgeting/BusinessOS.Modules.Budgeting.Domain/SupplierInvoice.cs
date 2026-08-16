using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.Budgeting.Domain;

public sealed class SupplierInvoice
{
    private SupplierInvoice() { }
    public SupplierInvoiceId Id { get; private set; } = SupplierInvoiceId.New();
    public BusinessProjectId ProjectId { get; private set; }
    public string SupplierName { get; private set; } = string.Empty;
    public string SupplierKey { get; private set; } = string.Empty;
    public string InvoiceNumber { get; private set; } = string.Empty;
    public string InvoiceNumberKey { get; private set; } = string.Empty;
    public Money Amount { get; private set; }
    public DateOnly InvoiceDate { get; private set; }
    public DateOnly DueDate { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset? ArchivedAtUtc { get; private set; }
    public ActualCostId? PostedActualCostId { get; private set; }
    public DateTimeOffset? PostedAtUtc { get; private set; }
    public bool IsPosted => PostedActualCostId is not null;

    public static SupplierInvoice Create(BusinessProjectId projectId, string supplierName, string invoiceNumber, Money amount, DateOnly invoiceDate, DateOnly dueDate, string? note, DateTimeOffset now)
    {
        if (projectId.Value == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        var invoice = new SupplierInvoice { ProjectId = projectId };
        invoice.SetFields(supplierName, invoiceNumber, amount, invoiceDate, dueDate, note);
        invoice.CreatedAtUtc = invoice.UpdatedAtUtc = now.ToUniversalTime();
        return invoice;
    }
    public void Update(string supplierName, string invoiceNumber, Money amount, DateOnly invoiceDate, DateOnly dueDate, string? note, DateTimeOffset now)
    {
        EnsureMutable(); SetFields(supplierName, invoiceNumber, amount, invoiceDate, dueDate, note); Touch(now);
    }
    public void Archive(DateTimeOffset now) { EnsureMutable(); ArchivedAtUtc = now.ToUniversalTime(); Touch(now); }
    public void MarkPosted(ActualCostId actualCostId, DateTimeOffset now)
    {
        EnsureActive();
        if (IsPosted) throw new InvalidOperationException("Invoice is already posted.");
        if (actualCostId.Value == Guid.Empty) throw new ArgumentException("Actual cost is required.", nameof(actualCostId));
        PostedActualCostId = actualCostId;
        PostedAtUtc = now.ToUniversalTime();
        Touch(now);
    }
    private void SetFields(string supplierName, string invoiceNumber, Money amount, DateOnly invoiceDate, DateOnly dueDate, string? note)
    {
        var supplier = supplierName?.Trim() ?? string.Empty;
        var number = invoiceNumber?.Trim() ?? string.Empty;
        var normalizedNote = note?.Trim();
        if (supplier.Length is 0 or > 256) throw new ArgumentException("Supplier is required.", nameof(supplierName));
        if (number.Length is 0 or > 100) throw new ArgumentException("Invoice number is required.", nameof(invoiceNumber));
        if (amount.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (invoiceDate == default || dueDate == default || dueDate < invoiceDate) throw new ArgumentException("Invoice dates are invalid.");
        if (normalizedNote?.Length > 1000) throw new ArgumentException("Note is too long.", nameof(note));
        SupplierName = supplier; SupplierKey = supplier.ToUpperInvariant(); InvoiceNumber = number; InvoiceNumberKey = number.ToUpperInvariant(); Amount = amount; InvoiceDate = invoiceDate; DueDate = dueDate; Note = string.IsNullOrEmpty(normalizedNote) ? null : normalizedNote;
    }
    private void EnsureActive() { if (ArchivedAtUtc is not null) throw new InvalidOperationException("Archived invoice cannot be changed."); }
    private void EnsureMutable() { EnsureActive(); if (IsPosted) throw new InvalidOperationException("Posted invoice cannot be changed."); }
    private void Touch(DateTimeOffset now) { UpdatedAtUtc = now.ToUniversalTime(); Version++; }
}
