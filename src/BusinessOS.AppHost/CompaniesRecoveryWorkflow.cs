using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace BusinessOS.AppHost;

public enum CompaniesRecoveryFailureCode
{
    None, InvalidIdentifier, Missing, Empty, Corrupted, NotCompaniesDatabase, NewerUnsupportedVersion,
    Unavailable, AlreadyInProgress, SafetyBackupFailed, DatabaseBusy, ReplacementFailed,
    ValidationFailed, CleanupFailed, InvalidInstallCleanupFailed, RollbackFailed, RecoveryStateUnknown, UnexpectedFailure,
}

public enum CompaniesRecoveryBackupStatusCode
{
    Valid, InvalidIdentifier, Missing, Empty, Corrupted, NotCompaniesDatabase, NewerUnsupportedVersion, Unavailable, UnexpectedFailure,
}

public sealed record CompaniesRecoveryBackup(
    string BackupId, DateTimeOffset CreatedAtUtc, long SizeBytes, bool IsRestorable,
    CompaniesRecoveryBackupStatusCode StatusCode, string StatusText);

public sealed record CompaniesRecoveryCatalogResult(
    bool Succeeded, CompaniesRecoveryFailureCode FailureCode, IReadOnlyList<CompaniesRecoveryBackup> Backups,
    string UserMessage, string? DiagnosticId);

public sealed record CompaniesRecoveryRestoreResult(
    bool Succeeded, CompaniesRecoveryFailureCode FailureCode, string UserMessage, string? DiagnosticId);

public interface ICompaniesRecoveryWorkflow
{
    Task<CompaniesRecoveryCatalogResult> LoadCatalogAsync(CancellationToken cancellationToken);
    Task<CompaniesRecoveryRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken);
}

public sealed class CompaniesRecoveryWorkflow(
    ICompaniesBackupCatalog catalog,
    ICompaniesDatabaseRestoreService restoreService,
    ILogger<CompaniesRecoveryWorkflow> logger) : ICompaniesRecoveryWorkflow
{
    public async Task<CompaniesRecoveryCatalogResult> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
            var backups = result.Backups.Select(MapBackup).ToArray();
            if (result.Succeeded)
                return new(true, CompaniesRecoveryFailureCode.None, backups,
                    backups.Length == 0 ? "Nie znaleziono żadnych kopii zapasowych." : "Kopie zapasowe są gotowe.", null);
            return CatalogFailure(result.FailureCode, backups);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var id = DiagnosticId();
            logger.LogError(exception, "Unexpected Companies recovery catalog failure; DiagnosticId {DiagnosticId}.", id);
            return new(false, CompaniesRecoveryFailureCode.UnexpectedFailure, [], "Nie udało się odczytać katalogu kopii zapasowych.", id);
        }
    }

    public async Task<CompaniesRecoveryRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await restoreService.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
                return new(true, CompaniesRecoveryFailureCode.None, "Kopia została przywrócona.", null);
            return RestoreFailure(result.FailureCode, backupId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var id = DiagnosticId();
            logger.LogError(exception, "Unexpected Companies restore failure; DiagnosticId {DiagnosticId}; BackupId {BackupId}.", id, backupId);
            return new(false, CompaniesRecoveryFailureCode.UnexpectedFailure, "Nie udało się przywrócić bazy danych.", id);
        }
    }

    private CompaniesRecoveryCatalogResult CatalogFailure(CompaniesBackupCatalogFailureCode code, IReadOnlyList<CompaniesRecoveryBackup> backups)
    {
        var id = DiagnosticId();
        logger.LogWarning("Companies recovery catalog failed with {FailureCode}; DiagnosticId {DiagnosticId}.", code, id);
        var mapped = code == CompaniesBackupCatalogFailureCode.BackupDirectoryNotFound
            ? CompaniesRecoveryFailureCode.Missing : CompaniesRecoveryFailureCode.Unavailable;
        var message = code == CompaniesBackupCatalogFailureCode.BackupDirectoryNotFound
            ? "Nie znaleziono katalogu kopii zapasowych." : "Nie udało się odczytać katalogu kopii zapasowych.";
        return new(false, mapped, backups, message, id);
    }

    private CompaniesRecoveryRestoreResult RestoreFailure(CompaniesRestoreFailureCode code, string backupId)
    {
        var id = DiagnosticId();
        logger.LogWarning("Companies recovery restore failed with {FailureCode}; DiagnosticId {DiagnosticId}; BackupId {BackupId}.", code, id, backupId);
        var (mapped, message) = code switch
        {
            CompaniesRestoreFailureCode.RestoreAlreadyInProgress => (CompaniesRecoveryFailureCode.AlreadyInProgress, "Inne przywracanie jest już w toku."),
            CompaniesRestoreFailureCode.InvalidBackup => (CompaniesRecoveryFailureCode.InvalidIdentifier, "Wybrana kopia nie może zostać przywrócona."),
            CompaniesRestoreFailureCode.BackupNotFound => (CompaniesRecoveryFailureCode.Missing, "Nie znaleziono wybranej kopii zapasowej."),
            CompaniesRestoreFailureCode.IntegrityCheckFailed or CompaniesRestoreFailureCode.StagingFailed => (CompaniesRecoveryFailureCode.Corrupted, "Ta kopia jest uszkodzona i nie może zostać przywrócona."),
            CompaniesRestoreFailureCode.NotCompaniesDatabase => (CompaniesRecoveryFailureCode.NotCompaniesDatabase, "Wybrany plik nie jest kopią bazy Companies."),
            CompaniesRestoreFailureCode.IncompatibleNewerSchema => (CompaniesRecoveryFailureCode.NewerUnsupportedVersion, "Ta kopia została utworzona przez nowszą wersję aplikacji."),
            CompaniesRestoreFailureCode.SafetyBackupFailed => (CompaniesRecoveryFailureCode.SafetyBackupFailed, "Nie udało się utworzyć kopii bezpieczeństwa aktualnej bazy."),
            CompaniesRestoreFailureCode.DatabaseCheckpointFailed or CompaniesRestoreFailureCode.DatabaseNotQuiescent => (CompaniesRecoveryFailureCode.DatabaseBusy, "Aktualna baza jest używana i nie może teraz zostać przywrócona."),
            CompaniesRestoreFailureCode.DatabaseSidecarCleanupFailed or CompaniesRestoreFailureCode.DatabaseReplacementFailed => (CompaniesRecoveryFailureCode.ReplacementFailed, "Nie udało się bezpiecznie zastąpić bazy danych."),
            CompaniesRestoreFailureCode.PostRestoreValidationFailed => (CompaniesRecoveryFailureCode.ValidationFailed, "Nie udało się potwierdzić stanu bazy po operacji."),
            CompaniesRestoreFailureCode.RequiredCleanupFailed => (CompaniesRecoveryFailureCode.CleanupFailed, "Poprzednia baza została przywrócona, ale nie udało się usunąć plików pomocniczych."),
            CompaniesRestoreFailureCode.FailedInstallCleanupFailed => (CompaniesRecoveryFailureCode.InvalidInstallCleanupFailed, "Nie udało się usunąć nieprawidłowo przywróconej bazy. Nie uruchamiaj ponownie aplikacji przed sprawdzeniem diagnostyki."),
            CompaniesRestoreFailureCode.RollbackFailed => (CompaniesRecoveryFailureCode.RollbackFailed, "Nie udało się przywrócić poprzedniej bazy."),
            CompaniesRestoreFailureCode.RecoveryStateUnknown => (CompaniesRecoveryFailureCode.RecoveryStateUnknown, "Nie udało się potwierdzić stanu bazy po operacji."),
            _ => (CompaniesRecoveryFailureCode.UnexpectedFailure, "Nie udało się przywrócić bazy danych."),
        };
        return new(false, mapped, message, id);
    }

    private static CompaniesRecoveryBackup MapBackup(CompaniesBackupDescriptor backup)
    {
        var status = backup.ValidationStatus == CompaniesBackupValidationStatus.Valid
            ? CompaniesRecoveryBackupStatusCode.Valid : backup.FailureCode switch
            {
                CompaniesBackupValidationFailureCode.InvalidBackupId => CompaniesRecoveryBackupStatusCode.InvalidIdentifier,
                CompaniesBackupValidationFailureCode.BackupNotFound => CompaniesRecoveryBackupStatusCode.Missing,
                CompaniesBackupValidationFailureCode.EmptyBackup => CompaniesRecoveryBackupStatusCode.Empty,
                CompaniesBackupValidationFailureCode.IntegrityCheckFailed => CompaniesRecoveryBackupStatusCode.Corrupted,
                CompaniesBackupValidationFailureCode.NotCompaniesDatabase => CompaniesRecoveryBackupStatusCode.NotCompaniesDatabase,
                CompaniesBackupValidationFailureCode.IncompatibleNewerSchema => CompaniesRecoveryBackupStatusCode.NewerUnsupportedVersion,
                CompaniesBackupValidationFailureCode.BackupOpenFailed or CompaniesBackupValidationFailureCode.ReparsePointRejected => CompaniesRecoveryBackupStatusCode.Unavailable,
                _ => CompaniesRecoveryBackupStatusCode.UnexpectedFailure,
            };
        return new(backup.BackupId, backup.CreatedAtUtc, backup.SizeBytes,
            status == CompaniesRecoveryBackupStatusCode.Valid, status, StatusText(status));
    }

    private static string StatusText(CompaniesRecoveryBackupStatusCode status) => status switch
    {
        CompaniesRecoveryBackupStatusCode.Valid => "Gotowa do użycia",
        CompaniesRecoveryBackupStatusCode.Empty => "Kopia jest pusta.",
        CompaniesRecoveryBackupStatusCode.Corrupted => "Kopia jest uszkodzona.",
        CompaniesRecoveryBackupStatusCode.NotCompaniesDatabase => "To nie jest kopia bazy Companies.",
        CompaniesRecoveryBackupStatusCode.NewerUnsupportedVersion => "Kopia pochodzi z nowszej wersji aplikacji.",
        CompaniesRecoveryBackupStatusCode.Missing => "Kopia nie jest już dostępna.",
        _ => "Kopia nie może zostać użyta.",
    };

    private static string DiagnosticId() => Guid.NewGuid().ToString("N");
}
