namespace JitHub.Models.CodeViewer;

public sealed class RepoTree
{
    public string? Sha { get; init; }
    public bool Truncated { get; init; }
    public RepoTreeNode Root { get; init; } = new RepoTreeNode { Name = string.Empty, Path = string.Empty, IsDirectory = true };
}
