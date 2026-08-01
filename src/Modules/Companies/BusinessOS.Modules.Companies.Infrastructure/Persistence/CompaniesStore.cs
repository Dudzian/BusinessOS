using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.Modules.Companies.Application;
using BusinessOS.Modules.Companies.Domain;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

internal sealed class CompaniesStore(IDbContextFactory<CompaniesDbContext> contextFactory) : ICompaniesStore, IAsyncDisposable
{
    private CompaniesDbContext? trackedContext;

    public async Task<IReadOnlyList<Company>> ListAsync(OrganizationId organizationId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            return await db.Companies.AsNoTracking().Where(company => company.OrganizationId == organizationId)
                .OrderBy(company => company.DisplayName).ThenBy(company => company.LegalName).ThenBy(company => company.Id)
                .ToListAsync(cancellationToken);
        }
        catch (DbException exception) { throw Failure(exception); }
    }

    public async Task<Company?> GetAsync(OrganizationId organizationId, CompanyId companyId, bool tracked, CancellationToken cancellationToken)
    {
        if (!tracked)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            try { return await db.Companies.AsNoTracking().SingleOrDefaultAsync(company => company.OrganizationId == organizationId && company.Id == companyId, cancellationToken); }
            catch (DbException exception) { throw Failure(exception); }
        }

        await ResetTrackedContextAsync();
        trackedContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        try { return await trackedContext.Companies.SingleOrDefaultAsync(company => company.OrganizationId == organizationId && company.Id == companyId, cancellationToken); }
        catch (DbException exception) { await ResetTrackedContextAsync(); throw Failure(exception); }
    }

    public async Task<bool> TaxIdExistsAsync(OrganizationId organizationId, string taxId, CompanyId? exceptCompanyId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            return await db.Companies.AsNoTracking().AnyAsync(company => company.OrganizationId == organizationId &&
                company.TaxIdentificationNumber == new TaxIdentificationNumber(taxId) &&
                (exceptCompanyId == null || company.Id != exceptCompanyId.Value), cancellationToken);
        }
        catch (DbException exception) { throw Failure(exception); }
    }

    public async Task AddAsync(Company company, CancellationToken cancellationToken)
    {
        await ResetTrackedContextAsync();
        trackedContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        try { await trackedContext.Companies.AddAsync(company, cancellationToken); }
        catch (DbException exception) { await ResetTrackedContextAsync(); throw Failure(exception); }
    }

    public async Task<CompaniesSaveStatus> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (trackedContext is null) throw Failure(new InvalidOperationException("No tracked Companies operation is active."));
        try
        {
            await trackedContext.SaveChangesAsync(cancellationToken);
            return CompaniesSaveStatus.Success;
        }
        catch (DbUpdateException exception) { return TranslateSaveException(exception); }
        finally { await ResetTrackedContextAsync(); }
    }

    internal static bool IsActiveTaxIdConflict(DbUpdateException exception)
    {
        if (exception.InnerException is not SqliteException { SqliteExtendedErrorCode: 2067 } sqlite) return false;
        var message = sqlite.Message;
        return message.Contains("ux_companies_organization_tax_id_active", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("companies.organization_id", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("companies.tax_identification_number", StringComparison.OrdinalIgnoreCase));
    }

    internal static CompaniesSaveStatus TranslateSaveException(DbUpdateException exception)
    {
        if (exception is DbUpdateConcurrencyException) return CompaniesSaveStatus.ConcurrencyConflict;
        if (IsActiveTaxIdConflict(exception)) return CompaniesSaveStatus.DuplicateTaxIdentificationNumber;
        throw Failure(exception);
    }

    private static CompaniesPersistenceException Failure(Exception exception) =>
        new("Companies persistence operation failed.", exception);

    private async Task ResetTrackedContextAsync()
    {
        if (trackedContext is not null) await trackedContext.DisposeAsync();
        trackedContext = null;
    }

    public ValueTask DisposeAsync() => trackedContext is null ? ValueTask.CompletedTask : trackedContext.DisposeAsync();
}
