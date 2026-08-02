using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BusinessOS.IntegrationTests;

public sealed class CompaniesBackupTwoHistoryValidationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"businessos-two-history-{Guid.NewGuid():N}");
    private string PathFor(string name) => Path.Combine(root, name + ".db");
    [Theory]
    [InlineData(null, null, CompaniesBackupValidationFailureCode.NotCompaniesDatabase)]
    [InlineData("C1", null, CompaniesBackupValidationFailureCode.None)]
    [InlineData("C1", "", CompaniesBackupValidationFailureCode.None)]
    [InlineData("C1", "P1", CompaniesBackupValidationFailureCode.None)]
    [InlineData("C1,C2", "P1,P2", CompaniesBackupValidationFailureCode.None)]
    [InlineData("C1,X", "P1", CompaniesBackupValidationFailureCode.IncompatibleNewerSchema)]
    [InlineData("C1", "P1,X", CompaniesBackupValidationFailureCode.IncompatibleNewerSchema)]
    [InlineData("C1,C1", "P1", CompaniesBackupValidationFailureCode.IncompatibleNewerSchema)]
    [InlineData("C1", "P1,P1", CompaniesBackupValidationFailureCode.IncompatibleNewerSchema)]
    [InlineData("C2,C1", "P1", CompaniesBackupValidationFailureCode.IncompatibleNewerSchema)]
    [InlineData("C1", "P2,P1", CompaniesBackupValidationFailureCode.IncompatibleNewerSchema)]
    [InlineData("C2", "P1", CompaniesBackupValidationFailureCode.IncompatibleNewerSchema)]
    public async Task Validator_enforces_independent_ordered_prefixes(string? companies, string? projects, CompaniesBackupValidationFailureCode expected)
    {
        Directory.CreateDirectory(root); var path = PathFor(Guid.NewGuid().ToString("N")); await Create(path, companies, projects);
        var options = new CompaniesPersistenceOptions { DatabasePath = PathFor("live"), BackupDirectory = root, Pooling = false };
        var sources = new IDatabaseMigrationHistorySource[] { new Source("__EFMigrationsHistory_Companies", true, ["C1", "C2"]), new Source("__EFMigrationsHistory_BusinessProjects", false, ["P1", "P2"]) };
        var result = await new CompaniesBackupValidator(options, new CompaniesBackupFileOperations(), NullLogger<CompaniesBackupValidator>.Instance, sources).ValidatePathAsync(path, default);
        result.FailureCode.Should().Be(expected); result.Succeeded.Should().Be(expected == CompaniesBackupValidationFailureCode.None);
    }
    [Fact] public async Task Validator_propagates_cancellation() { Directory.CreateDirectory(root); var path = PathFor("cancel"); await Create(path, "C1", "P1"); var options = new CompaniesPersistenceOptions { DatabasePath = PathFor("live"), BackupDirectory = root }; var validator = new CompaniesBackupValidator(options, new CompaniesBackupFileOperations(), NullLogger<CompaniesBackupValidator>.Instance, [new Source("__EFMigrationsHistory_Companies", true, ["C1"])]); await FluentActions.Invoking(() => validator.ValidatePathAsync(path, new(true))).Should().ThrowAsync<OperationCanceledException>(); }
    private static async Task Create(string path, string? companies, string? projects) { await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync(); foreach (var item in new[] { ("__EFMigrationsHistory_Companies", companies), ("__EFMigrationsHistory_BusinessProjects", projects) }) { if (item.Item2 is null) continue; await using var table = connection.CreateCommand(); table.CommandText = $"CREATE TABLE [{item.Item1}](MigrationId TEXT NOT NULL, ProductVersion TEXT NOT NULL);"; await table.ExecuteNonQueryAsync(); foreach (var id in item.Item2.Split(',', StringSplitOptions.RemoveEmptyEntries)) { await using var insert = connection.CreateCommand(); insert.CommandText = $"INSERT INTO [{item.Item1}] VALUES ($id,'10')"; insert.Parameters.AddWithValue("$id", id); await insert.ExecuteNonQueryAsync(); } } }
    private sealed record Source(string HistoryTable, bool IsRequired, IReadOnlyList<string> KnownMigrations) : IDatabaseMigrationHistorySource;
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
