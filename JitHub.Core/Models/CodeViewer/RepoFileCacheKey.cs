namespace JitHub.Models.CodeViewer;

public readonly record struct RepoFileCacheKey(
    string Owner,
    string Repo,
    string Sha,
    string UserId = "current");
