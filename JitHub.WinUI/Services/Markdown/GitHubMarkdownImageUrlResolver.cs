using System;
using System.Linq;

namespace JitHub.Services.Markdown;

public static class GitHubMarkdownImageUrlResolver
{
    public static bool TryResolve(
        string source,
        Uri? baseUri,
        string? documentPath,
        out GitHubMarkdownImageReference reference)
    {
        reference = default;
        source = source?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            return false;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? absoluteUri))
        {
            return TryParseRepositoryImageUri(absoluteUri, null, out reference);
        }

        if (!TryParseRepositoryImageUri(baseUri, documentPath, out var baseReference))
        {
            return false;
        }

        string imagePath = source.StartsWith("/", StringComparison.Ordinal)
            ? NormalizeRepositoryPath(source.TrimStart('/'))
            : NormalizeRepositoryPath(JoinRepositoryPath(GetDirectoryName(baseReference.Path), source));

        if (string.IsNullOrWhiteSpace(imagePath) || imagePath.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        Uri sourceUri = CreateGitHubBlobUri(
            baseReference.Owner,
            baseReference.Repository,
            baseReference.Ref,
            imagePath);
        reference = new GitHubMarkdownImageReference(
            baseReference.Owner,
            baseReference.Repository,
            baseReference.Ref,
            imagePath,
            sourceUri);
        return true;
    }

    public static Uri CreateRawUri(GitHubMarkdownImageReference reference)
    {
        string uri =
            $"https://raw.githubusercontent.com/{Uri.EscapeDataString(reference.Owner)}/{Uri.EscapeDataString(reference.Repository)}/{EscapePath(reference.Ref)}/{EscapePath(reference.Path)}";
        return new Uri(uri, UriKind.Absolute);
    }

    private static bool TryParseRepositoryImageUri(
        Uri? uri,
        string? knownDocumentPath,
        out GitHubMarkdownImageReference reference)
    {
        reference = default;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 5 ||
                !segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(segments[0]) ||
                string.IsNullOrWhiteSpace(segments[1]))
            {
                return false;
            }

            if (!TrySplitRefAndPath(segments.Skip(3).ToArray(), knownDocumentPath, out string gitRef, out string path))
            {
                return false;
            }

            reference = new GitHubMarkdownImageReference(
                segments[0],
                segments[1],
                gitRef,
                path,
                uri);
            return true;
        }

        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 4 ||
                string.IsNullOrWhiteSpace(segments[0]) ||
                string.IsNullOrWhiteSpace(segments[1]))
            {
                return false;
            }

            if (!TrySplitRefAndPath(segments.Skip(2).ToArray(), knownDocumentPath, out string gitRef, out string path))
            {
                return false;
            }

            reference = new GitHubMarkdownImageReference(
                segments[0],
                segments[1],
                gitRef,
                path,
                uri);
            return true;
        }

        return false;
    }

    private static bool TrySplitRefAndPath(
        string[] refAndPathSegments,
        string? knownPath,
        out string gitRef,
        out string path)
    {
        gitRef = string.Empty;
        path = string.Empty;

        if (refAndPathSegments.Length < 2)
        {
            return false;
        }

        string full = string.Join('/', refAndPathSegments);
        string normalizedKnownPath = NormalizeRepositoryPath(knownPath);
        if (!string.IsNullOrWhiteSpace(normalizedKnownPath))
        {
            if (full.Equals(normalizedKnownPath, StringComparison.Ordinal))
            {
                return false;
            }

            string suffix = "/" + normalizedKnownPath;
            if (full.EndsWith(suffix, StringComparison.Ordinal))
            {
                gitRef = full[..^suffix.Length];
                path = normalizedKnownPath;
                return !string.IsNullOrWhiteSpace(gitRef);
            }
        }

        gitRef = refAndPathSegments[0];
        path = string.Join('/', refAndPathSegments.Skip(1));
        return !string.IsNullOrWhiteSpace(gitRef) && !string.IsNullOrWhiteSpace(path);
    }

    private static Uri CreateGitHubBlobUri(string owner, string repository, string gitRef, string path)
    {
        string uri =
            $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/blob/{EscapePath(gitRef)}/{EscapePath(path)}";
        return new Uri(uri, UriKind.Absolute);
    }

    private static string JoinRepositoryPath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        return left.TrimEnd('/') + "/" + right;
    }

    private static string GetDirectoryName(string path)
    {
        path = NormalizeRepositoryPath(path);
        int lastSlash = path.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : path[..lastSlash];
    }

    private static string NormalizeRepositoryPath(string? path)
    {
        path = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (path.Length == 0)
        {
            return string.Empty;
        }

        bool rooted = path.StartsWith("/", StringComparison.Ordinal);
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new System.Collections.Generic.List<string>();
        foreach (string part in parts)
        {
            if (part.Equals(".", StringComparison.Ordinal))
            {
                continue;
            }

            if (part.Equals("..", StringComparison.Ordinal))
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (!rooted)
                {
                    stack.Add("..");
                }
                continue;
            }

            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static string EscapePath(string path)
        => string.Join('/', NormalizeRepositoryPath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
}
