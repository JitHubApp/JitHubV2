using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Providers;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Attaches the loaded view to its window, providers and environment, and
/// releases everything the loaded view holds when it unloads, in the order
/// the native resources require.
/// </summary>
internal sealed class ViewAttachment
{
    private readonly MarkdownRendererControl _control;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly PointerCursorPresenter _cursor;
    private readonly AbbreviationTooltipPresenter _abbreviationTooltip;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly CanvasDeviceRecovery _deviceRecovery;
    private readonly SelectionDismissalHook _selectionDismissal;
    private readonly SelectionDragShield _dragShield;
    private readonly SelectionHandlesPresenter _handles;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly PointerInputRouter _pointerInput;
    private readonly EmbedRealization _embeds;
    private readonly WindowDestroyingWatch _windowDestroying;
    private readonly SnapshotRetirementTracker _snapshotRetirements;
    private readonly RebuildScheduler _rebuild;
    private readonly CanvasTilePainter _painter;
    private readonly CanvasEvents _canvasEvents;
    private readonly GeometryRecovery _geometryRecovery;
    private readonly RecoveryClockWork _recoveryWork;
    private readonly ScrollViewTracking _scrolling;
    private readonly DeferredSceneReadiness _deferredScenes;
    private readonly BlockScrollNavigation _blockScroll;
    private readonly RendererAutomation _automation;
    private readonly ImageLifetimeScope _imageLifetime;
    private readonly MarkdownAutomationDocumentGeneration _automationDocumentGenerations;
    private readonly LayoutCommitState _layoutCommit;
    private readonly ImageRelayout _imageRelayout;
    private readonly VectorCapacityRetry _vectorCapacity;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly LayoutWidthWatch _width;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchSelectionRestore _touchRestore;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly FocusRingPresenter _focusRing;
    private readonly PerformanceSessionSubscription _performanceSession;
    private readonly SvgRendererSubscription _svgRenderer;
    private readonly ThemeSubscription _theme;
    private readonly EnvironmentSubscription _environment;
    private readonly ImagePlanSet _imagePlans;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImageResidency _imageResidency;
    private readonly ImageSourcePrefetch _imagePrefetch;
    private readonly CodeBlockHighlighting _highlighting;

    internal ViewAttachment(
        MarkdownRendererControl control,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        PointerCursorPresenter cursor,
        AbbreviationTooltipPresenter abbreviationTooltip,
        SelectionAdornerPresenter adorner,
        CanvasDeviceRecovery deviceRecovery,
        SelectionDismissalHook selectionDismissal,
        SelectionDragShield dragShield,
        SelectionHandlesPresenter handles,
        SelectionContextMenuPresenter contextMenu,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        PointerInputRouter pointerInput,
        EmbedRealization embeds,
        WindowDestroyingWatch windowDestroying,
        SnapshotRetirementTracker snapshotRetirements,
        RebuildScheduler rebuild,
        CanvasTilePainter painter,
        CanvasEvents canvasEvents,
        GeometryRecovery geometryRecovery,
        RecoveryClockWork recoveryWork,
        ScrollViewTracking scrolling,
        DeferredSceneReadiness deferredScenes,
        BlockScrollNavigation blockScroll,
        RendererAutomation automation,
        ImageLifetimeScope imageLifetime,
        MarkdownAutomationDocumentGeneration automationDocumentGenerations,
        LayoutCommitState layoutCommit,
        ImageRelayout imageRelayout,
        VectorCapacityRetry vectorCapacity,
        LazyLayoutRealization lazyLayout,
        LayoutWidthWatch width,
        MarkdownTouchSelectionSession touchSelection,
        TouchSelectionRestore touchRestore,
        TouchTapSuppressionPolicy touchTapSuppression,
        HorizontalOverflowScrollVisuals overflowScroll,
        FocusRingPresenter focusRing,
        PerformanceSessionSubscription performanceSession,
        SvgRendererSubscription svgRenderer,
        ThemeSubscription theme,
        EnvironmentSubscription environment,
        ImagePlanSet imagePlans,
        ImageSubscriptions imageSubscriptions,
        ImageResidency imageResidency,
        ImageSourcePrefetch imagePrefetch,
        CodeBlockHighlighting highlighting)
    {
        _control = control;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _cursor = cursor;
        _abbreviationTooltip = abbreviationTooltip;
        _adorner = adorner;
        _deviceRecovery = deviceRecovery;
        _selectionDismissal = selectionDismissal;
        _dragShield = dragShield;
        _handles = handles;
        _contextMenu = contextMenu;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _pointerInput = pointerInput;
        _embeds = embeds;
        _windowDestroying = windowDestroying;
        _snapshotRetirements = snapshotRetirements;
        _rebuild = rebuild;
        _painter = painter;
        _canvasEvents = canvasEvents;
        _geometryRecovery = geometryRecovery;
        _recoveryWork = recoveryWork;
        _scrolling = scrolling;
        _deferredScenes = deferredScenes;
        _blockScroll = blockScroll;
        _automation = automation;
        _imageLifetime = imageLifetime;
        _automationDocumentGenerations = automationDocumentGenerations;
        _layoutCommit = layoutCommit;
        _imageRelayout = imageRelayout;
        _vectorCapacity = vectorCapacity;
        _lazyLayout = lazyLayout;
        _width = width;
        _touchSelection = touchSelection;
        _touchRestore = touchRestore;
        _touchTapSuppression = touchTapSuppression;
        _overflowScroll = overflowScroll;
        _focusRing = focusRing;
        _performanceSession = performanceSession;
        _svgRenderer = svgRenderer;
        _theme = theme;
        _environment = environment;
        _imagePlans = imagePlans;
        _imageSubscriptions = imageSubscriptions;
        _imageResidency = imageResidency;
        _imagePrefetch = imagePrefetch;
        _highlighting = highlighting;
    }

    internal void Load()
    {
        if (_lifetime.IsDisposed)
            return;

        _lifetime.MarkLoaded();
        // RemoveFromVisualTree physically removes the Win2D child from the root.
        // Recreate it for a retained view; the old native child and its input
        // delegates must not retain the unloaded view through a WinRT cycle.
        _canvasEvents.EnsureCanvas();
        _windowDestroying.Attach(_control.XamlRoot);
        _canvasEvents.AttachRenderHandlers();
        _imageLifetime.Renew();
        _selectionDismissal.Register();
        _environment.Acquire();
        _width.Attach();
        _theme.Attach();
        _svgRenderer.Attach();
        _performanceSession.Attach();
        _rebuild.Request(RebuildReason.Full);
    }

    internal void Unload()
    {
        if (_lifetime.IsUnloaded)
            return;

        // Set before any unsubscription so dispatcher-queued lambdas
        // (e.g. from image load completions) that are already in-flight know
        // not to call RequestRebuild after we've torn down.
        _lifetime.MarkUnloaded();
        _touchRestore.Cancel();
        // Keep the recognizer barrier while the owner epoch is still intact.
        // A queued Cancel must apply its per-contact selection disposition
        // before that identity is discarded; a queued Release keeps selection.
        _touchTapSuppression.PrepareForReset();
        _deviceRecovery.Stop();
        _recoveryWork.Stop(force: true);
        _performanceSession.Detach();
        _pointerInput.Reset();
        _touchTapSuppression.Reset();
        // An unloaded view is reusable and no longer belongs to _control window.
        // Do not let a former host destroy it, or retain weak-event wrappers
        // for every virtualized view until the window eventually closes.
        _windowDestroying.Detach();
        // Win2D can retain queued native draw/resource callbacks after XAML starts
        // removing _control control. Stop those callbacks before releasing any snapshot
        // or DirectWrite resources they may reference.
        _canvasEvents.DetachRenderHandlers();
        _canvasEvents.DetachInputHandlers();
        // Win2D requires RemoveFromVisualTree from the containing control's
        // Unloaded boundary. At _control point XAML has begun detaching the renderer,
        // so each Win2D child can unregister its XamlRoot callback without racing a
        // subsequent window resize.
        _surface.RemoveCanvas();
        ReleaseLoadedResources();
    }

    private void ReleaseLoadedResources()
    {
        _vectorCapacity.Reset();
        _geometryRecovery.ClearPendingFocusGeometryWork();
        _deferredScenes.CancelRefreshAfterPaint();
        _painter.CancelDeferredSceneProbe();
        _contextMenu.Close();
        _horizontalOverflowDrag.Clear();
        _abbreviationTooltip.Hide();
        _abbreviationTooltip.ReleaseTimers();
        _width.Detach();
        _theme.Detach();
        _svgRenderer.Detach();
        _environment.Release();
        _selectionDismissal.Unregister();
        _rebuild.Retire();
        _lazyLayout.Cancel();
        _layoutCommit.ClearPending();
        _imageLifetime.Retire();
        _imageRelayout.Retire();
        _lazyLayout.ResetScrollVelocity();
        _blockScroll.ClearPending();
        _automation.ForgetQueuedSelectionChanged();
        _overflowScroll.ClearAutomationNotifications();
        _highlighting.Retire(restart: false);
        _scrolling.Detach();
        // Unsubscribe all in-flight image load handlers before clearing the plan
        // list; otherwise an async load completing after unload fires
        // a completion → RequestRebuild on a torn-down control, causing a
        // zombie rebuild cycle that re-subscribes handlers indefinitely.
        _imageSubscriptions.UnsubscribeAll();
        // Tear down embed plans before clearing the overlay so block embed
        // factories get RecycleBlock callbacks and inline embeds release
        // their Run.RealizedElement references — otherwise hosted controls
        // and their event handlers would leak past detach.
        _embeds.DerealizeAll();
        _embeds.ClearPlans();
        _embeds.ClearCopyButtonPool();
        _imagePlans.Clear();
        _imagePlans.Publication = null;
        _imageResidency.Reset();
        _imagePrefetch.Reset();
        _performanceSession.ReleaseDocumentScope();
        _embeds.ClearHostedElementHitTargets();
        _handles.ResetDrag();
        _touchTapSuppression.Reset();
        _touchSelection.Reset();
        _dragShield.SetActive(false);
        // Release native resources & hosted embeds so re-attaching the
        // control to a new visual parent doesn't leak DirectWrite layouts
        // or keep stale FrameworkElements alive. A failed XAML detach must not
        // abort the remaining one-shot cleanup or snapshot retirement below.
        _adorner.TryReleaseSurface();
        _surface.Overlay?.Children.Clear();
        // No refresh frame follows an unload, so dispose the detached adorner
        // surfaces, the child visual and the graphics device here.
        _adorner.DisposeForUnload();
        _cursor.Release();
        _handles.ReleaseForUnload();
        _focusRing.ForgetEvictedRing();
        var snap = _published.Snapshot;
        _published.Snapshot = null;
        _automationDocumentGenerations.Invalidate();
        _automation.NotifySnapshotChanged();
        _snapshotRetirements.Retire(snap);
    }
}
