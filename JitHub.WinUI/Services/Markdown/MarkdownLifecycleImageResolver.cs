using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Keeps lifecycle automation deterministic without changing production image resolution.
/// Every source except the fixture's repository-relative image uses the real resolver.
/// </summary>
internal sealed class MarkdownLifecycleImageResolver(IMarkdownImageResolver inner) : IMarkdownImageResolver
{
    private const string RelativeFixturePath = "docs/images/lifecycle-relative.png";
    private const string BlockedRemoteFixtureUrl =
        "https://example.invalid/jithub-markdown-lifecycle.png";
    private static readonly byte[] RelativeFixtureBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAFAgI/azstAAAAAElFTkSuQmCC");

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(source.Trim(), RelativeFixturePath, StringComparison.Ordinal))
        {
            var asset = new MarkdownImageAsset(
                RelativeFixtureBytes,
                "image/png",
                new Uri("https://github.com/JitHubApp/JitHubV2/blob/main/docs/images/lifecycle-relative.png"),
                "markdown-lifecycle:relative-image");
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(asset));
        }

        if (string.Equals(source.Trim(), BlockedRemoteFixtureUrl, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(MarkdownImageResolution.Blocked(
                MarkdownImageUnavailableReason.RemoteContentBlocked));
        }

        return inner.ResolveAsync(source, context, cancellationToken);
    }
}
