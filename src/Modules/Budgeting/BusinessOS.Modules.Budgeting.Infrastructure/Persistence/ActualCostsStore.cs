using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class ActualCostsStore(IDbContextFactory<BudgetingDbContext> factory) : IActualCostsStore, IAsyncDisposable
{
    private BudgetingDbContext? tracked;

    public async Task<IReadOnlyList<ActualCost>> ListAsync(BusinessProjectId projectId, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var costs = await db.ActualCosts.AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.ArchivedAtUtc == null)
                .ToArrayAsync(ct);
            return costs.OrderByDescending(x => x.IncurredOn)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .ThenBy(x => x.Id.Value)
                .ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { throw Failure(e); }
    }

    public async Task<ActualCost?> GetAsync(ActualCostId id, bool trackedEntity, CancellationToken ct)
    {
        try
        {
            if (!trackedEntity)
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                return await db.ActualCosts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            }
            tracked ??= await factory.CreateDbContextAsync(ct);
            return await tracked.ActualCosts.SingleOrDefaultAsync(x => x.Id == id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; }
        catch (Exception e) { await ResetTrackingAsync(); throw Failure(e); }
    }

    public async Task AddAsync(ActualCost cost, CancellationToken ct)
    {
        try { tracked ??= await factory.CreateDbContextAsync(ct); await tracked.ActualCosts.AddAsync(cost, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; }
        catch (Exception e) { await ResetTrackingAsync(); throw Failure(e); }
    }

    public async Task<ActualCostOperationStatus> SaveAsync(CancellationToken ct)
    {
        if (tracked is null) throw Failure(new InvalidOperationException("No tracked operation."));
        try { await tracked.SaveChangesAsync(ct); return ActualCostOperationStatus.Success; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateConcurrencyException) { return ActualCostOperationStatus.ConcurrencyConflict; }
        catch (Exception e) { throw Failure(e); }
        finally { await ResetTrackingAsync(); }
    }

    public async Task ResetTrackingAsync() { if (tracked is not null) await tracked.DisposeAsync(); tracked = null; }
    private static ActualCostsPersistenceException Failure(Exception e) => new("Actual costs persistence failed.", e);
    public ValueTask DisposeAsync() => tracked?.DisposeAsync() ?? ValueTask.CompletedTask;
}
