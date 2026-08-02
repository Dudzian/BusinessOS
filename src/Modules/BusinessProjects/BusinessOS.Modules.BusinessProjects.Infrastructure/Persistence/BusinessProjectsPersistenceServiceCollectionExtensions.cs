using BusinessOS.Modules.BusinessProjects.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;

public static class BusinessProjectsPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessProjectsPersistence(this IServiceCollection services, string databasePath)
    { var connection = $"Data Source={databasePath}"; services.AddPooledDbContextFactory<BusinessProjectsDbContext>(o => o.UseSqlite(connection, x => x.MigrationsHistoryTable("__EFMigrationsHistory_BusinessProjects"))); services.AddTransient<IBusinessProjectsStore, BusinessProjectsStore>(); services.AddSingleton<IBusinessProjectsDatabaseLifecycle>(sp => new BusinessProjectsDatabaseLifecycle(sp.GetRequiredService<IDbContextFactory<BusinessProjectsDbContext>>(), databasePath)); return services; }
}
