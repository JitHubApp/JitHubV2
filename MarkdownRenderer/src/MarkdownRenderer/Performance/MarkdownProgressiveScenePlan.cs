using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance.ProgressiveScenes;
using RendererDocument = MarkdownRenderer.Document.MarkdownDocument;
using Windows.Foundation;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Control-owned viewport work plan for deferred built-in scenes. The plan is
/// built once from immutable parser output, then visible boxes resolve their
/// private marker without rescanning the syntax tree.
/// </summary>
internal sealed partial class MarkdownProgressiveScenePlan : IScenePublicationTarget, IDisposable
{
    private static readonly IComparer<MarkdownDiagnostic> DiagnosticComparer =
        Comparer<MarkdownDiagnostic>.Create(CompareDiagnostics);

    private readonly object _gate = new();
    private readonly object _retirementLifecycleGate = new();
    private readonly RendererDocument _document;
    private readonly IMarkdownPerformanceSessionInternal _session;
    private readonly object _documentOwner;
    private readonly IReadOnlyDictionary<string, DeferredSceneWork> _works;
    private readonly SnapshotRetirementTracker _retirements = new();
    // Completed metadata is bounded by the immutable source work table and
    // holds weak graph references only. Strong reuse belongs to the session's
    // aggregate cache; unconsumed publications are transient layout handoffs.
    private readonly Dictionary<string, MarkdownCompletedSceneDescriptor> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownDeferredSceneResult> _pendingResults = new(StringComparer.Ordinal);
    private readonly MarkdownCompletedSceneCache _completedCache;
    private readonly Dictionary<string, ActiveSceneWork> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _diagnosticPublicationGate = new(1, 1);
    private HashSet<string> _visibleMarkers = new(StringComparer.Ordinal);
    private MarkdownRendererControl? _callbackOwner;
    private Func<Action, bool>? _enqueuePublication;
    private Action? _onPublished;
    private Action? _onRetryVisibleBand;
    private bool _hasVisibleBand;
    private bool _hasObservedVisiblePlaceholderStatus;
    private bool _visiblePlaceholderStatusPending;
    private CancellationTokenSource _generationCancellation = new();
    private ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>> _diagnosticContributions =
        ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableList<MarkdownDiagnostic> _diagnostics = ImmutableList<MarkdownDiagnostic>.Empty;
    private long _generation;
    private long _diagnosticsPublicationVersion;
    private bool _disposed;
    private Task? _retirementTask;

    private MarkdownProgressiveScenePlan(
        RendererDocument document,
        IMarkdownPerformanceSessionInternal session,
        object documentOwner,
        IReadOnlyDictionary<string, DeferredSceneWork> works)
    {
        _document = document;
        _session = session;
        _documentOwner = documentOwner;
        _works = works;
        _completedCache = ((MarkdownPerformanceSession)session).CompletedSceneCache;
    }

    /// <summary>
    /// Builds the immutable descriptor table away from the UI thread. Deferred
    /// callbacks retain their originating extension lifetime from parse time;
    /// scroll-time work only looks up markers on already-laid-out boxes.
    /// </summary>
    internal static Task<MarkdownProgressiveScenePlan?> TryCreateAsync(
        RendererDocument document,
        IMarkdownPerformanceSessionInternal session,
        object documentOwner,
        CancellationToken cancellationToken)
        => Task.Run(
            () => cancellationToken.IsCancellationRequested
                ? null
                : TryCreate(document, session, documentOwner, cancellationToken),
            CancellationToken.None);

    internal static MarkdownProgressiveScenePlan? TryCreate(
        RendererDocument document,
        IMarkdownPerformanceSessionInternal session,
        object documentOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(documentOwner);
        if (session.IsDisposed || cancellationToken.IsCancellationRequested)
            return null;

        var works = new Dictionary<string, DeferredSceneWork>(StringComparer.Ordinal);
        foreach (KeyValuePair<Block, MarkdownContentFragment> entry in document.ExtensionBlockNodeContent)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            Block block = entry.Key;
            if (!MarkdownSyntaxAdapter.TryGetBlockKind(block, out string syntaxKind))
            {
                continue;
            }

            MarkdownSyntaxNode? node = null;
            DeferredSceneWorkCatalog.Collect(entry.Value.Items, content =>
            {
                if (!content.Attributes.TryGetValue(MarkdownContentAttributes.DeferredScene, out string? marker) ||
                    marker is null ||
                    !DeferredSceneWorkCatalog.TryReadMarker(marker, syntaxKind, out _) ||
                    entry.Value.DeferredSceneRenderer is not { } renderer)
                {
                    return;
                }

                MarkdownSyntaxNode blockNode = node ??= MarkdownSyntaxAdapter.CreateBlockNode(block, document.Source);
                if (!content.SourceSpan.Equals(blockNode.SourceSpan))
                    return;

                var work = new DeferredSceneWork(
                    blockNode,
                    renderer,
                    entry.Value.DeferredSceneRendererLifetime,
                    content.Text ?? blockNode.Literal ?? string.Empty,
                    content.Language,
                    IsInline: false);
                // A malformed producer must not cause two visible boxes to
                // invoke the same callback/result slot. Independent markers
                // remain distinct even when their SourceSpan values coincide.
                works.TryAdd(marker, work);
            });
        }

        foreach (KeyValuePair<Inline, MarkdownContentFragment> entry in document.ExtensionInlineNodeContent)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            Inline inline = entry.Key;
            if (!MarkdownSyntaxAdapter.TryGetInlineKind(inline, out string syntaxKind))
                continue;

            MarkdownSyntaxNode? node = null;
            DeferredSceneWorkCatalog.Collect(entry.Value.Items, content =>
            {
                if (!content.Attributes.TryGetValue(MarkdownContentAttributes.DeferredScene, out string? marker) ||
                    marker is null ||
                    !DeferredSceneWorkCatalog.TryReadMarker(marker, syntaxKind, out _) ||
                    entry.Value.DeferredSceneRenderer is not { } renderer)
                {
                    return;
                }

                MarkdownSyntaxNode inlineNode = node ??= MarkdownSyntaxAdapter.CreateInlineNode(inline, document.Source);
                if (!content.SourceSpan.Equals(inlineNode.SourceSpan))
                    return;

                var work = new DeferredSceneWork(
                    inlineNode,
                    renderer,
                    entry.Value.DeferredSceneRendererLifetime,
                    content.SemanticText ?? inlineNode.Literal ?? string.Empty,
                    content.Language,
                    IsInline: true);
                works.TryAdd(marker, work);
            });
        }

        if (cancellationToken.IsCancellationRequested || works.Count == 0)
            return null;

        return new MarkdownProgressiveScenePlan(
            document,
            session,
            documentOwner,
            new ReadOnlyDictionary<string, DeferredSceneWork>(works));
    }

    internal static bool ContainsDeferredSceneFallback(RendererDocument document) =>
        DeferredSceneWorkCatalog.ContainsDeferredSceneFallback(document);

    internal static bool TryCollectVisibleDeferredSceneMarkers(
        IReadOnlyList<BlockBox> measuredTopLevelBlocks,
        IReadOnlyList<int> topLevelOrdinals,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy,
        out IReadOnlyList<string> markerKeys,
        out bool visibleSceneResourcesReady) =>
        DeferredSceneMarkerCollector.TryCollect(
            measuredTopLevelBlocks,
            topLevelOrdinals,
            placements,
            topLevelOrdinalByBlockIndex,
            viewportTop,
            viewportBottom,
            viewport,
            residencyPolicy,
            out markerKeys,
            out visibleSceneResourcesReady);

    internal static bool IsFiniteNonEmptyRect(Rect rect) =>
        DeferredSceneMarkerCollector.IsFiniteNonEmptyRect(rect);

    internal bool IsFor(RendererDocument document) => ReferenceEquals(_document, document);

    internal bool HasRegisteredWork
    {
        get
        {
            lock (_gate)
                return !_disposed && _works.Count != 0;
        }
    }

    internal bool IsRegisteredMarker(string marker)
    {
        if (string.IsNullOrEmpty(marker))
            return false;

        lock (_gate)
            return !_disposed && _works.ContainsKey(marker);
    }

    internal string[] GetRegisteredMarkerSnapshot()
    {
        lock (_gate)
        {
            if (_disposed || _works.Count == 0)
                return Array.Empty<string>();

            string[] markers = _works.Keys.ToArray();
            Array.Sort(markers, StringComparer.Ordinal);
            return markers;
        }
    }

    internal bool TryGetResult(string marker, out MarkdownContentFragment? result)
        => TryGetResult(marker, out result, out _);

    internal bool TryGetResult(string marker, out MarkdownContentFragment? result, out object? projectionIdentity)
    {
        projectionIdentity = null;
        lock (_gate)
        {
            if (!_disposed && TryGetCompletedNoLock(marker, consumeHandoff: true, out MarkdownDeferredSceneResult? completed))
            {
                result = completed!.Fragment;
                projectionIdentity = _completed[marker].ProjectionIdentity;
                return true;
            }
        }

        result = null;
        return false;
    }

    private bool TryGetCompletedNoLock(
        string marker,
        bool consumeHandoff,
        out MarkdownDeferredSceneResult? result)
    {
        result = null;
        if (!_completed.TryGetValue(marker, out MarkdownCompletedSceneDescriptor? descriptor))
            return false;
        if (_pendingResults.TryGetValue(marker, out result))
        {
            if (consumeHandoff)
            {
                _completedCache.Retain(this, marker, result, descriptor.EstimatedGraphWeightBytes);
                _pendingResults.Remove(marker);
            }
            return true;
        }
        if (_completedCache.TryGetResult(this, marker, out result))
            return true;
        if (!descriptor.TryGetOriginal(out result))
            return false;
        // UI layout and scheduling may reuse a live immutable result, but must
        // not reconstruct its content graph. Restoration runs on the worker.
        _completedCache.Retain(this, marker, result!, descriptor.EstimatedGraphWeightBytes);
        return true;
    }

    private bool TryRestoreOnWorker(
        string marker,
        CancellationToken cancellationToken,
        out MarkdownDeferredSceneResult? result,
        out MarkdownCompletedSceneDescriptor? descriptor)
    {
        object? metadataLease;
        lock (_gate)
        {
            if (_disposed || !_completed.TryGetValue(marker, out descriptor))
            {
                result = null;
                descriptor = null;
                return false;
            }
            _completedCache.TryGetMetadata(this, marker, out metadataLease);
        }
        bool restored = descriptor.TryRestore(out result, cancellationToken);
        GC.KeepAlive(metadataLease);
        return restored;
    }

    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
                return _diagnostics;
        }
    }

    /// <summary>
    /// Cancels outstanding work at a relayout boundary. Completed scenes can
    /// be reused from bounded retention or weak references to live content;
    /// their immutable vector geometry is independent of the viewport.
    /// </summary>
    internal Task AdvanceGeneration()
    {
        lock (_retirementLifecycleGate)
        {
            CancellationTokenSource oldCancellation;
            ActiveSceneWork[] activeWorks;
            lock (_gate)
            {
                if (_disposed)
                    return _retirementTask ?? Task.CompletedTask;

                oldCancellation = _generationCancellation;
                _generationCancellation = new CancellationTokenSource();
                _generation++;
                activeWorks = _active.Count == 0 && _graphRebinds.Count == 0
                    ? Array.Empty<ActiveSceneWork>()
                    : _active.Values.Concat(_graphRebinds.Values.Select(static request => request.Work)).ToArray();
            }

            Task[] activeTasks = new Task[activeWorks.Length];
            for (int index = 0; index < activeWorks.Length; index++)
            {
                ActiveSceneWork work = activeWorks[index];
                activeTasks[index] = work.Task;
                work.Cancel();
                _retirements.Track(work.Dispose(work.Task));
            }

            _retirements.Track(CancellationTokenSourceRetirement.CancelAndDisposeAfter(
                oldCancellation,
                Task.WhenAll(activeTasks)));
            Task generationRetirement = _retirements.DrainAsync();
            // A later generation or owner retirement must inherit this drain:
            // active work can leave `_active` before its captured drain has
            // completed. Tracking the drain itself carries completion and
            // faults into the next bounded epoch without retaining a history
            // of completed generation tasks.
            _retirements.Track(generationRetirement);
            return generationRetirement;
        }
    }

    internal bool ScheduleVisible(object documentOwner)
    {
        if (_session.IsDisposed || documentOwner is not MarkdownRendererControl control)
            return false;

        EnsureControlCallbacks(control);

        LayoutSnapshot? snapshot = control.CurrentSnapshot;
        if (snapshot is null || !control.TryGetViewport(out double top, out double height, out _) ||
            !double.IsFinite(top) || !double.IsFinite(height) || height <= 0)
        {
            control.RequestDeferredSceneProbeAfterPaint();
            NotifyVisiblePlaceholderStatusIfChanged(control, pending: true);
            return true;
        }
        double bottom = top + height;
        int visibleMarkerCount = -1;
        if (!snapshot.TryBeginPaint())
        {
            control.RequestDeferredSceneProbeAfterPaint();
            NotifyVisiblePlaceholderStatusIfChanged(control, pending: true);
            return true;
        }
        try
        {
            bool bandMeasured = snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                    top,
                    bottom,
                    out IReadOnlyList<BlockBox> visibleBlocks,
                    out IReadOnlyList<int> visibleOrdinals,
                    out LayoutPlacementGeneration placements,
                    out IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex);
            if (bandMeasured &&
                TryCollectVisibleDeferredSceneMarkers(
                    visibleBlocks,
                    visibleOrdinals,
                    placements,
                    topLevelOrdinalByBlockIndex,
                    top,
                    bottom,
                    viewport: null,
                    residencyPolicy: snapshot.VectorSceneResidencyPolicy,
                    out IReadOnlyList<string> measuredVisibleMarkers,
                    out _))
            {
                // Consume the policy-owned marker scratch before releasing the
                // snapshot lease, which serializes subsequent viewport probes.
                visibleMarkerCount = UpdateVisibleMarkers(measuredVisibleMarkers);
            }
        }
        finally
        {
            snapshot.EndPaint();
        }

        if (visibleMarkerCount < 0)
        {
            // The snapshot is being measured or has a stale inline geometry
            // index. A lazy band can finish measuring, or its scenes finish
            // preparing, without another layout commit, so ask the control to
            // re-probe after the next successful paint.
            control.RequestDeferredSceneProbeAfterPaint();
            NotifyVisiblePlaceholderStatusIfChanged(control, pending: true);
            return true;
        }

        NotifyVisiblePlaceholderStatusIfChanged(control, pending: visibleMarkerCount != 0);

        // The visible set is plan-owned and updated in place. Scheduling under
        // the same reentrant monitor avoids a per-scroll copy; TrySchedule only
        // queues background work and takes no external/UI callback inline.
        lock (_gate)
        {
            foreach (string marker in _visibleMarkers)
            {
                TrySchedule(
                    marker,
                    _enqueuePublication!,
                    _onPublished!,
                    _onRetryVisibleBand);
            }
        }

        return true;
    }

    private void NotifyVisiblePlaceholderStatusIfChanged(
        MarkdownRendererControl control,
        bool pending)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed)
                return;

            changed = !_hasObservedVisiblePlaceholderStatus ||
                _visiblePlaceholderStatusPending != pending;
            _hasObservedVisiblePlaceholderStatus = true;
            _visiblePlaceholderStatusPending = pending;
        }

        if (changed)
            control.DeferredSceneReadiness.NotifyItemStatusMayHaveChanged();
    }

    private void EnsureControlCallbacks(MarkdownRendererControl control)
    {
        if (ReferenceEquals(_callbackOwner, control))
            return;

        _callbackOwner = control;
        _enqueuePublication = action => control.DispatcherQueue?.TryEnqueue(() => action()) == true;
        _onPublished = control.RequestRebuildAfterDeferredScene;
        _onRetryVisibleBand = () =>
        {
            control.DispatcherQueue?.TryEnqueue(control.ScheduleVisibleDeferredScenes);
        };
    }

    /// <summary>
    /// Reconciles queued and active scene requests with the latest measured
    /// viewport. Admission waiters are canceled as soon as they leave that
    /// band, so a rapid jump cannot leave old scenes ahead of newly visible
    /// Math or Mermaid work in the shared scheduler.
    /// </summary>
    internal int UpdateVisibleMarkers(IEnumerable<string> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        lock (_gate)
        {
            if (_disposed)
                return 0;

            _hasVisibleBand = true;
            _visibleMarkers.Clear();
            foreach (string marker in markers)
            {
                if (_works.ContainsKey(marker))
                    _visibleMarkers.Add(marker);
            }

            foreach ((string marker, ActiveSceneWork work) in _active)
            {
                if (!_visibleMarkers.Contains(marker))
                    work.Cancel();
            }

            // A publication can leave the band before a candidate consumes it.
            // Move that handoff into bounded retention; don't leave forgotten
            // results pinned for every previously visited viewport.
            if (_pendingResults.Count > 0)
            {
                List<string>? retiredHandoffs = null;
                foreach ((string marker, MarkdownDeferredSceneResult result) in _pendingResults)
                {
                    if (!_visibleMarkers.Contains(marker))
                    {
                        _completedCache.Retain(this, marker, result, _completed[marker].EstimatedGraphWeightBytes);
                        (retiredHandoffs ??= []).Add(marker);
                    }
                }
                if (retiredHandoffs is not null)
                    foreach (string marker in retiredHandoffs)
                        _pendingResults.Remove(marker);
            }

            return _visibleMarkers.Count;
        }
    }

    internal bool TrySchedule(
        string marker,
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onRetryVisibleBand = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(marker);
        ArgumentNullException.ThrowIfNull(enqueuePublication);
        ArgumentNullException.ThrowIfNull(onPublished);

        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _session.IsDisposed || !_works.TryGetValue(marker, out DeferredSceneWork? work) ||
                _active.ContainsKey(marker) ||
                (_hasVisibleBand && !_visibleMarkers.Contains(marker)))
                return false;
            if (TryGetCompletedNoLock(marker, consumeHandoff: false, out _))
                return false;

            token = _generationCancellation.Token;
            var activeWork = new ActiveSceneWork(
                _generation,
                CancellationTokenSource.CreateLinkedTokenSource(token));
            Task task = Task.Run(
                () => PrepareAndPublishAsync(
                    marker,
                    work,
                    activeWork,
                    token,
                    enqueuePublication,
                    onPublished,
                    onRetryVisibleBand),
                CancellationToken.None);
            activeWork.Task = task;
            _retirements.Track(task);
            _active.Add(marker, activeWork);
        }

        return true;
    }

    private async Task PrepareAndPublishAsync(
        string marker,
        DeferredSceneWork work,
        ActiveSceneWork activeWork,
        CancellationToken generationToken,
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onRetryVisibleBand)
    {
        MarkdownDeferredSceneResult? result = null;
        MarkdownCompletedSceneDescriptor? descriptor = null;
        IMarkdownScenePreparationLease? sceneLease = null;
        ScenePublicationEnvelope? publicationEnvelope = null;
        CancellationTokenRegistration publicationCancellation = default;
        bool publicationCancellationRegistered = false;
        bool diagnosticGateHeld = false;
        try
        {
            try
            {
                activeWork.Token.ThrowIfCancellationRequested();
                if (!IsVisible(marker, activeWork))
                    throw new OperationCanceledException(activeWork.Token);
                sceneLease = await _session
                    .EnterScenePreparationAsync(_documentOwner, activeWork.Token)
                    .ConfigureAwait(false);
                CancellationToken workToken = sceneLease.CancellationToken;
                activeWork.Token.ThrowIfCancellationRequested();
                workToken.ThrowIfCancellationRequested();
                if (!IsVisible(marker, activeWork))
                    throw new OperationCanceledException(activeWork.Token);
                if (!TryRestoreOnWorker(marker, workToken, out result, out descriptor))
                {
                    result = await work.RenderAsync(workToken).ConfigureAwait(false);
                    workToken.ThrowIfCancellationRequested();
                    descriptor = MarkdownCompletedSceneDescriptor.Create(result, workToken);
                }
                workToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }
            catch (Exception)
            {
                // A worker/provider failure never escapes into layout or the UI
                // thread. Preserve the original source as accessible code fallback
                // and retain the exact syntax range for the diagnostic.
                result = work.CreateFailureResult("MDP0001", "Deferred native content could not be prepared.");
                descriptor = MarkdownCompletedSceneDescriptor.Create(result);
            }

            // Scene-preparation time ends when the immutable result is ready.
            // Its shared admission slot remains held below until the queued
            // layout publication commits or cancellation wins while queued.
            MarkdownPerformanceSession.CompleteScenePreparationTiming(sceneLease);

            if (result is null || descriptor is null || generationToken.IsCancellationRequested || activeWork.Token.IsCancellationRequested)
            {
                CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            ImmutableArray<MarkdownDiagnostic> newContribution = descriptor.DiagnosticContribution;
            bool publicationIsCurrent;
            bool diagnosticsChanged;
            lock (_gate)
            {
                publicationIsCurrent = IsPublicationCurrentNoLock(marker, activeWork, generationToken);
                diagnosticsChanged = publicationIsCurrent && !DiagnosticContributionEqualsNoLock(marker, newContribution);
            }

            if (!publicationIsCurrent)
            {
                CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            PreparedDiagnosticPublication? diagnosticPublication = null;
            if (diagnosticsChanged)
            {
                await _diagnosticPublicationGate.WaitAsync(activeWork.Token).ConfigureAwait(false);
                diagnosticGateHeld = true;

                ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>>? contributions = null;
                ImmutableList<MarkdownDiagnostic>? currentDiagnostics = null;
                ImmutableArray<MarkdownDiagnostic> previousContribution = ImmutableArray<MarkdownDiagnostic>.Empty;
                long publicationVersion = 0;
                lock (_gate)
                {
                    publicationIsCurrent = IsPublicationCurrentNoLock(marker, activeWork, generationToken);
                    if (publicationIsCurrent)
                    {
                        if (DiagnosticContributionEqualsNoLock(marker, newContribution))
                        {
                            diagnosticsChanged = false;
                        }
                        else
                        {
                            if (!_diagnosticContributions.TryGetValue(marker, out previousContribution))
                                previousContribution = ImmutableArray<MarkdownDiagnostic>.Empty;
                            contributions = _diagnosticContributions;
                            currentDiagnostics = _diagnostics;
                            publicationVersion = ++_diagnosticsPublicationVersion;
                        }
                    }
                }

                if (!publicationIsCurrent)
                {
                    CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                    return;
                }

                if (diagnosticsChanged)
                {
                    ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>> updatedContributions = newContribution.IsEmpty
                        ? contributions!.Remove(marker)
                        : contributions!.SetItem(marker, newContribution);
                    ImmutableList<MarkdownDiagnostic> updatedDiagnostics = ReplaceDiagnosticContribution(
                        currentDiagnostics!,
                        previousContribution,
                        newContribution);
                    activeWork.Token.ThrowIfCancellationRequested();
                    diagnosticPublication = new PreparedDiagnosticPublication(
                        publicationVersion,
                        updatedContributions,
                        updatedDiagnostics);
                }
                else
                {
                    _diagnosticPublicationGate.Release();
                    diagnosticGateHeld = false;
                }
            }

            activeWork.Token.ThrowIfCancellationRequested();
            CancellationToken publicationToken = sceneLease?.CancellationToken ?? activeWork.Token;
            publicationEnvelope = new ScenePublicationEnvelope(
                new QueuedScenePublication(
                    this,
                    marker,
                    activeWork,
                    generationToken,
                    result,
                    descriptor,
                    diagnosticPublication,
                    onPublished,
                    onRetryVisibleBand));
            publicationCancellation = publicationToken.Register(
                static state => ((ScenePublicationEnvelope)state!).CancelQueued(),
                publicationEnvelope);
            publicationCancellationRegistered = true;

            if (!publicationEnvelope.TryMarkQueued())
            {
                CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                return;
            }

            if (publicationEnvelope.IsQueued)
            {
                bool queued;
                try
                {
                    // This bound delegate captures only the clearable envelope.
                    // A canceled dispatcher item cannot pin its result,
                    // descriptor, plan, control, or callback roots.
                    queued = enqueuePublication(publicationEnvelope.Dispatch);
                }
                catch
                {
                    await publicationEnvelope.CancelQueuedOrWaitAsync().ConfigureAwait(false);
                    throw;
                }

                if (!queued)
                {
                    await publicationEnvelope.CancelQueuedOrWaitAsync().ConfigureAwait(false);
                    CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
                    return;
                }
            }

            await publicationEnvelope.Completion.ConfigureAwait(false);
            publicationToken.ThrowIfCancellationRequested();
            generationToken.ThrowIfCancellationRequested();
            activeWork.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
        }
        catch
        {
            CompleteWithoutPublication(marker, activeWork, onRetryVisibleBand);
            throw;
        }
        finally
        {
            try
            {
                if (publicationEnvelope is not null)
                    await publicationEnvelope.CancelQueuedOrWaitAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (publicationCancellationRegistered)
                        publicationCancellation.Dispose();
                }
                finally
                {
                    try
                    {
                        sceneLease?.Dispose();
                    }
                    finally
                    {
                        if (diagnosticGateHeld)
                            _diagnosticPublicationGate.Release();
                    }
                }
            }
        }
    }

    void IScenePublicationTarget.PublishOnUiThread(QueuedScenePublication publication)
    {
        string marker = publication.Marker;
        ActiveSceneWork activeWork = publication.ActiveWork;
        CancellationToken generationToken = publication.GenerationToken;
        MarkdownDeferredSceneResult result = publication.Result;
        MarkdownCompletedSceneDescriptor descriptor = publication.Descriptor;
        PreparedDiagnosticPublication? diagnosticPublication = publication.DiagnosticPublication;
        // The producer waits for this callback's envelope acknowledgement, so
        // its shared scene lease and plan retirement barrier remain live until
        // every local reference below has been released.
        // Retire its linked source before removing it from the active map so
        // generation and owner drains cannot miss this final cleanup interval.
        _retirements.Track(activeWork.Dispose(activeWork.Task));

        bool publish;
        bool retry;
        lock (_gate)
        {
            bool wasActive = _active.TryGetValue(marker, out ActiveSceneWork? current) &&
                ReferenceEquals(current, activeWork);
            if (wasActive)
                _active.Remove(marker);
            publish = wasActive && !_disposed &&
                activeWork.Generation == _generation &&
                (!_hasVisibleBand || _visibleMarkers.Contains(marker)) &&
                !generationToken.IsCancellationRequested &&
                !activeWork.Token.IsCancellationRequested &&
                !_session.IsDisposed &&
                (diagnosticPublication is null || diagnosticPublication.Version == _diagnosticsPublicationVersion);
            if (publish)
            {
                if (diagnosticPublication is not null)
                {
                    _diagnosticContributions = diagnosticPublication.Contributions;
                    _diagnostics = diagnosticPublication.Diagnostics;
                }
                // Transfer the construction-only strong metadata owner to
                // its separate session-wide byte budget before publishing
                // the weak descriptor. This does not walk the scene graph.
                if (descriptor.DetachMetadataOwner() is { } metadata)
                    _completedCache.RetainMetadata(this, marker, metadata, descriptor.MetadataWeightBytes);
                _completed[marker] = descriptor;
                _pendingResults[marker] = result;
            }
            retry = wasActive && !publish &&
                (activeWork.Token.IsCancellationRequested || activeWork.Generation != _generation) &&
                !_disposed && !_session.IsDisposed;
        }

        if (publish)
            publication.OnPublished();
        else if (retry)
            InvokeRetry(publication.OnRetryVisibleBand);
    }

    private bool IsPublicationCurrentNoLock(
        string marker,
        ActiveSceneWork activeWork,
        CancellationToken generationToken)
        => !_disposed &&
            !_session.IsDisposed &&
            _active.TryGetValue(marker, out ActiveSceneWork? current) &&
            ReferenceEquals(current, activeWork) &&
            activeWork.Generation == _generation &&
            (!_hasVisibleBand || _visibleMarkers.Contains(marker)) &&
            !generationToken.IsCancellationRequested &&
            !activeWork.Token.IsCancellationRequested;

    private bool DiagnosticContributionEqualsNoLock(
        string marker,
        ImmutableArray<MarkdownDiagnostic> contribution)
    {
        if (!_diagnosticContributions.TryGetValue(marker, out ImmutableArray<MarkdownDiagnostic> current))
            return contribution.IsEmpty;
        if (current.Length != contribution.Length)
            return false;
        for (int index = 0; index < current.Length; index++)
        {
            if (!EqualityComparer<MarkdownDiagnostic>.Default.Equals(current[index], contribution[index]))
                return false;
        }
        return true;
    }

    private static ImmutableList<MarkdownDiagnostic> ReplaceDiagnosticContribution(
        ImmutableList<MarkdownDiagnostic> snapshot,
        ImmutableArray<MarkdownDiagnostic> previous,
        ImmutableArray<MarkdownDiagnostic> replacement)
    {
        foreach (MarkdownDiagnostic diagnostic in previous)
        {
            int index = snapshot.BinarySearch(diagnostic, DiagnosticComparer);
            if (index >= 0)
                snapshot = snapshot.RemoveAt(index);
        }

        foreach (MarkdownDiagnostic diagnostic in replacement)
        {
            int index = snapshot.BinarySearch(diagnostic, DiagnosticComparer);
            if (index < 0)
                index = ~index;
            snapshot = snapshot.Insert(index, diagnostic);
        }

        return snapshot;
    }

    private static int CompareDiagnostics(MarkdownDiagnostic left, MarkdownDiagnostic right)
    {
        int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Code, right.Code);
        if (comparison != 0)
            return comparison;
        comparison = left.SourceSpan.Length.CompareTo(right.SourceSpan.Length);
        if (comparison != 0)
            return comparison;
        comparison = left.Severity.CompareTo(right.Severity);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Message, right.Message);
    }

    private void CompleteWithoutPublication(
        string marker,
        ActiveSceneWork activeWork,
        Action? onRetryVisibleBand)
    {
        // All producer/provider work has unwound on this path. Register source
        // retirement before dropping the active-map entry.
        _retirements.Track(activeWork.Dispose(activeWork.Task));

        bool removed;
        bool retry;
        lock (_gate)
        {
            removed = _active.TryGetValue(marker, out ActiveSceneWork? current) &&
                ReferenceEquals(current, activeWork);
            if (removed)
                _active.Remove(marker);
            retry = removed &&
                (activeWork.Token.IsCancellationRequested || activeWork.Generation != _generation) &&
                !_disposed && !_session.IsDisposed;
        }
        if (retry)
            InvokeRetry(onRetryVisibleBand);
    }

    private bool IsVisible(string marker, ActiveSceneWork activeWork)
    {
        lock (_gate)
        {
            return !_disposed &&
                _active.TryGetValue(marker, out ActiveSceneWork? current) &&
                ReferenceEquals(current, activeWork) &&
                (!_hasVisibleBand || _visibleMarkers.Contains(marker));
        }
    }

    private static void InvokeRetry(Action? retry)
    {
        try
        {
            retry?.Invoke();
        }
        catch
        {
            // A failed dispatcher retry must not escape from background scene work.
        }
    }

    public void Dispose() => _ = Retire();

    internal Task Retire()
    {
        lock (_retirementLifecycleGate)
        {
            CancellationTokenSource cancellation;
            ActiveSceneWork[] activeWorks;
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposed)
                    return _retirementTask ?? Task.CompletedTask;

                _disposed = true;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _retirementTask = completion.Task;
                cancellation = _generationCancellation;
                activeWorks = _active.Count == 0 && _graphRebinds.Count == 0
                    ? Array.Empty<ActiveSceneWork>()
                    : _active.Values.Concat(_graphRebinds.Values.Select(static request => request.Work)).ToArray();
                _active.Clear();
                _graphRebinds.Clear();
                _completed.Clear();
                _pendingResults.Clear();
                _completedCache.RemoveOwner(this);
                _callbackOwner = null;
                _enqueuePublication = null;
                _onPublished = null;
                _onRetryVisibleBand = null;
                _diagnosticsPublicationVersion++;
                _diagnosticContributions = ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>>.Empty
                    .WithComparers(StringComparer.Ordinal);
                _diagnostics = ImmutableList<MarkdownDiagnostic>.Empty;
            }

            foreach (ActiveSceneWork work in activeWorks)
            {
                work.Cancel();
                _retirements.Track(work.Dispose(work.Task));
            }

            _retirements.Track(CancellationTokenSourceRetirement.CancelAndDisposeAfter(
                cancellation,
                Task.WhenAll(activeWorks.Select(static work => work.Task))));
            _ = CompletePlanRetirementAsync(_retirements.DrainAsync(), completion);
            return completion.Task;
        }
    }

    private async Task CompletePlanRetirementAsync(Task retirement, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            // No producer can still be waiting for or releasing the gate after
            // the tracked plan drain completes. Late dispatcher callbacks only
            // acknowledge their TCS and never touch this semaphore.
            _diagnosticPublicationGate.Dispose();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is null)
            completion.TrySetResult();
        else
            completion.TrySetException(failure);
    }
}
