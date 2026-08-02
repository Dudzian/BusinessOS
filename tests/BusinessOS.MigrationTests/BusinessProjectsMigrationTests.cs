using BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace BusinessOS.MigrationTests;

public sealed class BusinessProjectsMigrationTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"businessos-project-migrations-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Migration_creates_complete_schema_and_independent_history()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        (await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='business_projects'")).Should().Be(1);
        (await Strings("PRAGMA table_info('business_projects')", "name")).Should().HaveCount(16);
        (await Strings("PRAGMA index_list('business_projects')", "name")).Should().Contain(["ix_business_projects_company_id", "ix_business_projects_status", "ix_business_projects_planned_opening_date", "ix_business_projects_is_deleted", "ix_business_projects_company_id_is_deleted", "ux_business_projects_company_name_active"]);
        (await Strings("SELECT sql FROM sqlite_master WHERE type='table' AND name='business_projects'", "sql")).Single().Should().Contain("COLLATE NOCASE");
        (await Strings("SELECT sql FROM sqlite_master WHERE type='index' AND name='ux_business_projects_company_name_active'", "sql")).Single().Should().Contain("is_deleted = 0");
        (await Strings("SELECT MigrationId FROM __EFMigrationsHistory_BusinessProjects", "MigrationId")).Should().ContainSingle();
        (await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory_BusinessProjects'")).Should().Be(1);
        var columns = await Strings("PRAGMA table_info('business_projects')", "name");
        columns.Should().Equal("id", "company_id", "name", "business_type", "location", "description", "status", "planned_start_date", "planned_opening_date", "base_currency", "created_at", "updated_at", "created_by", "updated_by", "version", "is_deleted");
        var indexes = await Strings("PRAGMA index_list('business_projects')", "name");
        indexes.Should().Contain(["ix_business_projects_company_id", "ix_business_projects_status", "ix_business_projects_planned_opening_date", "ix_business_projects_is_deleted", "ix_business_projects_company_id_is_deleted", "ux_business_projects_company_name_active"]);
        (await Strings("PRAGMA index_info('ux_business_projects_company_name_active')", "name")).Should().Equal("company_id", "name");
        var sql = (await Strings("SELECT sql FROM sqlite_master WHERE type='table' AND name='business_projects'", "sql")).Single();
        sql.Should().Contain("\"name\" TEXT COLLATE NOCASE");
        var indexSql = (await Strings("SELECT sql FROM sqlite_master WHERE type='index' AND name='ux_business_projects_company_name_active'", "sql")).Single();
        indexSql.Should().Contain("UNIQUE").And.Contain("is_deleted = 0");
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Down_and_second_up_recreate_the_schema()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync("0");
        (await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='business_projects'")).Should().Be(0);
        await db.Database.MigrateAsync();
        (await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='business_projects'")).Should().Be(1);
    }

    [Fact]
    public async Task Column_metadata_dates_status_and_history_are_stable()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        var columns = await Columns();
        columns.Select(column => (column.Name, column.Type, column.NotNull, column.PrimaryKey, column.DefaultValue)).Should().Equal(
            ("id", "TEXT", true, true, (string?)null), ("company_id", "TEXT", true, false, null), ("name", "TEXT", true, false, null),
            ("business_type", "TEXT", true, false, null), ("location", "TEXT", true, false, null), ("description", "TEXT", true, false, null),
            ("status", "TEXT", true, false, null), ("planned_start_date", "TEXT", true, false, null), ("planned_opening_date", "TEXT", true, false, null),
            ("base_currency", "TEXT", true, false, null), ("created_at", "TEXT", true, false, null), ("updated_at", "TEXT", true, false, null),
            ("created_by", "TEXT", true, false, null), ("updated_by", "TEXT", true, false, null), ("version", "INTEGER", true, false, null), ("is_deleted", "INTEGER", true, false, null));
        var id = Guid.NewGuid().ToString();
        await Execute($"INSERT INTO business_projects VALUES ('{id}','{Guid.NewGuid()}','P','Gym','L','', 'Draft','2026-01-02','2026-03-04','PLN','2026-01-01T00:00:00+00:00','2026-01-01T00:00:00+00:00','{Guid.NewGuid()}','{Guid.NewGuid()}',1,0)");
        (await Strings("SELECT planned_start_date || '|' || planned_opening_date || '|' || status || '|' || typeof(version) AS value FROM business_projects", "value")).Should().Equal("2026-01-02|2026-03-04|Draft|integer");
        (await Strings("SELECT MigrationId FROM __EFMigrationsHistory_BusinessProjects", "MigrationId")).Should().Equal("20260801172908_InitialBusinessProjectsPersistence");
        (await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory_Companies'")).Should().Be(0);
        var unique = await Index("ux_business_projects_company_name_active");
        unique.Should().Be((true, "c", true));
    }

    private BusinessProjectsDbContext Context() => new(new DbContextOptionsBuilder<BusinessProjectsDbContext>().UseSqlite(ConnectionString(), sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory_BusinessProjects")).Options);
    private string ConnectionString() => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    private async Task<long> Scalar(string sql) { await using var connection = new SqliteConnection(ConnectionString()); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture); }
    private async Task<List<string>> Strings(string sql, string column) { var result = new List<string>(); await using var connection = new SqliteConnection(ConnectionString()); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(reader.GetString(reader.GetOrdinal(column))); return result; }
    private async Task Execute(string sql) { await using var connection = new SqliteConnection(ConnectionString()); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    private async Task<List<Column>> Columns() { var result = new List<Column>(); await using var connection = new SqliteConnection(ConnectionString()); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA table_info('business_projects')"; await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(new(reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, reader.GetInt32(5) == 1, reader.IsDBNull(4) ? null : reader.GetString(4))); return result; }
    private async Task<(bool Unique, string Origin, bool Partial)> Index(string name) { await using var connection = new SqliteConnection(ConnectionString()); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA index_list('business_projects')"; await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) if (reader.GetString(1) == name) return (reader.GetInt32(2) == 1, reader.GetString(3), reader.GetInt32(4) == 1); throw new InvalidOperationException(); }
    private sealed record Column(string Name, string Type, bool NotNull, bool PrimaryKey, string? DefaultValue);
    public void Dispose() { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
}
