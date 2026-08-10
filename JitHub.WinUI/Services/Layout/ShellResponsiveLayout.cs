using System;

namespace JitHub.Services.Layout;

public sealed record ShellResponsiveState(
    double WindowWidth,
    bool IsRailInline,
    double ContentWidth,
    double TitleAreaWidth);

public static class ShellResponsiveLayout
{
    public const double RailWidth = 286;
    // Keep the rail inline only while the remaining workspace can still keep
    // its leading pane inline. This prevents the pane order from inverting at
    // the shell breakpoint (narrowing the window must never make a pane appear).
    public const double WorkspaceLeadingInlineWidth = 980;
    // Shell/frame borders and workspace padding sit between the window width
    // and AdaptiveWorkspace.ActualWidth. Reserve them here so the shell rail
    // collapses before those insets can force the page's leading pane out.
    public const double WorkspaceStructuralInset = 32;
    public const double RailCollapseWidth = RailWidth + WorkspaceLeadingInlineWidth + WorkspaceStructuralInset;
    public const double CompactTitleAreaWidth = 160;

    public static ShellResponsiveState Calculate(double windowWidth)
    {
        double safeWidth = Math.Max(0, windowWidth);
        bool isRailInline = safeWidth >= RailCollapseWidth;
        double contentWidth = Math.Max(0, safeWidth - (isRailInline ? RailWidth : 0));
        double titleAreaWidth = isRailInline ? RailWidth : CompactTitleAreaWidth;
        return new ShellResponsiveState(safeWidth, isRailInline, contentWidth, titleAreaWidth);
    }

    public static double CoordinateWorkspaceWidth(
        double windowWidth,
        double contentWidth,
        AdaptiveWorkspaceBreakpoints breakpoints)
    {
        double safeContentWidth = Math.Max(0, contentWidth);
        ShellResponsiveState shell = Calculate(windowWidth);
        if (shell.IsRailInline || safeContentWidth < breakpoints.Medium)
        {
            return safeContentWidth;
        }

        // Once the shell rail has collapsed, keep the inspector collapsed too.
        // The newly recovered rail width belongs to the primary reading surface;
        // it must not make an already-collapsed trailing pane reappear.
        return Math.Min(safeContentWidth, Math.BitDecrement(breakpoints.Wide));
    }
}
