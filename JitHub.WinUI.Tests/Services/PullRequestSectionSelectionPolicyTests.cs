using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PullRequestSectionSelectionPolicyTests
{
    [Theory]
    [InlineData(0, PullRequestWorkspaceSection.Conversation)]
    [InlineData(1, PullRequestWorkspaceSection.Files)]
    [InlineData(2, PullRequestWorkspaceSection.Commits)]
    [InlineData(3, PullRequestWorkspaceSection.Reviews)]
    [InlineData(4, PullRequestWorkspaceSection.Timeline)]
    [InlineData(-1, PullRequestWorkspaceSection.Conversation)]
    [InlineData(5, PullRequestWorkspaceSection.Conversation)]
    public void FromIndex_MatchesVisibleSegmentOrder(
        int selectedIndex,
        PullRequestWorkspaceSection expected)
    {
        Assert.Equal(expected, PullRequestSectionSelectionPolicy.FromIndex(selectedIndex));
    }
}
