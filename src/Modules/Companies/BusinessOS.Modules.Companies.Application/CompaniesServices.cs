using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Companies.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.Companies.Application;

public static class CompaniesServices
{
    public static IServiceCollection AddCompaniesModule(this IServiceCollection services)
    {
        services.AddTransient<ICompaniesCrudService, CompaniesCrudService>();
        services.AddTransient<ICompaniesLookupService, CompaniesLookupService>();
        return services;
    }
}

public interface ICompaniesExecutionContext
{
    OrganizationId OrganizationId { get; }
    UserId UserId { get; }
}

public sealed record CompanyLookupItem(Guid Id, string DisplayName, string BaseCurrency, CompanyStatusValue Status);
public interface ICompaniesLookupService
{
    Task<IReadOnlyList<CompanyLookupItem>> ListActiveAsync(CancellationToken cancellationToken);
    Task<CompanyLookupItem?> GetActiveAsync(Guid companyId, CancellationToken cancellationToken);
}
public sealed class CompaniesLookupException : Exception
{
    internal CompaniesLookupException(Exception innerException) : base("Companies lookup failed.", innerException) { }
}

public enum CompanyStatusValue { Draft, Active, Suspended }

public sealed record CompanyListItem(Guid Id, string LegalName, string DisplayName, string? TaxIdentificationNumber,
    string CountryCode, string BaseCurrency, CompanyStatusValue Status, DateTimeOffset UpdatedAtUtc, long Version);

public sealed record CompanyDetails(Guid Id, string LegalName, string DisplayName, string? TaxIdentificationNumber,
    string CountryCode, string BaseCurrency, string DefaultTimeZone, CompanyStatusValue Status,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid CreatedBy, Guid UpdatedBy, long Version);

public sealed record CreateCompanyRequest(string LegalName, string DisplayName, string? TaxIdentificationNumber,
    string CountryCode, string BaseCurrency, string DefaultTimeZone, CompanyStatusValue Status);
public sealed record UpdateCompanyRequest(Guid CompanyId, long ExpectedVersion, string LegalName, string DisplayName,
    string? TaxIdentificationNumber, string CountryCode, string BaseCurrency, string DefaultTimeZone, CompanyStatusValue Status);
public sealed record ArchiveCompanyRequest(Guid CompanyId, long ExpectedVersion);

public enum CompanyOperationStatus { Success, ValidationFailed, NotFound, ConcurrencyConflict, DuplicateTaxIdentificationNumber, DependentProjectsExist, PersistenceFailure, Cancelled }
public sealed record CompanyArchiveConstraintResult(bool IsAllowed, string SafeMessage)
{
    public static CompanyArchiveConstraintResult Allowed { get; } = new(true, string.Empty);
}
public interface ICompanyArchiveConstraint
{
    Task<CompanyArchiveConstraintResult> EvaluateAsync(Guid companyId, CancellationToken cancellationToken);
}
public sealed record CompanyOperationResult(CompanyOperationStatus Status, string SafeMessage, IReadOnlyDictionary<string, string[]> ValidationErrors)
{
    public static CompanyOperationResult Success(string message) => new(CompanyOperationStatus.Success, message, EmptyErrors);
    internal static readonly IReadOnlyDictionary<string, string[]> EmptyErrors = new Dictionary<string, string[]>();
}
public sealed record CompanyOperationResult<T>(CompanyOperationStatus Status, string SafeMessage,
    IReadOnlyDictionary<string, string[]> ValidationErrors, T? Value);

public interface ICompaniesCrudService
{
    Task<IReadOnlyList<CompanyListItem>> ListAsync(CancellationToken cancellationToken);
    Task<CompanyDetails?> GetAsync(Guid companyId, CancellationToken cancellationToken);
    Task<CompanyOperationResult<CompanyDetails>> CreateAsync(CreateCompanyRequest request, CancellationToken cancellationToken);
    Task<CompanyOperationResult<CompanyDetails>> UpdateAsync(UpdateCompanyRequest request, CancellationToken cancellationToken);
    Task<CompanyOperationResult> ArchiveAsync(ArchiveCompanyRequest request, CancellationToken cancellationToken);
}

public enum CompaniesSaveStatus { Success, ConcurrencyConflict, DuplicateTaxIdentificationNumber }
public sealed class CompaniesPersistenceException(string message, Exception innerException) : Exception(message, innerException);
public interface ICompaniesStore
{
    Task<IReadOnlyList<Company>> ListAsync(OrganizationId organizationId, CancellationToken cancellationToken);
    Task<Company?> GetAsync(OrganizationId organizationId, CompanyId companyId, bool tracked, CancellationToken cancellationToken);
    Task<bool> TaxIdExistsAsync(OrganizationId organizationId, string taxId, CompanyId? exceptCompanyId, CancellationToken cancellationToken);
    Task AddAsync(Company company, CancellationToken cancellationToken);
    Task<CompaniesSaveStatus> SaveChangesAsync(CancellationToken cancellationToken);
    Task ResetTrackingAsync() => Task.CompletedTask;
}

internal sealed class CompaniesLookupService(ICompaniesStore store, ICompaniesExecutionContext executionContext) : ICompaniesLookupService
{
    public async Task<IReadOnlyList<CompanyLookupItem>> ListActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await store.ListAsync(executionContext.OrganizationId, cancellationToken))
                .Where(company => company.Status == CompanyStatus.Active)
                .OrderBy(company => company.DisplayName, StringComparer.Ordinal)
                .ThenBy(company => company.Id.Value)
                .Select(Map).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CompaniesPersistenceException exception) { throw new CompaniesLookupException(exception); }
    }

    public async Task<CompanyLookupItem?> GetActiveAsync(Guid companyId, CancellationToken cancellationToken)
    {
        try
        {
            var company = await store.GetAsync(executionContext.OrganizationId, new CompanyId(companyId), false, cancellationToken);
            return company is { Status: CompanyStatus.Active } ? Map(company) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CompaniesPersistenceException exception) { throw new CompaniesLookupException(exception); }
    }

    private static CompanyLookupItem Map(Company company) =>
        new(company.Id.Value, company.DisplayName, company.BaseCurrency.Value, CompanyStatusValue.Active);
}

internal sealed class CompaniesCrudService(ICompaniesStore store, ICompaniesExecutionContext executionContext, TimeProvider timeProvider,
    IEnumerable<ICompanyArchiveConstraint> archiveConstraints) : ICompaniesCrudService
{
    public async Task<IReadOnlyList<CompanyListItem>> ListAsync(CancellationToken cancellationToken) =>
        (await store.ListAsync(executionContext.OrganizationId, cancellationToken))
            .OrderBy(company => company.DisplayName, StringComparer.Ordinal)
            .ThenBy(company => company.LegalName, StringComparer.Ordinal)
            .ThenBy(company => company.Id.Value)
            .Select(MapList).ToArray();

    public async Task<CompanyDetails?> GetAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await store.GetAsync(executionContext.OrganizationId, new CompanyId(companyId), false, cancellationToken);
        return company is null ? null : MapDetails(company);
    }

    public async Task<CompanyOperationResult<CompanyDetails>> CreateAsync(CreateCompanyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var company = Company.Create(executionContext.OrganizationId, request.LegalName, request.DisplayName,
                request.TaxIdentificationNumber, request.CountryCode, new CurrencyCode(NormalizeCurrency(request.BaseCurrency)),
                request.DefaultTimeZone, ToDomain(request.Status), executionContext.UserId, timeProvider.GetUtcNow());
            if (company.TaxIdentificationNumber is { } tax && await store.TaxIdExistsAsync(executionContext.OrganizationId, tax.Value!, null, cancellationToken))
                return Result<CompanyDetails>(CompanyOperationStatus.DuplicateTaxIdentificationNumber, "Firma z tym numerem podatkowym już istnieje.");
            await store.AddAsync(company, cancellationToken);
            return FromSave(await store.SaveChangesAsync(cancellationToken), company, "Firma została utworzona.");
        }
        catch (ArgumentException exception) { return Validation<CompanyDetails>(exception); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result<CompanyDetails>(CompanyOperationStatus.Cancelled, "Operacja została anulowana."); }
        catch (CompaniesPersistenceException) { return Result<CompanyDetails>(CompanyOperationStatus.PersistenceFailure, "Nie udało się zapisać firmy. Spróbuj ponownie."); }
    }

    public async Task<CompanyOperationResult<CompanyDetails>> UpdateAsync(UpdateCompanyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var company = await store.GetAsync(executionContext.OrganizationId, new CompanyId(request.CompanyId), true, cancellationToken);
            if (company is null) return Result<CompanyDetails>(CompanyOperationStatus.NotFound, "Nie znaleziono firmy.");
            if (company.Version.Value != request.ExpectedVersion) return Result<CompanyDetails>(CompanyOperationStatus.ConcurrencyConflict, "Firma została zmieniona przez inną operację. Odśwież dane.");
            company.Update(request.LegalName, request.DisplayName, request.TaxIdentificationNumber, request.CountryCode,
                request.BaseCurrency, request.DefaultTimeZone, ToDomain(request.Status), executionContext.UserId, timeProvider.GetUtcNow());
            return FromSave(await store.SaveChangesAsync(cancellationToken), company, "Zmiany zostały zapisane.");
        }
        catch (ArgumentException exception) { return Validation<CompanyDetails>(exception); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result<CompanyDetails>(CompanyOperationStatus.Cancelled, "Operacja została anulowana."); }
        catch (CompaniesPersistenceException) { return Result<CompanyDetails>(CompanyOperationStatus.PersistenceFailure, "Nie udało się zapisać firmy. Spróbuj ponownie."); }
        finally { await store.ResetTrackingAsync(); }
    }

    public async Task<CompanyOperationResult> ArchiveAsync(ArchiveCompanyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var company = await store.GetAsync(executionContext.OrganizationId, new CompanyId(request.CompanyId), true, cancellationToken);
            if (company is null) return Plain(CompanyOperationStatus.NotFound, "Nie znaleziono firmy.");
            if (company.Version.Value != request.ExpectedVersion) return Plain(CompanyOperationStatus.ConcurrencyConflict, "Firma została zmieniona przez inną operację. Odśwież dane.");
            foreach (var constraint in archiveConstraints)
            {
                var evaluation = await constraint.EvaluateAsync(company.Id.Value, cancellationToken);
                if (!evaluation.IsAllowed)
                    return Plain(CompanyOperationStatus.DependentProjectsExist,
                        string.IsNullOrWhiteSpace(evaluation.SafeMessage) ? "Najpierw zarchiwizuj projekty firmy." : evaluation.SafeMessage);
            }
            company.SoftDelete(executionContext.UserId, timeProvider.GetUtcNow());
            var saved = await store.SaveChangesAsync(cancellationToken);
            return saved switch
            {
                CompaniesSaveStatus.Success => CompanyOperationResult.Success("Firma została zarchiwizowana."),
                CompaniesSaveStatus.ConcurrencyConflict => Plain(CompanyOperationStatus.ConcurrencyConflict, "Firma została zmieniona przez inną operację. Odśwież dane."),
                _ => Plain(CompanyOperationStatus.PersistenceFailure, "Nie udało się zarchiwizować firmy. Spróbuj ponownie."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Plain(CompanyOperationStatus.Cancelled, "Operacja została anulowana."); }
        catch (CompaniesPersistenceException) { return Plain(CompanyOperationStatus.PersistenceFailure, "Nie udało się zarchiwizować firmy. Spróbuj ponownie."); }
        catch (Exception) { return Plain(CompanyOperationStatus.PersistenceFailure, "Nie udało się sprawdzić zależności firmy. Spróbuj ponownie."); }
        finally { await store.ResetTrackingAsync(); }
    }

    private static CompanyOperationResult<CompanyDetails> FromSave(CompaniesSaveStatus status, Company company, string success) => status switch
    {
        CompaniesSaveStatus.Success => new(CompanyOperationStatus.Success, success, CompanyOperationResult.EmptyErrors, MapDetails(company)),
        CompaniesSaveStatus.ConcurrencyConflict => Result<CompanyDetails>(CompanyOperationStatus.ConcurrencyConflict, "Firma została zmieniona przez inną operację. Odśwież dane."),
        CompaniesSaveStatus.DuplicateTaxIdentificationNumber => Result<CompanyDetails>(CompanyOperationStatus.DuplicateTaxIdentificationNumber, "Firma z tym numerem podatkowym już istnieje."),
        _ => throw new InvalidOperationException("Unsupported Companies save status."),
    };
    private static CompanyOperationResult<T> Validation<T>(ArgumentException exception) =>
        new(CompanyOperationStatus.ValidationFailed, "Popraw wskazane dane.", new Dictionary<string, string[]> { [exception.ParamName ?? "Company"] = ["Wartość jest nieprawidłowa."] }, default);
    private static CompanyOperationResult<T> Result<T>(CompanyOperationStatus status, string message) => new(status, message, CompanyOperationResult.EmptyErrors, default);
    private static CompanyOperationResult Plain(CompanyOperationStatus status, string message) => new(status, message, CompanyOperationResult.EmptyErrors);
    private static CompanyListItem MapList(Company c) => new(c.Id.Value, c.LegalName, c.DisplayName, c.TaxIdentificationNumber?.Value, c.CountryCode, c.BaseCurrency.Value, ToApplication(c.Status), c.UpdatedAt, c.Version.Value);
    private static CompanyDetails MapDetails(Company c) => new(c.Id.Value, c.LegalName, c.DisplayName, c.TaxIdentificationNumber?.Value, c.CountryCode, c.BaseCurrency.Value, c.DefaultTimeZone, ToApplication(c.Status), c.CreatedAt, c.UpdatedAt, c.CreatedBy.Value, c.UpdatedBy.Value, c.Version.Value);
    private static CompanyStatus ToDomain(CompanyStatusValue status) => status switch
    {
        CompanyStatusValue.Draft => CompanyStatus.Draft,
        CompanyStatusValue.Active => CompanyStatus.Active,
        CompanyStatusValue.Suspended => CompanyStatus.Suspended,
        _ => throw new ArgumentException("Unsupported company status.", nameof(status)),
    };
    private static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) throw new ArgumentException("Base currency is required.", nameof(currency));
        return currency.Trim().ToUpperInvariant();
    }
    private static CompanyStatusValue ToApplication(CompanyStatus status) => status switch
    {
        CompanyStatus.Draft => CompanyStatusValue.Draft,
        CompanyStatus.Active => CompanyStatusValue.Active,
        CompanyStatus.Suspended => CompanyStatusValue.Suspended,
        CompanyStatus.Archived => throw new InvalidOperationException("Archived companies are not available in CRUD reads."),
        _ => throw new InvalidOperationException("Unsupported company status."),
    };
}
