using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public interface ICompaniesDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public sealed class CompaniesDatabaseInitializer(IDbContextFactory<CompaniesDbContext> dbContextFactory, CompaniesPersistenceOptions options) : ICompaniesDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        options.EnsureDatabaseDirectory();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
