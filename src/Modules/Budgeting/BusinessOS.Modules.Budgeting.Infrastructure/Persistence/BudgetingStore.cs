using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

internal sealed class BudgetingStore(IDbContextFactory<BudgetingDbContext> factory) : IBudgetingStore, IAsyncDisposable
{
    private BudgetingDbContext? tracked;
    public Task<IReadOnlyList<Budget>> ListBudgetsAsync(BusinessProjectId id, CancellationToken ct) => Read(async db =>
    {
        var budgets = await db.Budgets
            .AsNoTracking()
            .Where(x => x.ProjectId == id && x.ArchivedAtUtc == null)
            .ToArrayAsync(ct);

        return (IReadOnlyList<Budget>)budgets
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToArray();
    }, ct);
    public async Task<Budget?> GetBudgetAsync(BudgetId id, bool tracking, CancellationToken ct) { if (!tracking) return await Read(db => db.Budgets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct), ct); return await Tracked(async db => await db.Budgets.SingleOrDefaultAsync(x => x.Id == id, ct), ct); }
    public Task<bool> NameExistsAsync(BusinessProjectId p, string n, BudgetId? except, CancellationToken ct) => Read(db => db.Budgets.AsNoTracking().AnyAsync(x => x.ProjectId == p && x.NormalizedName == n && x.ArchivedAtUtc == null && (except == null || x.Id != except), ct), ct);
    public Task AddBudgetAsync(Budget b, CancellationToken ct) => Tracked(async db => { await db.Budgets.AddAsync(b, ct); return true; }, ct);
    public Task<IReadOnlyList<BudgetVersion>> ListVersionsAsync(BudgetId id, CancellationToken ct) => Read(async db => (IReadOnlyList<BudgetVersion>)await db.BudgetVersions.AsNoTracking().Where(x => x.BudgetId == id).OrderBy(x => x.Number).ToArrayAsync(ct), ct);
    public Task<BudgetVersion?> GetVersionAsync(BudgetVersionId id, CancellationToken ct) => Read(db => db.BudgetVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct), ct);
    public Task<IReadOnlyList<BudgetLine>> ListLinesAsync(BudgetVersionId id, CancellationToken ct) => Read(async db => (IReadOnlyList<BudgetLine>)await db.BudgetLines.AsNoTracking().Where(x => x.VersionId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToArrayAsync(ct), ct);
    public Task AddVersionAsync(BudgetVersion v, CancellationToken ct) => Tracked(async db => { await db.BudgetVersions.AddAsync(v, ct); return true; }, ct);
    public Task AddLineAsync(BudgetLine l, CancellationToken ct) => Tracked(async db => { await db.BudgetLines.AddAsync(l, ct); return true; }, ct);
    public async Task<BudgetLine?> GetLineAsync(Guid id, bool tracking, CancellationToken ct) { if (!tracking) return await Read(db => db.BudgetLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct), ct); return await Tracked(async db => await db.BudgetLines.SingleOrDefaultAsync(x => x.Id == id, ct), ct); }
    public Task RemoveLineAsync(BudgetLine l, CancellationToken ct) => Tracked(db => { db.BudgetLines.Remove(l); return Task.FromResult(true); }, ct);
    public async Task<BudgetingOperationStatus> SaveAsync(CancellationToken ct) { if (tracked is null) throw Failure(new InvalidOperationException("No tracked operation.")); try { await tracked.SaveChangesAsync(ct); return BudgetingOperationStatus.Success; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (DbUpdateConcurrencyException) { return BudgetingOperationStatus.ConcurrencyConflict; } catch (DbUpdateException e) when (IsDuplicateBudgetName(e)) { return BudgetingOperationStatus.DuplicateName; } catch (DbUpdateException e) when (IsDuplicateVersionNumber(e)) { return BudgetingOperationStatus.ConcurrencyConflict; } catch (Exception e) { throw Failure(e); } finally { await ResetTrackingAsync(); } }
    public async Task<BudgetVersionCreationResult> CreateInitialVersionAsync(BudgetId id, long expected, string? note, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        try { var budget = await db.Budgets.SingleOrDefaultAsync(x => x.Id == id, ct); if (budget is null) return new(BudgetingOperationStatus.NotFound, null); if (budget.Status == BudgetStatus.Archived) return new(BudgetingOperationStatus.Archived, null); if (budget.Status != BudgetStatus.Draft) return new(BudgetingOperationStatus.ValidationFailure, null); if (budget.Version != expected) return new(BudgetingOperationStatus.ConcurrencyConflict, null); if (await db.BudgetVersions.AnyAsync(x => x.BudgetId == id, ct)) return new(BudgetingOperationStatus.ValidationFailure, null); var created = BudgetVersion.Create(id, 1, now, note); await db.BudgetVersions.AddAsync(created, ct); budget.RegisterRevision(now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(BudgetingOperationStatus.Success, created); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (DbUpdateConcurrencyException) { return new(BudgetingOperationStatus.ConcurrencyConflict, null); } catch (DbUpdateException e) when (IsDuplicateVersionNumber(e)) { return new(BudgetingOperationStatus.ConcurrencyConflict, null); } catch (Exception e) { throw Failure(e); }
    }
    public async Task<BudgetVersionCreationResult> CreateNextVersionAsync(Budget budget, long expected, string? note, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        try { var current = await db.Budgets.SingleOrDefaultAsync(x => x.Id == budget.Id, ct); if (current is null) return new(BudgetingOperationStatus.NotFound, null); if (current.Status == BudgetStatus.Archived) return new(BudgetingOperationStatus.Archived, null); if (current.Status != BudgetStatus.Draft) return new(BudgetingOperationStatus.ValidationFailure, null); if (current.Version != expected) return new(BudgetingOperationStatus.ConcurrencyConflict, null); var latest = await db.BudgetVersions.Where(x => x.BudgetId == budget.Id).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct); if (latest is null) return new(BudgetingOperationStatus.ValidationFailure, null); var next = BudgetVersion.Create(budget.Id, latest.Number + 1, now, note); await db.BudgetVersions.AddAsync(next, ct); var lines = await db.BudgetLines.AsNoTracking().Where(x => x.VersionId == latest.Id).ToArrayAsync(ct); await db.BudgetLines.AddRangeAsync(lines.Select(x => x.CopyTo(next.Id)), ct); current.RegisterRevision(now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(BudgetingOperationStatus.Success, next); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (DbUpdateConcurrencyException) { return new(BudgetingOperationStatus.ConcurrencyConflict, null); } catch (DbUpdateException e) when (IsDuplicateVersionNumber(e)) { return new(BudgetingOperationStatus.ConcurrencyConflict, null); } catch (Exception e) { throw Failure(e); }
    }
    public async Task ResetTrackingAsync() { if (tracked is not null) await tracked.DisposeAsync(); tracked = null; }
    private async Task<T> Tracked<T>(Func<BudgetingDbContext, Task<T>> action, CancellationToken ct) { try { if (tracked is null) tracked = await factory.CreateDbContextAsync(ct); return await action(tracked); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; } catch (BudgetingPersistenceException) { await ResetTrackingAsync(); throw; } catch (Exception e) { await ResetTrackingAsync(); throw Failure(e); } }
    private async Task<T> Read<T>(Func<BudgetingDbContext, Task<T>> f, CancellationToken ct) { try { await using var db = await factory.CreateDbContextAsync(ct); return await f(db); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) { throw Failure(e); } }
    private static BudgetingPersistenceException Failure(Exception e) => new("Budgeting persistence failed.", e);
    internal static bool IsDuplicateBudgetName(DbUpdateException e) => e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } s && s.Message.Contains("budgets.business_project_id, budgets.normalized_name", StringComparison.OrdinalIgnoreCase);
    internal static bool IsDuplicateVersionNumber(DbUpdateException e) => e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } s && s.Message.Contains("budget_versions.budget_id, budget_versions.number", StringComparison.OrdinalIgnoreCase);
    public ValueTask DisposeAsync() => tracked?.DisposeAsync() ?? ValueTask.CompletedTask;
}
