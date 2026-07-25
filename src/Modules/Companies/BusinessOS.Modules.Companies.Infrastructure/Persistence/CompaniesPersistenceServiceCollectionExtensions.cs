using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public static class CompaniesPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddCompaniesPersistence(this IServiceCollection services, Action<CompaniesPersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new CompaniesPersistenceOptions();
        configure(options);
        options.GetNormalizedDatabasePath();
        services.AddSingleton(options);
        services.AddDbContextFactory<CompaniesDbContext>(builder => builder.UseSqlite(
            options.BuildConnectionString(),
            sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory_Companies")));
        services.AddSingleton<ICompaniesDatabaseInitializer, CompaniesDatabaseInitializer>();
        return services;
    }
}
