using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class CostCashFlowReadStore(IDbContextFactory<BudgetingDbContext> factory) : ICostCashFlowReadStore
{
    public async Task<CostCashFlowSnapshotSource> GetSnapshotSourceAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = new BusinessProjectId(projectId);
        var actuals = await db.ActualCosts.AsNoTracking().Where(x => x.ProjectId == id && x.ArchivedAtUtc == null).ToArrayAsync(ct);
        var forecasts = await db.ForecastCosts.AsNoTracking().Where(x => x.ProjectId == id && x.ArchivedAtUtc == null).ToArrayAsync(ct);
        return new(projectId,
            actuals.Select(x => new CostCashFlowActualSource(x.Kind, x.Amount.Amount, x.Amount.Currency.Value, x.IncurredOn)).ToArray(),
            forecasts.Select(x => new CostCashFlowForecastSource(x.Kind, x.Money.Amount, x.Money.Currency.Value, x.ExpectedOn)).ToArray());
    }
}
