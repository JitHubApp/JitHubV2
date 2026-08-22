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
