using System.Reflection;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.Companies.Application;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BusinessOS.AppHost;

public static class BusinessOsHost
{
    public static IHost BuildHost(Assembly productAssembly)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(ProductInfo.FromAssembly(productAssembly));
                services.AddCompaniesModule();
                services.AddCompaniesPersistence(options =>
                {
                    var configuredPath = context.Configuration["BusinessOS:Persistence:DatabasePath"];
                    options.DatabasePath = string.IsNullOrWhiteSpace(configuredPath)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusinessOS", "Data", "businessos.db")
                        : configuredPath;
                });
                services.AddBusinessProjectsModule();
                services.AddBudgetingModule();
            })
            .Build();
    }
}
