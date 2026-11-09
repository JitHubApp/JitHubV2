using System.Collections.Generic;

namespace JitHub.Models.CodeViewer;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoTreeNode
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? Sha { get; init; }
    public long? Size { get; init; }
    public bool IsDirectory { get; init; }
    public string? ParentPath { get; init; }
    public ICollection<RepoTreeNode> Children { get; init; } = new List<RepoTreeNode>();
}
