using System;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.Services.Markdown;

public static class GitHubMarkdownImageUrlResolver
{
    public static bool TryResolve(
        string source,
        MarkdownRenderer.Images.MarkdownDocumentSource? documentSource,
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
            return TryParseRepositoryImageUri(absoluteUri, documentSource?.Path, out reference);
        }

        if (documentSource is null || !documentSource.HasRepositoryContext)
        {
            return false;
        }

        // GitHub's rendered README HTML commonly rewrites an authored relative
        // image such as `./assets/screenshot.png` to a root-relative raw route:
        // `/owner/repository/raw/{ref}/assets/screenshot.png`. Resolve that
        // route as the repository it names, but only when it is the current
        // document's repository and ref. Other root-relative paths retain their
        // normal meaning as repository-root assets.
        RootRelativeRawRouteResolution rawRoute = TryResolveRootRelativeGitHubRawRoute(
            source,
            documentSource.Owner!,
            documentSource.Repository!,
            documentSource.Ref!,
            out reference);
        if (rawRoute != RootRelativeRawRouteResolution.NotRoute)
        {
            return rawRoute == RootRelativeRawRouteResolution.Resolved;
        }

        if (!TryGetRelativeSourcePath(source, out string relativeSourcePath))
        {
            return false;
        }

        if (!TryResolveRelativeRepositoryLocation(
                relativeSourcePath,
                documentSource.Path!,
                documentSource.Ref!,
                out string imageRef,
                out string imagePath))
        {
            return false;
        }

        Uri sourceUri = CreateGitHubBlobUri(
            documentSource.Owner!,
            documentSource.Repository!,
            imageRef,
            imagePath);
        reference = new GitHubMarkdownImageReference(
            documentSource.Owner!,
            documentSource.Repository!,
            imageRef,
            imagePath,
            sourceUri);
        return true;
    }

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

        RootRelativeRawRouteResolution rawRoute = TryResolveRootRelativeGitHubRawRoute(
            source,
            baseReference.Owner,
            baseReference.Repository,
            baseReference.Ref,
            out reference);
        if (rawRoute != RootRelativeRawRouteResolution.NotRoute)
        {
            return rawRoute == RootRelativeRawRouteResolution.Resolved;
        }

        if (!TryGetRelativeSourcePath(source, out string relativeSourcePath))
        {
            return false;
        }

        if (!TryResolveRelativeRepositoryLocation(
                relativeSourcePath,
                baseReference.Path,
                baseReference.Ref,
                out string imageRef,
                out string imagePath))
        {
            return false;
        }

        Uri sourceUri = CreateGitHubBlobUri(
            baseReference.Owner,
            baseReference.Repository,
            imageRef,
            imagePath);
        reference = new GitHubMarkdownImageReference(
            baseReference.Owner,
            baseReference.Repository,
            imageRef,
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
        if (uri is null || !uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0)
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
                !(segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) ||
                  segments[2].Equals("raw", StringComparison.OrdinalIgnoreCase)) ||
                string.IsNullOrWhiteSpace(segments[0]) ||
                string.IsNullOrWhiteSpace(segments[1]) ||
                segments[0].IndexOfAny(['/', '\\']) >= 0 ||
                segments[1].IndexOfAny(['/', '\\']) >= 0)
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
                string.IsNullOrWhiteSpace(segments[1]) ||
                segments[0].IndexOfAny(['/', '\\']) >= 0 ||
                segments[1].IndexOfAny(['/', '\\']) >= 0)
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

        // GitHub emits canonical raw URLs with an explicit refs/heads or
        // refs/tags prefix. Treat that prefix as part of the ref rather than
        // misclassifying "heads/..." as the repository path.
        if (refAndPathSegments.Length >= 4 &&
            refAndPathSegments[0].Equals("refs", StringComparison.OrdinalIgnoreCase) &&
            (refAndPathSegments[1].Equals("heads", StringComparison.OrdinalIgnoreCase) ||
             refAndPathSegments[1].Equals("tags", StringComparison.OrdinalIgnoreCase)))
        {
            gitRef = string.Join('/', refAndPathSegments.Take(3));
            path = string.Join('/', refAndPathSegments.Skip(3));
            return !string.IsNullOrWhiteSpace(path);
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

    internal static Uri CreateGitHubRawRouteUri(GitHubMarkdownImageReference reference)
    {
        string uri =
            $"https://github.com/{Uri.EscapeDataString(reference.Owner)}/{Uri.EscapeDataString(reference.Repository)}/raw/{EscapePath(reference.Ref)}/{EscapePath(reference.Path)}";
        return new Uri(uri, UriKind.Absolute);
    }

    private static bool TryResolveRelativeRepositoryLocation(
        string relativeSourcePath,
        string documentPath,
        string documentRef,
        out string imageRef,
        out string imagePath)
    {
        imageRef = documentRef;
        imagePath = relativeSourcePath.StartsWith("/", StringComparison.Ordinal)
            ? NormalizeRepositoryPath(relativeSourcePath.TrimStart('/'))
            : NormalizeRepositoryPath(JoinRepositoryPath(GetDirectoryName(documentPath), relativeSourcePath));
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        if (!imagePath.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        // GitHub resolves a parent-relative URL that escapes the repository root
        // against the route segment following /blob/. This convention is used by
        // repositories such as lazygit to keep README media on an `assets` branch:
        // README.md + ../assets/demo.gif => ref `assets`, path `demo.gif`.
        // Admit exactly one escaped segment; additional traversal would leave the
        // repository route and is therefore rejected.
        string branchRelative = imagePath[3..];
        if (branchRelative.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        int slash = branchRelative.IndexOf('/');
        if (slash <= 0 || slash == branchRelative.Length - 1)
        {
            return false;
        }

        imageRef = branchRelative[..slash];
        imagePath = branchRelative[(slash + 1)..];
        return true;
    }

    private enum RootRelativeRawRouteResolution
    {
        NotRoute,
        Resolved,
        Rejected,
    }

    private const int MaximumRootRelativeRawRouteLength = 32 * 1024;
    private const int MaximumRootRelativeRawRouteSegments = 512;
    private const int MaximumEncodedRawMarkerLength = 64;

    private static RootRelativeRawRouteResolution TryResolveRootRelativeGitHubRawRoute(
        string source,
        string expectedOwner,
        string expectedRepository,
        string expectedRef,
        out GitHubMarkdownImageReference reference)
    {
        reference = default;
        if (!source.StartsWith("/", StringComparison.Ordinal) ||
            source.StartsWith("//", StringComparison.Ordinal))
        {
            return RootRelativeRawRouteResolution.NotRoute;
        }

        // Reject over-budget root-relative inputs before scanning delimiters or
        // creating substrings. The normal GitHub asset path is far below this
        // ceiling; oversized authored paths fail closed rather than amplifying
        // allocation in the resolver.
        if (source.Length > MaximumRootRelativeRawRouteLength)
            return RootRelativeRawRouteResolution.Rejected;

        int queryIndex = source.IndexOf('?');
        int fragmentIndex = source.IndexOf('#');
        int suffixIndex = queryIndex < 0
            ? fragmentIndex
            : fragmentIndex < 0
                ? queryIndex
                : Math.Min(queryIndex, fragmentIndex);
        int pathLength = suffixIndex < 0 ? source.Length : suffixIndex;

        // Scan only the four-segment route prefix first. Ordinary root-relative
        // assets (including adversarial slash storms) never allocate a segment
        // array or decode their full path.
        int ownerEnd = source.IndexOf('/', 1);
        if (ownerEnd < 0 || ownerEnd >= pathLength)
            return RootRelativeRawRouteResolution.NotRoute;
        int repositoryStart = ownerEnd + 1;
        int repositoryEnd = source.IndexOf('/', repositoryStart);
        if (repositoryEnd < 0 || repositoryEnd >= pathLength)
            return RootRelativeRawRouteResolution.NotRoute;
        int rawStart = repositoryEnd + 1;
        int rawEnd = source.IndexOf('/', rawStart);
        if (rawEnd < 0 || rawEnd >= pathLength)
        {
            return RootRelativeRawRouteResolution.NotRoute;
        }

        int rawMarkerLength = rawEnd - rawStart;
        if (rawMarkerLength > MaximumEncodedRawMarkerLength)
            return RootRelativeRawRouteResolution.NotRoute;
        string encodedRawMarker = source.Substring(rawStart, rawMarkerLength);
        if (!TryDecodeGitHubRouteSegment(encodedRawMarker, out string rawSegment))
        {
            // Do not let a route marker with an encoded separator fall through
            // and be reinterpreted as an ordinary repository-root asset.
            return LooksLikeEncodedRawRouteSeparator(encodedRawMarker)
                ? RootRelativeRawRouteResolution.Rejected
                : RootRelativeRawRouteResolution.NotRoute;
        }

        if (!rawSegment.Equals("raw", StringComparison.OrdinalIgnoreCase))
            return RootRelativeRawRouteResolution.NotRoute;

        // The raw-route shape is reserved even when it is unsafe or names a
        // different repository. It must not fall through and be reinterpreted
        // as an ordinary repository-root asset.
        if (suffixIndex >= 0 || source.Contains('\\') ||
            source.Length > MaximumRootRelativeRawRouteLength)
        {
            return RootRelativeRawRouteResolution.Rejected;
        }

        var segments = new List<string>(Math.Min(16, MaximumRootRelativeRawRouteSegments));
        int segmentStart = 1;
        for (int index = 1; index <= pathLength; index++)
        {
            if (index < pathLength && source[index] != '/')
                continue;

            if (index == segmentStart || segments.Count >= MaximumRootRelativeRawRouteSegments ||
                !TryDecodeGitHubRouteSegment(source.Substring(segmentStart, index - segmentStart), out string decoded))
            {
                return RootRelativeRawRouteResolution.Rejected;
            }

            segments.Add(decoded);
            segmentStart = index + 1;
        }

        if (segments.Count < 5 ||
            !segments[0].Equals(expectedOwner, StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals(expectedRepository, StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("raw", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(expectedRef))
        {
            return RootRelativeRawRouteResolution.Rejected;
        }

        int refSegmentCount = 0;
        int refAndPathSegmentCount = segments.Count - 3;
        for (int candidateCount = 1; candidateCount < refAndPathSegmentCount; candidateCount++)
        {
            if (string.Join('/', segments.GetRange(3, candidateCount))
                .Equals(expectedRef, StringComparison.Ordinal))
            {
                refSegmentCount = candidateCount;
                break;
            }
        }

        if (refSegmentCount == 0 || refSegmentCount >= refAndPathSegmentCount)
        {
            return RootRelativeRawRouteResolution.Rejected;
        }

        string path = string.Join('/', segments.GetRange(
            3 + refSegmentCount,
            refAndPathSegmentCount - refSegmentCount));
        if (!Uri.TryCreate("https://github.com" + source, UriKind.Absolute, out Uri? uri) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return RootRelativeRawRouteResolution.Rejected;
        }

        reference = new GitHubMarkdownImageReference(
            segments[0],
            segments[1],
            expectedRef,
            path,
            uri);
        return RootRelativeRawRouteResolution.Resolved;
    }

    private static bool TryDecodeGitHubRouteSegment(string encoded, out string decoded)
    {
        decoded = string.Empty;
        if (encoded.Length == 0)
        {
            return false;
        }

        for (int index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] == '%')
            {
                if (index + 2 >= encoded.Length ||
                    !Uri.IsHexDigit(encoded[index + 1]) ||
                    !Uri.IsHexDigit(encoded[index + 2]))
                {
                    return false;
                }

                index += 2;
            }
        }

        try
        {
            decoded = Uri.UnescapeDataString(encoded);
            return decoded.Length > 0 &&
                decoded is not ("." or "..") &&
                decoded.IndexOfAny(['/', '\\', '\0']) < 0;
        }
        catch (UriFormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static bool LooksLikeEncodedRawRouteSeparator(string encodedSegment)
    {
        if (!encodedSegment.Contains('%'))
            return false;

        try
        {
            string decoded = Uri.UnescapeDataString(encodedSegment);
            return decoded.StartsWith("raw/", StringComparison.OrdinalIgnoreCase) ||
                decoded.StartsWith("raw\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string JoinRepositoryPath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        return left.TrimEnd('/') + "/" + right;
    }

    private static bool TryGetRelativeSourcePath(string source, out string path)
    {
        path = string.Empty;
        if (source.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        int queryIndex = source.IndexOf('?');
        int fragmentIndex = source.IndexOf('#');
        int endIndex = queryIndex < 0
            ? fragmentIndex
            : fragmentIndex < 0
                ? queryIndex
                : Math.Min(queryIndex, fragmentIndex);
        string encodedPath = (endIndex < 0 ? source : source[..endIndex]).Trim();
        if (encodedPath.Length == 0)
        {
            return false;
        }

        try
        {
            path = Uri.UnescapeDataString(encodedPath);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return path.Length > 0 && path.IndexOf('\0') < 0;
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
                if (stack.Count > 0 && !stack[^1].Equals("..", StringComparison.Ordinal))
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
