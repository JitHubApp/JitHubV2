using JitHub.Services;
using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DeveloperRoutePolicyTests
{
    [Fact]
    public void ProductionRoute_IsUnavailableWithoutDeveloperMode()
    {
        Assert.False(DeveloperRoutePolicy.CanOpenDesignLab(
            isDeveloperModeEnabled: false,
            hasIsolatedAutomationRoots: false));
        Assert.False(DeveloperRoutePolicy.CanOpenDevConsole(
            isDeveloperModeEnabled: false));
    }

    [Fact]
    public void DevConsoleRequiresDeveloperModeAndHasNoAutomationBypass()
    {
        Assert.True(DeveloperRoutePolicy.CanOpenDevConsole(
            isDeveloperModeEnabled: true));
        Assert.False(DeveloperRoutePolicy.CanOpenDevConsole(
            isDeveloperModeEnabled: false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DeveloperAndIsolatedAutomationRoutesRemainAvailable(
        bool developerMode,
        bool automationRoots)
    {
        Assert.True(DeveloperRoutePolicy.CanOpenDesignLab(developerMode, automationRoots));
    }

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
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
