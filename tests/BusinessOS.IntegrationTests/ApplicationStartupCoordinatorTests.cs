using BusinessOS.AppHost;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class ApplicationStartupCoordinatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"businessos-startup-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(root, "data", "businessos.db");
    private string BackupDirectory => Path.Combine(root, "backups");

    [Fact]
    public async Task First_startup_migrates_a_new_database_without_creating_a_backup()
    {
        await using var services = CreateServices();
        var coordinator = services.GetRequiredService<IApplicationStartupCoordinator>();
        var result = await coordinator.InitializeAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.DatabaseWasCreated.Should().BeTrue();
        result.BackupCreated.Should().BeFalse();
        (await TableExists(DatabasePath, "companies")).Should().BeTrue();
        (await TableExists(DatabasePath, "business_projects")).Should().BeTrue();
        Directory.Exists(BackupDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Current_database_starts_without_creating_a_backup()
    {
        await using var services = CreateServices();
        var coordinator = services.GetRequiredService<IApplicationStartupCoordinator>();
        (await coordinator.InitializeAsync(CancellationToken.None)).Succeeded.Should().BeTrue();

        var second = await coordinator.InitializeAsync(CancellationToken.None);

        second.Succeeded.Should().BeTrue();
        second.MigrationsApplied.Should().BeFalse();
        second.BackupCreated.Should().BeFalse();
    }

    [Fact]
    public async Task Existing_database_with_pending_migration_is_backed_up_before_migration()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using (var connection = await Open(DatabasePath))
        {
            var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE pre_migration_marker(value TEXT NOT NULL); INSERT INTO pre_migration_marker VALUES ('before');";
            await command.ExecuteNonQueryAsync();
        }

        await using var services = CreateServices();
        var result = await services.GetRequiredService<IApplicationStartupCoordinator>().InitializeAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.BackupCreated.Should().BeTrue();
        File.Exists(result.BackupPath).Should().BeTrue();
        (await TableExists(result.BackupPath!, "pre_migration_marker")).Should().BeTrue();
        (await TableExists(result.BackupPath!, "companies")).Should().BeFalse();
        (await TableExists(DatabasePath, "companies")).Should().BeTrue();
        (await QuickCheck(result.BackupPath!)).Should().Be("ok");
    }

    [Fact]
    public async Task Backup_retention_keeps_only_configured_newest_backups_and_unrelated_files()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using (var connection = await Open(DatabasePath))
        {
            var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE marker(value TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        Directory.CreateDirectory(BackupDirectory);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(BackupDirectory, $"businessos-companies-2026072{i}T120000000Z-{new string((char)('a' + i), 32)}.db"), "old");
        }
        var unrelated = Path.Combine(BackupDirectory, "notes.db");
        File.WriteAllText(unrelated, "keep");

        await using var services = CreateServices(3, new FixedTimeProvider(DateTimeOffset.Parse("2020-01-01T00:00:00Z")));
        var backup = services.GetRequiredService<ICompaniesDatabaseBackupService>();
        var result = await backup.CreateBackupAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var retained = Directory.GetFiles(BackupDirectory, "businessos-companies-*.db");
        retained.Should().HaveCount(3).And.Contain(result.BackupPath!);
        retained.Select(Path.GetFileName).Should().Contain([
            $"businessos-companies-20260724T120000000Z-{new string('e', 32)}.db",
            $"businessos-companies-20260723T120000000Z-{new string('d', 32)}.db",
        ]);
        File.Exists(unrelated).Should().BeTrue();
        (await QuickCheck(result.BackupPath!)).Should().Be("ok");
        Directory.GetFiles(BackupDirectory, "*.tmp*").Should().BeEmpty();
    }

    [Fact]
    public async Task Temporary_backup_file_can_be_moved_and_deleted_after_quick_check()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using (var connection = await Open(DatabasePath))
        {
            var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE marker(value TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        await using var services = CreateServices();
        var result = await services.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        Directory.GetFiles(BackupDirectory, "*.tmp*").Should().BeEmpty();
        (await QuickCheck(result.BackupPath!)).Should().Be("ok");
        File.Delete(result.BackupPath!);
        File.Exists(result.BackupPath).Should().BeFalse();
    }

    [Fact]
    public async Task Backup_retention_with_MaxBackups_one_keeps_only_current_backup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using (var connection = await Open(DatabasePath))
        {
            var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE marker(value TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        Directory.CreateDirectory(BackupDirectory);
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(BackupDirectory, $"businessos-companies-2026072{i}T120000000Z-{new string((char)('a' + i), 32)}.db"), "old");
        }

        await using var services = CreateServices(1, new FixedTimeProvider(DateTimeOffset.Parse("2020-01-01T00:00:00Z")));
        var result = await services.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(CancellationToken.None);

        Directory.GetFiles(BackupDirectory, "businessos-companies-*.db").Should().Equal(result.BackupPath!);
        Directory.GetFiles(BackupDirectory, "*.tmp*").Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_can_be_disposed_before_complete_startup_directory_is_deleted()
    {
        var isolatedRoot = Path.Combine(Path.GetTempPath(), $"businessos-lifetime-{Guid.NewGuid():N}");
        var database = Path.Combine(isolatedRoot, "data", "businessos.db");
        var backups = Path.Combine(isolatedRoot, "backups");
        try
        {
            await using (var services = CreateServicesFor(database, backups, 3, TimeProvider.System))
            {
                var coordinator = services.GetRequiredService<IApplicationStartupCoordinator>();
                (await coordinator.InitializeAsync(CancellationToken.None)).Succeeded.Should().BeTrue();
                (await coordinator.InitializeAsync(CancellationToken.None)).Succeeded.Should().BeTrue();
            }

            var delete = () => Directory.Delete(isolatedRoot, true);
            delete.Should().NotThrow<IOException>().And.NotThrow<UnauthorizedAccessException>();
            Directory.Exists(isolatedRoot).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true);
        }
    }

    [Fact]
    public async Task BusinessProjects_registration_disables_sqlite_connection_pooling()
    {
        var databasePath = Path.Combine(root, "pooling", "businessos.db");
        var services = new ServiceCollection();
        services.AddBusinessProjectsPersistence(databasePath);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<BusinessProjectsDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        var builder = new SqliteConnectionStringBuilder(
            context.Database.GetDbConnection().ConnectionString);

        builder.DataSource.Should().Be(databasePath);
        builder.Pooling.Should().BeFalse();
    }

    private ServiceProvider CreateServices(int maxBackups = 10, TimeProvider? timeProvider = null) =>
        CreateServicesFor(DatabasePath, BackupDirectory, maxBackups, timeProvider ?? TimeProvider.System);

    private static ServiceProvider CreateServicesFor(string databasePath, string backupDirectory, int maxBackups, TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCompaniesPersistence(options =>
        {
            options.DatabasePath = databasePath;
            options.BackupDirectory = backupDirectory;
            options.MaxBackups = maxBackups;
            options.Pooling = false;
        });
        services.AddBusinessProjectsPersistence(databasePath);
        services.AddSingleton(timeProvider);
        services.AddSingleton<IApplicationStartupCoordinator, ApplicationStartupCoordinator>();
        return services.BuildServiceProvider();
    }

    private static async Task<SqliteConnection> Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> TableExists(string path, string table)
    {
        await using var connection = await Open(path);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string?> QuickCheck(string path)
    {
        await using var connection = await Open(path);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return (string?)await command.ExecuteScalarAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
