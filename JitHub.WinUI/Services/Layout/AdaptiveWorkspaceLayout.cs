namespace JitHub.Services.Layout;

public enum AdaptiveWorkspaceMode
{
    Wide,
    Medium,
    Narrow,
    Compact
}

public enum AdaptivePanePlacement
{
    Inline,
    LeftDrawer,
    RightDrawer,
    Hidden
}

public enum AdaptiveWorkspaceDrawer
{
    None,
    Leading,
    Trailing
}

public sealed record AdaptiveWorkspaceBreakpoints(
    double Wide = 1260,
    double Medium = 980,
    double Narrow = 720);

public sealed record AdaptiveWorkspaceState(
    AdaptiveWorkspaceMode Mode,
    AdaptivePanePlacement LeadingPanePlacement,
    AdaptivePanePlacement PrimaryPanePlacement,
    AdaptivePanePlacement TrailingPanePlacement,
    AdaptiveWorkspaceDrawer VisibleDrawer,
    double AvailableWidth)
{
    public bool IsLeadingPaneInline => LeadingPanePlacement == AdaptivePanePlacement.Inline;

    public bool IsTrailingPaneInline => TrailingPanePlacement == AdaptivePanePlacement.Inline;

    public bool ShouldShowLeadingPaneButton => LeadingPanePlacement == AdaptivePanePlacement.LeftDrawer;

    public bool ShouldShowTrailingPaneButton => TrailingPanePlacement == AdaptivePanePlacement.RightDrawer;
}

public static class AdaptiveWorkspaceLayout
{
    public static AdaptiveWorkspaceState CalculateForShell(
        double windowWidth,
        double contentWidth,
        bool hasLeadingPane,
        bool hasTrailingPane,
        AdaptiveWorkspaceBreakpoints? breakpoints = null,
        AdaptiveWorkspaceDrawer visibleDrawer = AdaptiveWorkspaceDrawer.None)
    {
        breakpoints ??= new AdaptiveWorkspaceBreakpoints();
        double coordinatedWidth = ShellResponsiveLayout.CoordinateWorkspaceWidth(
            windowWidth,
            contentWidth,
            breakpoints);
        AdaptiveWorkspaceState state = Calculate(
            coordinatedWidth,
            hasLeadingPane,
            hasTrailingPane,
            breakpoints,
            visibleDrawer);
        return state with { AvailableWidth = contentWidth };
    }

    public static AdaptiveWorkspaceState Calculate(
        double availableWidth,
        bool hasLeadingPane,
        bool hasTrailingPane,
        AdaptiveWorkspaceBreakpoints? breakpoints = null,
        AdaptiveWorkspaceDrawer visibleDrawer = AdaptiveWorkspaceDrawer.None)
    {
        breakpoints ??= new AdaptiveWorkspaceBreakpoints();

        AdaptiveWorkspaceMode mode = availableWidth >= breakpoints.Wide
            ? AdaptiveWorkspaceMode.Wide
            : availableWidth >= breakpoints.Medium
                ? AdaptiveWorkspaceMode.Medium
                : availableWidth >= breakpoints.Narrow
                    ? AdaptiveWorkspaceMode.Narrow
                    : AdaptiveWorkspaceMode.Compact;

        AdaptivePanePlacement leadingPlacement = hasLeadingPane
            ? mode is AdaptiveWorkspaceMode.Wide or AdaptiveWorkspaceMode.Medium
                ? AdaptivePanePlacement.Inline
                : AdaptivePanePlacement.LeftDrawer
            : AdaptivePanePlacement.Hidden;

        AdaptivePanePlacement trailingPlacement = hasTrailingPane
            ? mode == AdaptiveWorkspaceMode.Wide
                ? AdaptivePanePlacement.Inline
                : AdaptivePanePlacement.RightDrawer
            : AdaptivePanePlacement.Hidden;

        AdaptiveWorkspaceDrawer safeDrawer = visibleDrawer;
        if (safeDrawer == AdaptiveWorkspaceDrawer.Leading && leadingPlacement != AdaptivePanePlacement.LeftDrawer)
        {
            safeDrawer = AdaptiveWorkspaceDrawer.None;
        }
        else if (safeDrawer == AdaptiveWorkspaceDrawer.Trailing && trailingPlacement != AdaptivePanePlacement.RightDrawer)
        {
            safeDrawer = AdaptiveWorkspaceDrawer.None;
        }

        return new AdaptiveWorkspaceState(
            mode,
            leadingPlacement,
            AdaptivePanePlacement.Inline,
            trailingPlacement,
            safeDrawer,
            availableWidth);
    }
}
