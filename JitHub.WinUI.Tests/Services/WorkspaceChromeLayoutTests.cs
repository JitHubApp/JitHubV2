using System.Collections.Generic;
using JitHub.Services.Layout;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class WorkspaceChromeLayoutTests
{
    [Theory]
    [InlineData(1366, WorkspaceChromeMode.Wide, 20, 1326, true, true, false)]
    [InlineData(820, WorkspaceChromeMode.Wide, 20, 780, true, true, false)]
    [InlineData(819, WorkspaceChromeMode.Compact, 16, 787, true, false, true)]
    [InlineData(620, WorkspaceChromeMode.Compact, 16, 588, true, false, true)]
    [InlineData(619, WorkspaceChromeMode.Narrow, 12, 595, false, false, true)]
    [InlineData(320, WorkspaceChromeMode.Narrow, 12, 296, false, false, true)]
    public void Calculate_UsesOneStablePageChromePolicy(
        double width,
        WorkspaceChromeMode expectedMode,
        double expectedInset,
        double expectedContentWidth,
        bool expectedLabels,
        bool expectedContext,
        bool expectedStackedCommands)
    {
        WorkspaceChromeState state = WorkspaceChromeLayout.Calculate(width);

        Assert.Equal(expectedMode, state.Mode);
        Assert.Equal(new WorkspaceInsets(expectedInset, expectedInset, expectedInset, expectedInset), state.Insets);
        Assert.Equal(expectedContentWidth, state.ContentBounds.Width);
        Assert.Equal(expectedLabels, state.ShowActionLabels);
        Assert.Equal(expectedContext, state.ShowOptionalHeaderContext);
        Assert.Equal(expectedStackedCommands, state.StackCommandRows);
        Assert.Equal(new WorkspaceHeaderMetrics(48, 28, 22, 36, 12, 8), state.Header);
    }

    public static IEnumerable<object[]> CanonicalPageContracts()
    {
        foreach (WorkspaceChromeContract contract in WorkspaceChromeContracts.CanonicalPages)
        {
            yield return [contract];
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalPageContracts))]
    public void CanonicalPage_CompactPolicyIsCapabilityDriven(WorkspaceChromeContract contract)
    {
        WorkspaceChromeState state = WorkspaceChromeLayout.Calculate(760, contract);

        Assert.Equal(WorkspaceChromeMode.Compact, state.Mode);
        Assert.Equal(new WorkspaceInsets(16, 16, 16, 16), state.Insets);
        Assert.Equal(728, state.ContentBounds.Width);
        Assert.Equal(
            contract.Supports(WorkspaceChromeFeatures.ActionLabels),
            state.ShowActionLabels);
        Assert.False(state.ShowOptionalHeaderContext);
        Assert.Equal(
            contract.Supports(WorkspaceChromeFeatures.CommandRows),
            state.StackCommandRows);
    }

    [Theory]
    [MemberData(nameof(CanonicalPageContracts))]
    public void CanonicalPage_NarrowPolicyCollapsesLabelsAndContext(WorkspaceChromeContract contract)
    {
        WorkspaceChromeState state = WorkspaceChromeLayout.Calculate(540, contract);

        Assert.Equal(new WorkspaceInsets(12, 12, 12, 12), state.Insets);
        Assert.Equal(516, state.ContentBounds.Width);
        Assert.False(state.ShowActionLabels);
        Assert.False(state.ShowOptionalHeaderContext);
        Assert.Equal(
            contract.Supports(WorkspaceChromeFeatures.CommandRows),
            state.StackCommandRows);
    }

    [Theory]
    [MemberData(nameof(CanonicalPageContracts))]
    public void CanonicalPage_ContentWidthIgnoresChildDesiredWidth(WorkspaceChromeContract contract)
    {
        WorkspaceChromeState state = WorkspaceChromeLayout.Calculate(760, contract);

        Assert.Equal(728, state.ContentBounds.Arrange(0));
        Assert.Equal(728, state.ContentBounds.Arrange(420));
        Assert.Equal(728, state.ContentBounds.Arrange(2400));
        Assert.Equal(728, state.ContentBounds.Arrange(double.PositiveInfinity));
    }

    [Fact]
    public void ChoosePlacement_ReflowsOnlyWhenContractOwnsCommandRows()
    {
        WorkspaceElementPlacement wide = new(0, 2, 1);
        WorkspaceElementPlacement stacked = new(1, 0, 2, StretchHorizontally: true);
        WorkspaceChromeState notifications = WorkspaceChromeLayout.Calculate(
            760,
            WorkspaceChromeContracts.Notifications);
        WorkspaceChromeState dashboard = WorkspaceChromeLayout.Calculate(
            760,
            WorkspaceChromeContracts.Dashboard);

        Assert.Equal(stacked, WorkspaceChromeLayout.ChoosePlacement(notifications, wide, stacked));
        Assert.Equal(wide, WorkspaceChromeLayout.ChoosePlacement(dashboard, wide, stacked));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Calculate_InvalidWidthFallsBackToMostConstrainedLayout(double width)
    {
        WorkspaceChromeState state = WorkspaceChromeLayout.Calculate(width);

        Assert.Equal(WorkspaceChromeMode.Narrow, state.Mode);
        Assert.False(state.ShowActionLabels);
        Assert.True(state.StackCommandRows);
        Assert.Equal(0, state.ContentBounds.Width);
    }
}
