using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class SolutionConfigurationContractTests
{
    [Theory]
    [InlineData("MarkdownRenderer/tests/aot/MarkdownRenderer.AotSmoke/MarkdownRenderer.AotSmoke.csproj")]
    [InlineData("MarkdownRenderer/tests/MarkdownRenderer.PixelTests/MarkdownRenderer.PixelTests.csproj")]
    [InlineData("MarkdownRenderer/tests/aot/MarkdownRenderer.Svg.Resvg.AotSmoke/MarkdownRenderer.Svg.Resvg.AotSmoke.csproj")]
    [InlineData("MarkdownRenderer/tests/MarkdownRenderer.Svg.Resvg.Tests/MarkdownRenderer.Svg.Resvg.Tests.csproj")]
    public void SelfContainedManifestHosts_ReferencePinnedSdkBuildTools(string relativePath)
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        XElement packageReference = Assert.Single(
            project.Root!
                .Elements("ItemGroup")
                .Elements("PackageReference"),
            static package => string.Equals(
                package.Attribute("Include")?.Value,
                "Microsoft.Windows.SDK.BuildTools",
                StringComparison.OrdinalIgnoreCase));

        // The renderer pins package versions centrally; the reference itself
        // must not carry a competing version.
        Assert.Null(packageReference.Attribute("Version"));
        Assert.Null(packageReference.Attribute("VersionOverride"));
        Assert.Equal("all", packageReference.Attribute("PrivateAssets")?.Value, ignoreCase: true);

        XDocument packages = XDocument.Load(Path.Combine(root, "MarkdownRenderer", "Directory.Packages.props"));
        XElement packageVersion = Assert.Single(
            packages.Root!
                .Elements("ItemGroup")
                .Elements("PackageVersion"),
            static package => string.Equals(
                package.Attribute("Include")?.Value,
                "Microsoft.Windows.SDK.BuildTools",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("$(JitHubWindowsSdkBuildToolsVersion)", packageVersion.Attribute("Version")?.Value);
    }

    [Fact]
    public void WindowsSdkBuildToolsVersion_IsDefinedOnceAndImportedByRendererProps()
    {
        string root = FindRepositoryRoot();
        XDocument repositoryProps = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        XDocument rendererProps = XDocument.Load(Path.Combine(root, "MarkdownRenderer", "Directory.Build.props"));

        string version = Assert.Single(
            repositoryProps.Root!
                .Elements("PropertyGroup")
                .Elements("JitHubWindowsSdkBuildToolsVersion")).Value;
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", version);

        // The renderer subtree inherits the repository policy instead of
        // repeating the version, so the two scopes cannot drift.
        Assert.Empty(rendererProps.Root!.Descendants("JitHubWindowsSdkBuildToolsVersion"));
        XElement import = Assert.Single(rendererProps.Root!.Elements("Import"));
        Assert.Contains(
            "GetPathOfFileAbove('Directory.Build.props'",
            import.Attribute("Project")?.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitSolutionPlatformMappings_TargetDeclaredProjectPlatforms()
    {
        string root = FindRepositoryRoot();
        XDocument solution = XDocument.Load(Path.Combine(root, "JitHub.slnx"));

        foreach (XElement projectEntry in solution.Root!.Descendants("Project"))
        {
            XElement[] mappings = projectEntry.Elements("Platform").ToArray();
            if (mappings.Length == 0)
            {
                continue;
            }

            string relativePath = Assert.IsType<XAttribute>(projectEntry.Attribute("Path")).Value;
            string[] declaredPlatforms = GetDeclaredPlatforms(root, relativePath);

            foreach (XElement mapping in mappings)
            {
                string projectPlatform = NormalizePlatform(
                    Assert.IsType<XAttribute>(mapping.Attribute("Project")).Value);

                Assert.Contains(
                    projectPlatform,
                    declaredPlatforms,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ArchitectureSpecificProjects_MapDefaultSolutionConfigurations()
    {
        string root = FindRepositoryRoot();
        XDocument solution = XDocument.Load(Path.Combine(root, "JitHub.slnx"));

        foreach (XElement projectEntry in solution.Root!.Descendants("Project"))
        {
            string relativePath = Assert.IsType<XAttribute>(projectEntry.Attribute("Path")).Value;
            string[] declaredPlatforms = GetDeclaredPlatforms(root, relativePath);
            if (declaredPlatforms.Contains("AnyCPU", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string configuration in new[] { "Debug", "Release" })
            {
                XElement mapping = Assert.Single(
                    projectEntry.Elements("Platform"),
                    candidate => string.Equals(
                        candidate.Attribute("Solution")?.Value,
                        $"{configuration}|Any CPU",
                        StringComparison.OrdinalIgnoreCase));
                string mappedPlatform = NormalizePlatform(
                    Assert.IsType<XAttribute>(mapping.Attribute("Project")).Value);

                Assert.Contains(
                    mappedPlatform,
                    declaredPlatforms,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    // A project declares Platforms itself or inherits them from the nearest
    // Directory.Build.props that sets them (each folder props imports its
    // parent before setting its own values, so the nearest one wins).
    private static string[] GetDeclaredPlatforms(string root, string relativeProjectPath)
    {
        string projectPath = Path.Combine(root, relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
        string[] platforms = ReadPlatforms(XDocument.Load(projectPath));
        DirectoryInfo? directory = new FileInfo(projectPath).Directory;
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (platforms.Length == 0 && directory is not null)
        {
            string propsPath = Path.Combine(directory.FullName, "Directory.Build.props");
            if (File.Exists(propsPath))
            {
                platforms = ReadPlatforms(XDocument.Load(propsPath));
            }

            if (string.Equals(
                Path.TrimEndingDirectorySeparator(directory.FullName),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = directory.Parent;
        }

        return platforms.Length == 0 ? ["AnyCPU"] : platforms;
    }

    private static string[] ReadPlatforms(XDocument document) => document.Root!
        .Elements("PropertyGroup")
        .Elements("Platforms")
        .SelectMany(static element => element.Value.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(NormalizePlatform)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

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
}
