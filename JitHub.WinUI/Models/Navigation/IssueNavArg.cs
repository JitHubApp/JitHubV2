using JitHub.Core.Models.GitHub.Issues;
using JitHub.Core.Models.GitHub.Repositories;
using JitHub.WinUI.Models.LegacyGitHub;

namespace JitHub.WinUI.Models.Navigation;

public sealed class IssueNavArg : PageNavArg
{
    public IssueNavArg(GitHubRepository? repo, int issueId)
        : base(repo)
    {
        IssueId = issueId;
    }

    public IssueNavArg(Repository? repo, int issueId)
        : base(repo)
    {
        IssueId = issueId;
    }

    public int IssueId { get; }

    public bool IsNotificationHandoff { get; init; }

    public GitHubIssue? NavigationPreview { get; init; }

    public bool NoDetail => IssueId <= 0;
}
