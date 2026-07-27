using BusinessOS.AppHost;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class PersistenceStartupTests
{
    [Fact]
    public void Production_database_connection_pooling_defaults_to_true()
    {
        var options = new CompaniesPersistenceOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "production.db") };
        new SqliteConnectionStringBuilder(options.BuildConnectionString()).Pooling.Should().BeTrue();
    }

    [Fact]
    public void Test_database_connection_pooling_can_be_disabled()
    {
        var options = new CompaniesPersistenceOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "test.db"), Pooling = false };
        new SqliteConnectionStringBuilder(options.BuildConnectionString()).Pooling.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("3", 3)]
    public void MaxBackups_configuration_accepts_valid_values(string? configured, int expected) =>
        BusinessOsHost.ParseMaxBackups(configured).Should().Be(expected);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public void MaxBackups_configuration_rejects_invalid_values(string configured)
    {
        var action = () => BusinessOsHost.ParseMaxBackups(configured);
        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CompaniesPersistenceOptions_rejects_non_positive_MaxBackups(int value)
    {
        var options = new CompaniesPersistenceOptions { BackupDirectory = Path.GetTempPath(), MaxBackups = value };
        var action = options.GetNormalizedBackupDirectory;
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CompaniesPersistenceOptions_rejects_empty_backup_directory()
    {
        var options = new CompaniesPersistenceOptions { BackupDirectory = " ", MaxBackups = 10 };
        Action action = () => options.GetNormalizedBackupDirectory();
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ApplicationStartupResult_does_not_expose_exception_text()
    {
        var result = ApplicationStartupResult.Failure(ApplicationStartupFailureCode.UnexpectedFailure, "Bezpieczny komunikat.", "abc123");
        result.ToString().Should().NotContain("stack trace");
        result.UserMessage.Should().Be("Bezpieczny komunikat.");
    }

    [Fact]
    public void ApplicationStartupFailureCode_is_stable_and_explicit()
    {
        ((int)ApplicationStartupFailureCode.DatabaseInspectionFailed).Should().Be(1);
        ((int)ApplicationStartupFailureCode.BackupFailed).Should().Be(2);
        ((int)ApplicationStartupFailureCode.BackupIntegrityCheckFailed).Should().Be(3);
        ((int)ApplicationStartupFailureCode.MigrationFailed).Should().Be(4);
        ((int)ApplicationStartupFailureCode.Cancelled).Should().Be(5);
        ((int)ApplicationStartupFailureCode.UnexpectedFailure).Should().Be(6);
    }
}
