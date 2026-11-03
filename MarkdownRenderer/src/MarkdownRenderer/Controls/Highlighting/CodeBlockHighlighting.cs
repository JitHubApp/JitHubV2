using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Highlighting;

/// <summary>
/// Syntax highlighting of the published snapshot's code blocks. Blocks near
/// the viewport get a cached result or a provider request; results are
/// published on the UI thread to the snapshot that requested them.
/// </summary>
/// <remarks>
/// A generation fences every request: retiring the generation cancels its
/// work and keeps a late result from being published. At most two provider
/// requests run at once.
/// </remarks>
internal sealed class CodeBlockHighlighting
{
    private const int CacheMaxEntries = 128;
    private const long CacheBudgetBytes = 8L * 1024 * 1024;
    private const double OverscanPx = 1600;

    private readonly ICodeBlockHighlightingHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly EmbedRealization _embeds;
    private readonly BoundedCodeBlockHighlightCache<CodeBlockHighlightCacheKey> _cache = new(
        CacheMaxEntries,
        CacheBudgetBytes,
        static key => 64L + ((key.Language?.Length ?? 0) * sizeof(char)));
    private readonly HashSet<CodeBlockHighlightCacheKey> _inFlight = new();
    private readonly SemaphoreSlim _semaphore = new(2, 2);
    private readonly CodeBlockHighlightTasks _tasks = new();
    private CancellationTokenSource? _cts;
    private Task _retirement = Task.CompletedTask;
    private int _generation;

    internal CodeBlockHighlighting(
        ICodeBlockHighlightingHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        EmbedRealization embeds)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _embeds = embeds;
    }

    /// <summary>Retires the current highlight work and starts a new generation.</summary>
    internal Task Restart()
        => Retire(restart: true);

    /// <summary>
    /// Cancels the current generation's work and returns its retirement.
    /// With <paramref name="restart"/>, a new generation may start work.
    /// </summary>
    internal Task Retire(bool restart)
    {
        var old = _cts;
        Task workLifetime = OwnedResourceRetirement.CombineLifetimes(
            _retirement,
            _tasks.Drain());
        _cts = restart ? new CancellationTokenSource() : null;
        _generation++;
        _inFlight.Clear();
        Task retirement = _host.RetireCancellationTokenSource(old, workLifetime);
        _retirement = retirement;
        return retirement;
    }

    /// <summary>
    /// Applies cached highlights to the code blocks in the visible band and
    /// starts the provider for the rest.
    /// </summary>
    internal void ScheduleVisible()
    {
        if (!_embeds.HasMeasuredCodeBlocks ||
            !_host.IsCodeBlockSyntaxHighlightingEnabled ||
            _host.CodeHighlighter is not { } highlighter ||
            _published.Snapshot is not { } snapshot ||
            _host.ThemeSnapshot is not { } theme ||
            theme.IsHighContrast)
        {
            return;
        }

        var cts = _cts;
        if (!CodeBlockHighlightOperationLifetime.TryCaptureToken(cts, out CancellationToken operationToken))
            return;

        IMarkdownPerformanceSession? performanceSession = _host.PerformanceSession;
        bool runOnWorker = performanceSession is not null;
        var variant = ResolveThemeVariant(theme);
        int generation = _generation;
        int providerIdentity = RuntimeHelpers.GetHashCode(highlighter);
        IMarkdownPerformanceSessionInternal? sceneSession =
            performanceSession as IMarkdownPerformanceSessionInternal;
        if (sceneSession?.IsDisposed == true)
            return;
        DispatcherQueue? dispatcher = _host.DispatcherQueue;
        if (dispatcher is null)
            return;

        // Revision is provider code. It may synchronously replace the provider,
        // unload the control, or retire this generation. Capture all lifetime
        // state first, then fence immediately after this one callback without
        // calling Revision a second time.
        int providerRevision;
        try
        {
            providerRevision = highlighter.Revision;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Code highlighter revision failed: {ex.Message}");
            return;
        }

        if (!CodeBlockHighlightOperationLifetime.IsCurrentAfterProviderCallback(
                _host.CodeHighlighter,
                highlighter,
                _generation,
                generation,
                _lifetime.IsDisposed,
                _lifetime.IsUnloaded,
                operationToken))
        {
            return;
        }

        bool appliedCached = false;
        List<(CodeBlockHighlightCacheKey Key, string CodeText)>? pendingHighlights = null;
        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand highlightBand = performanceSession is { } activePerformanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                activePerformanceSession.Options.LookAheadViewports,
                _host.LazyScrollVelocityPixelsPerSecond)
            : LazyLayoutBand.FromViewport(
                viewportTop,
                viewportHeight,
                OverscanPx);
        // BlockBox instances are mutable and are disposed with their snapshot.
        // Inspect them only while holding the snapshot's nonblocking read
        // lease, and copy the immutable provider input before releasing it.
        if (!snapshot.TryBeginInteraction())
            return;
        try
        {
            IReadOnlyList<BlockBox> inspectedBlocks;
            if (!runOnWorker)
            {
                // The outer lease remains held while this legacy full measured
                // list is consumed; its returned BlockBox references do not
                // escape the protected inspection.
                inspectedBlocks = snapshot.GetMeasuredTopLevelBlocks();
            }
            else if (!snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                         highlightBand.Top,
                         highlightBand.Bottom,
                         out inspectedBlocks))
            {
                // Measurement or deferred scene readiness is transient. The
                // layout/scroll completion path schedules another inspection.
                return;
            }

            foreach (var block in EnumerateCodeBlocks(inspectedBlocks))
            {
                if (!IsInBand(snapshot, block, highlightBand.Top, highlightBand.Bottom))
                    continue;

                if (block.CodeText.Length > 200_000 || block.LineCount > 5_000)
                    continue;

                var key = CreateCacheKey(block, variant, providerIdentity, providerRevision);
                if (block.HasAppliedSyntaxHighlighting(key))
                    continue;
                if (_cache.TryGetValue(key, out var cached))
                {
                    appliedCached |= block.ApplySyntaxHighlighting(key, cached.Spans);
                    continue;
                }

                if (!_inFlight.Add(key))
                    continue;

                (pendingHighlights ??= [])
                    .Add((key, block.CodeText));
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (pendingHighlights is not null)
        {
            // One weak identity is shared across this admitted batch. The
            // provider work may outlive snapshot retirement, so task state and
            // dispatcher callbacks must not keep the retired document graph
            // rooted while they wait for completion.
            var snapshotIdentity = new WeakReference<LayoutSnapshot>(snapshot);
            for (int pendingIndex = 0; pendingIndex < pendingHighlights.Count; pendingIndex++)
            {
                if (!CodeBlockHighlightOperationLifetime.IsCurrentAfterProviderCallback(
                        _host.CodeHighlighter,
                        highlighter,
                        _generation,
                        generation,
                        _lifetime.IsDisposed,
                        _lifetime.IsUnloaded,
                        operationToken))
                {
                    for (int remaining = pendingIndex; remaining < pendingHighlights.Count; remaining++)
                        RemoveInFlight(pendingHighlights[remaining].Key, generation);
                    break;
                }

                var pending = pendingHighlights[pendingIndex];
                // Start user highlighter code only after releasing the layout
                // lease. Reserve its retirement lifetime before invoking
                // provider code: sessionless providers may reenter unload or
                // replacement synchronously before returning their Task.
                try
                {
                    CodeBlockHighlightOperationLifetime.StartAndTrack(
                        _tasks.Track,
                        () => runOnWorker
                            ? Task.Run(
                                () => HighlightAsync(
                                    snapshotIdentity, pending.CodeText, pending.Key,
                                    highlighter, generation,
                                    sceneSession, dispatcher, operationToken),
                                CancellationToken.None)
                            : HighlightAsync(
                                snapshotIdentity, pending.CodeText, pending.Key,
                                highlighter, generation,
                                sceneSession, dispatcher, operationToken));
                }
                catch (Exception ex)
                {
                    RemoveInFlight(pending.Key, generation);
                    MarkdownDiagnostics.WriteLine(
                        $"[MarkdownRendererControl] Code block highlighting could not start: {ex.Message}");
                }
            }
        }

        if (appliedCached)
            _host.InvalidateCanvas();
    }

    private async Task HighlightAsync(
        WeakReference<LayoutSnapshot> snapshotIdentity,
        string codeText,
        CodeBlockHighlightCacheKey key,
        ICodeHighlighter highlighter,
        int generation,
        IMarkdownPerformanceSessionInternal? sceneSession,
        DispatcherQueue dispatcher,
        CancellationToken token)
    {
        bool publicationQueued = false;
        try
        {
            token.ThrowIfCancellationRequested();
            await _semaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using IMarkdownScenePreparationLease? sceneLease = sceneSession is null
                    ? null
                    : await sceneSession.EnterScenePreparationAsync(_host.DocumentOwner, token)
                        .ConfigureAwait(false);
                CancellationToken workToken = sceneLease?.CancellationToken ?? token;
                if (workToken.IsCancellationRequested)
                    return;

                var request = new CodeBlockHighlightRequest(key.Language, codeText, key.ThemeVariant);
                CodeBlockHighlightResult? providerResult = await highlighter.HighlightAsync(
                    request,
                    workToken).ConfigureAwait(false);
                if (workToken.IsCancellationRequested)
                    return;

                CodeBlockHighlightResult result = dispatcher.HasThreadAccess
                    ? await Task.Run(
                        () => CodeBlockHighlightResultSnapshot.CopyProviderResult(
                            providerResult,
                            workToken),
                        CancellationToken.None).ConfigureAwait(false)
                    : CodeBlockHighlightResultSnapshot.CopyProviderResult(
                        providerResult,
                        workToken);
                if (workToken.IsCancellationRequested)
                    return;

                publicationQueued = dispatcher.TryEnqueue(() =>
                {
                    bool snapshotChanged =
                        !snapshotIdentity.TryGetTarget(out LayoutSnapshot? callbackSnapshot) ||
                        callbackSnapshot is null ||
                        callbackSnapshot.IsRetiringOrDisposed ||
                        !ReferenceEquals(_published.Snapshot, callbackSnapshot);
                    bool providerCurrent = false;
                    if (CodeBlockHighlightOperationLifetime.IsCurrentAfterProviderCallback(
                            _host.CodeHighlighter,
                            highlighter,
                            _generation,
                            generation,
                            _lifetime.IsDisposed,
                            _lifetime.IsUnloaded,
                            token))
                    {
                        try
                        {
                            int observedRevision = highlighter.Revision;
                            providerCurrent = CodeBlockHighlightOperationLifetime.IsCurrentAfterRevisionCallback(
                                _host.CodeHighlighter,
                                highlighter,
                                _generation,
                                generation,
                                _lifetime.IsDisposed,
                                _lifetime.IsUnloaded,
                                token,
                                observedRevision,
                                key.ProviderRevision);
                        }
                        catch (Exception ex)
                        {
                            MarkdownDiagnostics.WriteLine(
                                $"[MarkdownRendererControl] Code highlighter revision failed: {ex.Message}");
                        }
                    }

                    CodeBlockHighlightCompletionAction completion =
                        CodeBlockHighlightPublicationFence.DecideCompletion(
                            snapshotChanged,
                            _lifetime.IsUnloaded,
                            workToken.IsCancellationRequested,
                            sceneSession?.IsDisposed == true,
                            providerCurrent);
                    bool retryAfterSnapshotBusy = false;
                    try
                    {
                        if (completion != CodeBlockHighlightCompletionAction.Publish)
                            return;
                        _cache.Set(key, result);
                        if (_cache.TryGetValue(key, out _))
                        {
                            // The visible-band scheduler applies the retained
                            // result to matching blocks without a document scan.
                            _host.ScheduleVisibleCodeBlockHighlighting();
                        }
                        else if (callbackSnapshot is not null &&
                            ApplyUncachedResult(
                                callbackSnapshot, key, result,
                                out retryAfterSnapshotBusy))
                        {
                            // Oversized results cannot enter the bounded cache.
                            // Publish to the current band directly, without
                            // immediately requeuing the same expensive request.
                            _host.InvalidateCanvas();
                        }
                    }
                    finally
                    {
                        RemoveInFlight(key, generation);
                        if (completion == CodeBlockHighlightCompletionAction.RetryCurrentSnapshot ||
                            retryAfterSnapshotBusy)
                            _host.ScheduleVisibleCodeBlockHighlighting();
                    }
                });
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The completion key is released by the shared finally path.
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block highlighting failed: {ex.Message}");
        }
        finally
        {
            // Cancellation may occur after admission but before publication.
            // A stale key must not suppress a future visible-band request.
            if (!publicationQueued)
                dispatcher.TryEnqueue(() => RemoveInFlight(key, generation));
        }
    }

    private void RemoveInFlight(CodeBlockHighlightCacheKey key, int generation)
    {
        if (generation == _generation)
            _inFlight.Remove(key);
    }

    private bool ApplyUncachedResult(
        LayoutSnapshot snapshot,
        CodeBlockHighlightCacheKey key,
        CodeBlockHighlightResult result,
        out bool snapshotBusy)
    {
        snapshotBusy = false;
        if (!snapshot.TryBeginInteraction())
        {
            snapshotBusy = true;
            return false;
        }

        try
        {
            IReadOnlyList<BlockBox> inspectedBlocks;
            IEnumerable<CodeBlockBox> candidates;
            LazyLayoutBand band = default;
            if (_host.PerformanceSession is null)
            {
                inspectedBlocks = snapshot.GetMeasuredTopLevelBlocks();
                candidates = EnumerateCodeBlocks(inspectedBlocks);
            }
            else
            {
                _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
                band = LazyLayoutBand.FromDirectionalViewport(
                    viewportTop,
                    viewportHeight,
                    _host.PerformanceSession.Options.LookAheadViewports,
                    _host.LazyScrollVelocityPixelsPerSecond);
                if (!snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                        band.Top, band.Bottom, out inspectedBlocks))
                {
                    // The snapshot may still be measuring this band or waiting
                    // for deferred native scenes. Keep the oversized result
                    // retryable after the in-flight key is released.
                    snapshotBusy = true;
                    return false;
                }

                candidates = EnumerateCodeBlocks(inspectedBlocks);
            }

            bool changed = false;
            foreach (var candidate in candidates)
            {
                if (_host.PerformanceSession is not null &&
                    !IsInBand(snapshot, candidate, band.Top, band.Bottom))
                    continue;
                if (CreateCacheKey(candidate, key.ThemeVariant, key.ProviderIdentity, key.ProviderRevision).Equals(key))
                    changed |= candidate.ApplySyntaxHighlighting(key, result.Spans);
            }

            return changed;
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static bool IsInBand(
        LayoutSnapshot snapshot,
        CodeBlockBox block,
        double top,
        double bottom)
    {
        Rect bounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(block.BlockIndex, block.Bounds);
        return bounds.Bottom >= top && bounds.Top <= bottom;
    }

    private static IEnumerable<CodeBlockBox> EnumerateCodeBlocks(
        IReadOnlyList<BlockBox> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var codeBlock in EnumerateCodeBlocks(block))
                yield return codeBlock;
        }
    }

    private static IEnumerable<CodeBlockBox> EnumerateCodeBlocks(BlockBox block)
    {
        switch (block)
        {
            case CodeBlockBox codeBlock:
                yield return codeBlock;
                break;
            case ListItemBox listItem:
                foreach (var item in EnumerateCodeBlocks(listItem.Marker))
                    yield return item;
                foreach (var item in EnumerateCodeBlocks(listItem.Content))
                    yield return item;
                break;
            case StackBox stack:
                foreach (var child in stack.Children)
                {
                    foreach (var item in EnumerateCodeBlocks(child))
                        yield return item;
                }
                break;
        }
    }

    private CodeBlockHighlightCacheKey CreateCacheKey(
        CodeBlockBox block,
        MarkdownThemeVariant variant,
        int providerIdentity,
        int providerRevision)
        => new(block.CodeLanguage, block.Metadata.CodeTextHash, block.CodeText.Length, variant, providerIdentity, providerRevision);

    /// <summary>Drops every cached highlight result.</summary>
    internal void ClearCache()
        => _cache.Clear();

    private static MarkdownThemeVariant ResolveThemeVariant(ThemeSnapshot theme)
    {
        if (theme.IsHighContrast)
            return MarkdownThemeVariant.HighContrast;

        var bg = theme.SurfaceColor;
        double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance < 0.5 ? MarkdownThemeVariant.Dark : MarkdownThemeVariant.Light;
    }
}
