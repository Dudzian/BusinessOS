using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Microsoft.EntityFrameworkCore;
namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class SupplierInvoicePostingStore(IDbContextFactory<BudgetingDbContext> factory) : ISupplierInvoicePostingStore, IAsyncDisposable
{
    private BudgetingDbContext? tracked;
    public async Task<SupplierInvoice?> GetInvoiceAsync(SupplierInvoiceId id, CancellationToken ct) { try { tracked ??= await factory.CreateDbContextAsync(ct); return await tracked.SupplierInvoices.SingleOrDefaultAsync(x => x.Id == id, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) { throw new SupplierInvoicePostingPersistenceException(e); } }
    public async Task AddActualCostAsync(ActualCost cost, CancellationToken ct) { try { tracked ??= await factory.CreateDbContextAsync(ct); await tracked.ActualCosts.AddAsync(cost, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) { throw new SupplierInvoicePostingPersistenceException(e); } }
    public async Task<SupplierInvoicePostingStatus> SaveAsync(CancellationToken ct) { if (tracked is null) throw new SupplierInvoicePostingPersistenceException(new InvalidOperationException()); try { await tracked.SaveChangesAsync(ct); return SupplierInvoicePostingStatus.Success; } catch (DbUpdateConcurrencyException) { return SupplierInvoicePostingStatus.ConcurrencyConflict; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) { throw new SupplierInvoicePostingPersistenceException(e); } }
    public async Task ResetTrackingAsync() { if (tracked is not null) await tracked.DisposeAsync(); tracked = null; }
    public ValueTask DisposeAsync() => tracked?.DisposeAsync() ?? ValueTask.CompletedTask;
}
