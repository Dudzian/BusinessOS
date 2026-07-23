using System.Reflection;

namespace BusinessOS.AppHost;

public sealed record ProductInfo(string Name, string Version)
{
    public const string ProductName = "BusinessOS";

    public static ProductInfo FromAssembly(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = assembly.GetName().Version?.ToString();
        }

        return new ProductInfo(ProductName, string.IsNullOrWhiteSpace(version) ? "0.0.0" : version);
    }
}
