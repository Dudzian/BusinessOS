using BusinessOS.Modules.BusinessProjects.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;

public static class BusinessProjectsPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessProjectsPersistence(this IServiceCollection services, string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        services.AddPooledDbContextFactory<BusinessProjectsDbContext>(
            options => options.UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsHistoryTable(
                    "__EFMigrationsHistory_BusinessProjects")));
        services.AddTransient<IBusinessProjectsStore, BusinessProjectsStore>();
        services.AddSingleton<IBusinessProjectsDatabaseLifecycle>(sp =>
            new BusinessProjectsDatabaseLifecycle(
                sp.GetRequiredService<IDbContextFactory<BusinessProjectsDbContext>>(),
                databasePath));
        return services;
    }
}
