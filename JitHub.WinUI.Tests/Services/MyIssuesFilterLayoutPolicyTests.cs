using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MyIssuesFilterLayoutPolicyTests
{
    [Fact]
    public void EnglishLabels_UseSegmentedControlsAtNormalLeadingPaneWidth()
    {
        bool compact = MyIssuesFilterLayoutPolicy.ShouldUseCompact(
            316,
            ["Assigned", "Created", "Mentioned"],
            ["Open", "Closed", "All"]);

        Assert.False(compact);
    }

    [Fact]
    public void PseudoLongLabels_UseCompactPickersWithoutDependingOnWindowLanguage()
    {
        bool compact = MyIssuesFilterLayoutPolicy.ShouldUseCompact(
            316,
            [
                "Assigned to the authenticated account",
                "Created by the authenticated account",
                "Mentioning the authenticated account"
            ],
            [
                "Currently open work items",
                "Previously closed work items",
                "All available work item states"
            ]);

        Assert.True(compact);
    }

    [Fact]
    public void EnglishLabels_UseCompactPickersWhenPaneIsActuallyTooNarrow()
    {
        bool compact = MyIssuesFilterLayoutPolicy.ShouldUseCompact(
            220,
            ["Assigned", "Created", "Mentioned"],
            ["Open", "Closed", "All"]);

        Assert.True(compact);
    }
}
