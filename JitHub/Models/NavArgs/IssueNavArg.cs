using Octokit;

namespace JitHub.Models.NavArgs
{
    public class IssueNavArg : PageNavArg
    {
        private int _issueId;
        public int IssueId => _issueId;
        public bool NoDetail => _issueId <= 0;

        public IssueNavArg(Repository repo, int issueId) : base(repo)
        {
            _issueId = issueId;
        }
    }
}
