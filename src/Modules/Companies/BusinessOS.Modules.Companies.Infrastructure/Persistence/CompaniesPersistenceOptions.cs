using Microsoft.Data.Sqlite;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public sealed class CompaniesPersistenceOptions
{
    public const string ConfigurationKey = "BusinessOS:Persistence";
    public string DatabasePath { get; set; } = string.Empty;
    public string BackupDirectory { get; set; } = string.Empty;
    public int MaxBackups { get; set; } = 10;
    public bool Pooling { get; set; } = true;

    public string GetNormalizedDatabasePath()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("Companies SQLite database path is required.");
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DatabasePath));
    }

    public string GetNormalizedBackupDirectory()
    {
        if (string.IsNullOrWhiteSpace(BackupDirectory))
        {
            throw new InvalidOperationException("Companies SQLite backup directory is required.");
        }

        if (MaxBackups <= 0)
        {
            throw new InvalidOperationException("Companies SQLite maximum backup count must be greater than zero.");
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(BackupDirectory));
    }

    public string BuildConnectionString()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = GetNormalizedDatabasePath(),
            ForeignKeys = true,
            Pooling = Pooling,
        };
        return builder.ToString();
    }

    public void EnsureDatabaseDirectory()
    {
        var directory = Path.GetDirectoryName(GetNormalizedDatabasePath());
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
