using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Companies.Domain;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class CompanyPersistenceTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"businessos-companies-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Company_can_be_persisted_and_loaded_in_a_new_DbContext()
    {
        var company = CreateCompany();
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
            db.Companies.Add(company);
            await db.SaveChangesAsync();
        }

        await using var reopened = CreateContext();
        var loaded = await reopened.Companies.SingleAsync();
        loaded.Id.Should().Be(company.Id);
        loaded.OrganizationId.Should().Be(company.OrganizationId);
        loaded.LegalName.Should().Be(company.LegalName);
        loaded.DisplayName.Should().Be(company.DisplayName);
        loaded.TaxIdentificationNumber.Should().Be(company.TaxIdentificationNumber);
        loaded.CountryCode.Should().Be(company.CountryCode);
        loaded.BaseCurrency.Should().Be(company.BaseCurrency);
        loaded.DefaultTimeZone.Should().Be(company.DefaultTimeZone);
        loaded.Status.Should().Be(company.Status);
        loaded.CreatedAt.Should().Be(company.CreatedAt);
        loaded.UpdatedAt.Should().Be(company.UpdatedAt);
        loaded.CreatedBy.Should().Be(company.CreatedBy);
        loaded.UpdatedBy.Should().Be(company.UpdatedBy);
        loaded.Version.Should().Be(company.Version);
        loaded.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Foreign_company_without_tax_identification_number_round_trips_as_null()
    {
        var company = Company.Create(
            OrganizationId.New(),
            "Foreign company",
            "Foreign company",
            null,
            "DE",
            new CurrencyCode("EUR"),
            "Europe/Berlin",
            UserId.New(),
            DateTimeOffset.Parse("2026-07-24T09:00:00Z"));

        await Seed(company);

        await using var reopened = CreateContext();
        var loaded = await reopened.Companies.SingleAsync();
        loaded.TaxIdentificationNumber.Should().BeNull();
    }

    [Fact]
    public async Task Company_rename_persists_new_name_timestamps_and_version()
    {
        var company = CreateCompany();
        await Seed(company);
        await using var db = CreateContext();
        var loaded = await db.Companies.SingleAsync();
        var actor = UserId.New();
        var when = DateTimeOffset.Parse("2026-07-24T10:00:00Z");
        loaded.Rename("Renamed", actor, when);
        await db.SaveChangesAsync();

        await using var reopened = CreateContext();
        var saved = await reopened.Companies.SingleAsync();
        saved.DisplayName.Should().Be("Renamed");
        saved.UpdatedBy.Should().Be(actor);
        saved.UpdatedAt.Should().Be(when);
        saved.Version.Value.Should().Be(2);
    }

    [Fact]
    public async Task Soft_deleted_company_is_excluded_by_default_query_filter()
    {
        var company = CreateCompany();
        company.SoftDelete(UserId.New(), DateTimeOffset.UtcNow);
        await Seed(company);
        await using var db = CreateContext();
        (await db.Companies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Soft_deleted_company_can_be_loaded_with_IgnoreQueryFilters()
    {
        var company = CreateCompany();
        company.SoftDelete(UserId.New(), DateTimeOffset.UtcNow);
        await Seed(company);
        await using var db = CreateContext();
        (await db.Companies.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Stale_entity_version_causes_DbUpdateConcurrencyException()
    {
        var company = CreateCompany();
        await Seed(company);
        await using var first = CreateContext();
        await using var second = CreateContext();
        var firstCompany = await first.Companies.SingleAsync();
        var secondCompany = await second.Companies.SingleAsync();
        firstCompany.Rename("First", UserId.New(), DateTimeOffset.UtcNow);
        await first.SaveChangesAsync();
        secondCompany.Rename("Second", UserId.New(), DateTimeOffset.UtcNow.AddMinutes(1));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public void Test_connection_string_disables_pooling()
    {
        new SqliteConnectionStringBuilder(BuildTestConnectionString()).Pooling.Should().BeFalse();
    }

    [Fact]
    public async Task Temporary_SQLite_database_can_be_deleted_after_all_contexts_are_disposed()
    {
        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var reopened = CreateContext())
        {
            _ = await reopened.Companies.CountAsync();
        }

        DeleteDatabaseFiles();

        DatabaseFiles().Should().OnlyContain(path => !File.Exists(path));
    }

    private async Task Seed(Company company)
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        db.Companies.Add(company);
        await db.SaveChangesAsync();
    }

    private CompaniesDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CompaniesDbContext>()
            .UseSqlite(BuildTestConnectionString(), sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory_Companies"))
            .Options;
        return new CompaniesDbContext(options);
    }

    private string BuildTestConnectionString()
    {
        var baseConnectionString = new CompaniesPersistenceOptions
        {
            DatabasePath = databasePath,
        }.BuildConnectionString();

        return new SqliteConnectionStringBuilder(baseConnectionString)
        {
            Pooling = false,
        }.ToString();
    }

    private static Company CreateCompany() => Company.Create(
        OrganizationId.New(),
        "Legal",
        "Display",
        "5260250995",
        "PL",
        CurrencyCode.Pln,
        "Europe/Warsaw",
        UserId.New(),
        DateTimeOffset.Parse("2026-07-24T09:00:00Z"));

    private IEnumerable<string> DatabaseFiles() => new[] { databasePath, databasePath + "-shm", databasePath + "-wal" };

    private void DeleteDatabaseFiles()
    {
        foreach (var path in DatabaseFiles())
        {
            File.Delete(path);
        }
    }

    public void Dispose() => DeleteDatabaseFiles();
}
