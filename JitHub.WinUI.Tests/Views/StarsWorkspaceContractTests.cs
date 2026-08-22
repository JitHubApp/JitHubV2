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

    [Fact]
    public void NavigationAndCategoryEditorExposeStableVisualAlignment()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "StarsPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement navigationTemplate = Assert.Single(document.Descendants(), element =>
            element.Attribute(xaml + "Key")?.Value == "NavigationItemTemplate");
        XElement title = Assert.Single(document.Descendants(), element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "StarsCurrentViewTitle");
        string code = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "StarsPage.xaml.cs"));

        Assert.Contains(navigationTemplate.Descendants(), element =>
            element.Name.LocalName == "Grid" && element.Attribute("MinHeight")?.Value == "38");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Attribute(xaml + "Key")?.Value == "CategoryColorOptionTemplate");
        Assert.Equal("Left", title.Attribute("HorizontalAlignment")?.Value);
        Assert.Contains("PlaceholderText = L(\"Stars/Dialogs/Category/ColorPlaceholder\"", code, StringComparison.Ordinal);
        Assert.Contains("color.Items.Add(CreateCategoryColorItem(categoryColor));", code, StringComparison.Ordinal);
        Assert.Contains("Background = new SolidColorBrush(Windows.UI.Color.FromArgb", code, StringComparison.Ordinal);
        Assert.Contains("VerticalContentAlignment = VerticalAlignment.Center", code, StringComparison.Ordinal);
        Assert.Contains("color.SelectedIndex = selectedColorIndex >= 0 ? selectedColorIndex : 0;", code, StringComparison.Ordinal);
        Assert.Contains("Tag = hexColor", code, StringComparison.Ordinal);
        Assert.Contains("Child = color", code, StringComparison.Ordinal);
        Assert.Contains("colorField.Children.Add(colorFrame)", code, StringComparison.Ordinal);
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
