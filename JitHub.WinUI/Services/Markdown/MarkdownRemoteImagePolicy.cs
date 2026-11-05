using System;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

public sealed class MarkdownRemoteImagePolicy : IMarkdownRemoteImagePolicy
{
    private readonly IMarkdownNetworkState _networkState;

    public MarkdownRemoteImagePolicy()
        : this(new WindowsMarkdownNetworkState())
    {
    }

    internal MarkdownRemoteImagePolicy(IMarkdownNetworkState networkState)
    {
        _networkState = networkState;
    }

    public MarkdownRemoteImageDecision Evaluate(Uri uri, bool userInitiated)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return new MarkdownRemoteImageDecision(
                MarkdownRemoteImageAccess.AllowNetwork,
                MarkdownImageUnavailableReason.None,
                IsThirdParty: false);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return new MarkdownRemoteImageDecision(
                MarkdownRemoteImageAccess.Block,
                uri.Scheme == Uri.UriSchemeHttp
                    ? MarkdownImageUnavailableReason.InsecureRemoteContent
                    : MarkdownImageUnavailableReason.Unavailable,
                IsThirdParty: true);
        }

        bool isThirdParty = !IsTrustedGitHubHost(uri.Host);
        if (isThirdParty && !userInitiated)
        {
            return new MarkdownRemoteImageDecision(
                // A cache lookup is privacy preserving. The resolver only contacts the
                // origin after the user grants consent for this logical document.
                MarkdownRemoteImageAccess.CacheOnly,
                MarkdownImageUnavailableReason.RemoteContentBlocked,
                IsThirdParty: true);
        }

        if (!_networkState.IsOnline)
        {
            return new MarkdownRemoteImageDecision(
                MarkdownRemoteImageAccess.CacheOnly,
                MarkdownImageUnavailableReason.Offline,
                isThirdParty);
        }

        if (_networkState.IsMetered && !userInitiated)
        {
            return new MarkdownRemoteImageDecision(
                MarkdownRemoteImageAccess.CacheOnly,
                MarkdownImageUnavailableReason.MeteredConnection,
                isThirdParty);
        }

        return new MarkdownRemoteImageDecision(
            MarkdownRemoteImageAccess.AllowNetwork,
            MarkdownImageUnavailableReason.None,
            isThirdParty);
    }

    public static bool IsTrustedGitHubHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.Trim().TrimEnd('.');
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("github.githubassets.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".githubassets.com", StringComparison.OrdinalIgnoreCase);
    }
}
