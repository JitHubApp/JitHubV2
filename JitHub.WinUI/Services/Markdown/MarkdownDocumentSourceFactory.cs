using System;
using System.Collections.Generic;
using System.Linq;
using JitHub.Services.CodeViewer;
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
        string? path = null,
        string? repositoryTreeSha = null,
        string? reloadRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        string normalizedKind = kind.Trim();
        string normalizedOwner = owner.Trim();
        string normalizedRepository = repository.Trim();
        string normalizedRef = string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef.Trim();
        string normalizedPath = string.IsNullOrWhiteSpace(path) ? RepositoryRootDocumentPath : NormalizePath(path);
        List<string> identityComponents =
        [
            normalizedKind,
            normalizedOwner,
            normalizedRepository,
            Uri.EscapeDataString(normalizedRef),
            Uri.EscapeDataString(normalizedPath),
            GetTreeRevisionComponent(repositoryTreeSha)
        ];
        // A valid tree object ID already pins the entire path/relative-asset
        // context. A per-navigation revision is only a fallback when that
        // authoritative snapshot is unavailable; including both would make
        // identical reads of the same tree acquire different identities.
        if (!GitReferencePolicy.IsImmutableObjectId(repositoryTreeSha) &&
            !string.IsNullOrWhiteSpace(reloadRevision))
        {
            identityComponents.Add($"reload-{Uri.EscapeDataString(reloadRevision.Trim())}");
        }

        // Preserve stableId as the final component for audit source matching.
        identityComponents.Add(stableId.Trim());
        string documentId = string.Join(":", identityComponents);

        return new MarkdownDocumentSource(
            documentId,
            normalizedOwner,
            normalizedRepository,
            normalizedRef,
            normalizedPath);
    }

    public static MarkdownDocumentSource? TryCreateFromGitHubUrl(
        string kind,
        string stableId,
        string? githubUrl,
        string? gitRef = null,
        string? path = null,
        string? repositoryTreeSha = null,
        string? reloadRevision = null)
    {
        if (!TryGetRepository(githubUrl, out string owner, out string repository))
        {
            return null;
        }

        return CreateRepositoryDocument(kind, stableId, owner, repository, gitRef, path, repositoryTreeSha, reloadRevision);
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
        string? documentPath,
        string? repositoryTreeSha = null,
        string? reloadRevision = null)
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
        if (gitRef.Length == 0)
        {
            return null;
        }

        // Repository-relative image resolution depends on the complete ref/path
        // context and the tree snapshot. The final stable ID remains untouched
        // so README audit source matching can extract its :40-hex blob suffix.
        return CreateRepositoryDocument(
            "repository-file", stableId, segments[0], segments[1], gitRef, normalizedPath,
            repositoryTreeSha, reloadRevision);
    }

    private static string GetTreeRevisionComponent(string? repositoryTreeSha)
    {
        // A root tree object ID is useful for identifying relative-asset context,
        // but it is not a commit ID and must never be used for audit eligibility.
        if (GitReferencePolicy.IsImmutableObjectId(repositoryTreeSha))
        {
            return $"tree-{repositoryTreeSha!.ToLowerInvariant()}";
        }

        return "tree-unavailable";
    }

    private static string NormalizePath(string path) =>
        string.Join('/', path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
