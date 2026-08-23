using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class ShellWorkspaceContractTests
{
    [Fact]
    public void ResponsiveCoordinatorExclusivelyOwnsRailPlacement()
    {
        XDocument shell = LoadShellXaml();
        string[] forbiddenStateTargets =
        [
            "TitleLogoColumn.Width",
            "ShellRail.Width",
            "ShellRail.Visibility",
            "ShellRailDrawerButton.Visibility",
            "ShellTitleText.Visibility"
        ];

        string[] stateTargets = shell.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => (string?)element.Attribute("Target"))
            .Where(static target => !string.IsNullOrWhiteSpace(target))
            .Cast<string>()
            .ToArray();

        foreach (string forbidden in forbiddenStateTargets)
        {
            Assert.DoesNotContain(forbidden, stateTargets);
        }
    }

    [Fact]
    public void ShellCommandsAndModalExposeNativeAccessibilityContracts()
    {
        XDocument shell = LoadShellXaml();
        Dictionary<string, XElement> byId = shell.Descendants()
            .Where(element => element.Attribute("AutomationProperties.AutomationId") is not null)
            .Where(element => !((string)element.Attribute("AutomationProperties.AutomationId")!)
                .StartsWith("{", StringComparison.Ordinal))
            .ToDictionary(
                element => (string)element.Attribute("AutomationProperties.AutomationId")!,
                StringComparer.Ordinal);

        foreach (string id in new[]
        {
            "ShellRailDrawerButton",
            "ShellBackButton",
            "ShellForwardButton",
            "ShellSearchSubmitButton",
            "ShellNewRepositoryButton",
            "ShellSettingsTopButton",
            "ShellProfileTopButton",
            "ShellRepositoryRetryButton",
            "ShellModalCloseButton"
        })
        {
            XElement element = Assert.Contains(id, byId);
            Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name")));
            Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("ToolTipService.ToolTip")));
        }

        Assert.DoesNotContain("ShellUserFooterButton", byId.Keys);

        XElement modalContent = Assert.Contains("ShellModalContent", byId);
        Assert.Equal("Cycle", (string?)modalContent.Attribute("TabFocusNavigation"));
        XElement modalOverlay = Assert.Contains("ShellModalOverlay", byId);
        Assert.Null((string?)modalOverlay.Attribute("KeyDown"));

        string shellCode = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        Assert.Contains("Modal.AddHandler(KeyDownEvent", shellCode, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", shellCode, StringComparison.Ordinal);

        Assert.Contains("ViewModel.CanGoBack", (string?)byId["ShellBackButton"].Attribute("IsEnabled"), StringComparison.Ordinal);
        Assert.Contains("ViewModel.CanGoForward", (string?)byId["ShellForwardButton"].Attribute("IsEnabled"), StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsModalDismissEnabled", (string?)byId["ShellModalCloseButton"].Attribute("IsEnabled"), StringComparison.Ordinal);
    }

    [Fact]
    public void SearchAndRepositoryCollectionsExposeExplicitAccessibleNames()
    {
        XDocument shell = LoadShellXaml();
        foreach (string id in new[]
        {
            "ShellSearchTextBox",
            "ShellRepositoryList",
            "ShellSearchSuggestionsList"
        })
        {
            XElement element = Assert.Single(shell.Descendants(), candidate =>
                string.Equals(
                    (string?)candidate.Attribute("AutomationProperties.AutomationId"),
                    id,
                    StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(
                (string?)element.Attribute("AutomationProperties.Name")));
        }
    }

    [Fact]
    public void DynamicRailCommandsExposeMatchingNamesAndTooltips()
    {
        XDocument shell = LoadShellXaml();
        foreach (string templateKey in new[] { "ShellNavigationItemTemplate", "ShellRepositoryItemTemplate" })
        {
            XElement template = Assert.Single(shell.Descendants(), element =>
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Key" &&
                    string.Equals(attribute.Value, templateKey, StringComparison.Ordinal)));
            XElement button = Assert.Single(template.Descendants(), element => element.Name.LocalName == "Button");
            string? name = (string?)button.Attribute("AutomationProperties.Name");
            string? tooltip = (string?)button.Attribute("ToolTipService.ToolTip");

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name, tooltip);
        }
    }

    [Fact]
    public void PersistentNavigationToggleOwnsWideCollapseAndCompactDrawerBehavior()
    {
        string root = FindRepositoryRoot();
        XDocument shell = LoadShellXaml();
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        XElement toggle = Assert.Single(shell.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "ShellRailDrawerButton",
                StringComparison.Ordinal));
        XElement logo = Assert.Single(shell.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" &&
                string.Equals(attribute.Value, "AppLogoShellPage", StringComparison.Ordinal)));
        Assert.Null((string?)toggle.Attribute("Visibility"));
        Assert.Same(toggle.Parent, logo.Parent);
        Assert.Contains(logo, toggle.ElementsAfterSelf());
        Assert.DoesNotContain(shell.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "ShellRailCollapseButton",
                StringComparison.Ordinal));

        Assert.Contains("Shell.Navigation.Open", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Shell.Navigation.Close", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Shell.Navigation.CollapsePane", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Shell.Navigation.ExpandPane", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (_canShellRailInline)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_isShellRailCollapsedByUser = !_isShellRailCompact;", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "if (_canShellRailInline)\n            {\n                CloseShellRailDrawer(restoreFocus: false, animate: false);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ShellRailCollapseButton", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TrackShellCommand", codeBehind, StringComparison.Ordinal);
        Assert.Contains("\"shell.command.executed\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("TelemetryTaxonomy.Actions.Drawer", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellSelectionIndicatorUsesNativeNavigationViewGeometry()
    {
        XDocument shell = LoadShellXaml();
        XElement template = Assert.Single(shell.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" &&
                string.Equals(attribute.Value, "ShellNavigationItemTemplate", StringComparison.Ordinal)));
        XElement indicator = Assert.Single(template.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("Fill"),
                "{ThemeResource NavigationViewSelectionIndicatorForeground}",
                StringComparison.Ordinal));

        Assert.Equal("Rectangle", indicator.Name.LocalName);
        Assert.Equal("{ThemeResource NavigationViewSelectionIndicatorWidth}", (string?)indicator.Attribute("Width"));
        Assert.Equal("{ThemeResource NavigationViewSelectionIndicatorHeight}", (string?)indicator.Attribute("Height"));
        Assert.Equal("{ThemeResource NavigationViewSelectionIndicatorRadius}", (string?)indicator.Attribute("RadiusX"));
        Assert.Equal("{ThemeResource NavigationViewSelectionIndicatorRadius}", (string?)indicator.Attribute("RadiusY"));
    }

    [Fact]
    public void RepositoryRailUsesDirectionAwareMorphingCompactHeader()
    {
        string root = FindRepositoryRoot();
        XDocument shell = LoadShellXaml();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace labs = "using:CommunityToolkit.WinUI";

        XElement expanded = Assert.Single(shell.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "RepositoryExpandedHeaderSurface", StringComparison.Ordinal));
        XElement compact = Assert.Single(shell.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "RepositoryShyHeaderSurface", StringComparison.Ordinal));
        XElement list = Assert.Single(shell.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "ShellRepositoryList", StringComparison.Ordinal));
        XElement compactFilter = Assert.Single(compact.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "ShellRepositoryCompactFilter", StringComparison.Ordinal));

        Assert.Equal("RepositoryHeaderSurface", (string?)expanded.Attribute(labs + "TransitionHelper.Id"));
        Assert.Equal("RepositoryHeaderSurface", (string?)compact.Attribute(labs + "TransitionHelper.Id"));
        Assert.Equal("Collapsed", (string?)compact.Attribute("Visibility"));
        Assert.Equal("{ThemeResource AppTransientOverlayBrush}", (string?)compact.Attribute("Background"));
        Assert.Equal("34", (string?)compact.Attribute("Height"));
        Assert.Equal("Top", (string?)compact.Attribute("VerticalAlignment"));
        Assert.Null(compact.Attribute("MinHeight"));
        Assert.Equal("30", (string?)compactFilter.Attribute("Height"));
        Assert.Equal("2", (string?)compactFilter.Attribute("Padding"));
        Assert.Equal(3, compactFilter.Elements().Count(element => element.Name.LocalName == "SegmentedItem"));
        Assert.Single(compactFilter.Descendants(), element => element.Name.LocalName == "EqualPanel");
        Assert.Equal("ShellRepositoryList_Loaded", (string?)list.Attribute("Loaded"));
        Assert.Equal("ShellRepositoryList_SizeChanged", (string?)list.Attribute("SizeChanged"));
        Assert.Equal("ShellRepositoryList_Unloaded", (string?)list.Attribute("Unloaded"));

        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains(
            "new TransitionConfig { Id = \"RepositoryHeaderSurface\", ScaleMode = ScaleMode.None, EnableClipAnimation = true }",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new TransitionConfig { Id = \"RepositoryHeaderFilter\", ScaleMode = ScaleMode.None, EnableClipAnimation = true }",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Id = \"RepositoryHeaderFilter\", ScaleMode = ScaleMode.ScaleX",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RepositoryShyHeaderStartOffset", source, StringComparison.Ordinal);
        Assert.Contains("RepositoryShyHeaderRestoreOffset", source, StringComparison.Ordinal);
        Assert.Contains("RepositoryShyHeaderRevealTravel", source, StringComparison.Ordinal);
        Assert.Contains("RepositoryShyHeaderRehideTravel", source, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback", source, StringComparison.Ordinal);
        Assert.Contains("nameof(ShellPageViewModel.AreRepositoriesVisible)", source, StringComparison.Ordinal);
        Assert.Contains("AnimateRepositoryListReflow", source, StringComparison.Ordinal);
        Assert.Contains("ShellRepositoryExpandedFilter.SelectedIndex", source, StringComparison.Ordinal);
        Assert.Contains("ShellRepositoryCompactFilter.SelectedIndex", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellCommandFamiliesHaveTelemetryAndFocusReturnOwnership()
    {
        string root = FindRepositoryRoot();
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        foreach (string eventName in new[]
        {
            "shell.route.opened",
            "shell.repo.selected",
            "shell.command.opened",
            "shell.command.executed",
            "shell.search.submitted",
            "shell.search.completed",
            "shell.rail.refresh.completed"
        })
        {
            Assert.Contains($"\"{eventName}\"", viewModel, StringComparison.Ordinal);
        }

        Assert.Contains("RestoreFocusAfterModal", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_searchRestoreTarget.Focus", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_shellRailDrawerRestoreTarget.Focus", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TrackHistoryNavigation(\"back\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("TrackHistoryNavigation(\"forward\"", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryRetryIsFailureOnlyAndThereIsNoPersistentRefreshCommand()
    {
        XDocument shell = LoadShellXaml();
        XElement retry = shell.Descendants().Single(element =>
            string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "ShellRepositoryRetryButton",
                StringComparison.Ordinal));

        Assert.Contains("HasRepositoryRailError", (string?)retry.Attribute("Visibility"), StringComparison.Ordinal);
        Assert.Equal("RefreshRepositoriesButton_Click", (string?)retry.Attribute("Click"));
        Assert.Single(
            shell.Descendants(),
            element => string.Equals(
                (string?)element.Attribute("Click"),
                "RefreshRepositoriesButton_Click",
                StringComparison.Ordinal));
    }

    [Fact]
    public void SearchNavigationLabelMatchesItsCommandPaletteBehavior()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        Assert.Contains(
            "new(\"explore\", ShellNavigationText(\"Search\", \"Search\"), \"\\uE721\", new RelayCommand(FocusCommandSearchRequested))",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ShellNavigationText(\"Explore\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationsAndNativeHistoryAreFirstClassShellRoutes()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        XDocument shell = LoadShellXaml();

        Assert.Contains(
            "new(\"notifications\", ShellNavigationText(\"Notifications\", \"Notifications\")",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("typeof(NotificationsPage)", viewModel, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Left", codeBehind, StringComparison.Ordinal);
        Assert.Contains("XButton1Pressed", codeBehind, StringComparison.Ordinal);
        Assert.Contains("XButton2Pressed", codeBehind, StringComparison.Ordinal);

        foreach (string id in new[] { "ShellBackButton", "ShellForwardButton" })
        {
            XElement button = Assert.Single(shell.Descendants(), element =>
                string.Equals(
                    (string?)element.Attribute("AutomationProperties.AutomationId"),
                    id,
                    StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name")));
            Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTipService.ToolTip")));
        }
    }

    [Fact]
    public void FocusCaptureIsSafeBeforeShellVisualTreeAttachment()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));

        Assert.DoesNotContain("FocusManager.GetFocusedElement(XamlRoot)", source, StringComparison.Ordinal);
        Assert.Contains("private DependencyObject? TryGetFocusedElement()", source, StringComparison.Ordinal);
        Assert.Contains("if (xamlRoot is null)", source, StringComparison.Ordinal);
        Assert.Contains("catch (ArgumentException)", source, StringComparison.Ordinal);
        Assert.Contains("exception.HResult == unchecked((int)0x80070057)", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsLoaded || XamlRoot is null || ShellContentFrame.Content", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchRouteWaitsForAttachedShellVisualTree()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));

        Assert.Contains("QueueLaunchRoute(useAutomationSearchResults", source, StringComparison.Ordinal);
        Assert.Contains("if (!_hasPendingLaunchRoute || !IsLoaded || XamlRoot is null)", source, StringComparison.Ordinal);
        Assert.Contains("TryOpenPendingLaunchRoute();", source, StringComparison.Ordinal);

        int loadedHandler = source.IndexOf("private void Page_Loaded", StringComparison.Ordinal);
        int pendingLaunch = source.IndexOf("TryOpenPendingLaunchRoute();", loadedHandler, StringComparison.Ordinal);
        Assert.True(loadedHandler >= 0 && pendingLaunch > loadedHandler);
    }

    [Fact]
    public void AccountRoutesLiveInTheTitleBarWithoutDuplicateRailContent()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml"));
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        Assert.DoesNotContain("Pro User", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pro User", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ShellSettingsTopButton", xaml, StringComparison.Ordinal);
        Assert.Contains("ShellProfileTopButton", xaml, StringComparison.Ordinal);
        Assert.Contains("GoToSettingsPageCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("GoToProfilePageCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellUserFooterButton", xaml, StringComparison.Ordinal);
    }

    private static XDocument LoadShellXaml() =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml"));

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
