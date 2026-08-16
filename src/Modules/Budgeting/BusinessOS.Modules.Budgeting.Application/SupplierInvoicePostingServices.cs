using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Modules.Budgeting.Application;

public enum SupplierInvoicePostingStatus { Success, ValidationFailure, NotFound, Archived, AlreadyPosted, ConcurrencyConflict, ProjectUnavailable, PersistenceFailure, Cancelled }
public sealed record SupplierInvoicePostingResult<T>(SupplierInvoicePostingStatus Status, string SafeMessage, T? Value);
public sealed record SupplierInvoicePostingReceipt(SupplierInvoiceItem Invoice, ActualCostItem ActualCost);
public sealed class SupplierInvoicePostingPersistenceException(Exception inner) : Exception("Supplier invoice posting persistence failed.", inner);

public interface ISupplierInvoicePostingStore
{
    Task<SupplierInvoice?> GetInvoiceAsync(SupplierInvoiceId id, CancellationToken ct);
    Task AddActualCostAsync(ActualCost cost, CancellationToken ct);
    Task<SupplierInvoicePostingStatus> SaveAsync(CancellationToken ct);
    Task ResetTrackingAsync();
}

public interface ISupplierInvoicePostingService
{
    Task<SupplierInvoicePostingResult<SupplierInvoicePostingReceipt>> PostAsync(Guid invoiceId, long expectedInvoiceVersion, ActualCostKind kind, CancellationToken ct = default);
}

internal sealed class SupplierInvoicePostingService(ISupplierInvoicePostingStore store, IBudgetingProjectLookup projects, TimeProvider clock) : ISupplierInvoicePostingService
{
    public async Task<SupplierInvoicePostingResult<SupplierInvoicePostingReceipt>> PostAsync(Guid invoiceId, long expectedInvoiceVersion, ActualCostKind kind, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(kind)) return Fail(SupplierInvoicePostingStatus.ValidationFailure, "Wybierz prawidłowy rodzaj kosztu.");
            var invoice = await store.GetInvoiceAsync(new(invoiceId), ct);
            if (invoice is null) return Fail(SupplierInvoicePostingStatus.NotFound, "Nie znaleziono faktury.");
            if (invoice.ArchivedAtUtc is not null) return Fail(SupplierInvoicePostingStatus.Archived, "Faktura jest zarchiwizowana.");
            if (invoice.IsPosted) return Fail(SupplierInvoicePostingStatus.AlreadyPosted, "Faktura została już zaksięgowana.");
            if (invoice.Version != expectedInvoiceVersion) return Fail(SupplierInvoicePostingStatus.ConcurrencyConflict, "Faktura została zmieniona.");
            var project = await projects.GetAsync(invoice.ProjectId.Value, ct);
            if (project is not { Available: true }) return Fail(SupplierInvoicePostingStatus.ProjectUnavailable, "Projekt nie jest dostępny.");
            if (invoice.ProjectId.Value != project.Id || invoice.Amount.Amount <= 0 ||
                !string.Equals(invoice.Amount.Currency.Value, project.BaseCurrency, StringComparison.OrdinalIgnoreCase) ||
                invoice.InvoiceDate == default || string.IsNullOrWhiteSpace(invoice.InvoiceNumber) || invoice.InvoiceNumber.Length > 100)
                return Fail(SupplierInvoicePostingStatus.PersistenceFailure, "Nie udało się zaksięgować faktury.");
            var now = clock.GetUtcNow();
            var cost = ActualCost.Create(invoice.ProjectId, kind, $"Faktura {invoice.InvoiceNumber}", invoice.Amount, invoice.InvoiceDate, invoice.Note, now);
            await store.AddActualCostAsync(cost, ct);
            invoice.MarkPosted(cost.Id, now);
            var status = await store.SaveAsync(ct);
            if (status != SupplierInvoicePostingStatus.Success) return Fail(status, status == SupplierInvoicePostingStatus.ConcurrencyConflict ? "Faktura została zmieniona." : "Nie udało się zaksięgować faktury.");
            var actual = new ActualCostItem(cost.Id.Value, cost.ProjectId.Value, cost.Kind, cost.Name, cost.Amount.Amount, cost.Amount.Currency.Value, cost.IncurredOn, cost.Note, cost.CreatedAtUtc, cost.UpdatedAtUtc, cost.Version);
            return new(status, "Faktura została zaksięgowana jako koszt.", new(SupplierInvoicesCrudService.Map(invoice), actual));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail(SupplierInvoicePostingStatus.Cancelled, "Operacja została anulowana."); }
        catch (Exception e) when (e is SupplierInvoicePostingPersistenceException or BudgetingProjectLookupException) { return Fail(SupplierInvoicePostingStatus.PersistenceFailure, "Nie udało się zaksięgować faktury."); }
        catch (ArgumentException) { return Fail(SupplierInvoicePostingStatus.PersistenceFailure, "Nie udało się zaksięgować faktury."); }
        finally { await store.ResetTrackingAsync(); }
    }

    private static SupplierInvoicePostingResult<SupplierInvoicePostingReceipt> Fail(SupplierInvoicePostingStatus status, string message) => new(status, message, null);
}
