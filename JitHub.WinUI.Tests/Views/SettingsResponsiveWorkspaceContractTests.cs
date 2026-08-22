using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class SettingsResponsiveWorkspaceContractTests
{
    [Fact]
    public void HeaderAndWorkspaceUseOneCompactVerticalGap()
    {
        XDocument document = LoadSettingsPage();
        XElement layout = FindNamedElement(document, "SettingsLayout");
        XElement workspace = FindNamedElement(document, "SettingsWorkspace");
        XElement errorBar = document.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "SettingsErrorBar");

        Assert.Equal("0", (string?)layout.Attribute("RowSpacing"));
        Assert.Equal("0,10,0,0", (string?)workspace.Attribute("Margin"));
        Assert.Equal("0,8,0,0", (string?)errorBar.Attribute("Margin"));
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CompactNavigation_RemainsAFluentSelectorWithStableAutomationContract()
    {
        XDocument document = LoadSettingsPage();
        XElement selector = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "ComboBox" &&
                (string?)element.Attribute("AutomationProperties.AutomationId") == "SettingsCompactSectionPicker");

        Assert.Equal("Collapsed", (string?)selector.Attribute("Visibility"));
        Assert.Contains("SettingsSections", (string?)selector.Attribute("ItemsSource"), StringComparison.Ordinal);
        Assert.Contains("SelectedSection", (string?)selector.Attribute("SelectedItem"), StringComparison.Ordinal);

        XElement compactState = document.Descendants().Single(element =>
            element.Name.LocalName == "VisualState" &&
            (string?)element.Attribute(Xaml + "Name") == "CompactSettingsState");
        IReadOnlySet<string> compactTargets = compactState.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => (string?)element.Attribute("Target"))
            .Where(target => target is not null)
            .Select(target => target!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("CompactSectionPicker.Visibility", compactTargets);
        Assert.Contains("CompactSectionPicker.(Grid.Row)", compactTargets);
        Assert.Contains("CompactSectionPicker.(Grid.ColumnSpan)", compactTargets);
    }

    [Fact]
    public void Workspace_HasStableWideColumnsAndOnlyContentScrolls()
    {
        XDocument document = LoadSettingsPage();
        XElement workspace = FindNamedElement(document, "SettingsWorkspace");
        XElement contentPanel = FindNamedElement(document, "SettingsContentPanel");
        XElement contentScroller = FindNamedElement(document, "SettingsContentScrollViewer");
        XElement sectionList = document.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "SettingsSectionList");

        Assert.Equal("220", workspace.Elements().Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements().First().Attribute("Width")?.Value);
        Assert.Equal("1", (string?)contentPanel.Attribute("Grid.Column"));
        Assert.Equal("Disabled", (string?)contentScroller.Attribute("HorizontalScrollMode"));
        Assert.Equal("Enabled", (string?)contentScroller.Attribute("VerticalScrollMode"));
        Assert.Equal("Disabled", (string?)sectionList.Attribute("ScrollViewer.VerticalScrollMode"));
        Assert.Equal("Settings sections", (string?)sectionList.Attribute("AutomationProperties.Name"));
        Assert.Single(document.Descendants(), element => element.Name.LocalName == "ScrollViewer");
        Assert.Contains(contentPanel.Descendants(), element => ReferenceEquals(element, contentScroller));
    }

    [Fact]
    public void NarrowState_ReflowsControlsInsteadOfGrowingContentWidth()
    {
        XDocument document = LoadSettingsPage();
        XElement narrowState = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "VisualState" &&
                (string?)element.Attribute(Xaml + "Name") == "NarrowSettingsState");
        HashSet<string> targets = narrowState.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => (string?)element.Attribute("Target"))
            .Where(target => target is not null)
            .Select(target => target!)
            .ToHashSet(StringComparer.Ordinal);

        string[] controlsExpectedOnSecondRow =
        [
            "SettingsDeveloperModeToggle",
            "SettingsSignOutButton",
            "SettingsDiagnosticsToggle",
            "SettingsStoreTelemetryToggle",
            "SettingsClearQueryCacheButton",
            "SettingsClearStarLibraryButton",
            "SettingsClearImageCacheButton",
            "SettingsClearRepoFileCacheButton",
            "SettingsClearAllCacheButton",
            "SettingsViewSourceButton"
        ];

        foreach (string control in controlsExpectedOnSecondRow)
        {
            Assert.Contains($"{control}.(Grid.Row)", targets);
            Assert.Contains($"{control}.(Grid.Column)", targets);
        }

        Assert.Contains("SettingsContentScrollViewer.Padding", targets);
        Assert.Contains("SettingsContentPanel.(Grid.ColumnSpan)", targets);
    }

    [Fact]
    public void SectionNavigation_WrapsLocalizedLabelsWithoutChangingWorkspaceWidth()
    {
        XDocument document = LoadSettingsPage();
        XElement compactPicker = FindNamedElement(document, "CompactSectionPicker");
        XElement[] navigationLabels = compactPicker
            .Descendants()
            .Concat(FindNamedElement(document, "SettingsSectionRail").Descendants())
            .Where(element =>
                element.Name.LocalName == "TextBlock" &&
                ((string?)element.Attribute("Text"))?.Contains("Title", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(2, navigationLabels.Length);
        Assert.All(navigationLabels, label =>
            Assert.Equal("WrapWholeWords", (string?)label.Attribute("TextWrapping")));
        Assert.DoesNotContain(navigationLabels, label => label.Attribute("TextTrimming") is not null);
    }

    [Fact]
    public void ThemeCards_UseNativeSingleSelectionSemantics()
    {
        XDocument document = LoadSettingsPage();
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml.cs"));
        string[] automationIds = ["SettingsThemeSystem", "SettingsThemeLight", "SettingsThemeDark"];

        XElement[] cards = automationIds
            .Select(id => document.Descendants().Single(element =>
                (string?)element.Attribute("AutomationProperties.AutomationId") == id))
            .ToArray();

        Assert.All(cards, card =>
        {
            Assert.Equal("RadioButton", card.Name.LocalName);
            Assert.Equal("SettingsTheme", (string?)card.Attribute("GroupName"));
            Assert.EndsWith("_Checked", (string?)card.Attribute("Checked"), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace((string?)card.Attribute("AutomationProperties.Name")));
        });
        Assert.Equal(3, source.Split("AddHandler(UIElement.KeyDownEvent", StringSplitOptions.None).Length - 1);
        Assert.Contains("handledEventsToo: true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializedActions_AreNamedAndRouteThroughTheExclusiveUiGate()
    {
        XDocument document = LoadSettingsPage();
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml.cs"));
        (string AutomationId, string Handler)[] actions =
        [
            ("SettingsSignOutButton", "SignOutButton_Click"),
            ("SettingsClearQueryCacheButton", "ClearQueryCacheButton_Click"),
            ("SettingsClearStarLibraryButton", "ClearStarLibraryButton_Click"),
            ("SettingsClearImageCacheButton", "ClearImageCacheButton_Click"),
            ("SettingsClearRepoFileCacheButton", "ClearRepoFileCacheButton_Click"),
            ("SettingsClearAllCacheButton", "ClearAllCacheButton_Click"),
            ("SettingsExportDiagnosticsButton", "ExportDiagnosticsButton_Click"),
            ("SettingsClearDiagnosticsButton", "ClearDiagnosticsButton_Click")
        ];

        foreach ((string automationId, string handler) in actions)
        {
            XElement button = document.Descendants().Single(element =>
                (string?)element.Attribute("AutomationProperties.AutomationId") == automationId);
            Assert.Equal(handler, (string?)button.Attribute("Click"));
            Assert.Equal(automationId, (string?)button.Attribute(Xaml + "Name"));

            int handlerStart = source.IndexOf($"void {handler}", StringComparison.Ordinal);
            Assert.True(handlerStart >= 0, $"Could not find {handler}.");
            int nextMethod = source.IndexOf("\n    private ", handlerStart + 1, StringComparison.Ordinal);
            string handlerBody = source[handlerStart..(nextMethod < 0 ? source.Length : nextMethod)];
            Assert.Contains("RunExclusiveUiActionAsync", handlerBody, StringComparison.Ordinal);
        }

        Assert.Contains("SettingsConfirmClearRepoFileCache", source, StringComparison.Ordinal);
        Assert.Contains("origin.Focus(FocusState.Programmatic)", source, StringComparison.Ordinal);

        string signOutSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Dialogs",
            "AccountSignOutDialogFlow.cs"));
        Assert.Contains("confirmation.Opened", signOutSource, StringComparison.Ordinal);
        Assert.Contains("removeLocalData.Focus(FocusState.Programmatic)", signOutSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveAutomation_CoversThemesWidthsDialogsAndPseudoLongLabels()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));

        foreach (string size in new[] { "(1366, 900)", "(1180, 800)", "(900, 700)", "(760, 650)", "(640, 600)" })
        {
            Assert.Contains(size, source, StringComparison.Ordinal);
        }

        Assert.Contains("AssertSettingsThemeCardSemantics", source, StringComparison.Ordinal);
        Assert.Contains("AssertSettingsSignOutConfirmation", source, StringComparison.Ordinal);
        Assert.Contains("AssertSettingsExportPicker", source, StringComparison.Ordinal);
        Assert.Contains("SettingsConfirmClearRepoFileCache", source, StringComparison.Ordinal);
        Assert.Contains("IsHighContrastEnabled", source, StringComparison.Ordinal);
        Assert.Contains("settings-pseudo-long-labels", source, StringComparison.Ordinal);
    }

    private static XElement FindNamedElement(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XDocument LoadSettingsPage() =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages", "SettingsPage.xaml"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
