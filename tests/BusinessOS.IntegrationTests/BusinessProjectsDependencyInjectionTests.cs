using System.Reflection;
using BusinessOS.AppHost;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.Companies.Application;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace BusinessOS.IntegrationTests;

public sealed class BusinessProjectsDependencyInjectionTests
{
    [Fact]
    public void AppHost_composes_cross_module_services_without_a_dependency_cycle()
    {
        using var host = BusinessOsHost.BuildHost(Assembly.GetExecutingAssembly());
        host.Services.GetRequiredService<ICompaniesCrudService>().Should().NotBeNull();
        host.Services.GetRequiredService<ICompaniesLookupService>().Should().NotBeNull();
        host.Services.GetRequiredService<IBusinessProjectsCrudService>().Should().NotBeNull();
        host.Services.GetRequiredService<IBusinessProjectCompanyAccess>().Should().NotBeNull();
        host.Services.GetServices<ICompanyArchiveConstraint>().Should().ContainSingle();
    }
}
