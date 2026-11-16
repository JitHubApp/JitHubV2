using System;

namespace MarkdownRenderer.Sample.Services.Links;

/// <summary>Reserved sample URLs report activation without invoking the shell.</summary>
internal static class SampleLinkRouting
{
    internal static bool IsInertFixtureUri(Uri? uri)
        => uri is { IsAbsoluteUri: true } &&
           (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
           string.Equals(uri.Host, "example.invalid", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only absolute web and mail links may open in the shell.</summary>
    internal static bool IsAllowedExternalUri(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true })
            return false;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }
}
