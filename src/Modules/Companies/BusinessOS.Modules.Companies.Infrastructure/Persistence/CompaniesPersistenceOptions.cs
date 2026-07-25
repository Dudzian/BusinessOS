using Microsoft.Data.Sqlite;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public sealed class CompaniesPersistenceOptions
{
    public const string ConfigurationKey = "BusinessOS:Persistence";
    public string DatabasePath { get; set; } = string.Empty;

    public string GetNormalizedDatabasePath()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("Companies SQLite database path is required.");
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DatabasePath));
    }

    public string BuildConnectionString()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = GetNormalizedDatabasePath(),
            ForeignKeys = true,
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
