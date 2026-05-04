using JitHub.Models.GitHub;
using JitHub.Models.LegacyGitHub;

namespace JitHub.Models.NavArgs;

public sealed class CommitPageNavArg : PageNavArg
{
    private string? _branch;
    private string? _gitRef;

    public CommitPageNavArg(GitHubRepository? repo)
        : base(repo)
    {
    }

    public CommitPageNavArg(Repository? repo)
        : base(repo)
    {
    }

    public string? Branch => _branch;

    public string? GitRef => _gitRef;

    public bool NoBranch => string.IsNullOrWhiteSpace(_branch);

    public bool NoRef => string.IsNullOrWhiteSpace(_gitRef);

    public static CommitPageNavArg CreateWithBranch(GitHubRepository? repo, string? branch)
    {
        return new CommitPageNavArg(repo)
        {
            _branch = branch
        };
    }

    public static CommitPageNavArg CreateWithBranch(Repository? repo, string? branch)
    {
        return new CommitPageNavArg(repo)
        {
            _branch = branch
        };
    }

    public static CommitPageNavArg Create(GitHubRepository? repo, string? branch, string? gitRef)
    {
        return new CommitPageNavArg(repo)
        {
            _branch = branch,
            _gitRef = gitRef
        };
    }

    public static CommitPageNavArg Create(Repository? repo, string? branch, string? gitRef)
    {
        return new CommitPageNavArg(repo)
        {
            _branch = branch,
            _gitRef = gitRef
        };
    }

    public static CommitPageNavArg CreateWithGitRef(GitHubRepository? repo, string? gitRef)
    {
        return new CommitPageNavArg(repo)
        {
            _gitRef = gitRef
        };
    }

    public static CommitPageNavArg CreateWithGitRef(Repository? repo, string? gitRef)
    {
        return new CommitPageNavArg(repo)
        {
            _gitRef = gitRef
        };
    }
}
