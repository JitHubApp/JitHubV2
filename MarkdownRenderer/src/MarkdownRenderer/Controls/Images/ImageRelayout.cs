using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// Relayout of the published snapshot after images change size. Completed
/// images add their block index to a pending set; one coalesced operation
/// at a time relayouts a bounded batch of those roots off the UI thread and
/// publishes the result, either in place or as a private raster placement.
/// The recovery clock retries any handoff the dispatcher rejected or withheld.
/// </summary>
internal sealed class ImageRelayout
{
    private readonly IImageRelayoutHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly LayoutCommitState _layoutCommit;
    private readonly MarkdownViewSurface _surface;
    private readonly PointerInputRouter _pointerInput;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly RasterPlacementPublisher _rasterPlacement;
    private readonly RecoveryClock _recoveryClock;

    // A sparse HashSet can scan an old storm's entire retained slot array to
    // capture just 32 live indices. A balanced live-only tree keeps capture
    // O(log N + batch size), and naturally prioritizes earlier document roots.
    private readonly SortedSet<int> _pending = [];
    private bool _queued;
    private bool _active;
    private long _dispatchTicket;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private ImageRelayoutWorkPayload<LayoutSnapshot>? _payload;

    internal ImageRelayout(
        IImageRelayoutHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        LayoutCommitState layoutCommit,
        MarkdownViewSurface surface,
        PointerInputRouter pointerInput,
        ImageSubscriptions imageSubscriptions,
        RasterPlacementPublisher rasterPlacement,
        RecoveryClock recoveryClock)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _layoutCommit = layoutCommit;
        _surface = surface;
        _pointerInput = pointerInput;
        _imageSubscriptions = imageSubscriptions;
        _rasterPlacement = rasterPlacement;
        _recoveryClock = recoveryClock;
    }

    /// <summary>True while a relayout is queued, running or has pending block indices.</summary>
    internal bool HasPendingWork => _queued || _active || _pending.Count > 0;

    /// <summary>
    /// Records that the image at <paramref name="blockIndex"/> changed size
    /// and schedules one coalesced relayout of the published snapshot.
    /// </summary>
    internal void Queue(int blockIndex)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is null)
            return;

        if (_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return;

        // These indices belong to the still-published snapshot, not the
        // requested replacement. Only a successful snapshot commit clears them.
        _pending.Add(blockIndex);
        if (_queued || _active)
        {
            _recoveryClock.Start();
            return;
        }

        _queued = true;
        long ticket = ++_dispatchTicket;
        _recoveryClock.Start();
        if (!_recoveryClock.IsRunning)
        {
            // Do not start an asynchronous handoff without its recovery route.
            _queued = false;
            _host.RequestFullRebuild();
            return;
        }
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = _host.DispatcherQueue;
        var receiver = new WeakReference<ImageRelayout>(this);
        try
        {
            // The timer retains the pending set on rejection and can also
            // claim accepted-but-withheld dispatch. Neither callback keeps this owner alive.
            dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (receiver.TryGetTarget(out var relayout))
                        relayout.ProcessQueued(ticket);
                });
        }
        catch (Exception exception)
        {
            RelayoutDiagnostics.ReportFailure("relayout dispatch", exception.Message);
        }
    }

    /// <summary>Queues the earliest pending block, if any; the rest coalesce into it.</summary>
    internal void QueueFirstPending()
    {
        foreach (int pending in _pending)
        {
            Queue(pending);
            break;
        }
    }

    /// <summary>Recovery clock retry of the latest dispatch.</summary>
    internal void ProcessQueued() => ProcessQueued(_dispatchTicket);

    /// <summary>Publishes a ready completion whose dispatcher handoff was lost.</summary>
    internal void PublishReadyCompletion() => _payload?.PublishAfterWorkDrains();

    /// <summary>
    /// Completes a relayout whose worker faulted before it could publish,
    /// then asks for a full rebuild unless geometry recovery took over.
    /// </summary>
    internal void RecoverFaultedHandoff()
    {
        if (_task is { IsFaulted: true } failed &&
            _cts is { } cancellation)
        {
            RelayoutDiagnostics.ReportFailure("relayout handoff", failed.Exception!);
            Complete(null, null, 0, cancellation, null);
            if (!_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
                _host.RequestFullRebuild();
        }
    }

    /// <summary>
    /// Drops pending and queued work when a new snapshot is committed. These
    /// indices belong to the snapshot being replaced; an active operation
    /// finishes against its own snapshot and is fenced on completion.
    /// </summary>
    internal void ClearForSnapshotReplacement()
    {
        _pending.Clear();
        _queued = false;
        ++_dispatchTicket;
        if (!_active)
            _host.StopRecoveryClock(force: false);
    }

    /// <summary>Drops pending work and cancels the active operation for geometry recovery.</summary>
    internal void CancelForGeometryRecovery()
    {
        _pending.Clear();
        _queued = false;
        ++_dispatchTicket;

        // Retire/cancel through the normal work barriers; never dispose a CTS
        // or a native snapshot while its worker is still unwinding.
        CancelActive();
    }

    /// <summary>
    /// Requests cancellation of the active operation. Its source retires on
    /// completion, behind the worker's own task.
    /// </summary>
    internal void CancelActive()
    {
        if (_cts is { } cancellation)
            _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation);
    }

    /// <summary>
    /// Retires all relayout work when the loaded resources are released. The
    /// active worker's cancellation source retires behind its own task.
    /// </summary>
    internal void Retire()
    {
        _queued = false;
        _active = false;
        ++_dispatchTicket;
        _host.StopRecoveryClock(force: true);
        Interlocked.Exchange(ref _payload, null)?.DiscardPublication();
        _ = _host.RetireCancellationTokenSource(_cts, _task);
        _cts = null;
        _task = null;
        _pending.Clear();
    }

    /// <summary>
    /// Starts the relayout dispatched with <paramref name="ticket"/>, unless
    /// a newer dispatch replaced it. Leaves the pending set intact while a
    /// rebuild or a snapshot gesture must finish first.
    /// </summary>
    internal void ProcessQueued(long ticket)
    {
        if (!_queued || ticket != _dispatchTicket)
            return;

        LayoutSnapshot? snapshot = _published.Snapshot;
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || snapshot is null ||
            _pending.Count == 0)
        {
            _queued = false;
            if (_layoutCommit.Pending is null)
                _host.StopRecoveryClock(force: false);
            return;
        }

        // Do not compete with a replacement build. If it fails or is canceled
        // without committing, the timer resumes work for the old visible graph.
        if (_host.DefersToRebuild())
            return;

        // Keep the pending set and recovery route intact. Neither in-place
        // reflow nor private snapshot replacement may change geometry owned
        // by a live or not-yet-captured table/selection gesture.
        if (_pointerInput.HasSnapshotInteraction)
            return;

        _queued = false;
        CancellationTokenSource? cancellation = null;
        try
        {
            IMarkdownPerformanceSessionInternal? session =
                _host.PerformanceSession as IMarkdownPerformanceSessionInternal;
            int[] changed = ImageRelayoutBatch.CapturePendingIndices(
                _pending,
                optedIn: session is not null);
            (int BlockIndex, double OffsetFromTop)? anchor = null;
            if (_surface.Scroll is { VerticalOffset: > 0 } scroll &&
                snapshot.TryCaptureScrollAnchor(scroll.VerticalOffset, out var captured) &&
                captured.BlockIndex is { } blockIndex)
            {
                anchor = (blockIndex, captured.OffsetFromTop);
            }

            // Rebuild start and unload explicitly cancel this operation. A
            // failed replacement's canceled CTS must not poison recovery of
            // the snapshot that remains published afterward.
            cancellation = new CancellationTokenSource();
            var payload = new ImageRelayoutWorkPayload<LayoutSnapshot>(
                snapshot, changed, _host.LayoutWidth);
            var receiver = new WeakReference<ImageRelayout>(this);

            _active = true;
            _cts = cancellation;
            _payload = payload;
            RasterPlacementInputs? rasterPlacement =
                _rasterPlacement.TryCreateInputs(snapshot, optedIn: session is not null);
            _task = ImageRelayoutWork.RunAsync(
                payload, anchor, _published.Generation, cancellation, _host.DispatcherQueue,
                receiver, session, rasterPlacement, snapshot.PointerInputEpoch,
                _surface.Scroll?.VerticalOffset);
            // The worker now owns only this immutable bounded index array.
            // Leave any storm remainder in the UI-owned pending set for the
            // normal completion/recovery route to schedule next.
            foreach (int completedBlockIndex in changed)
                _pending.Remove(completedBlockIndex);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _payload, null)?.DiscardPublication();
            _ = _host.RetireCancellationTokenSource(cancellation, _task);
            _cts = null;
            _task = null;
            _active = false;
            _host.StopRecoveryClock(force: false);
            RelayoutDiagnostics.ReportFailure("relayout setup", exception);
            _host.RequestFullRebuild();
        }
    }

    /// <summary>
    /// Applies a finished relayout on the UI thread. Only the current
    /// operation publishes; any completion retires its own cancellation
    /// source and schedules whatever stayed pending.
    /// </summary>
    internal void Complete(
        LayoutSnapshot? snapshot,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        CancellationTokenSource cancellation,
        Exception? failure,
        RasterPlacementPlan? candidate = null,
        int? retryImageBlockIndex = null,
        bool retryCurrentImages = false,
        bool rootMeasureCommitted = false,
        double? capturedScrollOffset = null)
    {
        bool current = ReferenceEquals(_cts, cancellation);
        Task? workLifetime = current ? _task : null;
        long started = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        if (current)
        {
            Interlocked.Exchange(ref _payload, null)?.DiscardPublication();
            _cts = null;
            _task = null;
            _active = false;
            if (_layoutCommit.Pending is null)
                _host.StopRecoveryClock(force: false);
        }

        bool canceled = cancellation.IsCancellationRequested;
        _ = _host.RetireCancellationTokenSource(cancellation, workLifetime);
        try
        {
            // Cancellation does not make a failed live mutation safe. This
            // check precedes the ordinary canceled-result publication fence.
            if (_host.RequestGeometryRecoveryIfRequired(snapshot))
                return;

            // Private preparation remains cancelable. Once root placement has
            // committed, its live size/paint state must still reach the UI even
            // if cancellation arrived after that non-cancelable commit point.
            // A private whole-tree candidate has not committed and is excluded.
            if (current && snapshot is not null && !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                (!canceled || rootMeasureCommitted) &&
                generation == _published.Generation && ReferenceEquals(snapshot, _published.Snapshot))
            {
                if (failure is not null)
                {
                    RelayoutDiagnostics.ReportFailure("relayout", failure);
                    _host.RequestFullRebuild();
                }
                else if (retryImageBlockIndex.HasValue || retryCurrentImages)
                {
                    // Private measurement lost an input/layout/resource fence.
                    // Keep only a scalar for a singleton. Multi-image fallback
                    // recovers from the current control-owned subscriptions,
                    // never retaining the worker's dirty array in a dispatch.
                    if (retryImageBlockIndex is { } retryIndex)
                        _pending.Add(retryIndex);
                    if (retryCurrentImages)
                        _imageSubscriptions.AddBlockIndicesTo(_pending);
                    if (snapshot.IsRetiringOrDisposed ||
                        _host.LayoutWidth != snapshot.Size.Width)
                        _host.RequestFullRebuild();
                }
                else if (candidate is not null)
                {
                    if (_pointerInput.HasSnapshotInteraction)
                    {
                        // A gesture may have started after the worker captured
                        // its source. Discard this private candidate and retry
                        // after release; do not reset capture or request a full
                        // rebuild solely because the user is interacting.
                        _imageSubscriptions.AddBlockIndicesTo(_pending);
                    }
                    else if (!_rasterPlacement.TryPublish(snapshot, candidate, generation))
                    {
                        // A changed width/policy/raster invalidates the private
                        // proof. Rebuild rather than mutate the old tree here.
                        _imageSubscriptions.AddBlockIndicesTo(_pending);
                        _host.RequestFullRebuild();
                    }
                }
                else
                    _host.ApplyLazyLayoutCommit(snapshot, rootMeasureCommitted &&
                        (canceled || capturedScrollOffset != _surface.Scroll?.VerticalOffset) ? null : anchor);
            }
        }
        catch (Exception exception)
        {
            RelayoutDiagnostics.ReportFailure("relayout", exception);
            _host.RequestFullRebuild();
        }
        finally
        {
            // A replacement can cancel an active reflow and then fail itself.
            // Recover current image geometry without retaining the worker's
            // dirty array in the delayed UI envelope or touching a retired graph.
            if (current && canceled && !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                ReferenceEquals(snapshot, _published.Snapshot) && snapshot?.GeometryRecoveryRequired != true)
            {
                _imageSubscriptions.AddBlockIndicesTo(_pending);
            }
            // The pending set is cleared before every snapshot replacement.
            // New-snapshot completions may accumulate while an old task drains.
            if (current && _pending.Count > 0 && !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                _published.Snapshot?.GeometryRecoveryRequired != true)
            {
                foreach (int pending in _pending)
                {
                    Queue(pending);
                    break;
                }
            }
            if (started != 0)
                MarkdownPerformanceEventSource.Log.ResourceWork(
                    4, Stopwatch.GetTimestamp() - started, 1);
        }
    }
}
