using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Companies.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class CompanyCrudDomainTests
{
    private static readonly OrganizationId Organization = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly UserId User = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-01T10:00:00+02:00");

    [Fact]
    public void Legacy_create_normalizes_values_and_defaults_to_active()
    {
        var company = Company.Create(Organization, " Legal ", " Display ", "526-025-09-95", " pl ", new CurrencyCode("PLN"), " Europe/Warsaw ", User, Now);
        company.Status.Should().Be(CompanyStatus.Active); company.CountryCode.Should().Be("PL");
        company.TaxIdentificationNumber!.Value.Value.Should().Be("5260250995"); company.UpdatedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Create_accepts_explicit_status_and_empty_foreign_tax_id()
    {
        Company.Create(Organization, "Legal", "Display", null, "de", new("EUR"), "Europe/Berlin", CompanyStatus.Draft, User, Now).Status.Should().Be(CompanyStatus.Draft);
    }

    [Theory]
    [InlineData(CompanyStatus.Archived)]
    [InlineData((CompanyStatus)999)]
    public void Create_rejects_archived_and_undefined_status(CompanyStatus status) =>
        Assert.Throws<ArgumentException>(() => Company.Create(Organization, "Legal", "Display", null, "DE", new("EUR"), "Europe/Berlin", status, User, Now));

    [Theory]
    [InlineData(CompanyStatus.Archived)]
    [InlineData((CompanyStatus)999)]
    public void Update_rejects_archived_and_undefined_status_without_changing_version(CompanyStatus status)
    {
        var company = Create();
        Assert.Throws<ArgumentException>(() => company.Update("New", "New", "5260250995", "PL", "PLN", "Europe/Warsaw", status, User, Now));
        company.Version.Value.Should().Be(1);
    }

    [Theory]
    [InlineData("", "Display", "PL", "Europe/Warsaw")]
    [InlineData("Legal", "", "PL", "Europe/Warsaw")]
    [InlineData("Legal", "Display", "", "Europe/Warsaw")]
    [InlineData("Legal", "Display", "PL", "")]
    public void Create_rejects_required_fields(string legal, string display, string country, string zone) =>
        Assert.Throws<ArgumentException>(() => Company.Create(Organization, legal, display, "5260250995", country, CurrencyCode.Pln, zone, User, Now));

    [Fact]
    public void Create_rejects_invalid_polish_nip() =>
        Assert.Throws<ArgumentException>(() => Company.Create(Organization, "Legal", "Display", "1234567890", "PL", CurrencyCode.Pln, "Europe/Warsaw", User, Now));

    [Fact]
    public void Create_enforces_name_and_timezone_limits()
    {
        Assert.Throws<ArgumentException>(() => Company.Create(Organization, new string('x', 257), "Display", null, "DE", new("EUR"), "Europe/Berlin", User, Now));
        Assert.Throws<ArgumentException>(() => Company.Create(Organization, "Legal", "Display", null, "DE", new("EUR"), new string('x', 129), User, Now));
    }

    [Fact]
    public void Update_changes_all_fields_audit_and_version_once()
    {
        var company = Create(); var actor = UserId.New(); var updated = Now.AddHours(1);
        company.Update("New legal", "New display", null, " de ", " eur ", "Europe/Berlin", CompanyStatus.Suspended, actor, updated);
        company.LegalName.Should().Be("New legal"); company.DisplayName.Should().Be("New display"); company.CountryCode.Should().Be("DE");
        company.BaseCurrency.Value.Should().Be("EUR"); company.Status.Should().Be(CompanyStatus.Suspended); company.Version.Value.Should().Be(2);
        company.UpdatedBy.Should().Be(actor); company.UpdatedAt.Should().Be(updated.ToUniversalTime());
    }

    [Fact]
    public void Soft_delete_updates_audit_once_and_prevents_further_changes()
    {
        var company = Create(); company.SoftDelete(User, Now.AddHours(1)); company.Version.Value.Should().Be(2);
        company.Status.Should().Be(CompanyStatus.Archived);
        Assert.Throws<InvalidOperationException>(() => company.SoftDelete(User, Now.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => company.Rename("No", User, Now.AddHours(2)));
        company.Version.Value.Should().Be(2);
    }

    private static Company Create() => Company.Create(Organization, "Legal", "Display", "5260250995", "PL", CurrencyCode.Pln, "Europe/Warsaw", User, Now);
}
