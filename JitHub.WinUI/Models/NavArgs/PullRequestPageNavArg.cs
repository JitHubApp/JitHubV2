using JitHub.Models.GitHub;
using Octokit;

namespace JitHub.Models.NavArgs;

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
