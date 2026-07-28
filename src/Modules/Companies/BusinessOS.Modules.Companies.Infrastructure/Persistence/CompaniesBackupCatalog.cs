using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public interface ICompaniesBackupCatalog
{
    Task<CompaniesBackupCatalogResult> ListAsync(CancellationToken cancellationToken);
    Task<CompaniesBackupValidationResult> ValidateAsync(string backupId, CancellationToken cancellationToken);
}

public enum CompaniesBackupCatalogFailureCode { None, BackupDirectoryNotFound, BackupDirectoryUnavailable, EnumerationFailed, UnexpectedFailure }
public sealed record CompaniesBackupCatalogResult(bool Succeeded, CompaniesBackupCatalogFailureCode FailureCode, IReadOnlyList<CompaniesBackupDescriptor> Backups);

internal interface ICompaniesBackupFileOperations
{
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateFiles(string path);
    bool FileExists(string path);
    FileAttributes GetAttributes(string path);
    long GetLength(string path);
}

internal sealed class CompaniesBackupFileOperations : ICompaniesBackupFileOperations
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);
    public bool FileExists(string path) => File.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public long GetLength(string path) => new FileInfo(path).Length;
}

internal static class CompaniesMigrationHistoryCompatibility
{
    public static bool IsKnownMigrationPrefix(IReadOnlyList<string> applied, IReadOnlyList<string> known)
    {
        if (applied.Count > known.Count) return false;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < applied.Count; index++)
            if (!unique.Add(applied[index]) || !string.Equals(applied[index], known[index], StringComparison.Ordinal)) return false;
        return true;
    }
}

public enum CompaniesBackupValidationStatus { Valid, Invalid }

public enum CompaniesBackupValidationFailureCode
{
    None, InvalidBackupId, BackupNotFound, EmptyBackup, ReparsePointRejected, BackupOpenFailed,
    IntegrityCheckFailed, NotCompaniesDatabase, IncompatibleNewerSchema, UnexpectedFailure,
}

public sealed record CompaniesBackupDescriptor(string BackupId, string FileName, DateTimeOffset CreatedAtUtc,
    long SizeBytes, CompaniesBackupValidationStatus ValidationStatus, CompaniesBackupValidationFailureCode FailureCode);

public sealed record CompaniesBackupValidationResult(bool Succeeded, CompaniesBackupValidationFailureCode FailureCode)
{
    public static CompaniesBackupValidationResult Success() => new(true, CompaniesBackupValidationFailureCode.None);
    public static CompaniesBackupValidationResult Failure(CompaniesBackupValidationFailureCode code) => new(false, code);
}

internal sealed class CompaniesBackupValidator(
    CompaniesPersistenceOptions options,
    IDbContextFactory<CompaniesDbContext> contextFactory,
    ICompaniesBackupFileOperations files,
    ILogger<CompaniesBackupValidator> logger)
{
    public string? Resolve(string backupId, out CompaniesBackupValidationFailureCode failure)
    {
        failure = CompaniesBackupValidationFailureCode.InvalidBackupId;
        if (!CompaniesBackupFileName.TryParse(backupId, out _) || backupId.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            backupId.IndexOf(Path.AltDirectorySeparatorChar) >= 0 || Path.GetFileName(backupId) != backupId)
        {
            return null;
        }

        try
        {
            var directory = Path.GetFullPath(options.GetNormalizedBackupDirectory());
            var path = Path.GetFullPath(Path.Combine(directory, backupId));
            var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(prefix, comparison)) return null;
            if (!files.FileExists(path)) { failure = CompaniesBackupValidationFailureCode.BackupNotFound; return null; }
            var attributes = files.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { failure = CompaniesBackupValidationFailureCode.ReparsePointRejected; return null; }
            if ((attributes & FileAttributes.Directory) != 0) return null;
            if (files.GetLength(path) == 0) { failure = CompaniesBackupValidationFailureCode.EmptyBackup; return null; }
            failure = CompaniesBackupValidationFailureCode.None;
            return path;
        }
        catch (FileNotFoundException) { failure = CompaniesBackupValidationFailureCode.BackupNotFound; return null; }
        catch (DirectoryNotFoundException) { failure = CompaniesBackupValidationFailureCode.BackupNotFound; return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to resolve Companies backup.");
            failure = CompaniesBackupValidationFailureCode.BackupOpenFailed;
            return null;
        }
    }

    public async Task<CompaniesBackupValidationResult> ValidatePathAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using var connection = new SqliteConnection(builder.ToString());
            try { await connection.OpenAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning(exception, "Unable to open Companies backup."); return CompaniesBackupValidationResult.Failure(CompaniesBackupValidationFailureCode.BackupOpenFailed); }

            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                var rows = new List<string>();
                await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(reader.GetString(0));
                if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.Ordinal))
                    return CompaniesBackupValidationResult.Failure(CompaniesBackupValidationFailureCode.IntegrityCheckFailed);
            }

            await using var history = connection.CreateCommand();
            history.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory_Companies';";
            if (await history.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                return CompaniesBackupValidationResult.Failure(CompaniesBackupValidationFailureCode.NotCompaniesDatabase);
            history.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory_Companies ORDER BY rowid;";
            var applied = new List<string>();
            await using (var reader = await history.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) applied.Add(reader.GetString(0));
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var known = context.Database.GetMigrations().ToArray();
            return CompaniesMigrationHistoryCompatibility.IsKnownMigrationPrefix(applied, known) ? CompaniesBackupValidationResult.Success() :
                CompaniesBackupValidationResult.Failure(CompaniesBackupValidationFailureCode.IncompatibleNewerSchema);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SqliteException exception) { logger.LogWarning(exception, "Companies backup SQLite validation failed."); return CompaniesBackupValidationResult.Failure(CompaniesBackupValidationFailureCode.IntegrityCheckFailed); }
        catch (Exception exception) { logger.LogError(exception, "Unexpected Companies backup validation failure."); return CompaniesBackupValidationResult.Failure(CompaniesBackupValidationFailureCode.UnexpectedFailure); }
    }
}

internal sealed class CompaniesBackupCatalog(CompaniesPersistenceOptions options, CompaniesBackupValidator validator, ICompaniesBackupFileOperations files) : ICompaniesBackupCatalog
{
    public async Task<CompaniesBackupCatalogResult> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = options.GetNormalizedBackupDirectory();
        try { if (!files.DirectoryExists(directory)) return new(false, CompaniesBackupCatalogFailureCode.BackupDirectoryNotFound, []); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(false, CompaniesBackupCatalogFailureCode.BackupDirectoryUnavailable, []); }
        var descriptors = new List<CompaniesBackupDescriptor>();
        try
        {
            foreach (var path in files.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (!CompaniesBackupFileName.TryParse(name, out var created)) continue;
                var validation = await ValidateAsync(name, cancellationToken).ConfigureAwait(false);
                long size = 0;
                if (validation.Succeeded)
                {
                    try { size = files.FileExists(path) ? files.GetLength(path) : 0; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { validation = CompaniesBackupValidationResult.Failure(CompaniesBackupValidationFailureCode.BackupOpenFailed); }
                }
                descriptors.Add(new(name, name, created, size,
                    validation.Succeeded ? CompaniesBackupValidationStatus.Valid : CompaniesBackupValidationStatus.Invalid, validation.FailureCode));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(false, CompaniesBackupCatalogFailureCode.EnumerationFailed, descriptors); }
        catch (Exception) { return new(false, CompaniesBackupCatalogFailureCode.UnexpectedFailure, descriptors); }
        return new(true, CompaniesBackupCatalogFailureCode.None,
            descriptors.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.FileName, StringComparer.Ordinal).ToArray());
    }

    public async Task<CompaniesBackupValidationResult> ValidateAsync(string backupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = validator.Resolve(backupId, out var failure);
        return path is null ? CompaniesBackupValidationResult.Failure(failure) : await validator.ValidatePathAsync(path, cancellationToken).ConfigureAwait(false);
    }
}
