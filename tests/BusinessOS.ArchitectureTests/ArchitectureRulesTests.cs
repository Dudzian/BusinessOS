using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
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

    [Fact]
    public void Project_references_follow_block_1_boundaries()
    {
        var root = FindRepositoryRoot();
        var references = LoadProjectReferences(root);

        foreach (var (project, projectReferences) in references)
        {
            var projectName = Path.GetFileNameWithoutExtension(project);

            if (projectName.EndsWith(".Domain", StringComparison.Ordinal))
            {
                projectReferences.Should().NotContain(reference => reference.Contains(".Application", StringComparison.Ordinal));
                projectReferences.Should().NotContain(reference => reference.Contains("BusinessOS.Desktop", StringComparison.Ordinal));
            }

            if (projectName.EndsWith(".Application", StringComparison.Ordinal))
            {
                projectReferences.Should().NotContain(reference => reference.Contains("BusinessOS.Desktop", StringComparison.Ordinal));
            }

            if (projectName.StartsWith("BusinessOS.Modules.", StringComparison.Ordinal))
            {
                projectReferences.Should().NotContain(reference => reference.Contains("BusinessOS.Desktop", StringComparison.Ordinal));
            }
        }

        references["src/BusinessOS.Desktop/BusinessOS.Desktop.csproj"]
            .Should().Contain("src/BusinessOS.AppHost/BusinessOS.AppHost.csproj");
        references["src/BusinessOS.Desktop/BusinessOS.Desktop.csproj"]
            .Should().NotContain(reference => reference.Contains(".Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void Cross_platform_solution_filter_excludes_desktop_project()
    {
        var projects = ReadSolutionFilterProjects(Path.Combine(FindRepositoryRoot(), "BusinessOS.CrossPlatform.slnf"));

        projects.Should().NotBeEmpty();
        projects.Should().NotContain("src/BusinessOS.Desktop/BusinessOS.Desktop.csproj");
        projects.Should().NotContain("src/BusinessOS.AppHost/BusinessOS.AppHost.csproj");
    }

    [Fact]
    public void Project_references_do_not_contain_cycles()
    {
        var references = LoadProjectReferences(FindRepositoryRoot());
        var cycle = FindProjectReferenceCycle(references);

        cycle.Should().BeNull($"project references must be acyclic, but found: {FormatCycle(cycle)}");
    }

    [Fact]
    public void Solution_filter_parser_reads_desktop_only_from_projects_array()
    {
        using var temp = TemporaryDirectory.Create();
        var filter = Path.Combine(temp.Path, "BusinessOS.CrossPlatform.slnf");
        File.WriteAllText(filter, """
            {
              "solution": {
                "path": "BusinessOS.sln",
                "notes": "BusinessOS.Desktop appears here but is not a project item",
                "projects": [
                  "src/BusinessOS.Desktop/BusinessOS.Desktop.csproj"
                ]
              }
            }
            """);

        var projects = ReadSolutionFilterProjects(filter);

        projects.Should().Contain("src/BusinessOS.Desktop/BusinessOS.Desktop.csproj");
        projects.Should().NotContain("BusinessOS.Desktop");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"solution\": {} }")]
    [InlineData("{ \"solution\": { \"projects\": [] } }")]
    [InlineData("not json")]
    public void Solution_filter_parser_rejects_invalid_or_empty_project_lists(string content)
    {
        using var temp = TemporaryDirectory.Create();
        var filter = Path.Combine(temp.Path, "BusinessOS.CrossPlatform.slnf");
        File.WriteAllText(filter, content);

        var read = () => ReadSolutionFilterProjects(filter);

        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Project_reference_parser_reads_multiline_and_single_quoted_references()
    {
        using var temp = TemporaryDirectory.Create();
        var projectDirectory = Path.Combine(temp.Path, "src", "App");
        var referencedProjectDirectory = Path.Combine(temp.Path, "src", "Domain");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(referencedProjectDirectory);
        File.WriteAllText(Path.Combine(referencedProjectDirectory, "Domain.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
        var project = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference
                  Include='../Domain/Domain.csproj'
                  PrivateAssets="all">
                  <Aliases>Domain</Aliases>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """);

        var references = ReadProjectReferences(temp.Path, project);

        references.Should().Contain("src/Domain/Domain.csproj");
    }

    [Fact]
    public void Project_reference_parser_rejects_empty_include()
    {
        using var temp = TemporaryDirectory.Create();
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="" />
              </ItemGroup>
            </Project>
            """);

        var read = () => ReadProjectReferences(temp.Path, project);

        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Project_reference_cycle_detector_reports_cycle_path()
    {
        var references = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.csproj"] = ["B.csproj"],
            ["B.csproj"] = ["C.csproj"],
            ["C.csproj"] = ["A.csproj"],
        };

        var cycle = FindProjectReferenceCycle(references);

        cycle.Should().NotBeNull();
        cycle!.Should().Equal("A.csproj", "B.csproj", "C.csproj", "A.csproj");
    }

    [Fact]
    public void Project_reference_cycle_detector_accepts_acyclic_graph()
    {
        var references = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["A.csproj"] = ["B.csproj"],
            ["B.csproj"] = ["C.csproj"],
            ["C.csproj"] = [],
        };

        var cycle = FindProjectReferenceCycle(references);

        cycle.Should().BeNull();
    }

    [Fact]
    public void BusinessOS_project_enumeration_ignores_cache_and_keeps_source_projects()
    {
        using var temp = TemporaryDirectory.Create();
        var sourceProjectDirectory = Path.Combine(temp.Path, "src", "Example");
        var cacheProjectDirectory = Path.Combine(temp.Path, ".cache", "nuget", "Fake.Package");
        Directory.CreateDirectory(sourceProjectDirectory);
        Directory.CreateDirectory(cacheProjectDirectory);
        File.WriteAllText(Path.Combine(sourceProjectDirectory, "Example.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
        File.WriteAllText(Path.Combine(cacheProjectDirectory, "Fake.Package.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");

        var references = LoadProjectReferences(temp.Path);

        references.Keys.Should().Contain("src/Example/Example.csproj");
        references.Keys.Should().NotContain(".cache/nuget/Fake.Package/Fake.Package.csproj");
    }

    [Fact]
    public void Project_reference_parser_rejects_missing_targets()
    {
        using var temp = TemporaryDirectory.Create();
        var projectDirectory = Path.Combine(temp.Path, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        var project = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Missing/Missing.csproj" />
              </ItemGroup>
            </Project>
            """);

        var read = () => ReadProjectReferences(temp.Path, project);

        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*src/App/App.csproj*../Missing/Missing.csproj*src/Missing/Missing.csproj*");
    }

    [Fact]
    public void Project_reference_parser_rejects_targets_outside_repository()
    {
        using var temp = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var outsideProject = Path.Combine(outside.Path, "Outside.csproj");
        File.WriteAllText(outsideProject, """<Project Sdk="Microsoft.NET.Sdk" />""");
        var projectDirectory = Path.Combine(temp.Path, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        var project = Path.Combine(projectDirectory, "App.csproj");
        var include = Path.GetRelativePath(projectDirectory, outsideProject);
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="{include}" />
              </ItemGroup>
            </Project>
            """);

        var read = () => ReadProjectReferences(temp.Path, project);

        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*App.csproj*Outside.csproj*outside the repository*");
    }


    [Fact]
    public void BusinessOS_project_enumeration_accepts_repo_under_parent_artifacts_directory()
    {
        using var temp = TemporaryDirectory.Create();
        var repositoryRoot = Path.Combine(temp.Path, "artifacts", "BusinessOS");
        var projectDirectory = Path.Combine(repositoryRoot, "src", "Example");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "Example.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");

        var references = LoadProjectReferences(repositoryRoot);

        references.Keys.Should().Contain("src/Example/Example.csproj");
    }

    [Fact]
    public void Project_reference_parser_rejects_nested_docs_src_target()
    {
        using var temp = TemporaryDirectory.Create();
        var appDirectory = Path.Combine(temp.Path, "src", "App");
        var fakeDirectory = Path.Combine(temp.Path, "docs", "src");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(fakeDirectory);
        File.WriteAllText(Path.Combine(fakeDirectory, "Fake.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
        var appProject = Path.Combine(appDirectory, "App.csproj");
        File.WriteAllText(appProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../docs/src/Fake.csproj" />
              </ItemGroup>
            </Project>
            """);

        var read = () => ReadProjectReferences(temp.Path, appProject);

        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*src/App/App.csproj*../../docs/src/Fake.csproj*docs/src/Fake.csproj*outside the allowed BusinessOS project scope*");
    }

    [Fact]
    public void Project_reference_parser_rejects_existing_non_csproj_target()
    {
        using var temp = TemporaryDirectory.Create();
        var appDirectory = Path.Combine(temp.Path, "src", "App");
        var domainDirectory = Path.Combine(temp.Path, "src", "Domain");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(domainDirectory);
        File.WriteAllText(Path.Combine(domainDirectory, "readme.txt"), "not a project");
        var appProject = Path.Combine(appDirectory, "App.csproj");
        File.WriteAllText(appProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Domain/readme.txt" />
              </ItemGroup>
            </Project>
            """);

        var read = () => ReadProjectReferences(temp.Path, appProject);

        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*src/App/App.csproj*../Domain/readme.txt*src/Domain/readme.txt*not a .csproj project*");
    }

    [Fact]
    public void Project_scope_accepts_directory_name_starting_with_two_dots_inside_repository()
    {
        using var temp = TemporaryDirectory.Create();
        var projectDirectory = Path.Combine(temp.Path, "src", "..Example");
        Directory.CreateDirectory(projectDirectory);
        var project = Path.Combine(projectDirectory, "Example.csproj");
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk" />""");

        IsPathInsideDirectory(temp.Path, project).Should().BeTrue();
        IsBusinessOsProjectPath(temp.Path, project).Should().BeTrue();
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

    private static string[] ReadSolutionFilterProjects(string solutionFilterPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(solutionFilterPath));
            if (!document.RootElement.TryGetProperty("solution", out var solution) ||
                solution.ValueKind != JsonValueKind.Object ||
                !solution.TryGetProperty("projects", out var projectsElement) ||
                projectsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Solution filter must contain a solution.projects array.");
            }

            var projects = projectsElement.EnumerateArray()
                .Select(project => project.ValueKind == JsonValueKind.String ? project.GetString() : null)
                .Where(project => !string.IsNullOrWhiteSpace(project))
                .Select(project => NormalizeRepositoryPath(project!))
                .ToArray();

            if (projects.Length == 0)
            {
                throw new InvalidOperationException("Solution filter must contain at least one project.");
            }

            return projects;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Solution filter is not valid JSON.", ex);
        }
    }

    private static Dictionary<string, string[]> LoadProjectReferences(string repositoryRoot)
    {
        return EnumerateBusinessOsProjects(repositoryRoot)
            .ToDictionary(
                path => ToRepositoryRelativePath(repositoryRoot, path),
                path => ReadProjectReferences(repositoryRoot, path),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateBusinessOsProjects(string repositoryRoot)
    {
        foreach (var rootName in new[] { "src", "tests" })
        {
            var root = Path.Combine(repositoryRoot, rootName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                if (!IsGeneratedOrToolPath(repositoryRoot, project))
                {
                    yield return project;
                }
            }
        }
    }

    private static string[] ReadProjectReferences(string repositoryRoot, string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath) ?? repositoryRoot;
        var document = XDocument.Load(projectPath);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => ResolveProjectReference(repositoryRoot, projectPath, projectDirectory, element.Attribute("Include")?.Value))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveProjectReference(string repositoryRoot, string projectPath, string projectDirectory, string? include)
    {
        var sourceProject = ToRepositoryRelativePath(repositoryRoot, projectPath);
        if (string.IsNullOrWhiteSpace(include))
        {
            throw new InvalidOperationException($"ProjectReference in {sourceProject} must include a non-empty Include attribute.");
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include));
        var resolvedRelativePath = ToRepositoryRelativePath(repositoryRoot, resolvedPath);
        if (!IsPathInsideDirectory(repositoryRoot, resolvedPath))
        {
            throw new InvalidOperationException($"ProjectReference in {sourceProject} includes '{include}' resolved to '{resolvedPath}', which is outside the repository.");
        }

        if (!File.Exists(resolvedPath))
        {
            throw new InvalidOperationException($"ProjectReference in {sourceProject} includes '{include}' resolved to missing project '{resolvedRelativePath}'.");
        }

        if (!IsBusinessOsProjectPath(repositoryRoot, resolvedPath))
        {
            throw new InvalidOperationException($"ProjectReference in {sourceProject} includes '{include}' resolved to '{resolvedRelativePath}', which is outside the allowed BusinessOS project scope.");
        }

        if (!string.Equals(Path.GetExtension(resolvedPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ProjectReference in {sourceProject} includes '{include}' resolved to '{resolvedRelativePath}', which is not a .csproj project.");
        }

        return resolvedRelativePath;
    }

    private static string[]? FindProjectReferenceCycle(IReadOnlyDictionary<string, string[]> references)
    {
        var states = references.Keys.ToDictionary(project => project, _ => VisitState.Unvisited, StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var project in references.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (states[project] == VisitState.Unvisited && TryVisit(project, references, states, path, out var cycle))
            {
                return cycle;
            }
        }

        return null;
    }

    private static bool TryVisit(
        string project,
        IReadOnlyDictionary<string, string[]> references,
        IDictionary<string, VisitState> states,
        List<string> path,
        out string[] cycle)
    {
        if (!states.TryGetValue(project, out var state))
        {
            states[project] = VisitState.Unvisited;
            state = VisitState.Unvisited;
        }

        if (state == VisitState.Visited)
        {
            cycle = [];
            return false;
        }

        if (state == VisitState.Visiting)
        {
            var cycleStart = path.FindIndex(item => string.Equals(item, project, StringComparison.OrdinalIgnoreCase));
            cycle = [.. path.Skip(cycleStart), project];
            return true;
        }

        states[project] = VisitState.Visiting;
        path.Add(project);

        if (references.TryGetValue(project, out var projectReferences))
        {
            foreach (var reference in projectReferences.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (TryVisit(reference, references, states, path, out cycle))
                {
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        states[project] = VisitState.Visited;
        cycle = [];
        return false;
    }

    private static string FormatCycle(string[]? cycle)
    {
        return cycle is null ? "<none>" : string.Join(" -> ", cycle);
    }

    private static string ToRepositoryRelativePath(string repositoryRoot, string path)
    {
        return NormalizeRepositoryPath(Path.GetRelativePath(repositoryRoot, path));
    }

    private static string NormalizeRepositoryPath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsGeneratedOrToolPath(string repositoryRoot, string path)
    {
        string[] excludedDirectories = ["bin", "obj", ".cache", ".tools", "artifacts", ".git", "TestResults"];
        var relativePath = NormalizeRepositoryPath(Path.GetRelativePath(Path.GetFullPath(repositoryRoot), Path.GetFullPath(path)));
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => excludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsBusinessOsProjectPath(string repositoryRoot, string path)
    {
        if (IsGeneratedOrToolPath(repositoryRoot, path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var sourceRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "src"));
        var testsRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "tests"));
        return IsPathInsideDirectory(sourceRoot, fullPath) || IsPathInsideDirectory(testsRoot, fullPath);
    }

    private static bool IsPathInsideDirectory(string directory, string path)
    {
        var relativePath = NormalizeRepositoryPath(Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path)));
        return relativePath != ".." &&
            !relativePath.StartsWith("../", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relativePath);
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

    private enum VisitState
    {
        Unvisited,
        Visiting,
        Visited,
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"BusinessOS.ArchitectureTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
