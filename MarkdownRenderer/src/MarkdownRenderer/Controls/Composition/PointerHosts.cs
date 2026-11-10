using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Composition;

/// <summary>
/// The pointer input owners’ hosts: input dispatch, document interaction, overflow drag, routing and deferred gestures.
/// </summary>
/// <remarks>
/// The owners these hosts forward to are constructed after the owners that
/// need the hosts, so the control connects them once its owners exist.
/// </remarks>
internal sealed class PointerHosts :
    IPointerInputSink,
    IPointerInteractionHost,
    IHorizontalOverflowPointerHost,
    IPointerInputRouterHost,
    IDeferredGestureLayoutHost
{
    private readonly MarkdownRendererControl _control;
    private Parts? _owners;

    internal PointerHosts(MarkdownRendererControl control)
    {
        _control = control;
    }

    /// <summary>The owners the hosts forward to.</summary>
    internal sealed record Parts(
        PublishedLayout Published,
        PointerInputRouter PointerInput,
        EmbedRealization Embeds,
        GeometryRecovery GeometryRecovery,
        InteractiveTextAdorner TextAdorner,
        BlockScrollNavigation BlockScroll,
        LinkActivation Links,
        LayoutCommitState LayoutCommit,
        LazyLayoutRealization LazyLayout,
        TouchSelectionRestore TouchRestore,
        InteractionReset InteractionReset,
        PointerInteractionResume PointerResume,
        KeyboardFocusModel Focus,
        HorizontalOverflowScrollVisuals OverflowScroll,
        PendingFocusGeometryWork FocusGeometryWork,
        RendererFocusEntry FocusEntry,
        FocusRingPresenter FocusRing);

    internal void Connect(Parts owners) => _owners = owners;

    private Parts Owners => _owners ??
        throw new InvalidOperationException("The renderer used its PointerHosts before connecting them.");

    void IPointerInputSink.DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
        => Owners.PointerInput.DispatchPointerInput(kind, sender, args);

    bool IPointerInputSink.DispatchRecognizedGesture(PointerInputKind kind, Point point, Microsoft.UI.Input.PointerDeviceType device)
        => Owners.PointerInput.DispatchRecognizedGesture(kind, point, device);

    bool IPointerInteractionHost.IsSelectionEnabled => _control.IsSelectionEnabled;

    bool IPointerInteractionHost.IsCurrentScrollTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision)
        => Owners.BlockScroll.IsCurrentTarget(snapshot, snapshotGeneration, layoutRevision);

    void IPointerInteractionHost.CancelPendingTouchSelectionRestore()
        => Owners.TouchRestore.Cancel();

    void IPointerInteractionHost.RememberFocusResumePoint(Point documentPoint)
        => Owners.FocusGeometryWork.RememberResumePoint(documentPoint);

    void IPointerInteractionHost.FocusRendererForPointerInteraction() => Owners.FocusEntry.FocusWithoutPromotion();

    void IPointerInteractionHost.ClearKeyboardFocusItem()
    {
        if (Owners.Focus.FocusedIndex >= 0)
        {
            Owners.Focus.ClearFocus();
            Owners.FocusRing.Update();
        }
    }

    bool IPointerInteractionHost.IsPointOverCodeBlockAction(Point point) => Owners.Embeds.HitTargets.IsPointOverCodeBlockAction(point);

    bool IPointerInteractionHost.IsPointOverEmbed(Point point) => Owners.Embeds.HitTargets.IsPointOverEmbed(point);

    bool IPointerInteractionHost.TryHitTestEmbed(Point point, out DocumentPosition position)
        => Owners.Embeds.HitTargets.TryHitTest(
            point, _control.FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft, out position);

    bool IPointerInteractionHost.TryGetVectorPointerState(Point point, out bool linked, out bool selectable)
        => TryGetVectorPointerState(point, out linked, out selectable);

    void IPointerInteractionHost.InvalidateInteractiveTextAdorner() => Owners.TextAdorner.Invalidate();

    void IPointerInteractionHost.UpdateSelectionOverlay() => Owners.TextAdorner.UpdateSelectionOverlay();

    void IPointerInteractionHost.ClearSelectionForExternalInteraction() => Owners.InteractionReset.ClearSelectionForExternalInteraction();

    void IPointerInteractionHost.ResumePendingRebuildAfterPointerInteraction()
        => Owners.PointerResume.ResumePendingWork();

    void IHorizontalOverflowPointerHost.RefreshHorizontalOverflowVisualState(IHorizontalOverflowBox target, double oldOffset)
        => Owners.OverflowScroll.Refresh(target, oldOffset);

    bool IPointerInputRouterHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void IPointerInputRouterHost.ResumePendingRebuildAfterPointerInteraction()
        => Owners.PointerResume.ResumePendingWork();

    void IPointerInputRouterHost.ClearSelectionForExternalInteraction(bool resetClickTracking, bool resetDeferredPointerInput)
        => Owners.InteractionReset.ClearSelectionForExternalInteraction(resetClickTracking, resetDeferredPointerInput);

    bool IPointerInputRouterHost.ActivateLinkIntent(
        LinkActivationIntent intent,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
        => Owners.Links.ActivateIntent(intent, snapshot, snapshotGeneration, layoutRevision);

    double IDeferredGestureLayoutHost.RasterizationScale => _control.XamlRoot?.RasterizationScale ?? 1;

    bool IDeferredGestureLayoutHost.IsPointOverEmbed(Point point) => Owners.Embeds.HitTargets.IsPointOverEmbed(point);

    bool IDeferredGestureLayoutHost.IsPointOverCodeBlockAction(Point point) => Owners.Embeds.HitTargets.IsPointOverCodeBlockAction(point);

    bool IDeferredGestureLayoutHost.IsLayoutPublicationPending(LayoutSnapshot snapshot) =>
        Owners.LayoutCommit.IsPublishing ||
        Owners.LayoutCommit.Pending is not null || Owners.LayoutCommit.AppliedRevision < snapshot.LayoutRevision;

    bool IDeferredGestureLayoutHost.IsLazyLayoutBandCovered(LayoutSnapshot snapshot, LazyLayoutBand band) =>
        Owners.LazyLayout.IsBandCovered(snapshot, band);

    bool IDeferredGestureLayoutHost.HasSupersedingLazyLayoutRequest(LayoutSnapshot snapshot) =>
        Owners.LazyLayout.HasSupersedingRequest(snapshot);

    void IDeferredGestureLayoutHost.QueueLazyLayoutForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor)
        => Owners.LazyLayout.QueueForBand(snapshot, band, visibleVectorBand, preserveScrollAnchor);

    private bool TryGetVectorPointerState(
        Point point,
        out bool linked,
        out bool selectable)
    {
        if (Owners.Published.Snapshot is not null)
        {
            foreach (BlockBox block in Owners.Published.Snapshot.Blocks)
            {
                if (DocumentHitTesting.TryGetVectorPointerStateInBlock(block, point, out linked, out selectable))
                    return true;
            }
        }

        linked = false;
        selectable = false;
        return false;
    }
}
