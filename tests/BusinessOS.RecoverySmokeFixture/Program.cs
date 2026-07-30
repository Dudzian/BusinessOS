using System.Text.Json;
using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Companies.Domain;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 3 || args[1] != "--root") throw new ArgumentException("Usage: <prepare-ready|prepare-startup-failure|validate-restored> --root <directory>");
var command = args[0];
var root = Path.GetFullPath(args[2]);
var dataDirectory = Path.Combine(root, "data");
var backupDirectory = Path.Combine(root, "backups");
var databasePath = Path.Combine(dataDirectory, "businessos.db");
Directory.CreateDirectory(root);

if (command == "validate-restored")
{
    await using var validationProvider = Services(databasePath, backupDirectory);
    var factory = validationProvider.GetRequiredService<IDbContextFactory<CompaniesDbContext>>();
    await using var context = await factory.CreateDbContextAsync();
    var company = await context.Companies.SingleAsync();
    if (company.DisplayName != "Selected Backup Company" || company.Version.Value != 1) throw new InvalidDataException("Restored Company does not match the selected backup.");
    await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
    await connection.OpenAsync();
    await using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
    if (!string.Equals((string?)await check.ExecuteScalarAsync(), "ok", StringComparison.Ordinal)) throw new InvalidDataException("quick_check failed.");
    Write(new { DatabasePath = databasePath, CompanyDisplayName = company.DisplayName, ConcurrencyToken = company.Version.Value, QuickCheck = "ok" });
    return;
}

Directory.CreateDirectory(dataDirectory);
Directory.CreateDirectory(backupDirectory);
await using (var provider = Services(databasePath, backupDirectory))
{
    var factory = provider.GetRequiredService<IDbContextFactory<CompaniesDbContext>>();
    await using var context = await factory.CreateDbContextAsync();
    await context.Database.MigrateAsync();
    context.Companies.Add(Create("Selected Backup Company"));
    await context.SaveChangesAsync();
    var backup = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default);
    if (!backup.Succeeded || backup.BackupPath is null) throw new InvalidDataException("Fixture backup failed.");
    var backupId = Path.GetFileName(backup.BackupPath);

    await context.Database.EnsureDeletedAsync();
    if (command == "prepare-ready")
    {
        await context.Database.MigrateAsync();
        context.Companies.Add(Create("Current Live Company"));
        await context.SaveChangesAsync();
        var invalidId = CompaniesBackupFileName.Create(DateTimeOffset.UtcNow.AddSeconds(1), Guid.NewGuid());
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, invalidId), "not a sqlite database");
        Write(new { DatabasePath = databasePath, BackupDirectory = backupDirectory, BackupId = backupId, InvalidBackupId = invalidId, ExpectedValidBackupCount = 1, ExpectedInvalidBackupCount = 1, ExpectedConcurrencyToken = 1L });
        return;
    }

    if (command == "prepare-startup-failure")
    {
        var invalidId = CompaniesBackupFileName.Create(DateTimeOffset.UtcNow.AddSeconds(1), Guid.NewGuid());
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, invalidId), "not a sqlite database");
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        await File.WriteAllTextAsync(dataDirectory, "blocked database directory");
        Write(new { DatabasePath = databasePath, BlockedPath = dataDirectory, BackupDirectory = backupDirectory, BackupId = backupId, InvalidBackupId = invalidId, ExpectedValidBackupCount = 1, ExpectedInvalidBackupCount = 1, ExpectedConcurrencyToken = 1L });
        return;
    }
}
throw new ArgumentException($"Unknown command: {command}");

static ServiceProvider Services(string databasePath, string backupDirectory)
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddCompaniesPersistence(options => { options.DatabasePath = databasePath; options.BackupDirectory = backupDirectory; options.MaxBackups = 10; });
    return services.BuildServiceProvider();
}
static Company Create(string displayName) => Company.Create(OrganizationId.New(), displayName + " Legal", displayName, "5260250995", "PL", CurrencyCode.Pln, "Europe/Warsaw", UserId.New(), DateTimeOffset.UtcNow);
static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
