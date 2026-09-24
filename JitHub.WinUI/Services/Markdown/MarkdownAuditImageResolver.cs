using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Records resolver outcomes only during an explicit audit or lifecycle test launch.
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
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        MarkdownImageResolution resolution = await inner.ResolveAsync(
            source,
            context,
            cancellationToken).ConfigureAwait(false);
        MarkdownLifecycleAutomationBridge.RecordImageResolution(
            source,
            resolution,
            startedAt,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return resolution;
    }
}
