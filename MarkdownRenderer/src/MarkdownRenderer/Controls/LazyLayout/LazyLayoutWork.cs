using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Threading;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.Scheduling;

namespace MarkdownRenderer.Controls.LazyLayout;

/// <summary>
/// The worker side of lazy layout. Its delayed callbacks carry only a weak
/// owner reference and value metadata, never the snapshot or the owner.
/// </summary>
internal static class LazyLayoutWork
{
    /// <summary>
    /// Measures <paramref name="band"/> off the UI thread, preparing deferred
    /// scenes when the scene graph needs rebinding, then stages one weak
    /// completion with <paramref name="handoff"/> and dispatches it.
    /// </summary>
    /// <remarks>
    /// Kept whole apart from the staging: the layout closure captures the snapshot in this
    /// frame, and the finally must clear it here so a retained delegate cannot hold it.
    /// </remarks>
    internal static async Task RunAsync(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        CancellationToken operationToken,
        IMarkdownUiScheduler? dispatcher,
        IMarkdownPerformanceSessionInternal? session,
        object sceneOwner,
        WeakReference<LazyLayoutRealization> receiver,
        WeakSnapshotPublicationDrainFence<LayoutSnapshot> handoff)
    {
        LazyLayoutWorkResult result = default;
        Exception? failure = null;
        bool canceled = false;
        Func<CancellationToken, LazyLayoutWorkResult>? layoutWork = null;
        try
        {
            layoutWork = layoutToken =>
            {
                var anchor = anchorOffset is { } offset
                    ? snapshot.CaptureScrollAnchor(offset)
                    : null;
                LazyLayoutCommit commit = snapshot.EnsureMeasuredBand(
                    band, visibleVectorBand, layoutToken);
                return new LazyLayoutWorkResult(commit, anchor);
            };
            bool restoredSceneResources = false;
            while (true)
            {
                Task<LazyLayoutWorkResult> layoutTask = session is null
                    ? Task.Run(() => layoutWork(operationToken), CancellationToken.None)
                    : session.ScheduleLayoutWorkAsync(
                        MarkdownLayoutWorkPriority.Background,
                        layoutWork,
                        operationToken);
                result = await layoutTask.ConfigureAwait(false);
                if (restoredSceneResources)
                    result = new LazyLayoutWorkResult(
                        result.Commit with { NativeSceneResourcesChanged = true }, result.ScrollAnchor);
                if (!result.Commit.SceneGraphRebindRequired)
                    break;

                // The layout queue slot and snapshot lock are released before
                // waiting for provider/native scene work. Keep this lazy request
                // alive so scroll supersession cancels it and covered viewport
                // events cannot start a second realization/refill loop.
                if (session is null || !await session.PrepareDeferredSceneBandAsync(
                        sceneOwner, snapshot, visibleVectorBand, operationToken).ConfigureAwait(false))
                    break;
                restoredSceneResources = true;
                operationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (
            operationToken.IsCancellationRequested ||
            session?.IsDisposed == true)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            result = default;
            failure = ex;
        }

        try
        {
            StageAndDispatchCompletion(
                snapshot,
                receiver,
                result,
                failure,
                anchorOffset,
                pipelineGeneration,
                realizationGeneration,
                canceled,
                operationToken,
                dispatcher,
                handoff);
        }
        finally
        {
            // The task continuation marks the operation drained only after this
            // method completes. Clear graph-bearing locals before that signal.
            layoutWork = null;
            snapshot = null!;
        }
    }

    private static void StageAndDispatchCompletion(
        LayoutSnapshot snapshot,
        WeakReference<LazyLayoutRealization> receiver,
        LazyLayoutWorkResult result,
        Exception? failure,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        bool canceled,
        CancellationToken operationToken,
        IMarkdownUiScheduler? dispatcher,
        WeakSnapshotPublicationDrainFence<LayoutSnapshot> handoff)
    {
        var publication = CreateCompletionPublication(
            snapshot,
            receiver,
            result,
            failure,
            anchorOffset,
            pipelineGeneration,
            realizationGeneration,
            canceled,
            operationToken);

        if (handoff.TryStage(realizationGeneration, publication) &&
            dispatcher is not null)
        {
            try
            {
                dispatcher.TryEnqueue(
                    MarkdownUiPriority.Low,
                    CreateCompletionDispatchCallback(
                        receiver,
                        realizationGeneration));
            }
            catch (Exception exception)
            {
                // The UI recovery timer owns the retained weak handoff.
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] lazy layout completion dispatch failed: " +
                    $"{exception.GetType().Name} (0x{exception.HResult:X8}).");
            }
        }
    }

    private static WeakSnapshotPublication<LayoutSnapshot> CreateCompletionPublication(
        LayoutSnapshot snapshot,
        WeakReference<LazyLayoutRealization> receiver,
        LazyLayoutWorkResult result,
        Exception? failure,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        bool canceled,
        CancellationToken operationToken)
        => new(snapshot, publishedSnapshot =>
        {
            if (receiver.TryGetTarget(out var realization))
            {
                realization.Complete(
                    publishedSnapshot,
                    result,
                    failure,
                    anchorOffset,
                    pipelineGeneration,
                    realizationGeneration,
                    canceled,
                    operationToken);
            }
        });

    private static Action
        CreateCompletionDispatchCallback(
            WeakReference<LazyLayoutRealization> receiver,
            long realizationGeneration)
        => () =>
        {
            if (receiver.TryGetTarget(out var realization))
                realization.TryPublishCompletion(realizationGeneration);
        };
}
