using Octokit;

namespace JitHub.Models.NavArgs
{
    public class CommitPageNavArg : PageNavArg
    {
        private string _branch;
        private string _gitRef;
        public string Branch => _branch;
        public string GitRef => _gitRef;
        public bool NoBranch => string.IsNullOrWhiteSpace(_branch);
        public bool NoRef => string.IsNullOrWhiteSpace(_gitRef);

        public CommitPageNavArg(Repository repo) : base(repo)
        {
        }

        public static CommitPageNavArg CreateWithBranch(Repository repo, string branch)
        {
            return new CommitPageNavArg(repo) { _branch = branch };
        }

        public static CommitPageNavArg Create(Repository repo, string branch, string _ref)
        {
            return new CommitPageNavArg(repo) { _gitRef = _ref, _branch = branch };
        }

        public static CommitPageNavArg CreateWithGitRef(Repository repo, string _ref)
        {
            return new CommitPageNavArg(repo) { _gitRef = _ref };
        }
    }
}
