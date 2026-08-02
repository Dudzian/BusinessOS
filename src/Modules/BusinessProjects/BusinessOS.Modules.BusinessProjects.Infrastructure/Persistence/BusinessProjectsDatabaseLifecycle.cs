using Microsoft.EntityFrameworkCore;
namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;

public sealed record BusinessProjectsMigrationState(bool DatabaseExists, IReadOnlyList<string> PendingMigrations)
{ public bool HasPendingMigrations => PendingMigrations.Count > 0; }
public interface IBusinessProjectsDatabaseLifecycle
{
    Task<BusinessProjectsMigrationState> InspectAsync(CancellationToken cancellationToken);
    Task InitializeAsync(CancellationToken cancellationToken);
}
public sealed class BusinessProjectsDatabaseLifecycle(IDbContextFactory<BusinessProjectsDbContext> factory, string databasePath) : IBusinessProjectsDatabaseLifecycle
{
    public async Task<BusinessProjectsMigrationState> InspectAsync(CancellationToken ct)
    { if (!File.Exists(databasePath)) return new(false, ["new-database"]); await using var db = await factory.CreateDbContextAsync(ct); return new(true, (await db.Database.GetPendingMigrationsAsync(ct)).ToArray()); }
    public async Task InitializeAsync(CancellationToken ct)
    { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!); await using var db = await factory.CreateDbContextAsync(ct); await db.Database.MigrateAsync(ct); if (!await db.Database.CanConnectAsync(ct) || (await db.Database.GetPendingMigrationsAsync(ct)).Any()) throw new InvalidOperationException("BusinessProjects database migration verification failed."); }
}
