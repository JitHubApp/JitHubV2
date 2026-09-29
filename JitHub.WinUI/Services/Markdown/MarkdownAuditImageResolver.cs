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
    IMarkdownImageSourceByteAdmittedResolver,
    IMarkdownImagePrefetcher
{
    public ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        inner is IMarkdownImagePrefetcher prefetcher
            ? prefetcher.PrefetchAsync(sources, context, cancellationToken)
            : ValueTask.CompletedTask;

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(source, context, null, cancellationToken);

    public ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return ResolveCoreAsync(source, context, admission, cancellationToken);
    }

    private async ValueTask<MarkdownImageResolution> ResolveCoreAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        MarkdownImageResolution resolution = admission is not null &&
            inner is IMarkdownImageSourceByteAdmittedResolver admitted
            ? await admitted.ResolveWithSourceByteAdmissionAsync(
                source, context, admission, cancellationToken).ConfigureAwait(false)
            : await inner.ResolveAsync(
                source, context, cancellationToken).ConfigureAwait(false);
        MarkdownLifecycleAutomationBridge.RecordImageResolution(
            source,
            resolution,
            startedAt,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return resolution;
    }
}
