using JitHub.Services.Layout;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepoCodeResponsiveLayoutTests
{
    [Theory]
    [InlineData(1366, AdaptivePanePlacement.Inline)]
    [InlineData(1180, AdaptivePanePlacement.Inline)]
    [InlineData(900, AdaptivePanePlacement.LeftDrawer)]
    [InlineData(760, AdaptivePanePlacement.LeftDrawer)]
    [InlineData(640, AdaptivePanePlacement.LeftDrawer)]
    public void CodeWorkspace_PreservesDetailByMovingFileTreeToDrawer(
        double availableWidth,
        AdaptivePanePlacement expectedPlacement)
    {
        AdaptiveWorkspaceState state = AdaptiveWorkspaceLayout.Calculate(
            availableWidth,
            hasLeadingPane: true,
            hasTrailingPane: false,
            new AdaptiveWorkspaceBreakpoints(Wide: 1260, Medium: 980, Narrow: 720));

        Assert.Equal(expectedPlacement, state.LeadingPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Inline, state.PrimaryPanePlacement);
        Assert.Equal(AdaptivePanePlacement.Hidden, state.TrailingPanePlacement);
    }

    [Theory]
    [InlineData(1180, RepoCodeBreadcrumbMode.Expanded)]
    [InlineData(700, RepoCodeBreadcrumbMode.Expanded)]
    [InlineData(699, RepoCodeBreadcrumbMode.Compact)]
    [InlineData(640, RepoCodeBreadcrumbMode.Compact)]
    [InlineData(double.NaN, RepoCodeBreadcrumbMode.Compact)]
    public void Breadcrumb_CollapsesLowPriorityActionsWithoutHidingFilename(
        double availableWidth,
        RepoCodeBreadcrumbMode expectedMode)
    {
        RepoCodeBreadcrumbState state = RepoCodeResponsiveLayout.CalculateBreadcrumb(availableWidth);

        Assert.Equal(expectedMode, state.Mode);
        Assert.NotEqual(state.ShowFullPath, state.ShowFileName);
        Assert.NotEqual(state.ShowDirectActions, state.ShowActionsOverflow);
        Assert.True(state.ShowFullPath || state.ShowFileName);
    }
}
