using JitHub.WinUI.Tests.TestSupport;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

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
        MsBuildEvaluation app = EvaluateApp(root, "Content");
        Dictionary<string, MsBuildItem> published = app.GetItems("Content")
            .GroupBy(static content => PublishedPath(content), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);

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

    private static string PublishedPath(MsBuildItem content)
    {
        string link = content.GetMetadata("Link");
        return string.IsNullOrEmpty(link) ? content.Identity : link;
    }

    private static MsBuildEvaluation EvaluateApp(string root, string itemType) =>
        MsBuild.Evaluate(
            Path.Combine(root, "JitHub.WinUI", "JitHub.WinUI.csproj"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Configuration"] = "Release",
                ["Platform"] = "x64",
            },
            [],
            [itemType]);

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
