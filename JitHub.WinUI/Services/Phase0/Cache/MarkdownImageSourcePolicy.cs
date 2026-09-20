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
            // GitHub's web renderer proxies legacy HTTP image references through
            // an HTTPS endpoint. The native client does not transmit an insecure
            // request: address the same origin over TLS, then apply the normal
            // HTTPS policy, redirect validation, limits, and cache partitioning.
            // Servers without HTTPS still fail closed; there is no HTTP fallback.
            var upgraded = new UriBuilder(parsed)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = parsed.IsDefaultPort ? -1 : parsed.Port,
            };
            absoluteUri = upgraded.Uri;
            return MarkdownImageSourceDisposition.SharedHttps;
        }

        return parsed.Scheme == Uri.UriSchemeHttps
            ? MarkdownImageSourceDisposition.SharedHttps
            : MarkdownImageSourceDisposition.NotHandled;
    }
}
