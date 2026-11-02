using System;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    /// <summary>
    /// A failed live layout is not transient lock contention. Preserve the last
    /// painted tiles and committed logical selection, but discard geometry-bound
    /// input before requesting one offside replacement for this snapshot.
    /// Runs only on the control's UI dispatcher; workers publish scalar state.
    /// </summary>
    private bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || snapshot is null ||
            !ReferenceEquals(snapshot, _published.Snapshot) || !snapshot.GeometryRecoveryRequired)
        {
            return false;
        }

        if (!snapshot.TryClaimGeometryRecoveryRequest())
            return true;

        // Deferred presses/captured handles must not keep the Full request
        // waiting for a release that can no longer safely hit-test this tree.
        TryGeometryRecoveryCleanup(_pointerInput.Reset);
        TryGeometryRecoveryCleanup(ResetVectorCapacityObserver);
        _horizontalOverflowDrag.Clear();
        _focusGeometryWork.Clear();
        _adorner.ClearGeometry();
        // This tuple describes re-publication of this snapshot's geometry, not
        // an offside replacement candidate. It cannot repair a failed tree.
        _pendingLayoutCommit = null;
        ++_lastLayoutCommitTicket;
        ResetBitmapOutputRecovery();
        TryGeometryRecoveryCleanup(CancelLazyLayoutRealization);
        _pendingImageRelayoutBlockIndices.Clear();
        _imageRelayoutQueued = false;
        ++_lastImageRelayoutDispatchTicket;

        // Retire/cancel through the normal work barriers; never dispose a CTS
        // or a native snapshot while its worker is still unwinding.
        if (_imageRelayoutCts is { } imageCancellation)
            _ = CancellationTokenSourceRetirement.RequestCancellation(imageCancellation);

        // A disconnected XAML input site can throw while closing a flyout or
        // hiding a visual. Such cleanup must neither consume the one recovery
        // request without scheduling it nor leave another capture unreleased.
        TryGeometryRecoveryCleanup(() => ResetTransientInteractionState(clearSelection: false));
        TryGeometryRecoveryCleanup(() => _surface.Canvas?.ReleasePointerCaptures());
        TryGeometryRecoveryCleanup(() => _dragShield.SetActive(false));
        TryGeometryRecoveryCleanup(_contextMenu.Close);
        TryGeometryRecoveryCleanup(_abbreviationTooltip.Hide);
        TryGeometryRecoveryCleanup(() => _cursor.SetShape(null));
        TryGeometryRecoveryCleanup(_focusRing.Collapse);
        TryGeometryRecoveryCleanup(() => _handles.SetVisibility(Visibility.Collapsed));
        TryGeometryRecoveryCleanup(_adorner.ReleaseSurface);
        TryGeometryRecoveryCleanup(InvalidateAutomationLayout);

        if (snapshot.GeometryRecoveryIsDeviceLost)
        {
            // Keep the device-loss backoff even if a detached adorner refuses
            // its final native property update.
            if (!TryGeometryRecoveryCleanup(() => HandleCanvasDeviceLost(snapshot.GeometryRecoveryFailureHResult)))
                RequestRebuild(RebuildReason.Full);
        }
        else
        {
            RequestRebuild(RebuildReason.Full);
        }

        return true;
    }

    private static bool TryGeometryRecoveryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
            return true;
        }
        catch (Exception failure)
        {
            Diagnostics.MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] geometry recovery cleanup failed: {failure.Message}");
            return false;
        }
    }

    private void OnCanvasDeviceRecoveryTimer(DispatcherQueueTimer sender, object args)
    {
        // A stopped timer can already have a tick queued. It must not clear a
        // newer recovery after unload/reload or a subsequent device failure.
        if (!ReferenceEquals(sender, _canvasDeviceRecoveryTimer))
            return;

        StopCanvasDeviceRecovery();
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        RequestRebuild(RebuildReason.Full);
        InvalidateCanvas();
        _adorner.Invalidate();
    }

    private void StopCanvasDeviceRecovery()
    {
        var timer = _canvasDeviceRecoveryTimer;
        _canvasDeviceRecoveryTimer = null;
        _canvasDeviceRecoveryQueued = false;
        if (timer is null)
            return;

        // Clear ownership first, including when the dispatcher is shutting
        // down. No background closure or retry loop retains this control.
        try { timer.Stop(); }
        catch (Exception) { }
        finally
        {
            try { timer.Tick -= OnCanvasDeviceRecoveryTimer; }
            catch (Exception) { }
        }
    }
}
