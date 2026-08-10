using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.BuildingBlocks.Domain.Ids;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class BudgetVarianceReadStore(IDbContextFactory<BudgetingDbContext> factory) : IBudgetVarianceReadStore
{
    public async Task<IReadOnlyList<BudgetVarianceBudgetItem>> ListBudgetsAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Budgets.AsNoTracking().Where(x => x.ProjectId == new BusinessProjectId(projectId)).ToArrayAsync(ct);
        var values = rows.Select(x => new BudgetVarianceBudgetItem(x.Id.Value, x.Name, x.Status, x.UpdatedAtUtc)).ToArray();
        return values.OrderBy(x => x.Name, StringComparer.Ordinal).ThenByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id).ToArray();
    }

    public async Task<IReadOnlyList<BudgetVarianceVersionItem>> ListVersionsAsync(Guid budgetId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.BudgetVersions.AsNoTracking().Where(x => x.BudgetId == new BudgetId(budgetId)).ToArrayAsync(ct);
        var values = rows.Select(x => new BudgetVarianceVersionItem(x.Id.Value, x.BudgetId.Value, x.Number, x.CreatedAtUtc)).ToArray();
        return values.OrderBy(x => x.Number).ThenBy(x => x.Id).ToArray();
    }

    public async Task<BudgetVarianceSnapshotSource?> GetSnapshotSourceAsync(Guid projectId, Guid budgetId, Guid versionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == new BudgetId(budgetId) && x.ProjectId == new BusinessProjectId(projectId), ct);
        if (budget is null) return null;
        var version = await db.BudgetVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == new BudgetVersionId(versionId) && x.BudgetId == new BudgetId(budgetId), ct);
        if (version is null) return null;
        var lineRows = await db.BudgetLines.AsNoTracking().Where(x => x.VersionId == new BudgetVersionId(versionId)).ToArrayAsync(ct);
        var lines = lineRows.Select(x => new BudgetVarianceLineSource(x.Kind, x.Amount.Amount, x.Amount.Currency.Value)).ToArray();
        var actualRows = await db.ActualCosts.AsNoTracking().Where(x => x.ProjectId == new BusinessProjectId(projectId) && x.ArchivedAtUtc == null).ToArrayAsync(ct);
        var actuals = actualRows.Select(x => new BudgetVarianceActualSource(x.Kind, x.Amount.Amount, x.Amount.Currency.Value)).ToArray();
        return new(projectId, budgetId, budget.Name, budget.Status, versionId, version.Number, lines, actuals);
    }
}
