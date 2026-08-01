using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BusinessOS.Modules.Companies.Application;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public static class CompaniesPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddCompaniesPersistence(this IServiceCollection services, Action<CompaniesPersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new CompaniesPersistenceOptions();
        configure(options);
        options.GetNormalizedDatabasePath();
        options.GetNormalizedBackupDirectory();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<CompaniesDbContext>(builder => builder.UseSqlite(
            options.BuildConnectionString(),
            sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory_Companies")));
        services.AddTransient<ICompaniesStore, CompaniesStore>();
        services.AddSingleton<ICompaniesDatabaseInitializer, CompaniesDatabaseInitializer>();
        services.AddSingleton<ICompaniesMigrationInspector, CompaniesMigrationInspector>();
        services.AddSingleton<ICompaniesDatabaseBackupService, CompaniesDatabaseBackupService>();
        services.AddSingleton<CompaniesBackupValidator>();
        services.AddSingleton<ICompaniesBackupFileOperations, CompaniesBackupFileOperations>();
        services.AddSingleton<ICompaniesBackupCatalog, CompaniesBackupCatalog>();
        services.AddSingleton<ICompaniesRestoreFileOperations, CompaniesRestoreFileOperations>();
        services.AddSingleton<ICompaniesDatabaseMaintenance, CompaniesDatabaseMaintenance>();
        services.AddSingleton<ICompaniesRestoreStager, CompaniesRestoreStager>();
        services.AddSingleton<ICompaniesDatabaseRestoreService, CompaniesDatabaseRestoreService>();
        return services;
    }
}
