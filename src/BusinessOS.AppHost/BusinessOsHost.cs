using System.Reflection;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.BusinessProjects.Application;
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
                services.AddSingleton<ICompaniesExecutionContext, LocalCompaniesExecutionContext>();
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
                services.AddSingleton<IApplicationStartupCoordinator, ApplicationStartupCoordinator>();
                services.AddSingleton<ICompaniesRecoveryWorkflow, CompaniesRecoveryWorkflow>();
                services.AddBusinessProjectsModule();
                services.AddBudgetingModule();
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

// Replaced by identity/workspace context when multi-user support is introduced.
internal sealed class LocalCompaniesExecutionContext : ICompaniesExecutionContext
{
    public OrganizationId OrganizationId { get; } = new(new Guid("11111111-1111-1111-1111-111111111111"));
    public UserId UserId { get; } = new(new Guid("22222222-2222-2222-2222-222222222222"));
}
