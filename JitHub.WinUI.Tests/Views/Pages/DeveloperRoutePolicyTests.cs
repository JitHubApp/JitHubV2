using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Pages;

public sealed class DeveloperRoutePolicyTests
{
    [Fact]
    public void AppLaunchUsesTheDeveloperRouteGateAndLabDoesNotPresentAFakeVersion()
    {
        string root = FindRepositoryRoot();
        string appSource = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml.cs"));
        string labXaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "Design",
            "DesignLabPage.xaml"));

        Assert.Contains("DeveloperRoutePolicy.CanOpenDesignLab", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dev-console\" =>", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0-designlab", labXaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Design-only sample data", labXaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
