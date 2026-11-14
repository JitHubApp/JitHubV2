using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels.Shell;

/// <summary>
/// Link routing that needs the app: payload-backed README sources and the shell's
/// route confirmation. The policy itself is tested in JitHub.Markdown.Tests.
/// </summary>
public sealed class MarkdownLinkRoutingIntegrationTests
{
    [Fact]
    public void InternalRoute_IsHandledOnlyAfterShellConfirmsTheRequestedRoute()
    {
        string root = FindRepositoryRoot();
        string navigator = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Shell",
            "ShellMarkdownRouteNavigator.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Shell",
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
