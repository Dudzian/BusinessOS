using System.Reflection;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;
using BusinessOS.Modules.Companies.Application;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BusinessOS.BuildingBlocks.Domain.Ids;

namespace BusinessOS.AppHost;

public static class BusinessOsHost
{
    public static IHost BuildHost(Assembly productAssembly)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(ProductInfo.FromAssembly(productAssembly));
                services.AddSingleton<LocalCompaniesExecutionContext>();
                services.AddSingleton<ICompaniesExecutionContext>(sp => sp.GetRequiredService<LocalCompaniesExecutionContext>());
                services.AddSingleton<IBusinessProjectsExecutionContext>(sp => sp.GetRequiredService<LocalCompaniesExecutionContext>());
                services.AddSingleton(TimeProvider.System);
                services.AddCompaniesModule();
                services.AddCompaniesPersistence(options =>
                {
                    var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var configuredPath = context.Configuration["BusinessOS:Persistence:DatabasePath"];
                    options.DatabasePath = string.IsNullOrWhiteSpace(configuredPath)
                        ? Path.Combine(localData, "BusinessOS", "Data", "businessos.db")
                        : configuredPath;
                    options.BackupDirectory = context.Configuration["BusinessOS:Persistence:BackupDirectory"]
                        ?? Path.Combine(localData, "BusinessOS", "Backups", "Companies");
                    var configuredMaxBackups = context.Configuration["BusinessOS:Persistence:MaxBackups"];
                    options.MaxBackups = ParseMaxBackups(configuredMaxBackups);
                });
                var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var projectsDatabasePath = context.Configuration["BusinessOS:Persistence:DatabasePath"];
                projectsDatabasePath = string.IsNullOrWhiteSpace(projectsDatabasePath)
                    ? Path.Combine(localData, "BusinessOS", "Data", "businessos.db") : projectsDatabasePath;
                services.AddBusinessProjectsPersistence(projectsDatabasePath);
                services.AddBudgetingPersistence(projectsDatabasePath);
                services.AddSingleton<IDatabaseMigrationHistorySource, BusinessProjectsMigrationHistorySource>();
                services.AddTransient<IBusinessProjectCompanyAccess, BusinessProjectCompanyAccess>();
                services.AddTransient<ICompanyArchiveConstraint, BusinessProjectsCompanyArchiveConstraint>();
                services.AddSingleton<IApplicationStartupCoordinator, ApplicationStartupCoordinator>();
                services.AddSingleton<ICompaniesRecoveryWorkflow, CompaniesRecoveryWorkflow>();
                services.AddBusinessProjectsModule();
                services.AddBudgetingModule();
                services.AddTransient<IBudgetingProjectLookup, BudgetingProjectLookup>();
            })
            .Build();
    }

    public static int ParseMaxBackups(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue)) return 10;
        if (!int.TryParse(configuredValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new InvalidOperationException("BusinessOS persistence MaxBackups must be a positive Int32 value.");
        }

        return value;
    }
}

internal sealed class BudgetingProjectLookup(IBusinessProjectsCrudService projects, ICompaniesLookupService companies) : IBudgetingProjectLookup
{
    public async Task<BudgetProjectInfo?> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = await projects.GetAsync(projectId, cancellationToken);
            return project is null ? null : new(project.Id, project.Name, project.BaseCurrency,
                project.Status is not (BusinessProjectStatusValue.Closed or BusinessProjectStatusValue.Cancelled));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (BusinessProjectsReadException exception) { throw new BudgetingProjectLookupException("Business project lookup failed.", exception); }
        catch (CompaniesLookupException exception) { throw new BudgetingProjectLookupException("Company lookup failed.", exception); }
    }

    public async Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = new List<BudgetProjectInfo>();
            foreach (var company in await companies.ListActiveAsync(cancellationToken))
                foreach (var project in await projects.ListAsync(company.Id, null, cancellationToken))
                    if (project.Status is not (BusinessProjectStatusValue.Closed or BusinessProjectStatusValue.Cancelled)) result.Add(new(project.Id, project.Name, project.BaseCurrency, true));
            return result.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (BusinessProjectsReadException exception) { throw new BudgetingProjectLookupException("Business project lookup failed.", exception); }
        catch (CompaniesLookupException exception) { throw new BudgetingProjectLookupException("Company lookup failed.", exception); }
    }
}

// Replaced by identity/workspace context when multi-user support is introduced.
internal sealed class LocalCompaniesExecutionContext : ICompaniesExecutionContext, IBusinessProjectsExecutionContext
{
    public OrganizationId OrganizationId { get; } = new(new Guid("11111111-1111-1111-1111-111111111111"));
    public UserId UserId { get; } = new(new Guid("22222222-2222-2222-2222-222222222222"));
}

internal sealed class BusinessProjectCompanyAccess(ICompaniesLookupService companies) : IBusinessProjectCompanyAccess
{
    public async Task<BusinessProjectCompanyInfo?> GetAccessibleCompanyAsync(Guid companyId, CancellationToken cancellationToken)
    {
        try
        {
            var company = await companies.GetActiveAsync(companyId, cancellationToken);
            return company is null ? null : new(company.Id, company.DisplayName, company.BaseCurrency);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CompaniesLookupException exception)
        {
            throw new BusinessProjectCompanyAccessException("Company access lookup failed.", exception);
        }
    }

}

internal sealed class BusinessProjectsCompanyArchiveConstraint(IBusinessProjectsCompanyConstraintReader projects) : ICompanyArchiveConstraint
{
    public async Task<CompanyArchiveConstraintResult> EvaluateAsync(Guid companyId, CancellationToken cancellationToken) =>
        await projects.HasNonArchivedProjectsAsync(companyId, cancellationToken)
            ? new(false, "Najpierw zarchiwizuj wszystkie projekty firmy.")
            : CompanyArchiveConstraintResult.Allowed;
}
