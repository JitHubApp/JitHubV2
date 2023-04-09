using Octokit;

namespace JitHub.Models.NavArgs
{
    public class PullRequestPageNavArg : PageNavArg
    {
        private int _pullRequestId;
        public int PullRequestId => _pullRequestId;
        public bool NoDetail => _pullRequestId <= 0;

        public PullRequestPageNavArg(Repository repo, int pullRequestId) : base(repo)
        {
            _pullRequestId = pullRequestId;
        }
    }
}
