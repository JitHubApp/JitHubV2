using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// Prefetches a document's image sources through a resolver that supports
/// it, once per source text, resolver, context and extension revision. The
/// request is queued during a rebuild and started after the render
/// completes, on the thread pool, so resolver policy work never runs on the
/// UI thread.
/// </summary>
internal sealed class ImageSourcePrefetch
{
    private IMarkdownImagePrefetcher? _activePrefetcher;
    private string? _prefetchedMarkdownSource;
    private MarkdownImageResolveContext? _activeContext;
    private int _prefetchedRegistryRevision = -1;
    private PendingImagePrefetch? _pending;

    /// <summary>
    /// Queues a prefetch of <paramref name="document"/>'s image sources unless
    /// the same one already ran or <paramref name="imageLifetime"/> ended.
    /// </summary>
    internal void Queue(
        Markdig.Syntax.MarkdownDocument document,
        string markdownSource,
        MarkdownExtensionRegistry registry,
        IMarkdownImageResolver? resolver,
        MarkdownImageResolveContext context,
        CancellationTokenSource? imageLifetime)
    {
        if (resolver is not IMarkdownImagePrefetcher prefetcher ||
            imageLifetime is not { IsCancellationRequested: false } lifetime)
        {
            return;
        }

        if (ReferenceEquals(_activePrefetcher, prefetcher) &&
            string.Equals(_prefetchedMarkdownSource, markdownSource, StringComparison.Ordinal) &&
            Equals(_activeContext, context) &&
            _prefetchedRegistryRevision == registry.Revision)
        {
            return;
        }

        _activePrefetcher = prefetcher;
        _prefetchedMarkdownSource = markdownSource;
        _activeContext = context;
        _prefetchedRegistryRevision = registry.Revision;
        _pending = new PendingImagePrefetch(
            document,
            registry.SafeHtmlPolicy,
            prefetcher,
            context,
            lifetime.Token);
    }

    /// <summary>Starts the queued prefetch, if any, on the thread pool.</summary>
    internal void StartPending()
    {
        PendingImagePrefetch? pending = Interlocked.Exchange(ref _pending, null);
        if (pending is not null)
        {
            // The first resolver call can perform synchronous host-policy work.
            // Queue collection and resolution together so RenderCompleted never
            // inherits that work on the UI thread.
            _ = Task.Run(() => PrefetchObservedAsync(
                pending.Document,
                pending.SafeHtmlPolicy,
                pending.Prefetcher,
                pending.Context,
                pending.CancellationToken));
        }
    }

    /// <summary>Forgets what was prefetched, so the next rebuild prefetches again.</summary>
    internal void Reset()
    {
        _prefetchedMarkdownSource = null;
        _activePrefetcher = null;
        _activeContext = null;
        _prefetchedRegistryRevision = -1;
        _pending = null;
    }

    private static async Task PrefetchObservedAsync(
        Markdig.Syntax.MarkdownDocument document,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImagePrefetcher prefetcher,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> sources = MarkdownImagePrefetchSourceCollector.Collect(
                document,
                safeHtmlPolicy,
                cancellationToken);
            if (sources.Count > 0)
            {
                await prefetcher.PrefetchAsync(sources, context, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRenderer] image prefetch failed: {exception.Message}");
        }
    }
}
