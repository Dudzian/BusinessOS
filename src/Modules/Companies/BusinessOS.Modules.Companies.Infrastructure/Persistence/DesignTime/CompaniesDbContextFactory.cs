using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence.DesignTime;

public sealed class CompaniesDbContextFactory : IDesignTimeDbContextFactory<CompaniesDbContext>
{
    public CompaniesDbContext CreateDbContext(string[] args)
    {
        var databasePath = Environment.GetEnvironmentVariable("BUSINESSOS_EF_DATABASE_PATH");
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(FindRepositoryRoot(), ".cache", "ef", "companies-design-time.db");
        }

        var options = new CompaniesPersistenceOptions { DatabasePath = databasePath };
        options.EnsureDatabaseDirectory();
        var builder = new DbContextOptionsBuilder<CompaniesDbContext>();
        builder.UseSqlite(options.BuildConnectionString(), sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory_Companies"));
        return new CompaniesDbContext(builder.Options);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BusinessOS.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
