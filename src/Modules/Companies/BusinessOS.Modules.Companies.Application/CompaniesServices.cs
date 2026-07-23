using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.Companies.Application;

public static class CompaniesServices
{
    public static IServiceCollection AddCompaniesModule(
        this IServiceCollection services)
    {
        return services;
    }
}
