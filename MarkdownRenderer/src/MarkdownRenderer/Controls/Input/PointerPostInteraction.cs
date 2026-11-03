using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Runs the work a pointer input recorded during its snapshot lease, after
/// the lease ends: releasing a pointer capture, resuming a rebuild, clearing
/// the selection, refreshing the adorner and activating a link.
/// </summary>
/// <remarks>
/// Each step can reenter XAML, so each is followed by the same currency
/// check. <see cref="PointerInputRouter"/> marks itself as draining around
/// <see cref="Run"/> so reentrant input queues instead of nesting here.
/// </remarks>
internal sealed class PointerPostInteraction
{
    private readonly IPointerInputRouterHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly SelectionAdornerPresenter _adorner;
    private uint _contextCaptureReleaseInFlightId;

    internal PointerPostInteraction(
        IPointerInputRouterHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        SelectionAdornerPresenter adorner)
    {
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _adorner = adorner;
    }

    /// <summary>
    /// True while <see cref="Run"/> releases the capture of
    /// <paramref name="pointerId"/> for a context gesture. The cancel or exit
    /// that release routes must not end the gesture.
    /// </summary>
    internal bool IsCaptureReleaseInFlight(uint pointerId) =>
        pointerId == _contextCaptureReleaseInFlightId && _contextCaptureReleaseInFlightId != 0;

    /// <summary>
    /// Runs <paramref name="input"/>'s recorded work. Returns false, after
    /// stopping at the first step that left the layout stale, when the
    /// snapshot, generation or layout revision is no longer current.
    /// </summary>
    internal bool Run(
        ref PointerInput input,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision) ||
            (input.ReleaseCanvasCaptureAfterInteraction &&
             (input.InteractionCanvas is not { } capturedCanvas ||
              !ReferenceEquals(_surface.Canvas, capturedCanvas))))
        {
            return false;
        }

        // Pointer capture release can synchronously route CaptureLost. It
        // must run after the snapshot lease, and reentrant pointer input
        // must join the existing FIFO queue instead of nesting here.
        if (input.ReleaseCanvasCaptureAfterInteraction)
        {
            ReleaseCanvasCapture(ref input);
            if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision) ||
                !ReferenceEquals(_surface.Canvas, input.InteractionCanvas))
            {
                return false;
            }
        }

        if (input.ResumeRebuildAfterInteraction)
        {
            _host.ResumePendingRebuildAfterPointerInteraction();
            if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision))
                return false;
        }

        if (input.ClearSelectionAfterInteraction)
        {
            _host.ClearSelectionForExternalInteraction(
                resetClickTracking: false,
                resetDeferredPointerInput: true);
            if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision))
                return false;
        }

        if (input.RefreshSelectionAdornerAfterInteraction)
        {
            _adorner.InvalidateGeometry();
            _adorner.Invalidate();
        }

        if (!IsCurrent(snapshot, snapshotGeneration, layoutRevision))
            return false;

        if (input.TryGetLinkActivation(out LinkActivationIntent activation))
            _host.ActivateLinkIntent(
                activation,
                snapshot,
                snapshotGeneration,
                layoutRevision);

        return IsCurrent(snapshot, snapshotGeneration, layoutRevision);
    }

    private void ReleaseCanvasCapture(ref PointerInput input)
    {
        UIElement captureTarget = input.InteractionCanvas!;
        if (input.ReleasePointerIdAfterInteraction == 0)
        {
            captureTarget.ReleasePointerCapture(input.Pointer);
        }
        else if (captureTarget.PointerCaptures is { } captures)
        {
            foreach (Pointer capturedPointer in captures)
            {
                if (capturedPointer.PointerId == input.ReleasePointerIdAfterInteraction)
                {
                    _contextCaptureReleaseInFlightId = input.ReleasePointerIdAfterInteraction;
                    try
                    {
                        captureTarget.ReleasePointerCapture(capturedPointer);
                    }
                    finally
                    {
                        _contextCaptureReleaseInFlightId = 0;
                    }
                    break;
                }
            }
        }
    }

    private bool IsCurrent(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(_published.Snapshot, snapshot) &&
        snapshotGeneration == _published.Generation &&
        snapshot.LayoutRevision == layoutRevision;
}
