using JitHub.Models.GitHub;
using Octokit;

namespace JitHub.Models.NavArgs;

public class PageNavArg
{
    private GitHubRepository _repo;

    public PageNavArg(GitHubRepository? repo)
    {
        _repo = repo ?? new GitHubRepository();
    }

    public PageNavArg(Repository? repo)
        : this(ToGitHubRepository(repo))
    {
    }

    public GitHubRepository Repo => _repo;

    public PageNavArg WithRepo(GitHubRepository? repo)
    {
        _repo = repo ?? new GitHubRepository();
        return this;
    }

    public PageNavArg WithRepo(Repository? repo)
    {
        return WithRepo(ToGitHubRepository(repo));
    }

    internal static GitHubRepository ToGitHubRepository(Repository? repo)
    {
        if (repo is null)
        {
            return new GitHubRepository();
        }

        return new GitHubRepository
        {
            Id = repo.Id,
            Name = repo.Name ?? string.Empty,
            FullName = string.IsNullOrWhiteSpace(repo.FullName)
                ? $"{repo.Owner?.Login}/{repo.Name}"
                : repo.FullName,
            Description = repo.Description,
            DefaultBranch = repo.DefaultBranch ?? string.Empty,
            HtmlUrl = repo.HtmlUrl?.ToString() ?? string.Empty,
            Private = repo.Private,
            Fork = repo.Fork,
            StargazersCount = repo.StargazersCount,
            WatchersCount = repo.WatchersCount,
            ForksCount = repo.ForksCount,
            OpenIssuesCount = repo.OpenIssuesCount,
            Language = repo.Language,
            UpdatedAt = repo.UpdatedAt,
            Owner = new GitHubRepositoryOwner
            {
                Login = repo.Owner?.Login ?? string.Empty,
                AvatarUrl = repo.Owner?.AvatarUrl?.ToString(),
                HtmlUrl = repo.Owner?.HtmlUrl?.ToString()
            }
        };
    }
}
