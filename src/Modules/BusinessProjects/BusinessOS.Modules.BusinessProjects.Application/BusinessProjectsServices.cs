using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.BusinessProjects.Application;

public static class BusinessProjectsServices
{
    public static IServiceCollection AddBusinessProjectsModule(
        this IServiceCollection services)
    {
        return services;
    }
}
