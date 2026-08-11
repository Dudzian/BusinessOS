using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class BudgetForecastReadStore(IDbContextFactory<BudgetingDbContext> factory) : IBudgetForecastReadStore
{
    public async Task<IReadOnlyList<BudgetForecastBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Budgets.AsNoTracking().Where(x => x.ProjectId == new BusinessProjectId(projectId)).ToArrayAsync(ct);
        return rows.Select(x => new BudgetForecastBudgetItem(x.Id.Value, x.Name, x.Status, x.UpdatedAtUtc)).OrderBy(x => x.Name, StringComparer.Ordinal).ThenByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id).ToArray();
    }
    public async Task<IReadOnlyList<BudgetForecastVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.BudgetVersions.AsNoTracking().Where(x => x.BudgetId == new BudgetId(budgetId)).ToArrayAsync(ct);
        return rows.Select(x => new BudgetForecastVersionItem(x.Id.Value, x.BudgetId.Value, x.Number, x.CreatedAtUtc)).OrderBy(x => x.Number).ThenBy(x => x.Id).ToArray();
    }
    public async Task<BudgetForecastSnapshotSource?> GetSnapshotSourceAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == new BudgetId(budgetId) && x.ProjectId == new BusinessProjectId(projectId), ct);
        if (budget is null) return null;
        var version = await db.BudgetVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == new BudgetVersionId(versionId) && x.BudgetId == new BudgetId(budgetId), ct);
        if (version is null) return null;
        var lines = (await db.BudgetLines.AsNoTracking().Where(x => x.VersionId == new BudgetVersionId(versionId)).ToArrayAsync(ct)).Select(x => new BudgetForecastLineSource(x.Kind, x.Amount.Amount, x.Amount.Currency.Value)).ToArray();
        var actuals = (await db.ActualCosts.AsNoTracking().Where(x => x.ProjectId == new BusinessProjectId(projectId) && x.ArchivedAtUtc == null).ToArrayAsync(ct)).Select(x => new BudgetForecastActualSource(x.Kind, x.Amount.Amount, x.Amount.Currency.Value)).ToArray();
        var forecasts = (await db.ForecastCosts.AsNoTracking().Where(x => x.ProjectId == new BusinessProjectId(projectId) && x.ArchivedAtUtc == null).ToArrayAsync(ct)).Select(x => new BudgetForecastCostSource(x.Kind, x.Money.Amount, x.Money.Currency.Value)).ToArray();
        return new(projectId, budgetId, budget.Name, budget.Status, versionId, version.Number, lines, actuals, forecasts);
    }
}
