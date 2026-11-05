using System;
using System.Collections.Generic;

namespace JitHub.Markdown;

/// <summary>
/// Resolves GitHub's root-relative raw image route,
/// <c>/owner/repository/raw/{ref}/path</c>, against the current document's
/// repository and ref.
/// </summary>
/// <remarks>
/// GitHub's rendered README HTML commonly rewrites an authored relative image
/// such as <c>./assets/screenshot.png</c> to this route. The route shape is
/// reserved: once a source looks like it, the source either resolves to the
/// current repository and ref or is rejected, and never falls through to be
/// read as an ordinary repository-root asset.
/// </remarks>
internal static class GitHubRootRelativeRawRoute
{
    private const int MaximumLength = 32 * 1024;
    private const int MaximumSegments = 512;
    private const int MaximumEncodedRawMarkerLength = 64;

    /// <summary>Resolves <paramref name="source"/> if it is a raw route for the expected repository and ref.</summary>
    /// <param name="source">The trimmed image source.</param>
    /// <param name="expectedOwner">The document's repository owner.</param>
    /// <param name="expectedRepository">The document's repository name.</param>
    /// <param name="expectedRef">The document's ref.</param>
    /// <param name="reference">The resolved image when the result is <see cref="GitHubRootRelativeRawRouteResolution.Resolved"/>.</param>
    /// <returns>Whether the source is not a raw route, resolved, or a rejected raw route.</returns>
    public static GitHubRootRelativeRawRouteResolution TryResolve(
        string source,
        string expectedOwner,
        string expectedRepository,
        string expectedRef,
        out GitHubMarkdownImageReference reference)
    {
        reference = default;
        if (ClassifyShape(source, out int pathLength) is GitHubRootRelativeRawRouteResolution early)
        {
            return early;
        }

        if (!TryDecodeSegments(source, pathLength, out List<string> segments) ||
            segments.Count < 5 ||
            !segments[0].Equals(expectedOwner, StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals(expectedRepository, StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("raw", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(expectedRef) ||
            !TryCountRefSegments(segments, expectedRef, out int refSegmentCount))
        {
            return GitHubRootRelativeRawRouteResolution.Rejected;
        }

        int refAndPathSegmentCount = segments.Count - 3;
        string path = string.Join('/', segments.GetRange(
            3 + refSegmentCount,
            refAndPathSegmentCount - refSegmentCount));
        if (!Uri.TryCreate("https://github.com" + source, UriKind.Absolute, out Uri? uri) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return GitHubRootRelativeRawRouteResolution.Rejected;
        }

        reference = new GitHubMarkdownImageReference(
            segments[0],
            segments[1],
            expectedRef,
            path,
            uri);
        return GitHubRootRelativeRawRouteResolution.Resolved;
    }

    /// <summary>
    /// Decides from the four-segment prefix alone whether <paramref name="source"/> has
    /// the raw-route shape.
    /// </summary>
    /// <returns>
    /// The final result when the prefix settles it, or <see langword="null"/> for a raw
    /// route whose full path still has to be checked.
    /// </returns>
    private static GitHubRootRelativeRawRouteResolution? ClassifyShape(string source, out int pathLength)
    {
        pathLength = 0;
        if (!source.StartsWith("/", StringComparison.Ordinal) ||
            source.StartsWith("//", StringComparison.Ordinal))
        {
            return GitHubRootRelativeRawRouteResolution.NotRoute;
        }

        // Reject over-budget root-relative inputs before scanning delimiters or
        // creating substrings. The normal GitHub asset path is far below this
        // ceiling; oversized authored paths fail closed rather than amplifying
        // allocation in the resolver.
        if (source.Length > MaximumLength)
            return GitHubRootRelativeRawRouteResolution.Rejected;

        int queryIndex = source.IndexOf('?');
        int fragmentIndex = source.IndexOf('#');
        int suffixIndex = queryIndex < 0
            ? fragmentIndex
            : fragmentIndex < 0
                ? queryIndex
                : Math.Min(queryIndex, fragmentIndex);
        pathLength = suffixIndex < 0 ? source.Length : suffixIndex;

        // Scan only the four-segment route prefix first. Ordinary root-relative
        // assets (including adversarial slash storms) never allocate a segment
        // array or decode their full path.
        int ownerEnd = source.IndexOf('/', 1);
        if (ownerEnd < 0 || ownerEnd >= pathLength)
            return GitHubRootRelativeRawRouteResolution.NotRoute;
        int repositoryStart = ownerEnd + 1;
        int repositoryEnd = source.IndexOf('/', repositoryStart);
        if (repositoryEnd < 0 || repositoryEnd >= pathLength)
            return GitHubRootRelativeRawRouteResolution.NotRoute;
        int rawStart = repositoryEnd + 1;
        int rawEnd = source.IndexOf('/', rawStart);
        if (rawEnd < 0 || rawEnd >= pathLength)
            return GitHubRootRelativeRawRouteResolution.NotRoute;

        int rawMarkerLength = rawEnd - rawStart;
        if (rawMarkerLength > MaximumEncodedRawMarkerLength)
            return GitHubRootRelativeRawRouteResolution.NotRoute;
        string encodedRawMarker = source.Substring(rawStart, rawMarkerLength);
        if (!TryDecodeSegment(encodedRawMarker, out string rawSegment))
        {
            // Do not let a route marker with an encoded separator fall through
            // and be reinterpreted as an ordinary repository-root asset.
            return LooksLikeEncodedRawRouteSeparator(encodedRawMarker)
                ? GitHubRootRelativeRawRouteResolution.Rejected
                : GitHubRootRelativeRawRouteResolution.NotRoute;
        }

        if (!rawSegment.Equals("raw", StringComparison.OrdinalIgnoreCase))
            return GitHubRootRelativeRawRouteResolution.NotRoute;

        // The raw-route shape is reserved even when it is unsafe or names a
        // different repository, so from here every failure is a rejection.
        return suffixIndex >= 0 || source.Contains('\\')
            ? GitHubRootRelativeRawRouteResolution.Rejected
            : null;
    }

    private static bool TryDecodeSegments(string source, int pathLength, out List<string> segments)
    {
        segments = new List<string>(Math.Min(16, MaximumSegments));
        int segmentStart = 1;
        for (int index = 1; index <= pathLength; index++)
        {
            if (index < pathLength && source[index] != '/')
                continue;

            if (index == segmentStart || segments.Count >= MaximumSegments ||
                !TryDecodeSegment(source.Substring(segmentStart, index - segmentStart), out string decoded))
            {
                return false;
            }

            segments.Add(decoded);
            segmentStart = index + 1;
        }

        return true;
    }

    /// <summary>
    /// Finds how many segments after <c>raw</c> spell <paramref name="expectedRef"/>,
    /// leaving at least one path segment after them.
    /// </summary>
    private static bool TryCountRefSegments(List<string> segments, string expectedRef, out int refSegmentCount)
    {
        refSegmentCount = 0;
        int refAndPathSegmentCount = segments.Count - 3;
        for (int candidateCount = 1; candidateCount < refAndPathSegmentCount; candidateCount++)
        {
            if (string.Join('/', segments.GetRange(3, candidateCount))
                .Equals(expectedRef, StringComparison.Ordinal))
            {
                refSegmentCount = candidateCount;
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeSegment(string encoded, out string decoded)
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
}
