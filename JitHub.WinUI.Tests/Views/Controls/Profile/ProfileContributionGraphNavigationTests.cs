using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Controls.Profile;

public sealed class ProfileContributionGraphNavigationTests
{
    [Fact]
    public void WebsiteShowcase_DisablesOnlyTooltipPresentationAndKeepsAccessibilityProjection()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Profile",
            "ProfileContributionGraph.xaml.cs"));

        Assert.Contains("_toolTipsEnabled = !Program.CurrentLaunchOptions.WebsiteShowcase", source, StringComparison.Ordinal);
        Assert.Contains("if (_toolTipsEnabled)", source, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(_calendarCanvas, _cellToolTip)", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAccessibilityView(_calendarCanvas, AccessibilityView.Raw)", source, StringComparison.Ordinal);
        Assert.Contains("SetAccessibleName", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
