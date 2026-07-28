using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class CompaniesBackupCatalogSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "businessos-catalog-" + Guid.NewGuid().ToString("N"));
    private string BackupDirectory => Path.Combine(root, "backups");

    [Theory]
    [InlineData(new string[0], new[] { "A", "B" }, true)]
    [InlineData(new[] { "A" }, new[] { "A", "B" }, true)]
    [InlineData(new[] { "A", "B" }, new[] { "A", "B" }, true)]
    [InlineData(new[] { "B" }, new[] { "A", "B" }, false)]
    [InlineData(new[] { "B", "A" }, new[] { "A", "B" }, false)]
    [InlineData(new[] { "A", "X" }, new[] { "A", "B" }, false)]
    [InlineData(new[] { "A", "A" }, new[] { "A", "B" }, false)]
    [InlineData(new[] { "A", "B", "C" }, new[] { "A", "B" }, false)]
    public void Migration_history_prefix_algorithm_is_ordinal_and_complete(string[] applied, string[] known, bool expected) =>
        CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(applied, known).Should().Be(expected);

    [Fact] public void Migration_history_accepts_empty_prefix() => CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix([], ["A", "B"]).Should().BeTrue();
    [Fact] public void Migration_history_accepts_ordered_prefix() => CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(["A"], ["A", "B"]).Should().BeTrue();
    [Fact] public void Migration_history_rejects_missing_first_migration() => CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(["B"], ["A", "B"]).Should().BeFalse();
    [Fact] public void Migration_history_rejects_reversed_migrations() => CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(["B", "A"], ["A", "B"]).Should().BeFalse();
    [Fact] public void Migration_history_rejects_unknown_migration() => CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(["A", "X"], ["A", "B"]).Should().BeFalse();
    [Fact] public void Migration_history_rejects_duplicate_migration() => CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(["A", "A"], ["A", "B"]).Should().BeFalse();
    [Fact] public void Migration_history_rejects_history_longer_than_known_set() => CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(["A", "B", "C"], ["A", "B"]).Should().BeFalse();

    [Fact]
    public async Task Catalog_returns_controlled_failure_when_directory_enumeration_starts_with_io_error()
    {
        using var provider = Services(new FaultCatalogFiles { Enumerate = _ => throw new IOException("start") });
        var result = await provider.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default);
        result.Succeeded.Should().BeFalse(); result.FailureCode.Should().Be(CompaniesBackupCatalogFailureCode.EnumerationFailed);
    }

    [Fact]
    public async Task Catalog_returns_controlled_failure_when_enumerator_fails_during_iteration()
    {
        using var provider = Services(new FaultCatalogFiles { Enumerate = _ => FailingEnumeration() });
        var result = await provider.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default);
        result.Succeeded.Should().BeFalse(); result.FailureCode.Should().Be(CompaniesBackupCatalogFailureCode.EnumerationFailed);
    }

    [Fact]
    public async Task Catalog_marks_candidate_invalid_when_file_disappears_after_enumeration()
    {
        var path = CandidatePath(); using var provider = Services(new FaultCatalogFiles { Enumerate = _ => [path], Exists = _ => false });
        var result = await provider.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default);
        result.Succeeded.Should().BeTrue(); result.Backups.Single().FailureCode.Should().Be(CompaniesBackupValidationFailureCode.BackupNotFound);
    }

    [Fact]
    public async Task Catalog_marks_candidate_invalid_when_attribute_read_fails()
    {
        var path = CandidatePath(); using var provider = Services(new FaultCatalogFiles { Enumerate = _ => [path], Attributes = _ => throw new IOException("attributes") });
        var result = await provider.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default);
        result.Succeeded.Should().BeTrue(); result.Backups.Single().FailureCode.Should().Be(CompaniesBackupValidationFailureCode.BackupOpenFailed);
    }

    [Fact]
    public async Task Catalog_marks_candidate_invalid_when_length_read_is_unauthorized()
    {
        var path = CandidatePath(); using var provider = Services(new FaultCatalogFiles { Enumerate = _ => [path], Length = _ => throw new UnauthorizedAccessException("length") });
        var result = await provider.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default);
        result.Succeeded.Should().BeTrue(); result.Backups.Single().FailureCode.Should().Be(CompaniesBackupValidationFailureCode.BackupOpenFailed);
    }

    [Fact]
    public async Task Catalog_distinguishes_empty_directory_from_unavailable_directory()
    {
        using var empty = Services(new FaultCatalogFiles()); var emptyResult = await empty.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default);
        emptyResult.Succeeded.Should().BeTrue(); emptyResult.Backups.Should().BeEmpty();
        using var unavailable = Services(new FaultCatalogFiles { DirectoryCheck = _ => throw new UnauthorizedAccessException() });
        var unavailableResult = await unavailable.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default);
        unavailableResult.Succeeded.Should().BeFalse(); unavailableResult.FailureCode.Should().Be(CompaniesBackupCatalogFailureCode.BackupDirectoryUnavailable);
    }

    [Fact]
    public async Task Validation_handles_file_removed_during_resolution()
    {
        var path = CandidatePath(); using var provider = Services(new FaultCatalogFiles { Exists = _ => false });
        var result = await provider.GetRequiredService<ICompaniesBackupCatalog>().ValidateAsync(Path.GetFileName(path), default);
        result.FailureCode.Should().Be(CompaniesBackupValidationFailureCode.BackupNotFound);
    }

    private ServiceProvider Services(ICompaniesBackupFileOperations operations)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddCompaniesPersistence(o => { o.DatabasePath = Path.Combine(root, "data.db"); o.BackupDirectory = BackupDirectory; o.Pooling = false; });
        services.AddSingleton(operations); return services.BuildServiceProvider();
    }

    private string CandidatePath() => Path.Combine(BackupDirectory, CompaniesBackupFileName.Create(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Guid.NewGuid()));
    private static IEnumerable<string> FailingEnumeration() { yield return "foreign"; throw new IOException("MoveNext"); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class FaultCatalogFiles : ICompaniesBackupFileOperations
    {
        public Func<string, bool> DirectoryCheck { get; init; } = _ => true;
        public Func<string, IEnumerable<string>> Enumerate { get; init; } = _ => [];
        public Func<string, bool> Exists { get; init; } = _ => true;
        public Func<string, FileAttributes> Attributes { get; init; } = _ => FileAttributes.Normal;
        public Func<string, long> Length { get; init; } = _ => 1;
        public bool DirectoryExists(string path) => DirectoryCheck(path);
        public IEnumerable<string> EnumerateFiles(string path) => Enumerate(path);
        public bool FileExists(string path) => Exists(path);
        public FileAttributes GetAttributes(string path) => Attributes(path);
        public long GetLength(string path) => Length(path);
    }
}
