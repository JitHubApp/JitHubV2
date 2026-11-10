namespace JitHub.Services;

public static class PullRequestSectionSelectionPolicy
{
    public static PullRequestWorkspaceSection FromIndex(int selectedIndex) => selectedIndex switch
    {
        1 => PullRequestWorkspaceSection.Files,
        2 => PullRequestWorkspaceSection.Commits,
        3 => PullRequestWorkspaceSection.Reviews,
        4 => PullRequestWorkspaceSection.Timeline,
        _ => PullRequestWorkspaceSection.Conversation
    };
}
