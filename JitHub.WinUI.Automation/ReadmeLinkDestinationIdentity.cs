namespace JitHub.WinUI.Automation;

/// <summary>
/// Aggregate-only diagnostics for comparing native README link destinations
/// with browser-resolved destinations. Destination strings are intentionally
/// never retained in this result or emitted to audit reports.
/// </summary>
internal readonly record struct ReadmeLinkDestinationSummary(
    int LinkElementCount,
    int NonEmptyDestinationCount,
    int DistinctRawDestinationCount,
    int InvalidDestinationCount,
    int DistinctCanonicalDestinationCount);

/// <summary>
/// Resolves destinations solely for audit comparison against the pinned README
/// URL. This helper never launches or fetches a destination and does not change
/// the application's link-navigation policy.
/// </summary>
internal static class ReadmeLinkDestinationIdentity
{
    public static bool TryCreatePinnedReadmeBaseUri(
        string? repositoryFullName,
        string? commitSha,
        string? readmePath,
        out Uri? readmeBaseUri)
    {
        readmeBaseUri = null;
        if (string.IsNullOrWhiteSpace(repositoryFullName) ||
            string.IsNullOrWhiteSpace(commitSha) ||
            string.IsNullOrWhiteSpace(readmePath))
        {
            return false;
        }

        string[] repositoryParts = repositoryFullName.Split('/');
        if (repositoryParts.Length != 2 ||
            repositoryParts.Any(string.IsNullOrWhiteSpace) ||
            repositoryParts.Any(static part => part is "." or "..") ||
            commitSha.Length != 40 ||
            !commitSha.All(Uri.IsHexDigit))
        {
            return false;
        }

        string[] pathParts = readmePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length == 0 || pathParts.Any(static part => part is "." or ".."))
            return false;

        string repository = string.Join('/', repositoryParts.Select(Uri.EscapeDataString));
        string path = string.Join('/', pathParts.Select(Uri.EscapeDataString));
        readmeBaseUri = new Uri(
            $"https://github.com/{repository}/blob/{commitSha}/{path}",
            UriKind.Absolute);
        return true;
    }

    public static bool TryNormalizeDestination(
        string? destination,
        Uri? pinnedReadmeBaseUri,
        out Uri? normalizedDestination)
    {
        normalizedDestination = null;
        if (string.IsNullOrWhiteSpace(destination) || !IsPinnedReadmeBaseUri(pinnedReadmeBaseUri))
            return false;

        string value = destination.Trim();
        Uri? candidate;
        bool hasExplicitScheme = HasExplicitScheme(value);
        if (hasExplicitScheme
                ? !Uri.TryCreate(value, UriKind.Absolute, out candidate)
                : !Uri.TryCreate(pinnedReadmeBaseUri, value, out candidate))
        {
            return false;
        }

        if (candidate is null || !candidate.IsAbsoluteUri ||
            !IsSupportedAuditDestination(candidate))
        {
            return false;
        }

        normalizedDestination = candidate;
        return true;
    }

    public static ReadmeLinkDestinationSummary Measure(
        IEnumerable<string?> destinations,
        Uri? pinnedReadmeBaseUri)
    {
        ArgumentNullException.ThrowIfNull(destinations);

        int linkElementCount = 0;
        int nonEmptyDestinationCount = 0;
        int invalidDestinationCount = 0;
        var rawDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canonicalDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? destination in destinations)
        {
            linkElementCount++;
            if (string.IsNullOrWhiteSpace(destination))
                continue;

            nonEmptyDestinationCount++;
            rawDestinations.Add(destination);
            if (!TryNormalizeDestination(destination, pinnedReadmeBaseUri, out Uri? normalized))
            {
                invalidDestinationCount++;
                continue;
            }

            // The same-byte browser oracle uses URL.href followed by
            // toLowerCase() when building its distinct destination set.
            canonicalDestinations.Add(normalized!.AbsoluteUri.ToLowerInvariant());
        }

        return new ReadmeLinkDestinationSummary(
            linkElementCount,
            nonEmptyDestinationCount,
            rawDestinations.Count,
            invalidDestinationCount,
            canonicalDestinations.Count);
    }

    private static bool IsPinnedReadmeBaseUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 5 &&
            segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) &&
            segments[3].Length == 40 &&
            segments[3].All(Uri.IsHexDigit);
    }

    private static bool IsSupportedAuditDestination(Uri uri)
    {
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(uri.UserInfo) &&
                !string.IsNullOrWhiteSpace(uri.Host);
        }

        // Match the renderer's existing safe external-link surface for the
        // comparison input. This only classifies the value; it never launches it.
        return uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExplicitScheme(string value)
    {
        int colon = value.IndexOf(':');
        if (colon <= 0)
            return false;

        int delimiter = value.IndexOfAny(['/', '?', '#']);
        return (delimiter < 0 || colon < delimiter) && Uri.CheckSchemeName(value[..colon]);
    }
}
