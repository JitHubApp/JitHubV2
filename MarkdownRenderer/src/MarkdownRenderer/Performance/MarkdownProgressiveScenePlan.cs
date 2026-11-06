using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// <remarks>
/// The plan is the composition root for one document's deferred scenes. It
/// owns the gate, the relayout generation, the active producer table and the
/// visible set, and sequences generation advance and retirement. Its
/// collaborators each own one stage: <see cref="DeferredSceneViewportProbe"/>
/// finds visible markers, <see cref="DeferredScenePublisher"/> prepares and
/// queues results, <see cref="DeferredSceneGraphRebinder"/> refills detached
/// graphs, and <see cref="DeferredSceneResultStore"/> and
/// <see cref="DeferredSceneDiagnostics"/> hold what was published. They reach
/// back only through the host interfaces the plan implements explicitly.
/// <para>
/// This file is longer than 500 lines because those host interface members,
/// scheduling, generation advance and retirement all read and write the
/// plan's gated state: the active producer table, the generation and its
/// cancellation, and the visible set.
/// </para>
/// </remarks>
internal sealed class MarkdownProgressiveScenePlan :
    IScenePublicationTarget,
    IDeferredSceneGraphRebindHost,
    IDeferredScenePublicationHost,
    IDeferredSceneViewportHost,
    IDisposable
{
    private readonly object _gate = new();
    private readonly object _retirementLifecycleGate = new();
    private readonly RendererDocument _document;
    private readonly IMarkdownPerformanceSessionInternal _session;
    private readonly object _documentOwner;
    private readonly IReadOnlyDictionary<string, DeferredSceneWork> _works;
    private readonly SnapshotRetirementTracker _retirements = new();
    private readonly DeferredSceneResultStore _results;
    private readonly Dictionary<string, ActiveSceneWork> _active = new(StringComparer.Ordinal);
    private readonly DeferredSceneDiagnostics _diagnosticSet = new();
    private readonly DeferredSceneGraphRebinder _graphRebinder;
    private readonly DeferredScenePublisher _publisher;
    private readonly DeferredSceneViewportProbe _viewportProbe;
    private HashSet<string> _visibleMarkers = new(StringComparer.Ordinal);
    private bool _hasVisibleBand;
    private CancellationTokenSource _generationCancellation = new();
    private long _generation;
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
        _results = new DeferredSceneResultStore(((MarkdownPerformanceSession)session).CompletedSceneCache, this);
        _graphRebinder = new DeferredSceneGraphRebinder(
            _gate,
            this,
            session,
            documentOwner,
            works,
            _results,
            _diagnosticSet,
            _retirements);
        _publisher = new DeferredScenePublisher(
            _gate,
            this,
            this,
            session,
            documentOwner,
            _diagnosticSet);
        _viewportProbe = new DeferredSceneViewportProbe(_gate, this, session);
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

        IReadOnlyDictionary<string, DeferredSceneWork>? works =
            DeferredSceneWorkCatalog.TryBuild(document, cancellationToken);
        return works is null
            ? null
            : new MarkdownProgressiveScenePlan(document, session, documentOwner, works);
    }

    internal static bool ContainsDeferredSceneFallback(RendererDocument document) =>
        DeferredSceneWorkCatalog.ContainsDeferredSceneFallback(document);

    internal static bool TryCollectVisibleDeferredSceneMarkers(
        IReadOnlyList<BlockBox> measuredTopLevelBlocks,
        IReadOnlyList<int> topLevelOrdinals,
        LayoutPlacementGeneration placements,
        LayoutPlacementTransformContext placementTransform,
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
            placementTransform,
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
            if (!_disposed && _results.TryGetCompleted(marker, consumeHandoff: true, out MarkdownDeferredSceneResult? completed))
            {
                result = completed!.Fragment;
                projectionIdentity = _results.GetProjectionIdentity(marker);
                return true;
            }
        }

        result = null;
        return false;
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
            if (_disposed || !_results.TryGetDescriptor(marker, out descriptor))
            {
                result = null;
                descriptor = null;
                return false;
            }
            _results.TryGetMetadata(marker, out metadataLease);
        }
        bool restored = descriptor.TryRestore(out result, cancellationToken);
        GC.KeepAlive(metadataLease);
        return restored;
    }

    internal bool CanDetachGraph(VectorSceneBox box) => _graphRebinder.CanDetachGraph(box);

    /// <summary>
    /// Refills the detached graphs a lazy band needs. Called by the active
    /// lazy-layout request, outside its layout queue slot.
    /// </summary>
    internal Task<bool> RebindBandAsync(
        LayoutSnapshot snapshot, LazyLayoutBand band, CancellationToken cancellationToken) =>
        _graphRebinder.RebindBandAsync(snapshot, band, cancellationToken);

    bool IDeferredSceneGraphRebindHost.IsDisposedNoLock => _disposed;

    long IDeferredSceneGraphRebindHost.GenerationNoLock => _generation;

    CancellationToken IDeferredSceneGraphRebindHost.GenerationTokenNoLock => _generationCancellation.Token;

    long IDeferredSceneGraphRebindHost.CurrentGeneration => Volatile.Read(ref _generation);

    bool IDeferredScenePublicationHost.TryRestoreOnWorker(
        string marker,
        CancellationToken cancellationToken,
        out MarkdownDeferredSceneResult? result,
        out MarkdownCompletedSceneDescriptor? descriptor) =>
        TryRestoreOnWorker(marker, cancellationToken, out result, out descriptor);

    bool IDeferredSceneGraphRebindHost.TryRestoreOnWorker(
        string marker,
        CancellationToken cancellationToken,
        out MarkdownDeferredSceneResult? result,
        out MarkdownCompletedSceneDescriptor? descriptor) =>
        TryRestoreOnWorker(marker, cancellationToken, out result, out descriptor);

    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
                return _diagnosticSet.Current;
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
                activeWorks = _active.Count == 0 && !_graphRebinder.HasRebindsNoLock
                    ? Array.Empty<ActiveSceneWork>()
                    : _active.Values.Concat(_graphRebinder.RebindWorksNoLock).ToArray();
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

    internal bool ScheduleVisible(object documentOwner) => _viewportProbe.ScheduleVisible(documentOwner);

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

            _results.RetireHandoffsOutside(_visibleMarkers);

            return _visibleMarkers.Count;
        }
    }

    bool IDeferredSceneViewportHost.IsDisposedNoLock => _disposed;

    int IDeferredSceneViewportHost.UpdateVisibleMarkers(IEnumerable<string> markers) =>
        UpdateVisibleMarkers(markers);

    void IDeferredSceneViewportHost.ScheduleVisibleMarkersNoLock(
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onRetryVisibleBand)
    {
        foreach (string marker in _visibleMarkers)
        {
            TrySchedule(
                marker,
                enqueuePublication,
                onPublished,
                onRetryVisibleBand);
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
            if (_results.TryGetCompleted(marker, consumeHandoff: false, out _))
                return false;

            token = _generationCancellation.Token;
            var activeWork = new ActiveSceneWork(
                _generation,
                CancellationTokenSource.CreateLinkedTokenSource(token));
            Task task = Task.Run(
                () => _publisher.PrepareAndPublishAsync(
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
                (diagnosticPublication is null || _diagnosticSet.IsCurrent(diagnosticPublication));
            if (publish)
            {
                if (diagnosticPublication is not null)
                    _diagnosticSet.Commit(diagnosticPublication);
                _results.Publish(marker, descriptor, result);
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

    bool IDeferredScenePublicationHost.IsPublicationCurrentNoLock(
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

    void IDeferredScenePublicationHost.CompleteWithoutPublication(
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

    bool IDeferredScenePublicationHost.IsVisible(string marker, ActiveSceneWork activeWork)
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
                activeWorks = _active.Count == 0 && !_graphRebinder.HasRebindsNoLock
                    ? Array.Empty<ActiveSceneWork>()
                    : _active.Values.Concat(_graphRebinder.RebindWorksNoLock).ToArray();
                _active.Clear();
                _graphRebinder.ClearNoLock();
                _results.Clear();
                _viewportProbe.ReleaseControlNoLock();
                _diagnosticSet.Reset();
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
            _diagnosticSet.Dispose();
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
