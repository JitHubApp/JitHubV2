using System;
using System.IO;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownLinkNavigationPolicyTests
{
    private static readonly Uri BaseUri = new("https://github.com/owner/repository/blob/main/README.md");

    [Theory]
    [InlineData("https://github.com/owner/repository")]
    [InlineData("mailto:developer@example.test")]
    [InlineData("../issues/12")]
    [InlineData("/owner/repository/pulls")]
    public void TryResolveLaunchUri_AllowsSupportedAbsoluteAndRelativeLinks(string value)
    {
        Assert.True(MarkdownLinkNavigationPolicy.TryResolveLaunchUri(value, BaseUri, out Uri? uri));
        Assert.NotNull(uri);
        Assert.True(MarkdownLinkNavigationPolicy.IsAllowedLaunchUri(uri));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/insecure")]
    [InlineData("data:text/html,unsafe")]
    [InlineData("file:///C:/Windows/System32/notepad.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("#local-heading")]
    [InlineData("")]
    public void TryResolveLaunchUri_RejectsUnsupportedOrNonLaunchLinks(string value)
    {
        Assert.False(MarkdownLinkNavigationPolicy.TryResolveLaunchUri(value, BaseUri, out Uri? uri));
        Assert.Null(uri);
    }

    [Theory]
    [InlineData("https://github.com/octocat", MarkdownGitHubRouteKind.User)]
    [InlineData("https://github.com/octocat/Hello-World", MarkdownGitHubRouteKind.Repository)]
    [InlineData("https://github.com/octocat/Hello-World/issues/42", MarkdownGitHubRouteKind.Issue)]
    [InlineData("https://github.com/octocat/Hello-World/pull/17", MarkdownGitHubRouteKind.PullRequest)]
    [InlineData("https://github.com/search?q=renderer", MarkdownGitHubRouteKind.ExternalGitHub)]
    [InlineData("https://github.com/sponsors/octocat", MarkdownGitHubRouteKind.ExternalGitHub)]
    [InlineData("https://github.com/orgs/github", MarkdownGitHubRouteKind.ExternalGitHub)]
    [InlineData("https://github.com/topics/winui", MarkdownGitHubRouteKind.ExternalGitHub)]
    [InlineData("https://github.com/octocat/Hello-World/issues", MarkdownGitHubRouteKind.ExternalGitHub)]
    [InlineData("https://example.com/octocat", MarkdownGitHubRouteKind.NotInternal)]
    public void ClassifyGitHubRoute_OnlyRoutesExactUserAndRepositoryRoots(
        string value,
        MarkdownGitHubRouteKind expected)
    {
        MarkdownGitHubRoute route = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(new Uri(value));

        Assert.Equal(expected, route.Kind);
    }

    [Theory]
    [InlineData("https://github.com/octocat/Hello-World/issues/42", 42)]
    [InlineData("https://github.com/octocat/Hello-World/pull/17", 17)]
    public void ClassifyGitHubRoute_PreservesInternalWorkItemNumber(string value, int expected)
    {
        MarkdownGitHubRoute route = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(new Uri(value));

        Assert.Equal("octocat", route.Owner);
        Assert.Equal("Hello-World", route.Repository);
        Assert.Equal(expected, route.Number);
    }

    [Fact]
    public void TryResolveLaunchUri_RevalidatesSchemeAfterRelativeResolution()
    {
        var unsafeBase = new Uri("file:///C:/workspace/README.md");

        Assert.False(MarkdownLinkNavigationPolicy.TryResolveLaunchUri("child.md", unsafeBase, out Uri? uri));
        Assert.Null(uri);
    }

    [Fact]
    public void GenericGitHubRoot_RelativePath_IsNeverEligibleForInternalRepositoryRouting()
    {
        Assert.True(MarkdownLinkNavigationPolicy.TryResolveLaunchUri(
            "docs/setup",
            new Uri("https://github.com/"),
            documentSource: null,
            out Uri? uri,
            out bool mayNavigateInternally));

        Assert.Equal("https://github.com/docs/setup", uri!.ToString().TrimEnd('/'));
        Assert.False(mayNavigateInternally);
    }

    [Fact]
    public void CanonicalRepositorySource_ResolvesRelativePath_WithSlashContainingRef()
    {
        MarkdownDocumentSource source = new(
            "readme:owner/repository:feature/secure-links:docs/README.md",
            "owner",
            "repository",
            "feature/secure-links",
            "docs/README.md");

        Assert.True(MarkdownLinkNavigationPolicy.TryResolveLaunchUri(
            "setup/install.md",
            new Uri("https://github.com/"),
            source,
            out Uri? uri,
            out bool mayNavigateInternally));

        Assert.Equal(
            "https://github.com/owner/repository/blob/feature/secure-links/docs/setup/install.md",
            uri!.ToString());
        Assert.True(mayNavigateInternally);
        Assert.Equal(MarkdownGitHubRouteKind.ExternalGitHub,
            MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(uri).Kind);
    }

    [Fact]
    public void InternalRoute_IsHandledOnlyAfterShellConfirmsTheRequestedRoute()
    {
        string root = FindRepositoryRoot();
        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        Assert.Contains("return shell.IsCurrentRoute(expectedRoute);", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("return true;\n    }\n\n    private bool TryCreateLaunchUri", viewer, StringComparison.Ordinal);
        Assert.Contains("public bool IsCurrentRoute(ShellWorkspaceTabIdentity identity)", shell, StringComparison.Ordinal);
        Assert.Contains("_contentFrame.Content is not null", shell, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "JitHub.WinUI")) &&
                Directory.Exists(Path.Combine(directory.FullName, "MarkdownRenderer")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
