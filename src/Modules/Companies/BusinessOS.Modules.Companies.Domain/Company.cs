using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.Companies.Domain;

public enum CompanyStatus
{
    Draft,
    Active,
    Suspended,
    Archived,
}

public sealed class Company
{
    private Company()
    {
    }

    public CompanyId Id { get; private set; } = CompanyId.New();

    public OrganizationId OrganizationId { get; private set; }

    public string LegalName { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public TaxIdentificationNumber TaxIdentificationNumber { get; private set; }

    public string CountryCode { get; private set; } = "PL";

    public CurrencyCode BaseCurrency { get; private set; } = CurrencyCode.Pln;

    public string DefaultTimeZone { get; private set; } = "Europe/Warsaw";

    public CompanyStatus Status { get; private set; } = CompanyStatus.Active;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public UserId CreatedBy { get; private set; }

    public UserId UpdatedBy { get; private set; }

    public EntityVersion Version { get; private set; } = new(1);

    public bool IsDeleted { get; private set; }

    public static Company Create(
        OrganizationId organizationId,
        string legalName,
        string displayName,
        string? taxIdentificationNumber,
        string countryCode,
        CurrencyCode currency,
        string timeZone,
        UserId actor,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("Legal name is required.", nameof(legalName));
        }

        var tax = new TaxIdentificationNumber(taxIdentificationNumber);
        if (string.Equals(countryCode, "PL", StringComparison.Ordinal) && !tax.IsValidForPoland)
        {
            throw new ArgumentException("Invalid Polish NIP.", nameof(taxIdentificationNumber));
        }

        var normalizedNow = now.ToUniversalTime();
        return new Company
        {
            OrganizationId = organizationId,
            LegalName = legalName.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? legalName.Trim() : displayName.Trim(),
            TaxIdentificationNumber = tax,
            CountryCode = countryCode,
            BaseCurrency = currency,
            DefaultTimeZone = timeZone,
            CreatedBy = actor,
            UpdatedBy = actor,
            CreatedAt = normalizedNow,
            UpdatedAt = normalizedNow,
        };
    }

    public void Rename(string displayName, UserId actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        DisplayName = displayName.Trim();
        UpdatedBy = actor;
        UpdatedAt = now.ToUniversalTime();
        Version = Version.Next();
    }

    public void SoftDelete(UserId actor, DateTimeOffset now)
    {
        IsDeleted = true;
        UpdatedBy = actor;
        UpdatedAt = now.ToUniversalTime();
        Version = Version.Next();
    }
}
