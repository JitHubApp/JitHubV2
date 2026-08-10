using System;

namespace JitHub.Services;

internal enum MarkdownImageSourceDisposition
{
    NotHandled,
    SharedHttps,
    BlockedInsecureRemote
}

internal static class MarkdownImageSourcePolicy
{
    public static MarkdownImageSourceDisposition ClassifyUnownedSource(
        string source,
        out Uri? absoluteUri)
    {
        absoluteUri = null;
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? parsed))
        {
            return MarkdownImageSourceDisposition.NotHandled;
        }

        absoluteUri = parsed;
        if (parsed.Scheme == Uri.UriSchemeHttp)
        {
            return MarkdownImageSourceDisposition.BlockedInsecureRemote;
        }

        return parsed.Scheme == Uri.UriSchemeHttps
            ? MarkdownImageSourceDisposition.SharedHttps
            : MarkdownImageSourceDisposition.NotHandled;
    }
}
