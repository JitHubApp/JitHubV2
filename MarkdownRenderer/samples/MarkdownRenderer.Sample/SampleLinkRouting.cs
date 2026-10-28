using System;

namespace MarkdownRenderer.Sample;

/// <summary>Reserved sample URLs report activation without invoking the shell.</summary>
internal static class SampleLinkRouting
{
    internal static bool IsInertFixtureUri(Uri? uri)
        => uri is { IsAbsoluteUri: true } &&
           (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
           string.Equals(uri.Host, "example.invalid", StringComparison.OrdinalIgnoreCase);
}
