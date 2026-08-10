using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class StarsWorkspaceContractTests
{
    [Fact]
    public void CompactCategoryDrawer_PreservesLatestRequestAcrossSplitViewAnimations()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "StarsPage.xaml"));
        XElement splitView = Assert.Single(document.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "StarsCategorySplitView");

        Assert.Equal("CategorySplitView_PaneClosing", splitView.Attribute("PaneClosing")?.Value);
        Assert.Equal("CategorySplitView_PaneClosed", splitView.Attribute("PaneClosed")?.Value);
        Assert.Equal("CategorySplitView_PaneOpened", splitView.Attribute("PaneOpened")?.Value);

        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "StarsPage.xaml.cs"));

        Assert.Contains("ApplyRequestedCategoryDrawerState();", source, StringComparison.Ordinal);
        Assert.Contains("_categoryDrawerTransition != CategoryDrawerTransition.None", source, StringComparison.Ordinal);
        Assert.Contains("CategoryDrawerTransition.Opening", source, StringComparison.Ordinal);
        Assert.Contains("CategoryDrawerTransition.Closing", source, StringComparison.Ordinal);
        Assert.Contains("CategorySplitView.IsPaneOpen = _categoryDrawerRequestedOpen;", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "JitHub.WinUI")) &&
                Directory.Exists(Path.Combine(directory.FullName, "JitHub.WinUI.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
