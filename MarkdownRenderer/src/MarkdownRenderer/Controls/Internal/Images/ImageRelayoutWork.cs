using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Relayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.Scheduling;

namespace MarkdownRenderer.Controls.Internal.Images;

/// <summary>
/// The worker side of an image relayout. It holds the snapshot only through
/// the payload's strong borrow, which it releases once the scheduled work
/// has exited, and reaches the UI owner only weakly.
/// </summary>
internal static class ImageRelayoutWork
{
    /// <summary>
    /// Relayouts the changed image roots of <paramref name="payload"/> off
    /// the UI thread, then hands one ready publication to the dispatcher.
    /// </summary>
    /// <remarks>
    /// Kept whole: the layout closure writes the locals the drains, binding and publication
    /// read, and the outer finally must clear them in this frame once the scheduled work returns.
    /// </remarks>
    internal static async Task RunAsync(
        ImageRelayoutWorkPayload<LayoutSnapshot> payload,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        RetirableCancellationTokenSource cancellation,
        IMarkdownUiScheduler? scheduler,
        WeakReference<ImageRelayout> receiver,
        IMarkdownPerformanceSessionInternal? session,
        RasterPlacementInputs? rasterPlacement,
        long pointerInputEpoch,
        double? capturedScrollOffset)
    {
        Exception? failure = null;
        long started = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        Func<CancellationToken, bool>? layoutWork = null;
        Task<bool>? layoutTask = null;
        WeakSnapshotPublication<LayoutSnapshot>? publication = null;
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        MarkdownLayoutContext? rootMeasureContext = null;
        RasterPlacementPlan? candidatePlan = null;
        int? retryImageBlockIndex = null;
        bool retryCurrentImages = false;
        bool rootMeasureCommitted = false;
        try
        {
            try
            {
                layoutWork = layoutToken =>
                {
                    // Completed standalone, paragraph, table, list and quote images need
                    // only their owning placements, not an entire document
                    // clone. Prefer the bounded root
                    // batch even when the whole-tree COW path would be eligible.
                    PreparedImageRelayoutResult result = session is null
                        ? PreparedImageRelayoutResult.Unavailable
                        : payload.Snapshot.TryRelayoutCompletedImageRoots(
                            payload.Changed, payload.Width, layoutToken, out rootMeasureContext,
                            expectedPointerInputEpoch: pointerInputEpoch);
                    rootMeasureCommitted = result == PreparedImageRelayoutResult.Committed;
                    if (result == PreparedImageRelayoutResult.Retry)
                    {
                        if (payload.Changed.Length == 1)
                            retryImageBlockIndex = payload.Changed[0];
                        else
                            retryCurrentImages = true;
                    }
                    else if (result == PreparedImageRelayoutResult.Unavailable)
                    {
                        if (rasterPlacement is not null)
                            candidate = payload.Snapshot.CreateSingleRasterCandidate(
                                payload.Changed, payload.Width, layoutToken,
                                rasterPlacement.ImageLifetime, rasterPlacement.ImagePreparationOwner,
                                rasterPlacement.RetirementSink,
                                rasterPlacement.ImageUnavailable, out candidateContext);
                        if (candidate is null)
                            if (!payload.Snapshot.TryRelayoutChangedBlocks(
                                    payload.Changed, payload.Width, layoutToken, pointerInputEpoch))
                            {
                                if (payload.Changed.Length == 1)
                                    retryImageBlockIndex = payload.Changed[0];
                                else
                                    retryCurrentImages = true;
                            }
                    }
                    return true;
                };
                layoutTask = session is null
                    ? Task.Run(() => layoutWork(cancellation.Token), CancellationToken.None)
                    : session.ScheduleLayoutWorkAsync(
                        MarkdownLayoutWorkPriority.Background,
                        layoutWork,
                        cancellation.Token);
                await layoutTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellation.IsCancellationRequested || session?.IsDisposed == true)
            {
                if (session?.IsDisposed == true && !cancellation.IsCancellationRequested)
                    _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            // Join construction cleanup only after scheduled native work exits.
            // The new context has its own ledger, including pre-snapshot faults.
            if (candidateContext is not null)
            {
                try { await candidateContext.DrainOwnedRetirementsAsync(failure).ConfigureAwait(false); }
                catch (Exception exception) { failure = exception; }
            }
            if (rootMeasureContext is not null)
            {
                try { await rootMeasureContext.DrainOwnedRetirementsAsync(failure).ConfigureAwait(false); }
                catch (Exception exception) { failure = exception; }
            }
            if (candidate is not null && failure is null && !cancellation.IsCancellationRequested && session?.IsDisposed != true)
            {
                try
                {
                    rasterPlacement!.Bind(candidate.Snapshot);
                    candidatePlan = new RasterPlacementPlan(candidate, generation, anchor, rasterPlacement.ScrollOffset);
                    candidate = null; // plan now owns all private native resources
                }
                catch (Exception exception) { failure = exception; }
            }

            if (started != 0)
                MarkdownPerformanceEventSource.Log.ResourceWork(
                    3, Stopwatch.GetTimestamp() - started, payload.Changed.Length);

            // The control is the strong owner of the currently published
            // snapshot. A stale delayed completion needs only weak identity;
            // it still carries the CTS ticket so ordinary UI cleanup can run.
            publication = CreatePublication(
                payload, anchor, generation, cancellation, failure, receiver, candidatePlan,
                retryImageBlockIndex, retryCurrentImages, rootMeasureCommitted, capturedScrollOffset);
            candidatePlan = null; // one-shot envelope owns publish or discard
            if (!payload.TryOwnPublication(publication))
            {
                // Unload/completion closes this specific operation owner. Its
                // existing CTS retirement barrier owns callback and work drain;
                // never dispose that ticket early from the losing worker path.
                return;
            }
            // Every delivery route uses the same ready, one-shot owner. Actual
            // layout has returned; release strong borrows before dispatch so a
            // retained callback/task cannot hold a retired graph indirectly.
            payload.ReleaseAfterWorkDrains();
            try
            {
                scheduler?.TryEnqueue(MarkdownUiPriority.Normal, payload.PublishAfterWorkDrains);
            }
            catch (Exception exception)
            {
                RelayoutDiagnostics.ReportFailure("relayout completion dispatch", exception.Message);
            }
            // Rejection/lost dispatch leaves the ready envelope and CTS owned
            // by the loaded control's recovery timer, or unload's drain fence.
        }
        finally
        {
            // These async/closure fields can survive in a retained completed
            // task. Clear them only after the scheduled work really returned,
            // not when cancellation was merely requested. The UI callback has
            // a separate weak handoff and never reads these worker aliases.
            // Clear the actual owner as well as local aliases: compiler-created
            // parameter/closure copies then retain only an empty owner.
            payload.ReleaseAfterWorkDrains();
            payload = null!;
            layoutWork = null;
            layoutTask = null;
            publication = null;
            try
            {
                candidate?.Dispose();
                candidatePlan?.Dispose();
                // Failed/canceled candidates can register retirement after an
                // unload drain epoch has begun. The worker's existing barrier
                // must include this private retirement, not just observe it in
                // the host's next epoch.
                Task candidateDrain = candidateContext?.DrainOwnedRetirementsAsync(failure) ?? Task.CompletedTask;
                Task rootMeasureDrain = rootMeasureContext?.DrainOwnedRetirementsAsync(failure) ?? Task.CompletedTask;
                await Task.WhenAll(candidateDrain, rootMeasureDrain).ConfigureAwait(false);
            }
            finally
            {
                candidate = null;
                candidatePlan = null;
                candidateContext = null;
                rootMeasureContext = null;
                retryImageBlockIndex = null;
                rasterPlacement = null;
                session = null;
                scheduler = null;
                failure = null;
                cancellation = null!;
                receiver = null!;
            }
        }
    }

    // Keep the graph-bearing temporary inside this factory frame, not in the
    // async caller's frame that may still be unwinding after task completion.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakSnapshotPublication<LayoutSnapshot> CreatePublication(
        ImageRelayoutWorkPayload<LayoutSnapshot> payload,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        RetirableCancellationTokenSource cancellation,
        Exception? failure,
        WeakReference<ImageRelayout> receiver,
        RasterPlacementPlan? candidate,
        int? retryImageBlockIndex,
        bool retryCurrentImages,
        bool rootMeasureCommitted,
        double? capturedScrollOffset) =>
        new(payload.Snapshot, publishedSnapshot =>
        {
            try
            {
                if (receiver.TryGetTarget(out var relayout))
                    relayout.Complete(publishedSnapshot, anchor, generation, cancellation, failure, candidate,
                        retryImageBlockIndex, retryCurrentImages, rootMeasureCommitted, capturedScrollOffset);
                else
                    _ = CancellationTokenSourceRetirement.CancelAndDisposeAfter(cancellation, lifetime: null);
            }
            finally { candidate?.Dispose(); }
        }, candidate is null ? null : candidate.Dispose);
}
