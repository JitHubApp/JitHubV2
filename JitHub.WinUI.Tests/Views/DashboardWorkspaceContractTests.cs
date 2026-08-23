using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class DashboardWorkspaceContractTests
{
    [Fact]
    public void MainAndSecondaryRailsOwnIndependentScrolling()
    {
        XDocument document = XDocument.Load(Path("JitHub.WinUI", "Views", "Pages", "DashboardPage.xaml"));
        XElement main = FindById(document, "DashboardMainRailScrollViewer");
        XElement side = FindById(document, "DashboardSideRailScrollViewer");
        XElement mainItems = FindById(document, "DashboardMainRail");
        XElement sideItems = FindById(document, "DashboardSideRail");
        XElement drawerItems = FindById(document, "DashboardSideDrawerWidgets");

        Assert.Equal("Auto", main.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Auto", side.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("{x:Bind ViewModel.MainWidgets, Mode=OneWay}", mainItems.Attribute("ItemsSource")?.Value);
        Assert.Equal("{x:Bind ViewModel.SideWidgets, Mode=OneWay}", sideItems.Attribute("ItemsSource")?.Value);
        Assert.Equal("{x:Bind ViewModel.SideWidgets, Mode=OneWay}", drawerItems.Attribute("ItemsSource")?.Value);
        Assert.DoesNotContain(main.Ancestors(), static element => element.Name.LocalName == "ScrollViewer");
        Assert.DoesNotContain(side.Ancestors(), static element => element.Name.LocalName == "ScrollViewer");
    }

    [Fact]
    public void DashboardHeaderMorphsWithoutDroppingResponsiveOverviewAccess()
    {
        XDocument document = XDocument.Load(Path("JitHub.WinUI", "Views", "Pages", "DashboardPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace labs = "using:CommunityToolkit.WinUI";
        XElement expanded = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "DashboardHeaderGrid", StringComparison.Ordinal));
        XElement compact = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "DashboardShyHeaderSurface", StringComparison.Ordinal));
        XElement mainRailHost = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "DashboardMainRailHost", StringComparison.Ordinal));
        XElement mainScrollViewer = FindById(document, "DashboardMainRailScrollViewer");
        string source = File.ReadAllText(Path(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "DashboardPage.xaml.cs"));

        Assert.Null(expanded.Attribute(labs + "TransitionHelper.Id"));
        Assert.Null(compact.Attribute(labs + "TransitionHelper.Id"));
        foreach (string id in new[]
        {
            "DashboardHeaderGreeting",
            "DashboardHeaderCustomize",
            "DashboardHeaderOverview"
        })
        {
            Assert.Single(expanded.Descendants(), element =>
                string.Equals((string?)element.Attribute(labs + "TransitionHelper.Id"), id, StringComparison.Ordinal));
            Assert.Single(compact.Descendants(), element =>
                string.Equals((string?)element.Attribute(labs + "TransitionHelper.Id"), id, StringComparison.Ordinal));
        }

        Assert.Equal("Collapsed", (string?)compact.Attribute("Visibility"));
        Assert.Equal("40", (string?)compact.Attribute("Height"));
        Assert.Equal("2", (string?)compact.Attribute("Grid.RowSpan"));
        Assert.Equal("{ThemeResource AppCanvasTransientOverlayBrush}", (string?)compact.Attribute("Background"));
        Assert.Equal("0,0,0,1", (string?)compact.Attribute("BorderThickness"));
        Assert.Equal("0,14,0,0", (string?)mainRailHost.Attribute("Margin"));
        Assert.Equal(
            "DashboardMainRailScrollViewer_ViewChanged",
            (string?)mainScrollViewer.Attribute("ViewChanged"));
        Assert.Contains(compact.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "DashboardShyCustomizeButton",
                StringComparison.Ordinal));
        Assert.Contains(compact.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "DashboardShyOverviewDrawerButton",
                StringComparison.Ordinal));

        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains("CustomScalingCalculator = HeaderGreetingScaling", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRevealTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRehideTravel", source, StringComparison.Ordinal);
        Assert.Contains("AnimateMainRailReflow", source, StringComparison.Ordinal);
        Assert.Contains(
            "DashboardShyOverviewDrawerButton.Visibility = showOverviewDrawerButton",
            source,
            StringComparison.Ordinal);
        Assert.Contains("GetActiveOverviewDrawerButton()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SideRailOverviewMorphsIntoAHorizontalCanvasMatchedStrip()
    {
        XDocument document = XDocument.Load(Path("JitHub.WinUI", "Views", "Pages", "DashboardPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace labs = "using:CommunityToolkit.WinUI";
        XElement sideScrollViewer = FindById(document, "DashboardSideRailScrollViewer");
        XElement sideItems = FindById(document, "DashboardSideRail");
        XElement compact = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Name"), "DashboardOverviewShySurface", StringComparison.Ordinal));
        XElement compactTemplate = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute(xaml + "Key"), "DashboardCompactMetricTemplate", StringComparison.Ordinal));
        XElement horizontalPanel = Assert.Single(compact.Descendants(), element => element.Name.LocalName == "ItemsStackPanel");
        string source = File.ReadAllText(Path("JitHub.WinUI", "Views", "Pages", "DashboardPage.xaml.cs"));
        string models = File.ReadAllText(Path("JitHub.WinUI", "ViewModels", "Pages", "DashboardModels.cs"));
        string contracts = File.ReadAllText(Path("JitHub.WinUI", "Services", "Dashboard", "DashboardHomeContracts.cs"));
        string queryService = File.ReadAllText(Path("JitHub.WinUI", "Services", "Dashboard", "GitHubDashboardQueryService.cs"));
        string brushes = File.ReadAllText(Path("JitHub.WinUI", "Styles", "Foundation", "Tokens.Brushes.xaml"));
        string colors = File.ReadAllText(Path("JitHub.WinUI", "Styles", "Foundation", "Tokens.Colors.xaml"));

        Assert.Equal("DashboardSideRailScrollViewer_ViewChanged", (string?)sideScrollViewer.Attribute("ViewChanged"));
        Assert.Equal("DashboardWidgetCard_Loaded", (string?)document.Descendants()
            .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"), "DashboardWidgetTemplate", StringComparison.Ordinal))
            .Descendants()
            .First(element => element.Name.LocalName == "Border")
            .Attribute("Loaded"));
        Assert.Equal("Collapsed", (string?)compact.Attribute("Visibility"));
        Assert.Equal("54", (string?)compact.Attribute("Height"));
        Assert.Equal("{ThemeResource AppCanvasTransientOverlayBrush}", (string?)compact.Attribute("Background"));
        Assert.Equal("Horizontal", (string?)horizontalPanel.Attribute("Orientation"));
        Assert.Equal("{x:Bind ViewModel.Metrics, Mode=OneWay}", (string?)compact.Descendants()
            .First(element => element.Name.LocalName == "ItemsControl")
            .Attribute("ItemsSource"));
        Assert.NotNull(compactTemplate.Descendants().Single(element => element.Attribute(labs + "TransitionHelper.Id") is not null));
        Assert.NotNull(sideItems.Attribute(xaml + "Name"));

        Assert.Contains("SourceToggleMethod = VisualStateToggleMethod.ByIsVisible", source, StringComparison.Ordinal);
        Assert.Contains("TargetToggleMethod = VisualStateToggleMethod.ByIsVisible", source, StringComparison.Ordinal);
        Assert.Contains("DashboardOverviewMetricRepositories", source, StringComparison.Ordinal);
        Assert.Contains("OverviewShyStartInset", source, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback", source, StringComparison.Ordinal);
        Assert.Contains("public string TransitionId => Metric.Id switch", models, StringComparison.Ordinal);
        Assert.DoesNotContain("public string TransitionId => Label switch", models, StringComparison.Ordinal);
        foreach (string id in new[] { "Repositories", "Issues", "PullRequests", "Followers" })
        {
            Assert.Contains($"public const string {id}", contracts, StringComparison.Ordinal);
            Assert.Contains($"DashboardMetricIds.{id}", queryService, StringComparison.Ordinal);
        }

        Assert.Contains("x:Key=\"AppCanvasTransientOverlayBrush\"", brushes, StringComparison.Ordinal);
        Assert.Contains("TintColor=\"{ThemeResource AppCanvasMaterialTintColor}\"", brushes, StringComparison.Ordinal);
        Assert.Contains("FallbackColor=\"{ThemeResource AppCanvasColor}\"", brushes, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AppCanvasMaterialTintColor\"", colors, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyCanonicalNotificationsWorkspaceExposesViewAll()
    {
        string models = File.ReadAllText(Path(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "DashboardModels.cs"));
        string viewModel = File.ReadAllText(Path(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "DashboardPageViewModel.cs"));

        Assert.Contains("public bool HasViewAll => IsNotifications;", models, StringComparison.Ordinal);
        Assert.Contains("case DashboardWidgetIds.Notifications:", viewModel, StringComparison.Ordinal);
        Assert.Contains("_shellViewModel.TryOpenNotificationsPage()", viewModel, StringComparison.Ordinal);
        Assert.Contains("TelemetryTaxonomy.NavigationResult(", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_shellViewModel.OpenMyIssuesPage", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardDataRefreshesUseKeyedSnapshotsInsteadOfBlankingCollections()
    {
        string source = File.ReadAllText(Path(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "DashboardPageViewModel.cs"));

        foreach (string collection in new[]
        {
            "RecentActivity",
            "RecentRepositoriesPreview",
            "RecommendedRepositoriesPreview",
            "Notifications",
            "NotificationsPreview",
            "Metrics"
        })
        {
            Assert.Contains($"{collection}.ApplySnapshot(", source, StringComparison.Ordinal);
            Assert.DoesNotContain($"{collection}.Clear()", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QuickActionsAreUniformAndRouteWithoutAnActiveRepository()
    {
        XDocument document = XDocument.Load(Path(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "DashboardPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement quickActionTemplate = Assert.Single(document.Descendants(), element =>
            element.Attribute(xaml + "Key")?.Value == "DashboardQuickActionTemplate");
        XElement quickActionButton = Assert.Single(quickActionTemplate.Elements(), element =>
            element.Name.LocalName == "Button");
        string viewModel = File.ReadAllText(Path(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "DashboardPageViewModel.cs"));

        Assert.Equal("94", quickActionButton.Attribute("Height")?.Value);
        Assert.Equal("Stretch", quickActionButton.Attribute("VerticalContentAlignment")?.Value);
        Assert.Contains("_shellViewModel.TryOpenMyIssuesPage", viewModel, StringComparison.Ordinal);
        Assert.Contains("_shellViewModel.TryOpenMyPullRequestsPage", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_shellViewModel.TryOpenActiveRepositoryIssues", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_shellViewModel.TryOpenActiveRepositoryPullRequests", viewModel, StringComparison.Ordinal);
        Assert.Contains("DashboardWidgetIds.Repositories => 370", viewModel, StringComparison.Ordinal);
        Assert.Contains("DashboardWidgetIds.QuickActions => 220", viewModel, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(Math.Floor(contentWidth / Math.Max(1, QuickActions.Count)) - 8, 104, 124)", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityLinksUseTokenizedPlatformVisualStates()
    {
        XDocument bridge = XDocument.Load(Path(
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "WinUIResourceBridge.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Dictionary<string, string> brushes = bridge.Root!.Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                element => element.Attribute(xaml + "Key")?.Value ?? string.Empty,
                element => element.Attribute("Color")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("{ThemeResource AppAccentColor}", brushes["HyperlinkForeground"]);
        Assert.Equal("{ThemeResource AppAccentHoverColor}", brushes["HyperlinkForegroundPointerOver"]);
        Assert.Equal("{ThemeResource AppAccentPressedColor}", brushes["HyperlinkForegroundPressed"]);
        XElement lightestSystemAccent = Assert.Single(bridge.Root!.Elements(), element =>
            element.Attribute(xaml + "Key")?.Value == "SystemAccentColorLight3");
        Assert.Equal("AppAccentHoverColor", lightestSystemAccent.Attribute("ResourceKey")?.Value);

        foreach (string control in new[] { "ActivitySentenceLine.xaml", "ActivityCard.xaml" })
        {
            string activity = File.ReadAllText(Path(
                "JitHub.WinUI",
                "Views",
                "Controls",
                "App",
                control));
            Assert.Contains("ActivityInlineLinkForegroundBrush\" Color=\"{ThemeResource AppAccentColor}", activity, StringComparison.Ordinal);
            Assert.DoesNotContain("x:Key=\"HyperlinkForegroundPointerOver\"", activity, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ActivityRowsAndWidgetHeadersCenterIconsWithPrimaryText()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument activity = XDocument.Load(Path(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "ActivitySentenceLine.xaml"));
        XElement activityRoot = Assert.Single(activity.Descendants(), element =>
            element.Attribute(xaml + "Name")?.Value == "LayoutRoot");
        XElement activityIcon = Assert.Single(activityRoot.Elements(), element =>
            element.Name.LocalName == "Border");
        XElement sentence = Assert.Single(activityRoot.Elements(), element =>
            element.Attribute(xaml + "Name")?.Value == "SentenceRichTextBlock");
        XElement timestamp = Assert.Single(activityRoot.Elements(), element =>
            element.Name.LocalName == "TextBlock" && element.Attribute("Grid.Column")?.Value == "2");

        Assert.Equal("Center", activityIcon.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("0,-2,0", activityIcon.Attribute("Translation")?.Value);
        Assert.Equal("Center", sentence.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Center", timestamp.Attribute("VerticalAlignment")?.Value);
        Assert.Equal(2, activityRoot
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Count());

        XDocument dashboard = XDocument.Load(Path(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "DashboardPage.xaml"));
        XElement widgetTemplate = Assert.Single(dashboard.Descendants(), element =>
            element.Attribute(xaml + "Key")?.Value == "DashboardWidgetTemplate");
        XElement widgetLayout = Assert.Single(widgetTemplate.Elements("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}Border"))
            .Elements()
            .Single(element => element.Name.LocalName == "Grid");
        XElement widgetHeader = widgetLayout.Elements()
            .First(element => element.Name.LocalName == "Grid" && element.Attribute("Grid.Row") is null);
        XElement widgetIcon = Assert.Single(widgetHeader.Elements(), element => element.Name.LocalName == "FontIcon");
        XElement widgetTitle = Assert.Single(widgetHeader.Elements(), element => element.Name.LocalName == "TextBlock");

        Assert.Equal("Center", widgetIcon.Attribute("VerticalAlignment")?.Value);
        Assert.Null(widgetIcon.Attribute("Margin"));
        Assert.Equal("Center", widgetTitle.Attribute("VerticalAlignment")?.Value);

        XElement metricTemplate = Assert.Single(dashboard.Descendants(), element =>
            element.Attribute(xaml + "Key")?.Value == "DashboardMetricTemplate");
        XElement metricIcon = Assert.Single(metricTemplate.Descendants(), element =>
            element.Name.LocalName == "FontIcon");
        Assert.Equal("Center", metricIcon.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("0,-3,0", metricIcon.Attribute("Translation")?.Value);
    }

    [Fact]
    public void OpeningUnreadNotificationUsesOneSharedSupportedMarkReadWorkflow()
    {
        string source = File.ReadAllText(Path(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "DashboardPageViewModel.cs"));
        string workflow = File.ReadAllText(Path(
            "JitHub.WinUI",
            "Services",
            "Notifications",
            "NotificationOpenWorkflow.cs"));

        Assert.Contains("new AsyncRelayCommand(() => OpenNotificationAsync(notification))", source, StringComparison.Ordinal);
        Assert.Contains("_notificationOpenWorkflow.ExecuteAsync(", source, StringComparison.Ordinal);
        Assert.Contains("_shellViewModel.OpenNotification(notification, \"home\")", source, StringComparison.Ordinal);
        Assert.Contains("BeginReadStateMutation(", workflow, StringComparison.Ordinal);
        Assert.Contains("_queryService.MarkThreadReadAsync(", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkThreadUnreadAsync", source + workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Notifications.Clear()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewDrawerImplementsModalKeyboardAndLightDismissContract()
    {
        string xaml = File.ReadAllText(Path(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "DashboardPage.xaml"));
        string code = File.ReadAllText(Path(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "DashboardPage.xaml.cs"));

        Assert.Contains("AutomationProperties.AccessibilityView=\"Raw\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"DashboardSideDrawer_PointerPressed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_sideDrawerRestoreTarget = sender as Control ?? TryGetFocusedControl();", code, StringComparison.Ordinal);
        Assert.Contains("DashboardSideDrawerCloseButton.Focus(FocusState.Keyboard)", code, StringComparison.Ordinal);
        Assert.Contains("if (e.Key == VirtualKey.Escape)", code, StringComparison.Ordinal);
        Assert.Contains("TabFocusNavigation=\"Cycle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FocusManager.GettingFocus += FocusManager_GettingFocus", code, StringComparison.Ordinal);
        Assert.Contains("FocusManager.LosingFocus += FocusManager_LosingFocus", code, StringComparison.Ordinal);
        Assert.Contains("e.Direction == FocusNavigationDirection.Previous", code, StringComparison.Ordinal);
        Assert.Contains("e.TrySetNewFocusedElement(wrappedTarget)", code, StringComparison.Ordinal);
        Assert.Contains("e.TrySetNewFocusedElement(wrappedTarget) || e.TryCancel()", code, StringComparison.Ordinal);
        Assert.Contains("RestoreSideDrawerFocus", code, StringComparison.Ordinal);
        Assert.Contains("SetSideDrawerOpen(false);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerOnlyDecorativeSurfacesStayOutOfControlView()
    {
        XDocument shell = XDocument.Load(Path("JitHub.WinUI", "Views", "Pages", "ShellPage.xaml"));
        XDocument adaptive = XDocument.Load(Path(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AdaptiveWorkspace.xaml"));

        AssertRawByName(shell, "SearchBoxContainer");
        AssertRawByName(shell, "ShellRailDrawerOverlay");
        AssertRawByName(adaptive, "DrawerOverlay");

        Assert.NotNull(FindById(shell, "ShellSearchTextBox"));
        Assert.NotNull(FindById(shell, "ShellSearchSubmitButton"));
    }

    private static XElement FindById(XDocument document, string id) =>
        Assert.Single(document.Descendants(), element =>
            string.Equals(
                element.Attribute("AutomationProperties.AutomationId")?.Value,
                id,
                StringComparison.Ordinal));

    private static void AssertRawByName(XDocument document, string name)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement element = Assert.Single(document.Descendants(), candidate =>
            string.Equals(candidate.Attribute(xaml + "Name")?.Value, name, StringComparison.Ordinal));
        Assert.Equal("Raw", element.Attribute("AutomationProperties.AccessibilityView")?.Value);
        Assert.Null(element.Attribute("AutomationProperties.AutomationId"));
        Assert.Null(element.Attribute("AutomationProperties.Name"));
    }

    private static string Path(params string[] segments) =>
        System.IO.Path.Combine([FindRepositoryRoot(), .. segments]);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
