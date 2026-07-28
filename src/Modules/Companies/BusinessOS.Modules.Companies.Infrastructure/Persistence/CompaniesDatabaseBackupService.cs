using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public interface ICompaniesDatabaseBackupService
{
    Task<CompaniesBackupResult> CreateBackupAsync(CancellationToken cancellationToken);
}

public enum CompaniesBackupFailureCode
{
    None,
    BackupFailed,
    IntegrityCheckFailed,
}

public sealed record CompaniesBackupResult(bool Succeeded, string? BackupPath, CompaniesBackupFailureCode FailureCode)
{
    public static CompaniesBackupResult Success(string path) => new(true, path, CompaniesBackupFailureCode.None);
    public static CompaniesBackupResult Failure(CompaniesBackupFailureCode code) => new(false, null, code);
}

public sealed partial class CompaniesDatabaseBackupService(
    CompaniesPersistenceOptions options,
    TimeProvider timeProvider,
    ILogger<CompaniesDatabaseBackupService> logger) : ICompaniesDatabaseBackupService
{
    public async Task<CompaniesBackupResult> CreateBackupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = options.GetNormalizedBackupDirectory();
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, CompaniesBackupFileName.Create(timeProvider.GetUtcNow(), Guid.NewGuid()));
        var temporaryPath = finalPath + ".tmp";

        try
        {
            logger.LogInformation("Starting Companies database backup.");
            await using (var source = new SqliteConnection(options.BuildConnectionString()))
            await using (var destination = new SqliteConnection(BuildBackupConnectionString(temporaryPath, SqliteOpenMode.ReadWriteCreate)))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            await using (var check = new SqliteConnection(BuildBackupConnectionString(temporaryPath, SqliteOpenMode.ReadOnly)))
            {
                await check.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = check.CreateCommand();
                command.CommandText = "PRAGMA quick_check;";
                var results = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(reader.GetString(0));
                }

                if (results.Count != 1 || !string.Equals(results[0], "ok", StringComparison.Ordinal))
                {
                    logger.LogError("Companies backup failed SQLite quick_check.");
                    return CompaniesBackupResult.Failure(CompaniesBackupFailureCode.IntegrityCheckFailed);
                }
            }

            File.Move(temporaryPath, finalPath, false);
            logger.LogInformation("Companies backup quick_check completed; backup created at {BackupPath}.", finalPath);
            ApplyRetention(finalPath);
            return CompaniesBackupResult.Success(finalPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Companies database backup failed.");
            return CompaniesBackupResult.Failure(CompaniesBackupFailureCode.BackupFailed);
        }
        finally
        {
            TryDeleteBackupArtifacts(temporaryPath);
        }
    }

    private void ApplyRetention(string currentPath)
    {
        try
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var currentFullPath = Path.GetFullPath(currentPath);
            var otherBackups = Directory.EnumerateFiles(options.GetNormalizedBackupDirectory(), "businessos-companies-*.db")
                .Where(path => CompaniesBackupFileName.TryParse(Path.GetFileName(path), out _))
                .Select(Path.GetFullPath)
                .Where(path => !comparer.Equals(path, currentFullPath))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();

            var retained = new HashSet<string>(comparer) { currentFullPath };
            foreach (var path in otherBackups.Take(options.MaxBackups - 1))
            {
                retained.Add(path);
            }

            foreach (var path in otherBackups.Where(path => !retained.Contains(path)))
            {
                File.Delete(path);
            }

            logger.LogInformation("Companies backup retention completed; retained at most {MaxBackups} backups.", options.MaxBackups);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Companies backup retention failed; the valid new backup is retained.");
        }
    }

    private void TryDeleteBackupArtifacts(string path)
    {
        foreach (var candidate in new[] { path, path + "-shm", path + "-wal" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Unable to clean a temporary Companies backup artifact during backup cleanup.");
            }
        }
    }

    private static string BuildBackupConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString();
}
