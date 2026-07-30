using BusinessOS.AppHost;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class CompaniesRecoveryWorkflowTests
{
    [Fact]
    public async Task Catalog_maps_valid_and_invalid_entries_without_exposing_paths()
    {
        var created = DateTimeOffset.UtcNow;
        var catalog = new StubCatalog(new(true, CompaniesBackupCatalogFailureCode.None,
        [
            new("valid.db", "ignored-path.db", created, 2048, CompaniesBackupValidationStatus.Valid, CompaniesBackupValidationFailureCode.None),
            new("invalid.db", "ignored-path.db", created, 0, CompaniesBackupValidationStatus.Invalid, CompaniesBackupValidationFailureCode.IntegrityCheckFailed),
        ]));
        var workflow = Create(catalog, new StubRestore(Success()));

        var result = await workflow.LoadCatalogAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Backups.Should().HaveCount(2);
        result.Backups[0].IsRestorable.Should().BeTrue();
        result.Backups[1].StatusCode.Should().Be(CompaniesRecoveryBackupStatusCode.Corrupted);
        PublicMessages([result.UserMessage, .. result.Backups.Select(x => x.StatusText)]);
    }

    [Theory]
    [InlineData(CompaniesBackupCatalogFailureCode.BackupDirectoryNotFound, CompaniesRecoveryFailureCode.Missing)]
    [InlineData(CompaniesBackupCatalogFailureCode.BackupDirectoryUnavailable, CompaniesRecoveryFailureCode.Unavailable)]
    [InlineData(CompaniesBackupCatalogFailureCode.EnumerationFailed, CompaniesRecoveryFailureCode.Unavailable)]
    [InlineData(CompaniesBackupCatalogFailureCode.UnexpectedFailure, CompaniesRecoveryFailureCode.Unavailable)]
    public async Task Catalog_failures_are_safe(CompaniesBackupCatalogFailureCode source, CompaniesRecoveryFailureCode expected)
    {
        var result = await Create(new StubCatalog(new(false, source, [])), new StubRestore(Success())).LoadCatalogAsync(CancellationToken.None);
        AssertFailure(result.Succeeded, result.FailureCode, expected, result.UserMessage, result.DiagnosticId);
    }

    [Theory]
    [InlineData(CompaniesRestoreFailureCode.RestoreAlreadyInProgress, CompaniesRecoveryFailureCode.AlreadyInProgress)]
    [InlineData(CompaniesRestoreFailureCode.SafetyBackupFailed, CompaniesRecoveryFailureCode.SafetyBackupFailed)]
    [InlineData(CompaniesRestoreFailureCode.DatabaseCheckpointFailed, CompaniesRecoveryFailureCode.DatabaseBusy)]
    [InlineData(CompaniesRestoreFailureCode.DatabaseNotQuiescent, CompaniesRecoveryFailureCode.DatabaseBusy)]
    [InlineData(CompaniesRestoreFailureCode.DatabaseSidecarCleanupFailed, CompaniesRecoveryFailureCode.ReplacementFailed)]
    [InlineData(CompaniesRestoreFailureCode.DatabaseReplacementFailed, CompaniesRecoveryFailureCode.ReplacementFailed)]
    [InlineData(CompaniesRestoreFailureCode.PostRestoreValidationFailed, CompaniesRecoveryFailureCode.ValidationFailed)]
    [InlineData(CompaniesRestoreFailureCode.FailedInstallCleanupFailed, CompaniesRecoveryFailureCode.InvalidInstallCleanupFailed)]
    [InlineData(CompaniesRestoreFailureCode.RequiredCleanupFailed, CompaniesRecoveryFailureCode.CleanupFailed)]
    [InlineData(CompaniesRestoreFailureCode.RollbackFailed, CompaniesRecoveryFailureCode.RollbackFailed)]
    [InlineData(CompaniesRestoreFailureCode.RecoveryStateUnknown, CompaniesRecoveryFailureCode.RecoveryStateUnknown)]
    [InlineData(CompaniesRestoreFailureCode.UnexpectedFailure, CompaniesRecoveryFailureCode.UnexpectedFailure)]
    public async Task Restore_failures_are_safe(CompaniesRestoreFailureCode source, CompaniesRecoveryFailureCode expected)
    {
        var restore = new StubRestore(Success() with { Succeeded = false, FailureCode = source });
        var result = await Create(new StubCatalog(new(true, CompaniesBackupCatalogFailureCode.None, [])), restore).RestoreAsync("canonical-id.db", CancellationToken.None);
        AssertFailure(result.Succeeded, result.FailureCode, expected, result.UserMessage, result.DiagnosticId);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var workflow = Create(new CancellingCatalog(), new StubRestore(Success()));
        await FluentActions.Invoking(() => workflow.LoadCatalogAsync(cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Restore_cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var workflow = Create(new StubCatalog(new(true, CompaniesBackupCatalogFailureCode.None, [])), new CancellingRestore());
        await FluentActions.Invoking(() => workflow.RestoreAsync("id", cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cleanup_failures_have_distinct_truthful_presentations()
    {
        var catalog = new StubCatalog(new(true, CompaniesBackupCatalogFailureCode.None, []));
        var rollbackCleanup = await Create(catalog, new StubRestore(Success() with { Succeeded = false, FailureCode = CompaniesRestoreFailureCode.RequiredCleanupFailed })).RestoreAsync("id", default);
        var invalidInstallCleanup = await Create(catalog, new StubRestore(Success() with { Succeeded = false, FailureCode = CompaniesRestoreFailureCode.FailedInstallCleanupFailed })).RestoreAsync("id", default);

        rollbackCleanup.FailureCode.Should().Be(CompaniesRecoveryFailureCode.CleanupFailed);
        rollbackCleanup.UserMessage.Should().Contain("Poprzednia baza została przywrócona");
        invalidInstallCleanup.FailureCode.Should().Be(CompaniesRecoveryFailureCode.InvalidInstallCleanupFailed);
        invalidInstallCleanup.UserMessage.Should().Contain("Nie udało się usunąć nieprawidłowo przywróconej bazy");
        invalidInstallCleanup.UserMessage.Should().NotContain("Poprzednia baza została przywrócona");
        rollbackCleanup.DiagnosticId.Should().NotBeNullOrWhiteSpace();
        invalidInstallCleanup.DiagnosticId.Should().NotBeNullOrWhiteSpace();
    }

    private static CompaniesRecoveryWorkflow Create(ICompaniesBackupCatalog catalog, ICompaniesDatabaseRestoreService restore) =>
        new(catalog, restore, NullLogger<CompaniesRecoveryWorkflow>.Instance);
    private static CompaniesRestoreResult Success() => new(true, CompaniesRestoreFailureCode.None, "id", true, true, "/secret/safety.db", true, false, false);
    private static void AssertFailure(bool succeeded, CompaniesRecoveryFailureCode actual, CompaniesRecoveryFailureCode expected, string message, string? id)
    { succeeded.Should().BeFalse(); actual.Should().Be(expected); id.Should().NotBeNullOrWhiteSpace(); PublicMessages(message); }
    private static void PublicMessages(params string[] messages) => string.Join('|', messages).Should().NotContainAny("Data Source=", "System.", "Microsoft.Data.Sqlite", ".db-wal", ".db-shm", "/home/", "C:\\");

    private sealed class StubCatalog(CompaniesBackupCatalogResult result) : ICompaniesBackupCatalog
    { public Task<CompaniesBackupCatalogResult> ListAsync(CancellationToken cancellationToken) => Task.FromResult(result); public Task<CompaniesBackupValidationResult> ValidateAsync(string backupId, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class CancellingCatalog : ICompaniesBackupCatalog
    { public Task<CompaniesBackupCatalogResult> ListAsync(CancellationToken cancellationToken) => Task.FromCanceled<CompaniesBackupCatalogResult>(cancellationToken); public Task<CompaniesBackupValidationResult> ValidateAsync(string backupId, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class StubRestore(CompaniesRestoreResult result) : ICompaniesDatabaseRestoreService
    { public Task<CompaniesRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken) => Task.FromResult(result); }
    private sealed class CancellingRestore : ICompaniesDatabaseRestoreService
    { public Task<CompaniesRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken) => Task.FromCanceled<CompaniesRestoreResult>(cancellationToken); }
}
