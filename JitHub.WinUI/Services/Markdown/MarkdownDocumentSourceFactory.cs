using System;
using System.Linq;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>Creates stable Markdown identity and repository context without URL guessing in views.</summary>
public static class MarkdownDocumentSourceFactory
{
    public const string RepositoryRootDocumentPath = "README.md";

    public static MarkdownDocumentSource CreateRepositoryDocument(
        string kind,
        string stableId,
        string owner,
        string repository,
        string? gitRef = null,
        string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        return new MarkdownDocumentSource(
            $"{kind.Trim()}:{owner.Trim()}/{repository.Trim()}:{stableId.Trim()}",
            owner.Trim(),
            repository.Trim(),
            string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef.Trim(),
            string.IsNullOrWhiteSpace(path) ? RepositoryRootDocumentPath : NormalizePath(path));
    }

    public static MarkdownDocumentSource? TryCreateFromGitHubUrl(
        string kind,
        string stableId,
        string? githubUrl,
        string? gitRef = null,
        string? path = null)
    {
        if (!TryGetRepository(githubUrl, out string owner, out string repository))
        {
            return null;
        }

        return CreateRepositoryDocument(kind, stableId, owner, repository, gitRef, path);
    }

    public static bool TryGetRepository(string? githubUrl, out string owner, out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;
        if (!Uri.TryCreate(githubUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        int offset = uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
            segments.Length >= 3 &&
            segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
        if (!(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) ||
            segments.Length < offset + 2)
        {
            return false;
        }

        owner = segments[offset];
        repository = segments[offset + 1];
        return owner.Length > 0 && repository.Length > 0;
    }

    public static MarkdownDocumentSource? TryCreateRepositoryFile(
        string stableId,
        string? githubBlobUrl,
        string? documentPath)
    {
        if (!Uri.TryCreate(githubBlobUrl, UriKind.Absolute, out Uri? uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(documentPath))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        string normalizedPath = NormalizePath(documentPath);
        string remainder = segments.Length > 3 ? string.Join('/', segments.Skip(3)) : string.Empty;
        string suffix = "/" + normalizedPath;
        if (segments.Length < 5 || !segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) ||
            !remainder.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        string gitRef = remainder[..^suffix.Length];
        return gitRef.Length == 0
            ? null
            : CreateRepositoryDocument("repository-file", stableId, segments[0], segments[1], gitRef, normalizedPath);
    }

    private static string NormalizePath(string path) =>
        string.Join('/', path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
