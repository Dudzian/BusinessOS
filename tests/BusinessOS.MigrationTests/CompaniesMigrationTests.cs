using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace BusinessOS.MigrationTests;

public sealed class CompaniesMigrationTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"businessos-migrations-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Latest_migration_applies_to_a_completely_empty_SQLite_database()
    {
        await Migrate();
        (await TableExists("companies")).Should().BeTrue();
    }

    [Fact]
    public async Task Running_migration_to_latest_version_twice_is_idempotent()
    {
        await Migrate();
        await Migrate();
        (await TableExists("companies")).Should().BeTrue();
    }

    [Fact]
    public async Task Database_has_no_pending_migrations_after_migration()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Initial_migration_creates_companies_table()
    {
        await Migrate();
        (await TableExists("companies")).Should().BeTrue();
    }

    [Fact]
    public async Task Initial_migration_creates_module_specific_migration_history_table()
    {
        await Migrate();
        (await TableExists("__EFMigrationsHistory_Companies")).Should().BeTrue();
    }

    [Fact]
    public async Task Companies_table_contains_every_required_persisted_column_with_expected_schema()
    {
        await Migrate();
        var columns = await ReadCompanyColumns();
        var expected = new[]
        {
            new ColumnSchema("id", "TEXT", true, true),
            new ColumnSchema("organization_id", "TEXT", true, false),
            new ColumnSchema("legal_name", "TEXT", true, false),
            new ColumnSchema("display_name", "TEXT", true, false),
            new ColumnSchema("tax_identification_number", "TEXT", false, false),
            new ColumnSchema("country_code", "TEXT", true, false),
            new ColumnSchema("base_currency", "TEXT", true, false),
            new ColumnSchema("default_time_zone", "TEXT", true, false),
            new ColumnSchema("status", "TEXT", true, false),
            new ColumnSchema("created_at", "TEXT", true, false),
            new ColumnSchema("updated_at", "TEXT", true, false),
            new ColumnSchema("created_by", "TEXT", true, false),
            new ColumnSchema("updated_by", "TEXT", true, false),
            new ColumnSchema("version", "INTEGER", true, false),
            new ColumnSchema("is_deleted", "INTEGER", true, false),
        };

        columns.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Migration_creates_expected_indexes()
    {
        await Migrate();
        var indexes = await QueryStrings("PRAGMA index_list('companies')", "name");
        indexes.Should().Contain(new[]
        {
            "ix_companies_organization_id",
            "ix_companies_status",
            "ix_companies_is_deleted",
            "ix_companies_organization_id_is_deleted",
        });
    }

    [Fact]
    public async Task Migration_can_be_reverted_to_zero_and_applied_again()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("0");
        (await TableExists("companies")).Should().BeFalse();
        await db.Database.MigrateAsync();
        (await TableExists("companies")).Should().BeTrue();
    }

    [Fact]
    public void Test_connection_string_disables_pooling()
    {
        new SqliteConnectionStringBuilder(BuildTestConnectionString()).Pooling.Should().BeFalse();
    }

    private async Task Migrate()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    private CompaniesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<CompaniesDbContext>()
            .UseSqlite(BuildTestConnectionString(), sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory_Companies"))
            .Options);

    private string BuildTestConnectionString()
    {
        var baseConnectionString = new CompaniesPersistenceOptions
        {
            DatabasePath = databasePath,
        }.BuildConnectionString();

        return new SqliteConnectionStringBuilder(baseConnectionString)
        {
            Pooling = false,
        }.ToString();
    }

    private async Task<bool> TableExists(string name) =>
        (await QueryStrings("SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name", "name", ("$name", name))).Count == 1;

    private async Task<List<ColumnSchema>> ReadCompanyColumns()
    {
        var columns = new List<ColumnSchema>();
        await using var connection = new SqliteConnection(BuildTestConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('companies')";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new ColumnSchema(
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("type")),
                reader.GetInt32(reader.GetOrdinal("notnull")) == 1,
                reader.GetInt32(reader.GetOrdinal("pk")) == 1));
        }

        return columns;
    }

    private async Task<List<string>> QueryStrings(string sql, string column, params (string Name, string Value)[] parameters)
    {
        var values = new List<string>();
        await using var connection = new SqliteConnection(BuildTestConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(reader.GetOrdinal(column)));
        }

        return values;
    }

    private IEnumerable<string> DatabaseFiles() => new[] { databasePath, databasePath + "-shm", databasePath + "-wal" };

    public void Dispose()
    {
        foreach (var path in DatabaseFiles())
        {
            File.Delete(path);
        }
    }

    private sealed record ColumnSchema(string Name, string Type, bool NotNull, bool PrimaryKey);
}
