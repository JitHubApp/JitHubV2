using System.Xml.Linq;
using JitHub.WinUI.Tests.TestSupport;
using Xunit;

namespace JitHub.WinUI.Tests;

/// <summary>
/// Checks the solution's platform mappings and the Windows SDK build-tools pin
/// against what MSBuild evaluates for each project, so inherited
/// <c>Directory.Build.props</c> values and central package versions count the
/// way a build sees them.
/// </summary>
public sealed class SolutionConfigurationContractTests
{
    private const string BuildToolsPackage = "Microsoft.Windows.SDK.BuildTools";
    private const string BuildToolsVersionProperty = "JitHubWindowsSdkBuildToolsVersion";

    private static readonly Dictionary<string, string> Release = new(StringComparer.Ordinal)
    {
        ["Configuration"] = "Release",
    };

    [Theory]
    [InlineData("MarkdownRenderer/tests/aot/MarkdownRenderer.AotSmoke/MarkdownRenderer.AotSmoke.csproj")]
    [InlineData("MarkdownRenderer/tests/MarkdownRenderer.Pixel.Tests/MarkdownRenderer.Pixel.Tests.csproj")]
    [InlineData("MarkdownRenderer/tests/aot/MarkdownRenderer.Svg.Resvg.AotSmoke/MarkdownRenderer.Svg.Resvg.AotSmoke.csproj")]
    [InlineData("MarkdownRenderer/tests/MarkdownRenderer.Svg.Resvg.Tests/MarkdownRenderer.Svg.Resvg.Tests.csproj")]
    public void SelfContainedManifestHosts_ReferencePinnedSdkBuildTools(string relativePath)
    {
        string root = FindRepositoryRoot();
        MsBuildEvaluation host = MsBuild.Evaluate(
            ProjectPath(root, relativePath),
            Release,
            [],
            ["PackageReference", "PackageVersion"]);

        // The renderer pins package versions centrally; the reference itself
        // must not carry a competing version.
        MsBuildItem reference = Assert.Single(host.GetItems("PackageReference"), IsBuildTools);
        Assert.Empty(reference.GetMetadata("Version"));
        Assert.Empty(reference.GetMetadata("VersionOverride"));
        Assert.Equal("all", reference.GetMetadata("PrivateAssets"), ignoreCase: true);

        MsBuildItem pin = Assert.Single(host.GetItems("PackageVersion"), IsBuildTools);
        Assert.Equal(RepositoryBuildToolsVersion(root), pin.GetMetadata("Version"));
    }

    [Fact]
    public void WindowsSdkBuildToolsVersion_IsOneRepositoryPolicyInEveryScope()
    {
        // The app and the renderer subtree must build with the same tools, so
        // the version is one repository value that neither scope can drift from.
        string root = FindRepositoryRoot();
        MsBuildEvaluation app = EvaluateApp(root, ["PackageReference"]);
        string version = app.GetProperty(BuildToolsVersionProperty);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", version);
        Assert.Equal(version, Assert.Single(app.GetItems("PackageReference"), IsBuildTools).GetMetadata("Version"));

        MsBuildEvaluation renderer = MsBuild.Evaluate(
            ProjectPath(root, "MarkdownRenderer/src/MarkdownRenderer/MarkdownRenderer.csproj"),
            Release,
            [BuildToolsVersionProperty],
            ["PackageVersion"]);
        Assert.Equal(version, renderer.GetProperty(BuildToolsVersionProperty));
        Assert.Equal(version, Assert.Single(renderer.GetItems("PackageVersion"), IsBuildTools).GetMetadata("Version"));
    }

    [Fact]
    public void ExplicitSolutionPlatformMappings_TargetDeclaredProjectPlatforms()
    {
        string root = FindRepositoryRoot();
        SolutionProject[] projects = ReadSolutionProjects(root);
        IReadOnlyDictionary<string, string[]> declaredPlatforms = EvaluateDeclaredPlatforms(root, projects);

        List<string> failures = [];
        foreach (SolutionProject project in projects)
        {
            string[] declared = declaredPlatforms[project.FullPath];
            failures.AddRange(project.Mappings
                .Where(mapping => !declared.Contains(mapping.ProjectPlatform, StringComparer.OrdinalIgnoreCase))
                .Select(mapping => $"{project.RelativePath}: {mapping.SolutionConfiguration} -> {mapping.ProjectPlatform} (declares {string.Join(';', declared)})"));
        }

        Assert.True(failures.Count == 0, "Solution mappings to undeclared platforms: " + string.Join(", ", failures));
    }

    [Fact]
    public void ArchitectureSpecificProjects_MapDefaultSolutionConfigurations()
    {
        string root = FindRepositoryRoot();
        SolutionProject[] projects = ReadSolutionProjects(root);
        IReadOnlyDictionary<string, string[]> declaredPlatforms = EvaluateDeclaredPlatforms(root, projects);

        List<string> failures = [];
        foreach (SolutionProject project in projects)
        {
            string[] declared = declaredPlatforms[project.FullPath];
            if (declared.Contains("AnyCPU", StringComparer.OrdinalIgnoreCase))
                continue;

            foreach (string configuration in new[] { "Debug", "Release" })
            {
                SolutionPlatformMapping[] defaults = project.Mappings
                    .Where(mapping => string.Equals(
                        mapping.SolutionConfiguration,
                        $"{configuration}|AnyCPU",
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (defaults.Length != 1 || !declared.Contains(defaults[0].ProjectPlatform, StringComparer.OrdinalIgnoreCase))
                    failures.Add($"{project.RelativePath}: {configuration}|Any CPU");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Architecture-specific projects without one declared default mapping: " + string.Join(", ", failures));
    }

    private static bool IsBuildTools(MsBuildItem package) =>
        string.Equals(package.Identity, BuildToolsPackage, StringComparison.OrdinalIgnoreCase);

    private static string RepositoryBuildToolsVersion(string root) =>
        EvaluateApp(root, []).GetProperty(BuildToolsVersionProperty);

    private static MsBuildEvaluation EvaluateApp(string root, string[] itemTypes) =>
        MsBuild.Evaluate(
            ProjectPath(root, "JitHub.WinUI/JitHub.WinUI.csproj"),
            new Dictionary<string, string>(Release, StringComparer.Ordinal) { ["Platform"] = "x64" },
            [BuildToolsVersionProperty],
            itemTypes);

    // GetTargetFrameworks returns each project's evaluated Platforms, the list
    // ProjectReference platform negotiation also reads. One probe evaluates the
    // whole solution in a single MSBuild run.
    private static IReadOnlyDictionary<string, string[]> EvaluateDeclaredPlatforms(string root, SolutionProject[] projects)
    {
        string probe = Path.Combine(Path.GetTempPath(), $"jithub-platform-probe-{Guid.NewGuid():N}.proj");
        new XDocument(
            new XElement(
                "Project",
                new XElement(
                    "ItemGroup",
                    projects.Select(project => new XElement(
                        "ProbeProject",
                        new XAttribute("Include", project.FullPath),
                        new XAttribute("AdditionalProperties", "Configuration=Release")))),
                new XElement(
                    "Target",
                    new XAttribute("Name", "Probe"),
                    new XAttribute("Returns", "@(ProbeResult)"),
                    new XElement(
                        "MSBuild",
                        new XAttribute("Projects", "@(ProbeProject)"),
                        new XAttribute("Targets", "GetTargetFrameworks"),
                        new XElement(
                            "Output",
                            new XAttribute("TaskParameter", "TargetOutputs"),
                            new XAttribute("ItemName", "ProbeResult"))))))
            .Save(probe);
        try
        {
            return MsBuild.GetTargetOutputs(probe, "Probe", new Dictionary<string, string>(), workingDirectory: root)
                .ToDictionary(
                    static result => Path.GetFullPath(result.Identity),
                    static result => result.GetMetadata("Platforms")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(NormalizePlatform)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private static SolutionProject[] ReadSolutionProjects(string root) =>
        XDocument.Load(Path.Combine(root, "JitHub.slnx"))
            .Root!
            .Descendants("Project")
            .Select(entry =>
            {
                string relativePath = (string)entry.Attribute("Path")!;
                return new SolutionProject(
                    relativePath,
                    ProjectPath(root, relativePath),
                    entry.Elements("Platform")
                        .Select(static mapping => new SolutionPlatformMapping(
                            NormalizePlatform((string)mapping.Attribute("Solution")!),
                            NormalizePlatform((string)mapping.Attribute("Project")!)))
                        .ToArray());
            })
            .ToArray();

    private static string ProjectPath(string root, string relativePath) =>
        Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string NormalizePlatform(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing JitHub.slnx.");
    }

    private sealed record SolutionProject(string RelativePath, string FullPath, SolutionPlatformMapping[] Mappings);

    private sealed record SolutionPlatformMapping(string SolutionConfiguration, string ProjectPlatform);
}
