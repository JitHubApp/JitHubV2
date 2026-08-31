using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Fail-closed image resolver used when the authenticated resolver cannot be resolved.
/// It deliberately owns every source so the renderer cannot fall back to URI loading.
/// </summary>
public sealed class DenyAllMarkdownImageResolver : IMarkdownImageResolver
{
    public static DenyAllMarkdownImageResolver Instance { get; } = new();

    private DenyAllMarkdownImageResolver()
    {
    }

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarkdownImageUnavailableReason reason = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                ? MarkdownImageUnavailableReason.InsecureRemoteContent
                : MarkdownImageUnavailableReason.RemoteContentBlocked;
        return ValueTask.FromResult(MarkdownImageResolution.Blocked(reason));
    }
}
