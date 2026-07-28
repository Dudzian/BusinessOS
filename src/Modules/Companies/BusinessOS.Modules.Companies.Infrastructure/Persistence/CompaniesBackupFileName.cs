using System.Globalization;
using System.Text.RegularExpressions;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public static partial class CompaniesBackupFileName
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    public static string Create(DateTimeOffset utcNow, Guid uniqueId) =>
        $"businessos-companies-{utcNow.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{uniqueId:N}.db";

    public static bool TryParse(string? fileName, out DateTimeOffset createdAtUtc)
    {
        createdAtUtc = default;
        if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName || fileName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var match = Pattern().Match(fileName);
        if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return false;
        }

        createdAtUtc = new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
        return true;
    }

    [GeneratedRegex(@"^businessos-companies-(\d{8}T\d{9}Z)-[0-9a-f]{32}\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
