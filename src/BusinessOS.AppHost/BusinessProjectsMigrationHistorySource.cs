using BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace BusinessOS.AppHost;

internal sealed class BusinessProjectsMigrationHistorySource(IDbContextFactory<BusinessProjectsDbContext> factory) : IDatabaseMigrationHistorySource
{
    public string HistoryTable => "__EFMigrationsHistory_BusinessProjects";
    public bool IsRequired => false;
    public IReadOnlyList<string> KnownMigrations { get { using var db = factory.CreateDbContext(); return db.Database.GetMigrations().ToArray(); } }
}
