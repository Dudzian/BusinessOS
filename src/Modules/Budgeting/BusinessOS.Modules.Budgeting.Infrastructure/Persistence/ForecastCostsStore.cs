using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class ForecastCostsStore(IDbContextFactory<BudgetingDbContext> factory) : IForecastCostsStore, IAsyncDisposable
{
    private BudgetingDbContext? tracked;

    public async Task<IReadOnlyList<ForecastCost>> ListAsync(BusinessProjectId projectId, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var costs = await db.ForecastCosts.AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.ArchivedAtUtc == null)
                .ToArrayAsync(ct);
            return costs.OrderBy(x => x.ExpectedOn)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .ThenBy(x => x.Id.Value)
                .ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { throw Failure(e); }
    }

    public async Task<ForecastCost?> GetAsync(ForecastCostId id, bool trackedEntity, CancellationToken ct)
    {
        try
        {
            if (!trackedEntity)
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                return await db.ForecastCosts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            }
            tracked ??= await factory.CreateDbContextAsync(ct);
            return await tracked.ForecastCosts.SingleOrDefaultAsync(x => x.Id == id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; }
        catch (Exception e) { await ResetTrackingAsync(); throw Failure(e); }
    }

    public async Task AddAsync(ForecastCost cost, CancellationToken ct)
    {
        try { tracked ??= await factory.CreateDbContextAsync(ct); await tracked.ForecastCosts.AddAsync(cost, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; }
        catch (Exception e) { await ResetTrackingAsync(); throw Failure(e); }
    }

    public async Task<ForecastCostOperationStatus> SaveAsync(CancellationToken ct)
    {
        if (tracked is null) throw Failure(new InvalidOperationException("No tracked operation."));
        try { await tracked.SaveChangesAsync(ct); return ForecastCostOperationStatus.Success; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateConcurrencyException) { return ForecastCostOperationStatus.ConcurrencyConflict; }
        catch (Exception e) { throw Failure(e); }
        finally { await ResetTrackingAsync(); }
    }

    public async Task ResetTrackingAsync() { if (tracked is not null) await tracked.DisposeAsync(); tracked = null; }
    private static ForecastCostsPersistenceException Failure(Exception e) => new("Forecast costs persistence failed.", e);
    public ValueTask DisposeAsync() => tracked?.DisposeAsync() ?? ValueTask.CompletedTask;
}
