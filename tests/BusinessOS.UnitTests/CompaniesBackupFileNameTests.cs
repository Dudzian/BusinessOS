using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class CompaniesBackupFileNameTests
{
    [Fact]
    public void Generator_and_parser_round_trip_UTC_timestamp()
    {
        var name = CompaniesBackupFileName.Create(DateTimeOffset.Parse("2026-07-28T12:34:56.789+02:00"), Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        CompaniesBackupFileName.TryParse(name, out var timestamp).Should().BeTrue();
        name.Should().Be("businessos-companies-20260728T103456789Z-00112233445566778899aabbccddeeff.db");
        timestamp.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("businessos-companies-20260728T103456789Z-00112233445566778899AABBCCDDEEFF.db")]
    [InlineData("businessos-companies-20260728T103456789-00112233445566778899aabbccddeeff.db")]
    [InlineData("businessos-companies-20260728T10345678Z-00112233445566778899aabbccddeeff.db")]
    [InlineData("businessos-companies-20260728T103456789Z-00112233445566778899aabbccddeeff.db.tmp")]
    [InlineData("businessos-companies-20260728T103456789Z-00112233445566778899aabbccddeeff.db-wal")]
    [InlineData("businessos-companies-20260728T103456789Z-00112233445566778899aabbccddeeff.db-shm")]
    [InlineData("businessos-budgeting-20260728T103456789Z-00112233445566778899aabbccddeeff.db")]
    [InlineData("../businessos-companies-20260728T103456789Z-00112233445566778899aabbccddeeff.db")]
    [InlineData("/tmp/businessos-companies-20260728T103456789Z-00112233445566778899aabbccddeeff.db")]
    [InlineData("folder/businessos-companies-20260728T103456789Z-00112233445566778899aabbccddeeff.db")]
    public void Parser_rejects_noncanonical_names(string name) => CompaniesBackupFileName.TryParse(name, out _).Should().BeFalse();
}
