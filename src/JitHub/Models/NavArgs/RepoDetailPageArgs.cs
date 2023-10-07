using Octokit;

namespace JitHub.Models.NavArgs
{
    public class RepoDetailPageArgs
    {
        public Repository Repo { get; set; }
        public string FullName { get; set; }
        public PageNavArg Ref { get; set; }
        public RepoPageType Page { get; set; }

        public RepoDetailPageArgs(RepoPageType page, PageNavArg @ref, Repository repo)
        {
            Page = page;
            Repo = repo;
            Ref = @ref;
        }

        public RepoDetailPageArgs(RepoPageType page, Repository repo)
        {
            Page = page;
            Repo = repo;
            Ref = CodeViewerNavArg.CreateWithBranch(Repo, Repo?.DefaultBranch);
        }

        public RepoDetailPageArgs(RepoPageType page, PageNavArg @ref, string repo)
        {
            Page = page;
            FullName = repo;
            Ref = @ref;
        }
    }

    public enum RepoPageType
    {
        CodePage,
        IssuePage,
        PullRequestPage,
        CommitPage
    }
}
