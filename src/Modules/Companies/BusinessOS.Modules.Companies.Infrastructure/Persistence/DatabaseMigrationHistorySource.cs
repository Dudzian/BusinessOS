using Microsoft.EntityFrameworkCore;
namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public interface IDatabaseMigrationHistorySource
{
    string HistoryTable { get; }
    bool IsRequired { get; }
    IReadOnlyList<string> KnownMigrations { get; }
}
internal sealed class CompaniesMigrationHistorySource(IDbContextFactory<CompaniesDbContext> factory) : IDatabaseMigrationHistorySource
{
    public string HistoryTable => "__EFMigrationsHistory_Companies";
    public bool IsRequired => true;
    public IReadOnlyList<string> KnownMigrations { get { using var db = factory.CreateDbContext(); return db.Database.GetMigrations().ToArray(); } }
}
