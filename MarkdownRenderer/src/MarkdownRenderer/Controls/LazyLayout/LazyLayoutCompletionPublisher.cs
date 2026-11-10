using System;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.LazyLayout;

/// <summary>
/// Applies a lazy-layout result that is still current for the published
/// snapshot and pipeline: records the capacity attempt, routes a failure to
/// device recovery or a rebuild, and otherwise publishes either the readiness
/// change of an already-applied revision or the new layout commit, then
/// releases waiting input. <see cref="LazyLayoutRealization"/> decides
/// whether a result is current and retires its work first.
/// </summary>
internal sealed class LazyLayoutCompletionPublisher
{
    private readonly ILazyLayoutHost _host;
    private readonly LayoutCommitState _layoutCommit;
    private readonly LayoutCommitPublisher _layoutPublisher;
    private readonly MarkdownViewSurface _surface;
    private readonly VectorCapacityRetry _vectorCapacity;
    private readonly PointerInputRouter _pointerInput;
    private readonly FocusedItemCommit _focusCommit;

    internal LazyLayoutCompletionPublisher(
        ILazyLayoutHost host,
        LayoutCommitState layoutCommit,
        LayoutCommitPublisher layoutPublisher,
        MarkdownViewSurface surface,
        VectorCapacityRetry vectorCapacity,
        PointerInputRouter pointerInput,
        FocusedItemCommit focusCommit)
    {
        _host = host;
        _layoutCommit = layoutCommit;
        _layoutPublisher = layoutPublisher;
        _surface = surface;
        _vectorCapacity = vectorCapacity;
        _pointerInput = pointerInput;
        _focusCommit = focusCommit;
    }

    /// <summary>
    /// Publishes the current result of a realization of
    /// <paramref name="snapshot"/> that attempted
    /// <paramref name="attemptedVisibleBand"/> at
    /// <paramref name="attemptedCapacityGeneration"/>.
    /// </summary>
    internal void Publish(
        LayoutSnapshot snapshot,
        LazyLayoutWorkResult result,
        Exception? failure,
        double? anchorOffset,
        long pipelineGeneration,
        LazyLayoutBand attemptedVisibleBand,
        long attemptedCapacityGeneration)
    {
        // Record only a completed attempt.
        _vectorCapacity.RecordAttempt(attemptedVisibleBand, attemptedCapacityGeneration);

        if (failure is not null)
        {
            _host.NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
            if (GraphicsDeviceErrors.IsDeviceLost(failure))
            {
                _host.HandleDeviceLost(failure);
            }
            else
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] lazy layout realization failed: {failure}");
                _host.RequestFullRebuild();
            }
            _host.StopRecoveryClockIfIdle();
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        _vectorCapacity.Synchronize(snapshot);

        if (result.Commit.LayoutRevision <= _layoutCommit.AppliedRevision)
        {
            // Moving the active band can finish visible readiness without a
            // geometry revision or resource change (for example, two already
            // measured text-only viewports). Publish that status transition
            // even when there is no new drawing lease to repaint.
            _host.NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
            // This band is now measured and its native resources are
            // settled, but no layout commit follows to re-run the deferred
            // scene probe. A Mermaid or Math fallback that scrolled into this
            // band while it was being prepared would otherwise never be
            // scheduled and keep showing its source as "Loading".
            _host.ScheduleVisibleDeferredScenes();
            if (result.Commit.NativeSceneResourcesChanged)
            {
                // A prepared/retired drawing lease changes only the raster
                // surface. Repaint without rebuilding layout or disturbing
                // selection, focus, source mapping, or scroll position.
                _host.InvalidateAutomationLayout();
                _host.InvalidateCanvas();
            }

            // A focus request can arrive while an already-measured band is
            // publishing its completion. Re-evaluate it even when that worker
            // produced no newer layout revision.
            _ = _focusCommit.ResumePendingLazy(snapshot);
            _host.TryResumePendingScrollToBlock(snapshot, pipelineGeneration);
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
            _host.StopRecoveryClockIfIdle();
            return;
        }

        // A user can jump to an already-measured band while this worker is
        // finishing. That jump need not queue replacement work, so the old
        // completion still arrives. Never restore its captured scroll anchor
        // over a newer viewport position.
        bool restoreAnchor = LazyLayoutScrollAnchorPolicy.ShouldRestore(
            anchorOffset, _surface.Scroll?.VerticalOffset, _host.IsScrollViewIntermediate);
        _layoutPublisher.Apply(snapshot, restoreAnchor ? result.ScrollAnchor : null);
        _vectorCapacity.Synchronize(snapshot);
        if (result.Commit.NativeSceneResourcesChanged)
            _host.NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
        _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
        _host.StopRecoveryClockIfIdle();
    }
}
