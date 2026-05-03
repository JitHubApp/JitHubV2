using JitHub.Models.GitHub;
using JitHub.Models.LegacyGitHub;

namespace JitHub.Models.NavArgs;

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

    public bool NoDetail => IssueId <= 0;
}
