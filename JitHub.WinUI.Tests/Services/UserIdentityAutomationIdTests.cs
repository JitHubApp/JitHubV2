using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class UserIdentityAutomationIdTests
{
    [Fact]
    public void RepeatedRowsForSameAuthorReceiveUniqueStableIds()
    {
        string first = UserIdentityAutomationId.Create("issue_comment", "IssueComment_11", "octocat");
        string refreshed = UserIdentityAutomationId.Create("issue_comment", "IssueComment_11", "octocat");
        string second = UserIdentityAutomationId.Create("issue_comment", "IssueComment_12", "octocat");

        Assert.Equal(first, refreshed);
        Assert.NotEqual(first, second);
        Assert.Equal("UserProfile_issue_comment_IssueComment_11_octocat", first);
    }

    [Fact]
    public void RepeatedPassiveRowsDoNotCollapseToOneUnavailableId()
    {
        string first = UserIdentityAutomationId.Create("pull_request_reviewer", "Review_41", null);
        string second = UserIdentityAutomationId.Create("pull_request_reviewer", "Review_42", "ghost");
        string bot = UserIdentityAutomationId.Create("pull_request_reviewer", "Review_43", "dependabot[bot]");

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, bot);
        Assert.StartsWith("UserProfile_Unavailable_pull_request_reviewer_", first);
        Assert.StartsWith("UserProfile_Unavailable_pull_request_reviewer_", second);
        Assert.StartsWith("UserProfile_Unavailable_pull_request_reviewer_", bot);
    }

    [Fact]
    public void HostScopeSeparatesSameLogicalActorInReviewerAndAssigneeLists()
    {
        string reviewer = UserIdentityAutomationId.Create("pull_request_requested_reviewer", "GitHubActor_7", "octocat");
        string assignee = UserIdentityAutomationId.Create("pull_request_assignee", "GitHubActor_7", "octocat");

        Assert.NotEqual(reviewer, assignee);
    }
}
