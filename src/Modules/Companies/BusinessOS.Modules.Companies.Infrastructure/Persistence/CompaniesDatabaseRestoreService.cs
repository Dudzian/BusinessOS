using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public interface ICompaniesDatabaseRestoreService
{
    Task<CompaniesRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken);
}

public enum CompaniesRestoreFailureCode
{
    None, RestoreAlreadyInProgress, InvalidBackup, BackupNotFound, IntegrityCheckFailed, NotCompaniesDatabase,
    IncompatibleNewerSchema, StagingFailed, SafetyBackupFailed, DatabaseSidecarCleanupFailed,
    DatabaseReplacementFailed, PostRestoreValidationFailed, FailedInstallCleanupFailed, RollbackFailed,
    RequiredCleanupFailed, DatabaseCheckpointFailed, DatabaseNotQuiescent, RecoveryStateUnknown, UnexpectedFailure,
}

public sealed record CompaniesRestoreResult(bool Succeeded, CompaniesRestoreFailureCode FailureCode, string BackupId,
    bool CurrentDatabaseExisted, bool SafetyBackupCreated, string? SafetyBackupPath, bool DatabaseReplaced,
    bool RollbackAttempted, bool RollbackSucceeded);

internal interface ICompaniesRestoreFileOperations
{
    bool FileExists(string path);
    long GetLength(string path);
    FileAttributes GetAttributes(string path);
    string ComputeSha256(string path);
    void Delete(string path);
    void Move(string source, string destination, bool overwrite);
    void Replace(string source, string destination, string backup, bool ignoreMetadataErrors);
}

internal sealed class CompaniesRestoreFileOperations : ICompaniesRestoreFileOperations
{
    public bool FileExists(string path) => File.Exists(path);
    public long GetLength(string path) => new FileInfo(path).Length;
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public void Delete(string path) => File.Delete(path);
    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void Replace(string source, string destination, string backup, bool ignoreMetadataErrors) => File.Replace(source, destination, backup, ignoreMetadataErrors);
}

internal sealed record CompaniesDatabaseCheckpointResult(bool Succeeded, bool Busy, int LogFrames, int CheckpointedFrames);

internal interface ICompaniesDatabaseMaintenance
{
    Task<CompaniesDatabaseCheckpointResult> CheckpointAsync(string databasePath, CancellationToken cancellationToken);
}

internal sealed class CompaniesDatabaseMaintenance : ICompaniesDatabaseMaintenance
{
    public async Task<CompaniesDatabaseCheckpointResult> CheckpointAsync(string databasePath, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount != 3)
                return new(false, false, 0, 0);
            var busy = reader.GetInt32(0);
            var log = reader.GetInt32(1);
            var checkpointed = reader.GetInt32(2);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new(false, false, log, checkpointed);
            return new(busy == 0 && log == checkpointed, busy != 0, log, checkpointed);
        }
        finally { SqliteConnection.ClearAllPools(); }
    }
}

internal interface ICompaniesRestoreStager
{
    Task StageAsync(string source, string staging, CancellationToken cancellationToken);
}

internal sealed class CompaniesRestoreStager : ICompaniesRestoreStager
{
    public async Task StageAsync(string source, string staging, CancellationToken cancellationToken)
    {
        await using var input = new SqliteConnection(Connection(source, SqliteOpenMode.ReadOnly));
        await using var output = new SqliteConnection(Connection(staging, SqliteOpenMode.ReadWriteCreate));
        await input.OpenAsync(cancellationToken).ConfigureAwait(false);
        await output.OpenAsync(cancellationToken).ConfigureAwait(false);
        input.BackupDatabase(output);
    }

    private static string Connection(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
}

internal sealed class CompaniesDatabaseRestoreService(
    CompaniesPersistenceOptions options,
    CompaniesBackupValidator validator,
    ICompaniesDatabaseBackupService backupService,
    ICompaniesRestoreFileOperations files,
    ICompaniesDatabaseMaintenance maintenance,
    ICompaniesRestoreStager stager,
    ILogger<CompaniesDatabaseRestoreService> logger) : ICompaniesDatabaseRestoreService
{
    private readonly SemaphoreSlim restoreGate = new(1, 1);

    public async Task<CompaniesRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken)
    {
        if (!await restoreGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(false, CompaniesRestoreFailureCode.RestoreAlreadyInProgress, backupId, false, false, null, false, false, false);
        try { return await RestoreCoreAsync(backupId, cancellationToken).ConfigureAwait(false); }
        finally { restoreGate.Release(); }
    }

    private async Task<CompaniesRestoreResult> RestoreCoreAsync(string backupId, CancellationToken cancellationToken)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        var databasePath = options.GetNormalizedDatabasePath();
        var directory = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("Database path has no directory.");
        var staging = Path.Combine(directory, $".{Path.GetFileName(databasePath)}.restore-{Guid.NewGuid():N}.tmp");
        var rollback = Path.Combine(directory, $".{Path.GetFileName(databasePath)}.rollback-{Guid.NewGuid():N}.db");
        var failed = rollback + ".failed";
        var currentExisted = files.FileExists(databasePath);
        var safetyCreated = false;
        string? safetyPath = null;
        var replacementInvoked = false;
        var databaseReplaced = false;
        var stable = false;
        var preserveRecoveryArtifacts = false;
        string? expectedRestoreFingerprint = null;
        string? originalDatabaseFingerprint = null;

        CompaniesRestoreResult Failure(CompaniesRestoreFailureCode code, bool attempted = false, bool succeeded = false) =>
            new(false, code, backupId, currentExisted, safetyCreated, safetyPath, databaseReplaced, attempted, succeeded);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = validator.Resolve(backupId, out var resolveFailure);
            if (source is null) return Failure(Map(resolveFailure));
            var validation = await validator.ValidatePathAsync(source, cancellationToken).ConfigureAwait(false);
            if (!validation.Succeeded) return Failure(Map(validation.FailureCode));
            Directory.CreateDirectory(directory);
            try { await stager.StageAsync(source, staging, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Companies restore staging failed. DiagnosticId {DiagnosticId}", diagnosticId); return Failure(CompaniesRestoreFailureCode.StagingFailed); }
            validation = await validator.ValidatePathAsync(staging, cancellationToken).ConfigureAwait(false);
            if (!validation.Succeeded) return Failure(CompaniesRestoreFailureCode.StagingFailed);
            expectedRestoreFingerprint = files.ComputeSha256(staging);

            if (currentExisted)
            {
                var safety = await backupService.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
                if (!safety.Succeeded) return Failure(CompaniesRestoreFailureCode.SafetyBackupFailed);
                safetyCreated = true;
                safetyPath = safety.BackupPath;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Cancellation is deferred after this point until replacement, validation, recovery and cleanup reach a stable state.
            if (currentExisted)
            {
                CompaniesDatabaseCheckpointResult checkpoint;
                try { checkpoint = await maintenance.CheckpointAsync(databasePath, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Companies WAL checkpoint failed. DiagnosticId {DiagnosticId}", diagnosticId);
                    return Failure(CompaniesRestoreFailureCode.DatabaseCheckpointFailed);
                }
                if (!checkpoint.Succeeded)
                {
                    logger.LogError("Companies database is not quiescent for restore. Busy {Busy}, LogFrames {LogFrames}, CheckpointedFrames {CheckpointedFrames}, DiagnosticId {DiagnosticId}", checkpoint.Busy, checkpoint.LogFrames, checkpoint.CheckpointedFrames, diagnosticId);
                    return Failure(checkpoint.Busy ? CompaniesRestoreFailureCode.DatabaseNotQuiescent : CompaniesRestoreFailureCode.DatabaseCheckpointFailed);
                }
                originalDatabaseFingerprint = files.ComputeSha256(databasePath);
            }
            SqliteConnection.ClearAllPools();
            try { DeleteSidecars(databasePath); }
            catch (Exception exception) { logger.LogError(exception, "Companies sidecar cleanup failed. DiagnosticId {DiagnosticId}", diagnosticId); return Failure(CompaniesRestoreFailureCode.DatabaseSidecarCleanupFailed); }

            try
            {
                replacementInvoked = true;
                if (currentExisted) files.Replace(staging, databasePath, rollback, true);
                else files.Move(staging, databasePath, false);
                databaseReplaced = true;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Companies replacement reported an ambiguous failure; reconciling files. DiagnosticId {DiagnosticId}", diagnosticId);
                return await ReconcileReplacementFailureAsync().ConfigureAwait(false);
            }

            return await ValidateInstalledAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Unexpected Companies restore failure. DiagnosticId {DiagnosticId}", diagnosticId); return Failure(CompaniesRestoreFailureCode.UnexpectedFailure); }
        finally
        {
            if (!preserveRecoveryArtifacts) TryCleanup(staging);
            if (stable) { TryCleanup(rollback); TryCleanup(failed); }
            else if (!replacementInvoked && !files.FileExists(rollback)) TryCleanup(rollback);
        }

        async Task<CompaniesRestoreResult> ReconcileReplacementFailureAsync()
        {
            SqliteConnection.ClearAllPools();
            var liveExists = files.FileExists(databasePath);
            var rollbackExists = files.FileExists(rollback);
            var stagingExists = files.FileExists(staging);
            if (liveExists && (await validator.ValidatePathAsync(databasePath, CancellationToken.None).ConfigureAwait(false)).Succeeded)
            {
                var liveFingerprint = files.ComputeSha256(databasePath);
                if (string.Equals(liveFingerprint, expectedRestoreFingerprint, StringComparison.Ordinal))
                {
                    databaseReplaced = true;
                    return await FinishSuccessAsync().ConfigureAwait(false);
                }
                if (currentExisted && string.Equals(liveFingerprint, originalDatabaseFingerprint, StringComparison.Ordinal) && !rollbackExists)
                {
                    stable = true;
                    return Failure(CompaniesRestoreFailureCode.DatabaseReplacementFailed);
                }
            }

            if (rollbackExists)
            {
                databaseReplaced = liveExists;
                return await RollbackAsync(CompaniesRestoreFailureCode.DatabaseReplacementFailed).ConfigureAwait(false);
            }

            preserveRecoveryArtifacts = stagingExists || rollbackExists;
            logger.LogCritical("Companies replacement recovery state is unknown; recovery artifacts retained. DiagnosticId {DiagnosticId}", diagnosticId);
            return Failure(CompaniesRestoreFailureCode.RecoveryStateUnknown);
        }

        async Task<CompaniesRestoreResult> ValidateInstalledAsync()
        {
            SqliteConnection.ClearAllPools();
            if ((await validator.ValidatePathAsync(databasePath, CancellationToken.None).ConfigureAwait(false)).Succeeded)
                return await FinishSuccessAsync().ConfigureAwait(false);
            logger.LogError("Companies post-restore validation failed. DiagnosticId {DiagnosticId}", diagnosticId);
            if (currentExisted) return await RollbackAsync(CompaniesRestoreFailureCode.PostRestoreValidationFailed).ConfigureAwait(false);
            try
            {
                SqliteConnection.ClearAllPools();
                DeleteArtifactsRequired(databasePath);
                if (files.FileExists(databasePath)) throw new IOException("Invalid installed database still exists.");
                stable = true;
                return Failure(CompaniesRestoreFailureCode.PostRestoreValidationFailed);
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "Unable to remove invalid Companies installation. DiagnosticId {DiagnosticId}", diagnosticId);
                return Failure(CompaniesRestoreFailureCode.FailedInstallCleanupFailed);
            }
        }

        async Task<CompaniesRestoreResult> RollbackAsync(CompaniesRestoreFailureCode originalFailure)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                DeleteSidecars(databasePath);
                if (files.FileExists(databasePath)) files.Replace(rollback, databasePath, failed, true);
                else files.Move(rollback, databasePath, false);
                SqliteConnection.ClearAllPools();
                if (!(await validator.ValidatePathAsync(databasePath, CancellationToken.None).ConfigureAwait(false)).Succeeded)
                    throw new InvalidDataException("Rollback validation failed.");
                stable = true;
                if (!TryCleanup(failed)) return Failure(CompaniesRestoreFailureCode.RequiredCleanupFailed, true, true);
                logger.LogWarning("Companies rollback succeeded. DiagnosticId {DiagnosticId}", diagnosticId);
                return Failure(originalFailure, true, true);
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "Companies rollback failed. DiagnosticId {DiagnosticId}", diagnosticId);
                return Failure(CompaniesRestoreFailureCode.RollbackFailed, true, false);
            }
        }

        Task<CompaniesRestoreResult> FinishSuccessAsync()
        {
            stable = true;
            if (!TryCleanup(rollback) || !TryCleanup(failed) || !TryCleanup(staging))
                return Task.FromResult(Failure(CompaniesRestoreFailureCode.RequiredCleanupFailed));
            return Task.FromResult(new CompaniesRestoreResult(true, CompaniesRestoreFailureCode.None, backupId, currentExisted,
                safetyCreated, safetyPath, true, false, false));
        }
    }

    private void DeleteSidecars(string path) { files.Delete(path + "-wal"); files.Delete(path + "-shm"); }
    private void DeleteArtifactsRequired(string path) { files.Delete(path + "-wal"); files.Delete(path + "-shm"); files.Delete(path); }
    private bool TryCleanup(string path)
    {
        var succeeded = true;
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            try { files.Delete(candidate); } catch (Exception exception) { succeeded = false; logger.LogWarning(exception, "Unable to clean Companies restore artifact {Artifact}.", candidate); }
        return succeeded;
    }

    private static string Connection(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
    private static CompaniesRestoreFailureCode Map(CompaniesBackupValidationFailureCode code) => code switch
    {
        CompaniesBackupValidationFailureCode.BackupNotFound => CompaniesRestoreFailureCode.BackupNotFound,
        CompaniesBackupValidationFailureCode.IntegrityCheckFailed or CompaniesBackupValidationFailureCode.BackupOpenFailed => CompaniesRestoreFailureCode.IntegrityCheckFailed,
        CompaniesBackupValidationFailureCode.NotCompaniesDatabase => CompaniesRestoreFailureCode.NotCompaniesDatabase,
        CompaniesBackupValidationFailureCode.IncompatibleNewerSchema => CompaniesRestoreFailureCode.IncompatibleNewerSchema,
        _ => CompaniesRestoreFailureCode.InvalidBackup,
    };
}
