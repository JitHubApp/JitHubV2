namespace JitHub.Services.Layout;

public enum RepoCodeBreadcrumbMode
{
    Expanded,
    Compact
}

public sealed record RepoCodeBreadcrumbState(
    RepoCodeBreadcrumbMode Mode,
    bool ShowFullPath,
    bool ShowDirectActions,
    bool ShowFileName,
    bool ShowActionsOverflow);

public static class RepoCodeResponsiveLayout
{
    public const double CompactBreadcrumbBreakpoint = 700;

    public static RepoCodeBreadcrumbState CalculateBreadcrumb(double availableWidth)
    {
        bool isCompact = !double.IsFinite(availableWidth) ||
            availableWidth < CompactBreadcrumbBreakpoint;

        return isCompact
            ? new RepoCodeBreadcrumbState(
                RepoCodeBreadcrumbMode.Compact,
                ShowFullPath: false,
                ShowDirectActions: false,
                ShowFileName: true,
                ShowActionsOverflow: true)
            : new RepoCodeBreadcrumbState(
                RepoCodeBreadcrumbMode.Expanded,
                ShowFullPath: true,
                ShowDirectActions: true,
                ShowFileName: false,
                ShowActionsOverflow: false);
    }
}
