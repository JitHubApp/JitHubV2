using Octokit;

namespace JitHub.Models.NavArgs
{
    public class PageNavArg
    {
        private Repository _repo;
        public Repository Repo => _repo;

        public PageNavArg(Repository repo)
        {
            _repo = repo;
        }

        public PageNavArg WithRepo(Repository repo)
        {
            _repo = repo;
            return this;
        }
    }
}
