using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Controls.App;

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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
