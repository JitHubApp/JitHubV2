using System;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Rendering;

/// <summary>
/// Recovers from a published snapshot whose live geometry failed. A failed
/// layout is not transient lock contention: the last painted tiles and the
/// committed logical selection stay, but geometry-bound input and pending
/// work are discarded before one replacement is requested for the snapshot.
/// </summary>
internal sealed class GeometryRecovery
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly PointerInputRouter _pointerInput;
    private readonly VectorCapacityRetry _vectorCapacity;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly PendingFocusGeometryWork _focusGeometryWork;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly FocusRingPresenter _focusRing;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly LayoutCommitState _layoutCommit;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly ImageRelayout _imageRelayout;
    private readonly InteractionReset _interactionReset;
    private readonly SelectionDragShield _dragShield;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly AbbreviationTooltipPresenter _abbreviationTooltip;
    private readonly PointerCursorPresenter _cursor;
    private readonly SelectionHandlesPresenter _handles;
    private readonly RendererAutomation _automation;
    private readonly CanvasDeviceRecovery _deviceRecovery;
    private readonly RebuildScheduler _rebuild;

    internal GeometryRecovery(
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewSurface surface,
        PointerInputRouter pointerInput,
        VectorCapacityRetry vectorCapacity,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        PendingFocusGeometryWork focusGeometryWork,
        HorizontalOverflowScrollVisuals overflowScroll,
        FocusRingPresenter focusRing,
        SelectionAdornerPresenter adorner,
        LayoutCommitState layoutCommit,
        ImageSubscriptions imageSubscriptions,
        LazyLayoutRealization lazyLayout,
        ImageRelayout imageRelayout,
        InteractionReset interactionReset,
        SelectionDragShield dragShield,
        SelectionContextMenuPresenter contextMenu,
        AbbreviationTooltipPresenter abbreviationTooltip,
        PointerCursorPresenter cursor,
        SelectionHandlesPresenter handles,
        RendererAutomation automation,
        CanvasDeviceRecovery deviceRecovery,
        RebuildScheduler rebuild)
    {
        _lifetime = lifetime;
        _published = published;
        _surface = surface;
        _pointerInput = pointerInput;
        _vectorCapacity = vectorCapacity;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _focusGeometryWork = focusGeometryWork;
        _overflowScroll = overflowScroll;
        _focusRing = focusRing;
        _adorner = adorner;
        _layoutCommit = layoutCommit;
        _imageSubscriptions = imageSubscriptions;
        _lazyLayout = lazyLayout;
        _imageRelayout = imageRelayout;
        _interactionReset = interactionReset;
        _dragShield = dragShield;
        _contextMenu = contextMenu;
        _abbreviationTooltip = abbreviationTooltip;
        _cursor = cursor;
        _handles = handles;
        _automation = automation;
        _deviceRecovery = deviceRecovery;
        _rebuild = rebuild;
    }

    /// <summary>
    /// Forgets focus-geometry retries, overflow-scroll retries and the focus
    /// ring publication, which all describe the current geometry.
    /// </summary>
    internal void ClearPendingFocusGeometryWork()
    {
        _focusGeometryWork.Clear();
        _overflowScroll.ClearPendingRetry();
        _focusRing.InvalidatePublication();
    }

    /// <summary>
    /// A failed live layout is not transient lock contention. Preserve the last
    /// painted tiles and committed logical selection, but discard geometry-bound
    /// input before requesting one offside replacement for this snapshot.
    /// Runs only on the control's UI dispatcher; workers publish scalar state.
    /// Returns true when <paramref name="snapshot"/> needs recovery, so the
    /// caller must not use its geometry.
    /// </summary>
    internal bool RequestIfRequired(LayoutSnapshot? snapshot)
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
        TryCleanup(_pointerInput.Reset);
        TryCleanup(_vectorCapacity.Reset);
        _horizontalOverflowDrag.Clear();
        ClearPendingFocusGeometryWork();
        _adorner.ClearGeometry();
        // This tuple describes re-publication of this snapshot's geometry, not
        // an offside replacement candidate. It cannot repair a failed tree.
        _layoutCommit.DropPending();
        _imageSubscriptions.ResetBitmapOutputRecovery();
        TryCleanup(_lazyLayout.Cancel);
        _imageRelayout.CancelForGeometryRecovery();

        // A disconnected XAML input site can throw while closing a flyout or
        // hiding a visual. Such cleanup must neither consume the one recovery
        // request without scheduling it nor leave another capture unreleased.
        TryCleanup(() => _interactionReset.ResetTransientState(clearSelection: false));
        TryCleanup(() => _surface.Canvas?.ReleasePointerCaptures());
        TryCleanup(() => _dragShield.SetActive(false));
        TryCleanup(_contextMenu.Close);
        TryCleanup(_abbreviationTooltip.Hide);
        TryCleanup(() => _cursor.SetShape(null));
        TryCleanup(_focusRing.Collapse);
        TryCleanup(() => _handles.SetVisibility(Visibility.Collapsed));
        TryCleanup(_adorner.ReleaseSurface);
        TryCleanup(_automation.InvalidateLayout);

        if (snapshot.GeometryRecoveryIsDeviceLost)
        {
            // Keep the device-loss backoff even if a detached adorner refuses
            // its final native property update.
            if (!TryCleanup(() => _deviceRecovery.HandleDeviceLost(snapshot.GeometryRecoveryFailureHResult)))
                _rebuild.Request(RebuildReason.Full);
        }
        else
        {
            _rebuild.Request(RebuildReason.Full);
        }

        return true;
    }

    private static bool TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
            return true;
        }
        catch (Exception failure)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] geometry recovery cleanup failed: {failure.Message}");
            return false;
        }
    }
}
