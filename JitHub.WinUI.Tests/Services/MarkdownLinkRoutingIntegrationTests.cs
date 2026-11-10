using System;
using System.IO;
using JitHub.Markdown;
using JitHub.Markdown.Links;
using JitHub.WinUI.ViewModels.CodeViewer;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Link routing that needs the app: payload-backed README sources and the shell's
/// route confirmation. The policy itself is tested in JitHub.Markdown.Tests.
/// </summary>
public sealed class MarkdownLinkRoutingIntegrationTests
{
    [Fact]
    public void PayloadBackedNestedReadme_ResolvesRelativeLinkFromPayloadContext()
    {
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string blob = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string readmePath = "docs/guides/README.md";
        string baseUrl = $"https://github.com/owner/repository/blob/{commit}/{readmePath}";
        MarkdownDocumentSource source = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blob,
            baseUrl,
            readmePath) ?? throw new InvalidOperationException("The test README source should be valid.");
        var payload = new MarkdownViewerDocumentPayload(
            "# Guide",
            baseUrl,
            readmePath,
            source);
        const string relativeLink = "../setup/install.md";

        Assert.True(MarkdownLinkNavigationPolicy.TryResolveLaunchUri(
            relativeLink,
            new Uri(payload.BaseUrl!, UriKind.Absolute),
            payload.DocumentSource,
            out Uri? resolved,
            out bool mayNavigateInternally));
        Assert.Equal(
            $"https://github.com/owner/repository/blob/{commit}/docs/setup/install.md",
            resolved!.ToString());
        Assert.True(mayNavigateInternally);
        // MarkdownPreview supplies repository context only through its atomic
        // payload. MarkdownViewer reads rendering and link activation from one
        // CurrentDocument() so the two cannot diverge; observing the link event
        // itself needs a live XAML runtime.
    }

    [Fact]
    public void InternalRoute_IsHandledOnlyAfterShellConfirmsTheRequestedRoute()
    {
        string root = FindRepositoryRoot();
        string navigator = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellMarkdownRouteNavigator.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        Assert.Contains("return shell.IsCurrentRoute(expectedRoute);", navigator, StringComparison.Ordinal);
        Assert.DoesNotContain("return true;", navigator, StringComparison.Ordinal);
        // Falling back to the external launcher when the shell does not confirm
        // the route is covered by MarkdownLinkActivatorTests.
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
