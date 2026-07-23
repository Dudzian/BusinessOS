using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.BusinessProjects.Domain;
using BusinessOS.Modules.Companies.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class FoundationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserId Actor = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Theory]
    [InlineData("PLN")]
    [InlineData("EUR")]
    [InlineData("USD")]
    public void Currency_code_accepts_iso_4217_code(string value)
    {
        new CurrencyCode(value).Value.Should().Be(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pln")]
    [InlineData("PLNN")]
    public void Currency_code_rejects_invalid_codes(string value)
    {
        FluentActions.Invoking(() => new CurrencyCode(value)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Currency_code_rejects_null()
    {
        var exception = Record.Exception(() => Activator.CreateInstance(typeof(CurrencyCode), [null]));

        exception.Should().BeOfType<System.Reflection.TargetInvocationException>()
            .Which.InnerException.Should().BeOfType<ArgumentNullException>();
    }

    [Fact]
    public void Default_currency_code_has_safe_empty_value()
    {
        var currency = default(CurrencyCode);

        currency.Value.Should().BeEmpty();
        currency.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Money_adds_amounts_in_same_currency()
    {
        var result = new Money(10.25m, CurrencyCode.Pln)
            .Add(new Money(1.75m, CurrencyCode.Pln));

        result.Should().Be(new Money(12.00m, CurrencyCode.Pln));
    }

    [Fact]
    public void Money_rejects_cross_currency_addition()
    {
        FluentActions
            .Invoking(() => new Money(1m, CurrencyCode.Pln).Add(new Money(1m, new CurrencyCode("EUR"))))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(1.235, 1.24)]
    [InlineData(-1.235, -1.24)]
    public void Money_rounding_uses_away_from_zero(
        decimal input,
        decimal expected)
    {
        FinancialRounding.RoundMoney(input).Should().Be(expected);
    }

    [Fact]
    public void Date_range_accepts_ordered_dates()
    {
        var range = new DateRange(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 2));

        range.End.Should().Be(new DateOnly(2026, 1, 2));
    }

    [Fact]
    public void Date_range_rejects_end_before_start()
    {
        FluentActions
            .Invoking(() => new DateRange(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1)))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void Company_can_be_created()
    {
        var company = CreateCompany(
            legalName: "Dudzian sp. z o.o.",
            displayName: "Dudzian",
            taxIdentificationNumber: "1234567890");

        company.DisplayName.Should().Be("Dudzian");
        company.Version.Value.Should().Be(1);
    }

    [Fact]
    public void Company_rejects_empty_legal_name()
    {
        FluentActions
            .Invoking(() => CreateCompany(legalName: string.Empty))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void Company_rejects_invalid_polish_nip()
    {
        FluentActions
            .Invoking(() => CreateCompany(taxIdentificationNumber: "123"))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void Business_project_can_be_created()
    {
        var project = CreateProject(
            start: new DateOnly(2026, 9, 1),
            opening: new DateOnly(2026, 10, 1));

        project.Name.Should().Be("Cafe");
        project.Status.Should().Be(BusinessProjectStatus.Draft);
    }

    [Fact]
    public void Business_project_rejects_opening_before_start()
    {
        FluentActions
            .Invoking(() => CreateProject(
                start: new DateOnly(2026, 2, 2),
                opening: new DateOnly(2026, 2, 1)))
            .Should()
            .Throw<ArgumentException>();
    }

    [Fact]
    public void Soft_delete_sets_flag_and_increments_version()
    {
        var company = CreateCompany();

        company.SoftDelete(Actor, FixedNow.AddMinutes(1));

        company.IsDeleted.Should().BeTrue();
        company.Version.Value.Should().Be(2);
    }

    [Fact]
    public void Entity_version_increments()
    {
        new EntityVersion(7).Next().Value.Should().Be(8);
    }

    private static Company CreateCompany(
        string legalName = "Dudzian",
        string displayName = "Dudzian",
        string? taxIdentificationNumber = null)
    {
        return Company.Create(
            OrganizationId.New(),
            legalName,
            displayName,
            taxIdentificationNumber,
            "PL",
            CurrencyCode.Pln,
            "Europe/Warsaw",
            Actor,
            FixedNow);
    }

    private static BusinessProject CreateProject(
        DateOnly start,
        DateOnly opening)
    {
        return BusinessProject.Create(
            CompanyId.New(),
            "Cafe",
            "Gastro",
            "Warsaw",
            "Local cafe",
            start,
            opening,
            CurrencyCode.Pln,
            Actor,
            FixedNow);
    }
}
