using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class SupplierInvoicesStore(IDbContextFactory<BudgetingDbContext> factory) : ISupplierInvoicesStore, IAsyncDisposable
{
    private BudgetingDbContext? tracked;
    public async Task<IReadOnlyList<SupplierInvoice>> ListAsync(BusinessProjectId projectId, CancellationToken ct) { try { await using var db = await factory.CreateDbContextAsync(ct); var rows = await db.SupplierInvoices.AsNoTracking().Where(x => x.ProjectId == projectId && x.ArchivedAtUtc == null).ToArrayAsync(ct); return rows.OrderBy(x => x.DueDate).ThenBy(x => x.InvoiceDate).ThenByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id.Value).ToArray(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) { throw Failure(e); } }
    public async Task<SupplierInvoice?> GetAsync(SupplierInvoiceId id, bool trackedEntity, CancellationToken ct) { try { if (!trackedEntity) { await using var db = await factory.CreateDbContextAsync(ct); return await db.SupplierInvoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); } tracked ??= await factory.CreateDbContextAsync(ct); return await tracked.SupplierInvoices.SingleOrDefaultAsync(x => x.Id == id, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; } catch (Exception e) { await ResetTrackingAsync(); throw Failure(e); } }
    public async Task<bool> IdentityExistsAsync(BusinessProjectId projectId, string supplierKey, string numberKey, SupplierInvoiceId? except, CancellationToken ct) { try { await using var db = await factory.CreateDbContextAsync(ct); return await db.SupplierInvoices.AnyAsync(x => x.ProjectId == projectId && x.SupplierKey == supplierKey && x.InvoiceNumberKey == numberKey && (!except.HasValue || x.Id != except.Value), ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) { throw Failure(e); } }
    public async Task AddAsync(SupplierInvoice x, CancellationToken ct) { tracked ??= await factory.CreateDbContextAsync(ct); await tracked.SupplierInvoices.AddAsync(x, ct); }
    public async Task<SupplierInvoiceOperationStatus> SaveAsync(CancellationToken ct) { if (tracked is null) throw Failure(new InvalidOperationException()); try { await tracked.SaveChangesAsync(ct); return SupplierInvoiceOperationStatus.Success; } catch (DbUpdateConcurrencyException) { return SupplierInvoiceOperationStatus.ConcurrencyConflict; } catch (DbUpdateException e) when (e.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true) { return SupplierInvoiceOperationStatus.DuplicateInvoice; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) { throw Failure(e); } finally { await ResetTrackingAsync(); } }
    public async Task ResetTrackingAsync() { if (tracked is not null) await tracked.DisposeAsync(); tracked = null; }
    private static SupplierInvoicesPersistenceException Failure(Exception e) => new("Supplier invoice persistence failed.", e);
    public ValueTask DisposeAsync() => tracked?.DisposeAsync() ?? ValueTask.CompletedTask;
}
