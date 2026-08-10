using System;
using System.IO;
using JitHub.Services.Layout;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AdaptiveWorkspaceLayoutTests
{
    [Fact]
    public void DrawerTransfersFocusAfterPageVisibilityStateAndRetriesAfterAnimation()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AdaptiveWorkspace.xaml.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "ApplyLayout(drawer);\n        if (State?.VisibleDrawer == drawer)\n        {\n            FocusDrawerImmediately(drawer);\n            QueueDrawerFocus(drawer);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_leftDrawerAnimator.SetOpen(true, animate, () => QueueDrawerFocus(drawer));", source, StringComparison.Ordinal);
        Assert.Contains("_rightDrawerAnimator.SetOpen(true, animate, () => QueueDrawerFocus(drawer));", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DrawerFocusTraversalSkipsStructuralContentWrappers()
    {
        string root = FindRepositoryRoot();
        string workspace = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AdaptiveWorkspace.xaml.cs"));
        Assert.Contains("!IsStructuralFocusContainer(control)", workspace, StringComparison.Ordinal);
        Assert.Contains("control is UserControl || control.GetType() == typeof(ContentControl)", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void DrawerLightDismissObservesHandledPointerPresses()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AdaptiveWorkspace.xaml.cs"));

        Assert.Contains("new PointerEventHandler(DrawerOverlay_PointerPressed)", source, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", source, StringComparison.Ordinal);
        Assert.Contains("IsWithin(source, LeftDrawer) || IsWithin(source, RightDrawer)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DrawerContentRemainsMountedAcrossCloseAndReopen()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AdaptiveWorkspace.xaml.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "state.LeadingPanePlacement == AdaptivePanePlacement.LeftDrawer",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.TrailingPanePlacement == AdaptivePanePlacement.RightDrawer",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "state.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading\n                    ? LeadingDrawerPresenter",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "state.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing\n                    ? TrailingDrawerPresenter",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShellAndWorkspace_CollapseInspectorThenRailThenLeadingPane()
    {
        AdaptiveWorkspaceBreakpoints breakpoints = new();

        ShellResponsiveState fullyWideShell = ShellResponsiveLayout.Calculate(1600);
        AdaptiveWorkspaceState fullyWideWorkspace = AdaptiveWorkspaceLayout.CalculateForShell(
            fullyWideShell.WindowWidth,
            fullyWideShell.ContentWidth,
            hasLeadingPane: true,
            hasTrailingPane: true,
            breakpoints);
        Assert.True(fullyWideShell.IsRailInline);
        Assert.Equal(AdaptivePanePlacement.Inline, fullyWideWorkspace.TrailingPanePlacement);

        ShellResponsiveState inspectorCollapsedShell = ShellResponsiveLayout.Calculate(1400);
        AdaptiveWorkspaceState inspectorCollapsedWorkspace = AdaptiveWorkspaceLayout.CalculateForShell(
            inspectorCollapsedShell.WindowWidth,
            inspectorCollapsedShell.ContentWidth,
            true,
            true,
            breakpoints);
        Assert.True(inspectorCollapsedShell.IsRailInline);
        Assert.Equal(AdaptivePanePlacement.RightDrawer, inspectorCollapsedWorkspace.TrailingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Inline, inspectorCollapsedWorkspace.LeadingPanePlacement);

        ShellResponsiveState railCollapsedShell = ShellResponsiveLayout.Calculate(1180);
        AdaptiveWorkspaceState railCollapsedWorkspace = AdaptiveWorkspaceLayout.CalculateForShell(
            railCollapsedShell.WindowWidth,
            railCollapsedShell.ContentWidth,
            true,
            true,
            breakpoints);
        Assert.False(railCollapsedShell.IsRailInline);
        Assert.Equal(ShellResponsiveLayout.CompactTitleAreaWidth, railCollapsedShell.TitleAreaWidth);
        Assert.Equal(AdaptivePanePlacement.RightDrawer, railCollapsedWorkspace.TrailingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Inline, railCollapsedWorkspace.LeadingPanePlacement);

        ShellResponsiveState leadingCollapsedShell = ShellResponsiveLayout.Calculate(900);
        AdaptiveWorkspaceState leadingCollapsedWorkspace = AdaptiveWorkspaceLayout.CalculateForShell(
            leadingCollapsedShell.WindowWidth,
            leadingCollapsedShell.ContentWidth,
            true,
            true,
            breakpoints);
        Assert.False(leadingCollapsedShell.IsRailInline);
        Assert.Equal(AdaptivePanePlacement.LeftDrawer, leadingCollapsedWorkspace.LeadingPanePlacement);
    }

    [Theory]
    [InlineData(1297, false)]
    [InlineData(1298, true)]
    [InlineData(1180, false)]
    [InlineData(900, false)]
    public void ShellRailBreakpointAlwaysLeavesNavigationReachable(double width, bool expectedInline)
    {
        ShellResponsiveState state = ShellResponsiveLayout.Calculate(width);

        Assert.Equal(expectedInline, state.IsRailInline);
        Assert.Equal(
            expectedInline ? ShellResponsiveLayout.RailWidth : ShellResponsiveLayout.CompactTitleAreaWidth,
            state.TitleAreaWidth);
        Assert.True(state.TitleAreaWidth > 0);
    }

    [Theory]
    [InlineData(1546, true, true, true)]
    [InlineData(1545, true, true, false)]
    [InlineData(1298, true, true, false)]
    [InlineData(1297, false, true, false)]
    [InlineData(980, false, true, false)]
    [InlineData(979, false, false, false)]
    public void CoordinatedBreakpointsNeverRevealPanesWhileWindowNarrows(
        double windowWidth,
        bool expectedRailInline,
        bool expectedLeadingInline,
        bool expectedTrailingInline)
    {
        ShellResponsiveState shell = ShellResponsiveLayout.Calculate(windowWidth);
        AdaptiveWorkspaceState workspace = AdaptiveWorkspaceLayout.CalculateForShell(
            windowWidth,
            shell.ContentWidth,
            hasLeadingPane: true,
            hasTrailingPane: true);

        Assert.Equal(expectedRailInline, shell.IsRailInline);
        Assert.Equal(expectedLeadingInline, workspace.IsLeadingPaneInline);
        Assert.Equal(expectedTrailingInline, workspace.IsTrailingPaneInline);
    }

    [Fact]
    public void WideLayoutKeepsAllPanesInline()
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.Calculate(
            1366,
            hasLeadingPane: true,
            hasTrailingPane: true);

        Assert.Equal(AdaptiveWorkspaceMode.Wide, state.Mode);
        Assert.Equal(AdaptivePanePlacement.Inline, state.LeadingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Inline, state.PrimaryPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Inline, state.TrailingPanePlacement);
        Assert.False(state.ShouldShowLeadingPaneButton);
        Assert.False(state.ShouldShowTrailingPaneButton);
    }

    [Fact]
    public void MediumLayoutKeepsLeadingInlineAndMovesInspectorToDrawer()
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.Calculate(
            1180,
            hasLeadingPane: true,
            hasTrailingPane: true);

        Assert.Equal(AdaptiveWorkspaceMode.Medium, state.Mode);
        Assert.Equal(AdaptivePanePlacement.Inline, state.LeadingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.RightDrawer, state.TrailingPanePlacement);
        Assert.False(state.ShouldShowLeadingPaneButton);
        Assert.True(state.ShouldShowTrailingPaneButton);
    }

    [Fact]
    public void NarrowLayoutMovesLeadingAndInspectorToDrawers()
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.Calculate(
            900,
            hasLeadingPane: true,
            hasTrailingPane: true);

        Assert.Equal(AdaptiveWorkspaceMode.Narrow, state.Mode);
        Assert.Equal(AdaptivePanePlacement.LeftDrawer, state.LeadingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Inline, state.PrimaryPanePlacement);
        Assert.Equal(AdaptivePanePlacement.RightDrawer, state.TrailingPanePlacement);
        Assert.True(state.ShouldShowLeadingPaneButton);
        Assert.True(state.ShouldShowTrailingPaneButton);
    }

    [Fact]
    public void CompactLayoutKeepsPrimaryInlineAndPanesAvailableAsDrawers()
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.Calculate(
            640,
            hasLeadingPane: true,
            hasTrailingPane: true);

        Assert.Equal(AdaptiveWorkspaceMode.Compact, state.Mode);
        Assert.Equal(AdaptivePanePlacement.LeftDrawer, state.LeadingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Inline, state.PrimaryPanePlacement);
        Assert.Equal(AdaptivePanePlacement.RightDrawer, state.TrailingPanePlacement);
        Assert.True(state.ShouldShowLeadingPaneButton);
        Assert.True(state.ShouldShowTrailingPaneButton);
    }

    [Fact]
    public void InvalidVisibleDrawerIsClosedWhenPaneBecomesInline()
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.Calculate(
            1366,
            hasLeadingPane: true,
            hasTrailingPane: true,
            visibleDrawer: AdaptiveWorkspaceDrawer.Leading);

        Assert.Equal(AdaptiveWorkspaceMode.Wide, state.Mode);
        Assert.Equal(AdaptiveWorkspaceDrawer.None, state.VisibleDrawer);
    }

    [Fact]
    public void MissingPanesAreHiddenAndDoNotShowButtons()
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.Calculate(
            640,
            hasLeadingPane: false,
            hasTrailingPane: false);

        Assert.Equal(AdaptivePanePlacement.Hidden, state.LeadingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Hidden, state.TrailingPanePlacement);
        Assert.False(state.ShouldShowLeadingPaneButton);
        Assert.False(state.ShouldShowTrailingPaneButton);
    }

    [Theory]
    [InlineData(1366, 1080, true, true)]
    [InlineData(1180, 1180, true, false)]
    [InlineData(900, 900, true, false)]
    [InlineData(760, 760, false, false)]
    public void CommitBreakpointsCollapseInspectorThenShellRailThenHistory(
        double windowWidth,
        double contentWidth,
        bool expectedLeadingInline,
        bool expectedTrailingInline)
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.CalculateForShell(
            windowWidth,
            contentWidth,
            hasLeadingPane: true,
            hasTrailingPane: true,
            new AdaptiveWorkspaceBreakpoints(Wide: 1040, Medium: 880, Narrow: 620));

        Assert.Equal(expectedLeadingInline, state.IsLeadingPaneInline);
        Assert.Equal(expectedTrailingInline, state.IsTrailingPaneInline);
        if (!expectedTrailingInline)
        {
            Assert.Equal(AdaptivePanePlacement.RightDrawer, state.TrailingPanePlacement);
        }
    }

    [Fact]
    public void ControlPublishesItsInitialResponsiveState()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AdaptiveWorkspace.xaml.cs"));

        Assert.Contains("bool hadState = State is not null;", source, StringComparison.Ordinal);
        Assert.Contains("if (!hadState ||", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
