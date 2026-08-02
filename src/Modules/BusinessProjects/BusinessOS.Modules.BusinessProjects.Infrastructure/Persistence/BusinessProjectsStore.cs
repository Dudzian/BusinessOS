using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.BusinessProjects.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;

internal sealed class BusinessProjectsStore(IDbContextFactory<BusinessProjectsDbContext> factory) : IBusinessProjectsStore, IAsyncDisposable
{
    private BusinessProjectsDbContext? tracked;

    public Task<IReadOnlyList<BusinessProject>> ListAsync(CompanyId companyId, BusinessProjectStatus? status, CancellationToken ct) =>
        ExecuteReadAsync(async db =>
        {
            var query = db.BusinessProjects.AsNoTracking().Where(project => project.CompanyId == companyId);
            if (status is not null) query = query.Where(project => project.Status == status);
            var projects = await query.ToListAsync(ct);
            return (IReadOnlyList<BusinessProject>)projects.OrderByDescending(project => project.UpdatedAt)
                .ThenBy(project => project.Name, StringComparer.Ordinal).ThenBy(project => project.Id.Value).ToArray();
        }, ct);

    public async Task<BusinessProject?> GetAsync(BusinessProjectId id, bool tracking, CancellationToken ct)
    {
        if (!tracking)
            return await ExecuteReadAsync(db => db.BusinessProjects.AsNoTracking().SingleOrDefaultAsync(project => project.Id == id, ct), ct);
        await ResetTrackingAsync();
        try
        {
            tracked = await factory.CreateDbContextAsync(ct);
            return await tracked.BusinessProjects.SingleOrDefaultAsync(project => project.Id == id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; }
        catch (Exception exception) { await ResetTrackingAsync(); throw Failure(exception); }
    }

    public Task<bool> NameExistsAsync(CompanyId companyId, string name, BusinessProjectId? exceptId, CancellationToken ct) =>
        ExecuteReadAsync(db => db.BusinessProjects.AsNoTracking().AnyAsync(project => project.CompanyId == companyId &&
            project.Name == name && (exceptId == null || project.Id != exceptId.Value), ct), ct);

    public Task<bool> HasActiveProjectsAsync(CompanyId companyId, CancellationToken ct) =>
        ExecuteReadAsync(db => db.BusinessProjects.AsNoTracking().AnyAsync(project => project.CompanyId == companyId, ct), ct);

    public async Task AddAsync(BusinessProject project, CancellationToken ct)
    {
        await ResetTrackingAsync();
        try
        {
            tracked = await factory.CreateDbContextAsync(ct);
            await tracked.BusinessProjects.AddAsync(project, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ResetTrackingAsync(); throw; }
        catch (Exception exception) { await ResetTrackingAsync(); throw Failure(exception); }
    }

    public async Task<BusinessProjectsSaveStatus> SaveChangesAsync(CancellationToken ct)
    {
        if (tracked is null) throw Failure(new InvalidOperationException("No tracked BusinessProjects operation is active."));
        try
        {
            await tracked.SaveChangesAsync(ct);
            return BusinessProjectsSaveStatus.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DbUpdateConcurrencyException) { return BusinessProjectsSaveStatus.ConcurrencyConflict; }
        catch (DbUpdateException exception) when (IsDuplicate(exception)) { return BusinessProjectsSaveStatus.DuplicateProjectName; }
        catch (Exception exception) { throw Failure(exception); }
        finally { await ResetTrackingAsync(); }
    }

    internal static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } sqlite &&
        sqlite.Message.Contains("business_projects.company_id, business_projects.name", StringComparison.OrdinalIgnoreCase);

    public async Task ResetTrackingAsync()
    {
        if (tracked is not null) await tracked.DisposeAsync();
        tracked = null;
    }

    private async Task<T> ExecuteReadAsync<T>(Func<BusinessProjectsDbContext, Task<T>> action, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await action(db);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw Failure(exception); }
    }

    private static BusinessProjectsPersistenceException Failure(Exception exception) =>
        new("BusinessProjects persistence operation failed.", exception);

    public ValueTask DisposeAsync() => tracked is null ? ValueTask.CompletedTask : tracked.DisposeAsync();
}
