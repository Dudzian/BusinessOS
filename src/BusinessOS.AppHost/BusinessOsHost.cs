using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.Companies.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BusinessOS.AppHost;

public static class BusinessOsHost
{
    public static IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddCompaniesModule();
                services.AddBusinessProjectsModule();
                services.AddBudgetingModule();
            })
            .Build();
    }
}
