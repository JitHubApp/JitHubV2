using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class SolutionConfigurationContractTests
{
    [Fact]
    public void RepositoryBuildProps_ExcludeConventionalAndIsolatedGeneratedTrees()
    {
        string root = FindRepositoryRoot();

        foreach (string propsPath in new[]
        {
            Path.Combine(root, "Directory.Build.props"),
            Path.Combine(root, "MarkdownRenderer", "Directory.Build.props")
        })
        {
            XDocument props = XDocument.Load(propsPath);
            string excludes = Assert.Single(
                props.Root!.Elements("PropertyGroup").Elements("DefaultItemExcludes")).Value;

            Assert.Contains("obj\\**", excludes, StringComparison.Ordinal);
            Assert.Contains("obj-*\\**", excludes, StringComparison.Ordinal);
            Assert.Contains("bin\\**", excludes, StringComparison.Ordinal);
            Assert.Contains("bin-*\\**", excludes, StringComparison.Ordinal);
            Assert.Contains("artifacts\\**", excludes, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("MarkdownRenderer/MarkdownRenderer.ManagedPacks.AotSmoke/MarkdownRenderer.ManagedPacks.AotSmoke.csproj")]
    [InlineData("MarkdownRenderer/MarkdownRenderer.PixelTests/MarkdownRenderer.PixelTests.csproj")]
    [InlineData("MarkdownRenderer/MarkdownRenderer.Svg.Resvg.AotSmoke/MarkdownRenderer.Svg.Resvg.AotSmoke.csproj")]
    [InlineData("MarkdownRenderer/MarkdownRenderer.Svg.Resvg.Tests/MarkdownRenderer.Svg.Resvg.Tests.csproj")]
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

        Assert.Equal("$(JitHubWindowsSdkBuildToolsVersion)", packageReference.Attribute("Version")?.Value);
        Assert.Equal("all", packageReference.Attribute("PrivateAssets")?.Value, ignoreCase: true);
    }

    [Fact]
    public void WindowsSdkBuildToolsVersion_IsConsistentAcrossPropsScopes()
    {
        string root = FindRepositoryRoot();
        string[] propsPaths =
        [
            Path.Combine(root, "Directory.Build.props"),
            Path.Combine(root, "MarkdownRenderer", "Directory.Build.props")
        ];

        string[] versions = propsPaths
            .Select(XDocument.Load)
            .Select(static props => Assert.Single(
                props.Root!
                    .Elements("PropertyGroup")
                    .Elements("JitHubWindowsSdkBuildToolsVersion")).Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(versions);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", versions[0]);
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
            XDocument project = XDocument.Load(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string[] declaredPlatforms = GetDeclaredPlatforms(project);

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
            XDocument project = XDocument.Load(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string[] declaredPlatforms = GetDeclaredPlatforms(project);
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

    private static string[] GetDeclaredPlatforms(XDocument project)
    {
        string[] platforms = project.Root!
            .Elements("PropertyGroup")
            .Elements("Platforms")
            .SelectMany(static element => element.Value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizePlatform)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return platforms.Length == 0 ? ["AnyCPU"] : platforms;
    }

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
