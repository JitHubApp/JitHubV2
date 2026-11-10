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
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Controls.Threading;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Controls.Composition;

/// <summary>
/// The layout and paint owners’ hosts: vector capacity, lazy layout, layout commit, focusable items, the recovery clock, device recovery and tile paint.
/// </summary>
/// <remarks>
/// The owners these hosts forward to are constructed after the owners that
/// need the hosts, so the control connects them once its owners exist.
/// </remarks>
internal sealed class LayoutHosts :
    IVectorCapacityRetryHost,
    ILazyLayoutHost,
    ILayoutCommitHost,
    IFocusableItemsPublicationHost,
    IRecoveryClockHost,
    IRecoveryClockWorkHost,
    ICanvasDeviceRecoveryHost,
    ICanvasTilePainterHost
{
    private readonly MarkdownRendererControl _control;
    private readonly IMarkdownUiScheduler? _uiScheduler;
    private Parts? _owners;

    /// <param name="control">The renderer the hosts serve.</param>
    /// <param name="uiScheduler">The renderer's UI thread; none when it has no dispatcher.</param>
    internal LayoutHosts(MarkdownRendererControl control, IMarkdownUiScheduler? uiScheduler)
    {
        _control = control;
        _uiScheduler = uiScheduler;
    }

    /// <summary>The owners the hosts forward to.</summary>
    internal sealed record Parts(
        CanvasDeviceRecovery DeviceRecovery,
        PipelineCancellationRetirement CancellationRetirement,
        RebuildScheduler Rebuild,
        CanvasInvalidation CanvasInvalidation,
        GeometryRecovery GeometryRecovery,
        InteractiveTextAdorner TextAdorner,
        RecoveryClockWork RecoveryWork,
        VisibleWorkScheduler VisibleWork,
        ScrollViewTracking Scrolling,
        DeferredSceneReadiness DeferredScenes,
        BlockScrollNavigation BlockScroll,
        RendererAutomation Automation,
        LazyLayoutRealization LazyLayout,
        SnapshotExtent Extent,
        LayoutCommitPublisher LayoutPublisher,
        HorizontalOverflowScrollVisuals OverflowScroll,
        PendingFocusGeometryWork FocusGeometryWork,
        FocusableItemsPublication FocusableItems,
        PointerInputRouter PointerInput);

    internal void Connect(Parts owners) => _owners = owners;

    private Parts Owners => _owners ??
        throw new InvalidOperationException("The renderer used its LayoutHosts before connecting them.");

    Microsoft.UI.Dispatching.DispatcherQueue? IVectorCapacityRetryHost.DispatcherQueue => _control.DispatcherQueue;

    IMarkdownPerformanceSession? IVectorCapacityRetryHost.PerformanceSession => _control.PerformanceSession;

    bool IVectorCapacityRetryHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void IVectorCapacityRetryHost.QueueLazyLayoutForViewport(bool forceCapacityRetry) =>
        Owners.LazyLayout.QueueForViewport(forceCapacityRetry);

    IMarkdownUiScheduler? ILazyLayoutHost.UiScheduler => _uiScheduler;

    IMarkdownPerformanceSession? ILazyLayoutHost.PerformanceSession => _control.PerformanceSession;

    object ILazyLayoutHost.DeferredSceneOwner => _control;

    long ILazyLayoutHost.PipelineGeneration => Owners.Rebuild.Generation;

    CancellationToken ILazyLayoutHost.PipelineToken => Owners.Rebuild.Token;

    bool ILazyLayoutHost.IsScrollViewIntermediate => Owners.Scrolling.IsIntermediate;

    bool ILazyLayoutHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void ILazyLayoutHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    void ILazyLayoutHost.HandleDeviceLost(Exception failure) => Owners.DeviceRecovery.HandleDeviceLost(failure);

    void ILazyLayoutHost.TryResumePendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration) =>
        Owners.BlockScroll.TryResumePending(snapshot, snapshotGeneration);

    void ILazyLayoutHost.NotifyVisibleDeferredSceneItemStatusMayHaveChanged() =>
        Owners.DeferredScenes.NotifyItemStatusMayHaveChanged();

    void ILazyLayoutHost.ScheduleVisibleDeferredScenes() => Owners.VisibleWork.ScheduleDeferredScenes();

    void ILazyLayoutHost.InvalidateAutomationLayout() => Owners.Automation.InvalidateLayout();

    void ILazyLayoutHost.InvalidateCanvas() => Owners.CanvasInvalidation.Invalidate();

    void ILazyLayoutHost.StopRecoveryClockIfIdle() => Owners.RecoveryWork.StopIfIdle();

    Task ILazyLayoutHost.RetireCancellationTokenSource(RetirableCancellationTokenSource? cts, Task? lifetime) =>
        Owners.CancellationRetirement.Retire(cts, lifetime);

    bool ILayoutCommitHost.IsScrollViewIntermediate => Owners.Scrolling.IsIntermediate;

    bool ILayoutCommitHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void ILayoutCommitHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    void ILayoutCommitHost.ApplySnapshotSize(Size size) => Owners.Extent.Apply(size);

    void ILayoutCommitHost.ScheduleVisibleCodeBlockHighlighting() => Owners.VisibleWork.ScheduleCodeBlockHighlighting();

    void ILayoutCommitHost.RefreshInteractiveTextAdornerAfterLayoutChange() =>
        Owners.TextAdorner.RefreshAfterLayoutChange();

    void ILayoutCommitHost.InvalidateAutomationLayout() => Owners.Automation.InvalidateLayout();

    void ILayoutCommitHost.InvalidateCanvas() => Owners.CanvasInvalidation.Invalidate();

    void ILayoutCommitHost.TryResumePendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration) =>
        Owners.BlockScroll.TryResumePending(snapshot, snapshotGeneration);

    Microsoft.UI.Dispatching.DispatcherQueue IFocusableItemsPublicationHost.DispatcherQueue => _control.DispatcherQueue;

    bool IFocusableItemsPublicationHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void IFocusableItemsPublicationHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    void IFocusableItemsPublicationHost.InvalidateAutomationLayout() => Owners.Automation.InvalidateLayout();

    void IRecoveryClockHost.ProcessRecoveryTick() => Owners.RecoveryWork.ProcessTick();

    bool IRecoveryClockWorkHost.HasPendingLazyLayout => Owners.LazyLayout.HasPendingWork;

    bool IRecoveryClockWorkHost.HasPendingOverflowScrollRetry => Owners.OverflowScroll.HasPendingRetry;

    bool IRecoveryClockWorkHost.HasPendingFocusGeometryWork => Owners.FocusGeometryWork.HasPendingWork;

    bool IRecoveryClockWorkHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void IRecoveryClockWorkHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    void IRecoveryClockWorkHost.PublishPendingLayout() => Owners.LayoutPublisher.PublishPending();

    void IRecoveryClockWorkHost.ProcessQueuedLazyLayout() => Owners.LazyLayout.ProcessQueued();

    void IRecoveryClockWorkHost.TryPublishLazyLayoutCompletion() => Owners.LazyLayout.TryPublishCompletion();

    void IRecoveryClockWorkHost.ProcessOverflowScrollRetry() => Owners.OverflowScroll.ProcessPendingRetry();

    void IRecoveryClockWorkHost.ProcessFocusGeometryWork() => Owners.FocusGeometryWork.Process();

    void IRecoveryClockWorkHost.TryPublishPendingFocusableItems() => Owners.FocusableItems.TryPublishPending();

    void IRecoveryClockWorkHost.NotifyLazyLayoutFailed(LayoutSnapshot snapshot) =>
        Owners.PointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);

    void ICanvasDeviceRecoveryHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    void ICanvasDeviceRecoveryHost.InvalidateCanvas() => Owners.CanvasInvalidation.Invalidate();

    Microsoft.UI.Dispatching.DispatcherQueue? ICanvasTilePainterHost.DispatcherQueue => _control.DispatcherQueue;

    bool ICanvasTilePainterHost.IsLoaded => _control.IsLoaded;

    bool ICanvasTilePainterHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void ICanvasTilePainterHost.ScheduleVisibleDeferredScenes() => Owners.VisibleWork.ScheduleDeferredScenes();
}
