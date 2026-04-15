using JitHub.Models.GitHub;
using Octokit;

namespace JitHub.Models.NavArgs;

public class CodeViewerNavArg : PageNavArg
{
    private CodeRefType _type;
    private string? _branch;
    private string? _gitRef;

    public CodeViewerNavArg(GitHubRepository? repo)
        : base(repo)
    {
    }

    public CodeViewerNavArg(Repository? repo)
        : base(repo)
    {
    }

    public CodeRefType Type => _type;

    public string? Branch => _branch;

    public string? GitRef => _gitRef;

    public bool IsBranch => _type == CodeRefType.Branch;

    public bool IsGitRef => _type == CodeRefType.GitRef;

    public static CodeViewerNavArg CreateWithRepo(GitHubRepository? repo)
    {
        return new CodeViewerNavArg(repo)
        {
            _branch = repo?.DefaultBranch,
            _type = CodeRefType.Branch
        };
    }

    public static CodeViewerNavArg CreateWithRepo(Repository? repo)
    {
        return new CodeViewerNavArg(repo)
        {
            _branch = repo?.DefaultBranch,
            _type = CodeRefType.Branch
        };
    }

    public static CodeViewerNavArg CreateWithBranch(GitHubRepository? repo, string? branch)
    {
        return new CodeViewerNavArg(repo)
        {
            _branch = string.IsNullOrWhiteSpace(branch) ? repo?.DefaultBranch : branch,
            _type = CodeRefType.Branch
        };
    }

    public static CodeViewerNavArg CreateWithBranch(Repository? repo, string? branch)
    {
        return new CodeViewerNavArg(repo)
        {
            _branch = string.IsNullOrWhiteSpace(branch) ? repo?.DefaultBranch : branch,
            _type = CodeRefType.Branch
        };
    }

    public static CodeViewerNavArg CreateWithGitRef(GitHubRepository? repo, string? gitRef)
    {
        return new CodeViewerNavArg(repo)
        {
            _gitRef = gitRef,
            _type = CodeRefType.GitRef
        };
    }

    public static CodeViewerNavArg CreateWithGitRef(Repository? repo, string? gitRef)
    {
        return new CodeViewerNavArg(repo)
        {
            _gitRef = gitRef,
            _type = CodeRefType.GitRef
        };
    }
}

public enum CodeRefType
{
    Branch,
    GitRef
}
