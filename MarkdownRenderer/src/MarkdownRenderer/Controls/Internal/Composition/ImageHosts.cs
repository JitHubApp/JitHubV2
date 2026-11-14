using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.Input.Hover;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Commit;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.Controls.Internal.Rendering;
using MarkdownRenderer.Controls.Internal.Selection.Adorner;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Composition;

/// <summary>
/// The image owners’ hosts: subscriptions, residency, raster placement, relayout and load completion.
/// </summary>
/// <remarks>
/// The owners these hosts forward to are constructed after the owners that
/// need the hosts, so the control connects them once its owners exist.
/// </remarks>
internal sealed class ImageHosts :
    IImageSubscriptionsHost,
    IImageResidencyHost,
    IRasterPlacementHost,
    IImageRelayoutHost,
    IImageLoadCompletionHost
{
    private readonly MarkdownRendererControl _control;
    private Parts? _owners;

    internal ImageHosts(MarkdownRendererControl control)
    {
        _control = control;
    }

    /// <summary>The owners the hosts forward to.</summary>
    internal sealed record Parts(
        RendererEvents Events,
        PointerHoverState Hover,
        PointerCursorPresenter Cursor,
        AbbreviationTooltipPresenter AbbreviationTooltip,
        SelectionAdornerPresenter Adorner,
        CanvasDeviceRecovery DeviceRecovery,
        RecoveryClock RecoveryClock,
        SnapshotRetirementTracker SnapshotRetirements,
        PipelineCancellationRetirement CancellationRetirement,
        RebuildScheduler Rebuild,
        CanvasInvalidation CanvasInvalidation,
        GeometryRecovery GeometryRecovery,
        InteractiveTextAdorner TextAdorner,
        RecoveryClockWork RecoveryWork,
        VisibleWorkScheduler VisibleWork,
        ScrollViewTracking Scrolling,
        RendererAutomation Automation,
        ImageLifetimeScope ImageLifetime,
        LayoutCommitState LayoutCommit,
        object ImagePreparationOwnerToken,
        RebuildInputCapture InputCapture,
        SnapshotExtent Extent,
        MarkdownViewSurface Surface,
        PointerInputRouter PointerInput,
        LayoutCommitPublisher LayoutPublisher,
        RasterPlacementPublisher RasterPlacement);

    internal void Connect(Parts owners) => _owners = owners;

    private Parts Owners => _owners ??
        throw new InvalidOperationException("The renderer used its ImageHosts before connecting them.");

    void IImageSubscriptionsHost.StartRecoveryClock() => Owners.RecoveryClock.Start();

    Microsoft.UI.Dispatching.DispatcherQueue? IImageResidencyHost.DispatcherQueue => _control.DispatcherQueue;

    IMarkdownPerformanceSession? IImageResidencyHost.PerformanceSession => _control.PerformanceSession;

    long IImageResidencyHost.AppliedLazyLayoutRevision => Owners.LayoutCommit.AppliedRevision;

    void IImageResidencyHost.InvalidateCanvas() => Owners.CanvasInvalidation.Invalidate();

    bool IRasterPlacementHost.HasCompetingLayoutWork =>
        Owners.DeviceRecovery.IsQueued || Owners.Rebuild.HasPendingOrBuilding || _control.HasPendingLazyLayoutWork;

    float IRasterPlacementHost.LayoutWidth => (float)Math.Max(50, _control.ActualWidth);

    object IRasterPlacementHost.ImagePreparationOwner => Owners.ImagePreparationOwnerToken;

    CancellationToken IRasterPlacementHost.ImageLifetime => Owners.ImageLifetime.Token;

    bool IRasterPlacementHost.IsScrollViewIntermediate => Owners.Scrolling.IsIntermediate;

    bool IRasterPlacementHost.MatchesLayoutInputs(MarkdownLayoutContext context) =>
        Owners.InputCapture.MatchesLayoutInputs(context);

    void IRasterPlacementHost.RetireSnapshot(LayoutSnapshot snapshot) => Owners.SnapshotRetirements.Retire(snapshot);

    void IRasterPlacementHost.ForgetHoverTarget()
    {
        Owners.Hover.Clear();
        Owners.AbbreviationTooltip.ForgetTarget();
    }

    void IRasterPlacementHost.ScheduleVisibleCodeBlockHighlighting() => Owners.VisibleWork.ScheduleCodeBlockHighlighting();

    void IRasterPlacementHost.NotifyAutomationSnapshotChanged() => Owners.Automation.NotifySnapshotChanged();

    void IRasterPlacementHost.HideAbbreviationTooltip() => Owners.AbbreviationTooltip.Hide();

    void IRasterPlacementHost.ResetCursor() => Owners.Cursor.SetShape(null);

    void IRasterPlacementHost.ApplySnapshotSize(Size size) => Owners.Extent.Apply(size);

    void IRasterPlacementHost.RefreshInteractiveTextAdornerAfterLayoutChange() =>
        Owners.TextAdorner.RefreshAfterLayoutChange();

    void IRasterPlacementHost.InvalidateAutomationLayout() => Owners.Automation.InvalidateLayout();

    void IRasterPlacementHost.InvalidateCanvas() => Owners.CanvasInvalidation.Invalidate();

    void IRasterPlacementHost.RaiseImageUnavailable(
        string url, MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgReason) =>
        Owners.Events.RaiseImageUnavailable(url, reason, svgReason);

    IMarkdownPerformanceSession? IImageRelayoutHost.PerformanceSession => _control.PerformanceSession;

    float IImageRelayoutHost.LayoutWidth => (float)Math.Max(50, _control.ActualWidth);

    double? IImageRelayoutHost.VerticalScrollOffset => Owners.Surface.Scroll?.VerticalOffset;

    bool IImageRelayoutHost.HasSnapshotInteraction => Owners.PointerInput.HasSnapshotInteraction;

    RasterPlacementInputs? IImageRelayoutHost.TryCreateRasterPlacementInputs(LayoutSnapshot snapshot, bool optedIn) =>
        Owners.RasterPlacement.TryCreateInputs(snapshot, optedIn);

    bool IImageRelayoutHost.TryPublishRasterPlacement(LayoutSnapshot snapshot, RasterPlacementPlan candidate, long generation) =>
        Owners.RasterPlacement.TryPublish(snapshot, candidate, generation);

    void IImageRelayoutHost.PublishRelayout(LayoutSnapshot snapshot, (int BlockIndex, double OffsetFromTop)? anchor) =>
        Owners.LayoutPublisher.Apply(snapshot, anchor);

    bool IImageRelayoutHost.DefersToRebuild() => Owners.Rebuild.DefersToRebuild();

    bool IImageRelayoutHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void IImageRelayoutHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    void IImageRelayoutHost.StopRecoveryClock(bool force) => Owners.RecoveryWork.Stop(force);

    Task IImageRelayoutHost.RetireCancellationTokenSource(RetirableCancellationTokenSource? cts, Task? lifetime) =>
        Owners.CancellationRetirement.Retire(cts, lifetime);

    IMarkdownPerformanceSession? IImageLoadCompletionHost.PerformanceSession => _control.PerformanceSession;

    float IImageLoadCompletionHost.LayoutWidth => (float)Math.Max(50, _control.ActualWidth);

    bool IImageLoadCompletionHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void IImageLoadCompletionHost.NotifyImageStatusChanged(ImageBox image) =>
        Owners.Automation.FindPeer()?
            .NotifyImageStatusChanged(image);

    void IImageLoadCompletionHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    void IImageLoadCompletionHost.RecordRejectedCrossThreadFullRebuild() =>
        Owners.Rebuild.RecordRejectedCrossThread(RebuildReason.Full);

    void IImageLoadCompletionHost.InvalidatePaint()
    {
        Owners.CanvasInvalidation.Invalidate();
        Owners.Adorner.Invalidate();
    }
}
