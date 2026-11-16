using JitHub.Core.Models.GitHub.Repositories;
using JitHub.WinUI.Models.LegacyGitHub;

namespace JitHub.WinUI.Models.Navigation;

public sealed class PullRequestPageNavArg : PageNavArg
{
    public PullRequestPageNavArg(GitHubRepository? repo, int pullRequestId)
        : base(repo)
    {
        PullRequestId = pullRequestId;
    }

    public PullRequestPageNavArg(Repository? repo, int pullRequestId)
        : base(repo)
    {
        PullRequestId = pullRequestId;
    }

    public int PullRequestId { get; }

    public bool NoDetail => PullRequestId <= 0;
}
