using JitHub.WinUI.Tests.TestSupport;
using Xunit;

namespace JitHub.WinUI.Tests;

/// <summary>
/// Checks, through MSBuild's evaluation of the Release app project, that the app
/// references every shipping renderer feature and publishes the third-party
/// notices those features and the Windows App SDK require.
/// </summary>
public sealed class AppRendererPackagingTests
{
    private static readonly string[] ShippingRendererProjects =
    [
        "MarkdownRenderer",
        "MarkdownRenderer.Math",
        "MarkdownRenderer.Mermaid",
        "MarkdownRenderer.Svg.Resvg",
        "MarkdownRenderer.SyntaxHighlighting.TextMate",
        "MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common",
    ];

    // Paths relative to the app's output and publish folders.
    private static readonly string[] RequiredNotices =
    [
        "THIRD-PARTY-NOTICES.md",
        @"ThirdPartyNotices\MarkdownRenderer.Math\THIRD-PARTY-NOTICES.md",
        @"ThirdPartyNotices\MarkdownRenderer.Mermaid\MPL2-SOURCE.md",
        @"ThirdPartyNotices\MarkdownRenderer.Mermaid\NATIVE_RUST_DEPENDENCIES.json",
        @"ThirdPartyNotices\MarkdownRenderer.Svg.Resvg\THIRD-PARTY-NOTICES.md",
        @"ThirdPartyNotices\MarkdownRenderer.Svg.Resvg\NATIVE_RUST_DEPENDENCIES.json",
        @"ThirdPartyNotices\MarkdownRenderer.Svg.Resvg\RUST-DEPENDENCY-LICENSES.txt",
        @"ThirdPartyNotices\MarkdownRenderer.TextMate\THIRD-PARTY-NOTICES.md",
        @"ThirdPartyNotices\Microsoft.Web.WebView2\LICENSE.txt",
        @"ThirdPartyNotices\Microsoft.Web.WebView2\NOTICE.txt",
        @"ThirdPartyNotices\Microsoft.WindowsAppSDK\Base-LICENSE.txt",
        @"ThirdPartyNotices\Microsoft.WindowsAppSDK\WinUI-NOTICE.txt",
    ];

    // The item types whose copy-to-publish files a project contributes to its
    // own publish folder and, through ProjectReference, to a referencing app's.
    private static readonly string[] CopiedItemTypes = ["Content", "None"];

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void App_ReferencesEveryShippingRendererFeature()
    {
        string root = FindRepositoryRoot();
        MsBuildEvaluation app = EvaluateApp(root, "ProjectReference");
        HashSet<string> referenced = app.GetItems("ProjectReference")
            .Select(static reference => Path.GetFullPath(reference.GetMetadata("FullPath")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(ShippingRendererProjects, project => Assert.Contains(
            Path.Combine(root, "MarkdownRenderer", "src", project, project + ".csproj"),
            referenced));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void App_PublishesTheRendererAndPlatformThirdPartyNotices()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, MsBuildItem> published = PublishedFiles(root)
            .GroupBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Item, StringComparer.OrdinalIgnoreCase);

        Assert.All(RequiredNotices, notice =>
        {
            Assert.True(published.TryGetValue(notice, out MsBuildItem? content), $"The app does not publish {notice}.");
            Assert.Contains(content!.GetMetadata("CopyToPublishDirectory"), new[] { "PreserveNewest", "Always" });
            string source = content.GetMetadata("FullPath");
            // Notices in this repository must exist; package notices resolve
            // under the NuGet package root once the app is restored.
            if (source.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                Assert.True(File.Exists(source), $"{notice} is published from a missing file: {source}");
        });
    }

    /// <summary>
    /// The files the app's publish copies by item: those of its referenced
    /// projects, which the SDK collects from immediate references only, and
    /// then the app's own, which publish last and so win a shared path.
    /// </summary>
    private static IEnumerable<(string Path, MsBuildItem Item)> PublishedFiles(string root)
    {
        MsBuildEvaluation app = EvaluateApp(root, ["ProjectReference", .. CopiedItemTypes]);
        string[] references = app.GetItems("ProjectReference")
            .Where(static reference => !string.Equals(reference.GetMetadata("Private"), "false", StringComparison.OrdinalIgnoreCase))
            .Select(static reference => reference.GetMetadata("FullPath"))
            .ToArray();
        MsBuildEvaluation[] referencedProjects = references
            .AsParallel()
            .AsOrdered()
            .Select(static project => Evaluate(project, CopiedItemTypes))
            .ToArray();

        return referencedProjects
            .Append(app)
            .SelectMany(static project => CopiedItemTypes.SelectMany(project.GetItems))
            .Where(static item => item.GetMetadata("CopyToPublishDirectory") is "PreserveNewest" or "Always")
            .Select(static item => (PublishedPath(item), item));
    }

    private static string PublishedPath(MsBuildItem item)
    {
        string targetPath = item.GetMetadata("TargetPath");
        if (!string.IsNullOrEmpty(targetPath))
            return targetPath;

        string link = item.GetMetadata("Link");
        return string.IsNullOrEmpty(link) ? item.Identity : link;
    }

    private static MsBuildEvaluation EvaluateApp(string root, params string[] itemTypes) =>
        Evaluate(Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"), itemTypes);

    private static MsBuildEvaluation Evaluate(string projectPath, IEnumerable<string> itemTypes) =>
        MsBuild.Evaluate(
            projectPath,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Configuration"] = "Release",
                ["Platform"] = "x64",
            },
            [],
            itemTypes);

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
