using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.Companies.Domain;

public enum CompanyStatus { Draft, Active, Suspended, Archived }

public sealed class Company
{
    private Company() { }
    public CompanyId Id { get; private set; } = CompanyId.New();
    public OrganizationId OrganizationId { get; private set; }
    public string LegalName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public TaxIdentificationNumber? TaxIdentificationNumber { get; private set; }
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

    public static Company Create(OrganizationId organizationId, string legalName, string displayName,
        string? taxIdentificationNumber, string countryCode, CurrencyCode currency, string timeZone,
        UserId actor, DateTimeOffset now) =>
        Create(organizationId, legalName, displayName, taxIdentificationNumber, countryCode, currency,
            timeZone, CompanyStatus.Active, actor, now);

    public static Company Create(OrganizationId organizationId, string legalName, string displayName,
        string? taxIdentificationNumber, string countryCode, CurrencyCode currency, string timeZone,
        CompanyStatus status, UserId actor, DateTimeOffset now)
    {
        EnsureMutableStatus(status);
        var values = Validate(legalName, displayName, taxIdentificationNumber, countryCode, currency.Value, timeZone);
        var utc = now.ToUniversalTime();
        return new Company
        {
            OrganizationId = organizationId,
            LegalName = values.LegalName,
            DisplayName = values.DisplayName,
            TaxIdentificationNumber = values.TaxId,
            CountryCode = values.CountryCode,
            BaseCurrency = values.Currency,
            DefaultTimeZone = values.TimeZone,
            Status = status,
            CreatedBy = actor,
            UpdatedBy = actor,
            CreatedAt = utc,
            UpdatedAt = utc,
        };
    }

    public void Update(string legalName, string displayName, string? taxIdentificationNumber,
        string countryCode, string currency, string timeZone, CompanyStatus status, UserId actor, DateTimeOffset now)
    {
        EnsureNotDeleted();
        EnsureMutableStatus(status);
        var values = Validate(legalName, displayName, taxIdentificationNumber, countryCode, currency, timeZone);
        LegalName = values.LegalName; DisplayName = values.DisplayName; TaxIdentificationNumber = values.TaxId;
        CountryCode = values.CountryCode; BaseCurrency = values.Currency; DefaultTimeZone = values.TimeZone;
        Status = status; Touch(actor, now);
    }

    public void Rename(string displayName, UserId actor, DateTimeOffset now) =>
        Update(LegalName, displayName, TaxIdentificationNumber?.Value, CountryCode, BaseCurrency.Value,
            DefaultTimeZone, Status, actor, now);

    public void SoftDelete(UserId actor, DateTimeOffset now)
    {
        EnsureNotDeleted();
        IsDeleted = true;
        Status = CompanyStatus.Archived;
        Touch(actor, now);
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted) throw new InvalidOperationException("Archived company cannot be changed.");
    }

    private static void EnsureMutableStatus(CompanyStatus status)
    {
        if (!Enum.IsDefined(status) || status == CompanyStatus.Archived)
            throw new ArgumentException("Archived or undefined status cannot be assigned directly.", nameof(status));
    }

    private void Touch(UserId actor, DateTimeOffset now)
    {
        UpdatedBy = actor; UpdatedAt = now.ToUniversalTime(); Version = Version.Next();
    }

    private static (string LegalName, string DisplayName, TaxIdentificationNumber? TaxId,
        string CountryCode, CurrencyCode Currency, string TimeZone) Validate(
        string legalName, string displayName, string? taxId, string countryCode, string currency, string timeZone)
    {
        var legal = Required(legalName, 256, nameof(legalName));
        var display = Required(displayName, 256, nameof(displayName));
        var country = Required(countryCode, 2, nameof(countryCode)).ToUpperInvariant();
        if (country.Length != 2 || country.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Country code must contain two ASCII letters.", nameof(countryCode));
        var zone = Required(timeZone, 128, nameof(timeZone));
        var normalizedTax = string.IsNullOrWhiteSpace(taxId) ? null : taxId.Trim();
        if (country == "PL") normalizedTax = normalizedTax?.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        TaxIdentificationNumber? number = normalizedTax is null ? null : new TaxIdentificationNumber(normalizedTax);
        if (country == "PL" && (number is null || !number.Value.IsValidForPoland))
            throw new ArgumentException("Invalid Polish NIP.", nameof(taxId));
        return (legal, display, number, country, new CurrencyCode(Required(currency, 3, nameof(currency)).ToUpperInvariant()), zone);
    }

    private static string Required(string value, int max, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", parameter);
        var normalized = value.Trim();
        if (normalized.Length > max) throw new ArgumentException($"Value may contain at most {max} characters.", parameter);
        return normalized;
    }
}
