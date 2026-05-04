using JitHub.Models.GitHub;
using JitHub.Models.LegacyGitHub;

namespace JitHub.Models.NavArgs;

public sealed class RepoDetailPageArgs
{
    public RepoDetailPageArgs(RepoPageType page, PageNavArg pageArg, GitHubRepository? repo)
    {
        Page = page;
        Repo = repo;
        Ref = pageArg;
    }

    public RepoDetailPageArgs(RepoPageType page, PageNavArg pageArg, Repository? repo)
        : this(page, pageArg, PageNavArg.ToGitHubRepository(repo))
    {
    }

    public RepoDetailPageArgs(RepoPageType page, GitHubRepository? repo)
    {
        Page = page;
        Repo = repo;
        Ref = CodeViewerNavArg.CreateWithBranch(repo, repo?.DefaultBranch);
    }

    public RepoDetailPageArgs(RepoPageType page, Repository? repo)
        : this(page, PageNavArg.ToGitHubRepository(repo))
    {
    }

    public RepoDetailPageArgs(RepoPageType page, PageNavArg pageArg, string repo)
    {
        Page = page;
        FullName = repo;
        Ref = pageArg;
    }

    public GitHubRepository? Repo { get; }

    public string? FullName { get; }

    public PageNavArg Ref { get; }

    public RepoPageType Page { get; }
}

public enum RepoPageType
{
    CodePage,
    IssuePage,
    PullRequestPage,
    CommitPage
}
