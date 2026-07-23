using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace BusinessOS.ArchitectureTests;

public sealed class ArchitectureRulesTests
{
    private static readonly Assembly[] DomainAssemblies =
    [
        typeof(BusinessOS.BuildingBlocks.Domain.Primitives.Money).Assembly,
        typeof(BusinessOS.Modules.Companies.Domain.Company).Assembly,
        typeof(BusinessOS.Modules.BusinessProjects.Domain.BusinessProject).Assembly,
        typeof(BusinessOS.Modules.Budgeting.Domain.Budget).Assembly,
    ];

    [Fact]
    public void Domain_assemblies_do_not_depend_on_framework_or_infrastructure()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.Sqlite",
            "Microsoft.UI.Xaml",
            "Microsoft.WindowsAppSDK",
            "BusinessOS.BuildingBlocks.Infrastructure",
        ];

        foreach (var assembly in DomainAssemblies)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name} must stay framework-independent");
        }
    }

    [Fact]
    public void Domain_types_are_not_declared_in_infrastructure_namespaces()
    {
        foreach (var assembly in DomainAssemblies)
        {
            assembly.GetTypes()
                .Select(type => type.Namespace ?? string.Empty)
                .Should()
                .NotContain(namespaceName => namespaceName.Contains("Infrastructure", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Repository_root_can_be_found_from_test_output_directory()
    {
        var root = FindRepositoryRoot();

        File.Exists(Path.Combine(root, "BusinessOS.sln")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "src")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "tests")).Should().BeTrue();
    }

    [Fact]
    public void Production_source_scan_detects_forbidden_text_in_temporary_source_file()
    {
        var root = FindRepositoryRoot();
        var tempFile = Path.Combine(root, "src", $"TemporaryForbiddenDependencyProbe.{Guid.NewGuid():N}.cs");

        try
        {
            File.WriteAllText(tempFile, "internal static class TemporaryForbiddenDependencyProbe { /* openpyxl */ }");

            var text = ReadProductionSourceText(root);
            text.Should().Contain("open" + "pyxl");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void Production_code_does_not_depend_on_python_or_excel_automation()
    {
        var text = ReadProductionSourceText(FindRepositoryRoot());

        text.Should().NotContain("open" + "pyxl");
        text.Should().NotContain("python" + ".exe");
        text.Should().NotContain("Microsoft.Office.Interop" + ".Excel");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BusinessOS.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing BusinessOS.sln.");
    }

    private static string ReadProductionSourceText(string repositoryRoot)
    {
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".csproj",
            ".props",
            ".targets",
        };

        var files = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .Where(path => !ContainsDirectory(path, "bin"))
            .Where(path => !ContainsDirectory(path, "obj"))
            .Where(path => !ContainsDirectory(path, "artifacts"));

        return string.Join('\n', files.Select(File.ReadAllText));
    }

    private static bool ContainsDirectory(string path, string directoryName)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return path
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, directoryName, StringComparison.OrdinalIgnoreCase));
    }
}
