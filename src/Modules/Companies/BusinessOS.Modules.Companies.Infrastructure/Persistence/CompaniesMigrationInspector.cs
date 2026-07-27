using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public interface ICompaniesMigrationInspector
{
    Task<CompaniesMigrationState> InspectAsync(CancellationToken cancellationToken);
}

public sealed record CompaniesMigrationState(bool DatabaseExists, IReadOnlyList<string> PendingMigrations)
{
    public bool HasPendingMigrations => PendingMigrations.Count > 0;
}

public sealed class CompaniesMigrationInspector(
    IDbContextFactory<CompaniesDbContext> dbContextFactory,
    CompaniesPersistenceOptions options) : ICompaniesMigrationInspector
{
    public async Task<CompaniesMigrationState> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(options.GetNormalizedDatabasePath()))
        {
            return new(false, ["new-database"]);
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        return new(true, pending.ToArray());
    }
}
