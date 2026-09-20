using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Records production resolver outcomes only when the explicit README audit mode is active.
/// It never changes the wrapped resolver's result or error behavior.
/// </summary>
internal sealed class MarkdownAuditImageResolver(IMarkdownImageResolver inner) :
    IMarkdownImageResolver,
    IMarkdownImagePrefetcher
{
    public ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        inner is IMarkdownImagePrefetcher prefetcher
            ? prefetcher.PrefetchAsync(sources, context, cancellationToken)
            : ValueTask.CompletedTask;

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        MarkdownImageResolution resolution = await inner.ResolveAsync(
            source,
            context,
            cancellationToken).ConfigureAwait(false);
        MarkdownLifecycleAutomationBridge.RecordImageResolution(source, resolution);
        return resolution;
    }
}
