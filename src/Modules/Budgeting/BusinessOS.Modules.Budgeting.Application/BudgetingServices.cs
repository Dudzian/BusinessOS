using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.Budgeting.Application;

public static class BudgetingServices
{
    public static IServiceCollection AddBudgetingModule(
        this IServiceCollection services)
    {
        return services;
    }
}
