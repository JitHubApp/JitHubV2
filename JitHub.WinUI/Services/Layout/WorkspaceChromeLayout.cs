using System;
using System.Collections.Generic;

namespace JitHub.Services.Layout;

public enum WorkspaceChromeMode
{
    Wide,
    Compact,
    Narrow
}

[Flags]
public enum WorkspaceChromeFeatures
{
    None = 0,
    ActionLabels = 1,
    OptionalHeaderContext = 2,
    CommandRows = 4,
    All = ActionLabels | OptionalHeaderContext | CommandRows
}

public readonly record struct WorkspaceChromeContract(
    string PageKey,
    WorkspaceChromeFeatures Features)
{
    public bool Supports(WorkspaceChromeFeatures feature) =>
        (Features & feature) == feature;
}

public static class WorkspaceChromeContracts
{
    public static readonly WorkspaceChromeContract Dashboard = new(
        "dashboard",
        WorkspaceChromeFeatures.ActionLabels);

    public static readonly WorkspaceChromeContract Profile = new(
        "profile",
        WorkspaceChromeFeatures.ActionLabels |
        WorkspaceChromeFeatures.OptionalHeaderContext |
        WorkspaceChromeFeatures.CommandRows);

    public static readonly WorkspaceChromeContract Notifications = new(
        "notifications",
        WorkspaceChromeFeatures.All);

    public static readonly WorkspaceChromeContract Stars = new(
        "stars",
        WorkspaceChromeFeatures.All);

    public static readonly WorkspaceChromeContract Gists = new(
        "gists",
        WorkspaceChromeFeatures.ActionLabels);

    public static readonly WorkspaceChromeContract RepositorySearch = new(
        "repository_search",
        WorkspaceChromeFeatures.CommandRows);

    public static readonly IReadOnlyList<WorkspaceChromeContract> CanonicalPages =
    (WorkspaceChromeContract[])
    [
        Dashboard,
        Profile,
        Notifications,
        Stars,
        Gists,
        RepositorySearch
    ];
}

public readonly record struct WorkspaceInsets(
    double Left,
    double Top,
    double Right,
    double Bottom);

public readonly record struct WorkspaceHeaderMetrics(
    double MinHeight,
    double TitleFontSize,
    double IconSize,
    double ActionHeight,
    double ColumnSpacing,
    double RowSpacing);

public readonly record struct WorkspaceContentBounds(double Width)
{
    /// <summary>
    /// Workspace structure owns arranged width. A child's desired width may
    /// influence its own content reflow, but never the page width.
    /// </summary>
    public double Arrange(double childDesiredWidth)
    {
        _ = childDesiredWidth;
        return Width;
    }
}

public readonly record struct WorkspaceElementPlacement(
    int Row,
    int Column,
    int ColumnSpan,
    bool StretchHorizontally = false);

public readonly record struct WorkspaceChromeState(
    WorkspaceChromeMode Mode,
    WorkspaceInsets Insets,
    WorkspaceContentBounds ContentBounds,
    WorkspaceHeaderMetrics Header,
    bool ShowActionLabels,
    bool ShowOptionalHeaderContext,
    bool StackCommandRows);

/// <summary>
/// Owns shared page-level geometry and responsive command policy. Page content
/// may reflow inside these bounds, but it cannot change the workspace width.
/// </summary>
public static class WorkspaceChromeLayout
{
    public const double CompactBreakpoint = 820;
    public const double NarrowBreakpoint = 620;

    public static readonly WorkspaceHeaderMetrics HeaderMetrics = new(
        MinHeight: 48,
        TitleFontSize: 28,
        IconSize: 22,
        ActionHeight: 36,
        ColumnSpacing: 12,
        RowSpacing: 8);

    public static WorkspaceChromeState Calculate(double availableWidth) =>
        Calculate(availableWidth, new WorkspaceChromeContract("default", WorkspaceChromeFeatures.All));

    public static WorkspaceChromeState Calculate(
        double availableWidth,
        WorkspaceChromeContract contract)
    {
        double width = double.IsFinite(availableWidth)
            ? Math.Max(0, availableWidth)
            : 0;

        WorkspaceChromeMode mode = width switch
        {
            >= CompactBreakpoint => WorkspaceChromeMode.Wide,
            >= NarrowBreakpoint => WorkspaceChromeMode.Compact,
            _ => WorkspaceChromeMode.Narrow
        };

        WorkspaceInsets insets = mode switch
        {
            WorkspaceChromeMode.Wide => new(20, 20, 20, 20),
            WorkspaceChromeMode.Compact => new(16, 16, 16, 16),
            _ => new(12, 12, 12, 12)
        };

        double contentWidth = Math.Max(0, width - insets.Left - insets.Right);
        return new WorkspaceChromeState(
            mode,
            insets,
            new WorkspaceContentBounds(contentWidth),
            HeaderMetrics,
            ShowActionLabels: contract.Supports(WorkspaceChromeFeatures.ActionLabels) &&
                mode != WorkspaceChromeMode.Narrow,
            ShowOptionalHeaderContext: contract.Supports(WorkspaceChromeFeatures.OptionalHeaderContext) &&
                mode == WorkspaceChromeMode.Wide,
            StackCommandRows: contract.Supports(WorkspaceChromeFeatures.CommandRows) &&
                mode != WorkspaceChromeMode.Wide);
    }

    public static WorkspaceElementPlacement ChoosePlacement(
        WorkspaceChromeState state,
        WorkspaceElementPlacement wide,
        WorkspaceElementPlacement stacked) =>
        state.StackCommandRows ? stacked : wide;
}
