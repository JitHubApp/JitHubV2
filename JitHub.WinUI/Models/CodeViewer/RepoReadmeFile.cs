namespace JitHub.Models.CodeViewer;

public sealed record RepoReadmeFile(
    string Name,
    string Path,
    RepoFileBlob Blob);
