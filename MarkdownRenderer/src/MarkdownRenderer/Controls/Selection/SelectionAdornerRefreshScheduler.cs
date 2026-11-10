using System;
using System.Diagnostics;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Coalesces selection adorner refresh requests onto the next rendering
/// frame and runs each attempt under the snapshot's paint lease.
/// </summary>
/// <remarks>
/// It owns the frame subscription, the pending-attempt state, the request
/// version a paint compares against, and the reentrancy guard. Painting
/// itself stays with the <see cref="ISelectionAdornerPainter"/>.
/// </remarks>
internal sealed class SelectionAdornerRefreshScheduler
{
    private readonly ISelectionAdornerHost _host;
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly ISelectionAdornerPainter _painter;

    private readonly SelectionAdornerRetryState _retryState = new();
    private bool _retryFrameSubscribed;
    private EventHandler<object>? _retryFrameHandler;
    private bool _refreshInProgress;
    private long _requestVersion;

    /// <param name="host">Device recovery and geometry recovery state.</param>
    /// <param name="renderer">The renderer element, for its load state.</param>
    /// <param name="lifetime">The renderer's unload and disposal state.</param>
    /// <param name="published">The published snapshot and its generation.</param>
    /// <param name="painter">Paints the adorner under the paint lease.</param>
    internal SelectionAdornerRefreshScheduler(
        ISelectionAdornerHost host,
        FrameworkElement renderer,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        ISelectionAdornerPainter painter)
    {
        _host = host;
        _renderer = renderer;
        _lifetime = lifetime;
        _published = published;
        _painter = painter;
    }

    /// <summary>True while a refresh is requested or running.</summary>
    internal bool HasPendingRefresh => _retryState.IsPending || _refreshInProgress;

    /// <summary>Advances on every request; a paint that saw an older value is stale.</summary>
    internal long RequestVersion => _requestVersion;

    /// <summary>Requests a refresh on the next rendering frame.</summary>
    internal void RequestRefresh()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        // Device recovery owns its bounded backoff. Keep the geometry/visual
        // dirty flags intact, but do not race the replacement surface every
        // Rendering frame while the recovery timer is waiting.
        if (_host.IsDeviceRecoveryQueued)
        {
            CancelRetry();
            return;
        }

        _retryState.Request();
        _requestVersion = unchecked(_requestVersion + 1);
        ScheduleRetry();
    }

    /// <summary>Drops any pending refresh and stops listening for frames.</summary>
    internal void CancelRetry()
    {
        _retryState.Cancel();
        UnsubscribeRetryFrame();
    }

    /// <remarks>
    /// One ordered pass over the attempt's lifecycle and currency checks;
    /// kept whole so each early exit stays next to the state it guards.
    /// </remarks>
    private void TryRefreshAttempt()
    {
        if (_refreshInProgress)
        {
            // A synchronous XAML/native callback can re-enter the frame path.
            // Preserve that new request for the next rendering frame instead
            // of nesting a second snapshot lease or letting the outer attempt
            // clear its dirtiness.
            _retryState.Request();
            _requestVersion = unchecked(_requestVersion + 1);
            ScheduleRetry();
            return;
        }

        if (_host.IsDeviceRecoveryQueued)
        {
            CancelRetry();
            return;
        }

        if (!_retryState.TryBeginFrame())
            return;

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        long requestVersion = _requestVersion;
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
        {
            CompleteRetry();
            return;
        }

        if (snapshot.GeometryRecoveryRequired)
        {
            _host.RequestGeometryRecoveryIfRequired(snapshot);
            CancelRetry();
            return;
        }
        if (snapshot.IsRetiringOrDisposed)
        {
            CancelRetry();
            return;
        }

        long generation = _published.Generation;
        var operation = new SelectionAdornerRefreshOperation(_painter, generation);
        SelectionAdornerRefreshResult result;
        _refreshInProgress = true;
        try
        {
            result = SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref operation);
        }
        finally
        {
            _refreshInProgress = false;
        }

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        if (_host.IsDeviceRecoveryQueued)
        {
            // The native operation may have discovered device loss and queued
            // the normal backoff while it held the paint lease. Do not let the
            // stale revision created by surface release bypass that backoff.
            CancelRetry();
            return;
        }

        if (result != SelectionAdornerRefreshResult.Completed)
        {
            if (result == SelectionAdornerRefreshResult.AwaitLifecycle)
            {
                // Surface attachment/shutdown failures have no useful
                // per-frame retry. Keep dirty state and wait for the next
                // lifecycle/resource request. Restore the dirty state even if
                // a shutdown exception occurred after the visual commit.
                _painter.MarkGeometryDirty();
                if (requestVersion != _requestVersion)
                    ScheduleRetry();
                else
                    CancelRetry();
            }
            else
            {
                ScheduleRetry();
            }
            return;
        }

        if (requestVersion != _requestVersion ||
            !ReferenceEquals(snapshot, _published.Snapshot) || generation != _published.Generation ||
            snapshot.LayoutRevision != _painter.PaintedLayoutRevision)
        {
            _painter.MarkGeometryDirty();
            _retryState.Request();
            ScheduleRetry();
            return;
        }

        CompleteRetry();
    }

    private void OnRetryFrame(object? sender, object args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = HasPendingRefresh &&
            !_host.IsDeviceRecoveryQueued &&
            performance.IsMeasurementEnabled();
        // The harness changes this thread-static ID at its Rendering sample;
        // it labels the interval currently open. Work before that sample uses
        // the prior ID, while work after it uses the new ID, in either handler
        // subscription order.
        long logicalFrameId = measure
            ? MarkdownPerformanceEventSource.GetLogicalFrameId()
            : 0;
        measure = logicalFrameId > 0;
        if (!measure)
        {
            // Keep idle, uncorrelated, and device-recovery callbacks on the
            // normal path without adding timing/allocation probes to them.
            TryRefreshAttempt();
            return;
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        try
        {
            TryRefreshAttempt();
        }
        finally
        {
            performance.PaintWork(
                logicalFrameId,
                Stopwatch.GetTimestamp() - started,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
        }
    }

    private void ScheduleRetry()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        _retryState.Retry();
        if (_retryFrameSubscribed)
            return;

        _retryFrameHandler ??= OnRetryFrame;
        CompositionTarget.Rendering += _retryFrameHandler;
        _retryFrameSubscribed = true;
    }

    private void CompleteRetry()
    {
        _retryState.Complete();
        UnsubscribeRetryFrame();
    }

    private void UnsubscribeRetryFrame()
    {
        if (!_retryFrameSubscribed)
            return;

        if (_retryFrameHandler is { } handler)
            CompositionTarget.Rendering -= handler;
        _retryFrameSubscribed = false;
    }
}
