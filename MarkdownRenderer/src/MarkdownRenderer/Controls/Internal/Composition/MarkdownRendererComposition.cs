using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Controls.Internal.Focus.Hosted;
using MarkdownRenderer.Controls.Internal.Highlighting;
using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.Input.Gestures;
using MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;
using MarkdownRenderer.Controls.Internal.Input.Hover;
using MarkdownRenderer.Controls.Internal.Input.Keyboard;
using MarkdownRenderer.Controls.Internal.Input.Links;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Controls.Internal.Navigation;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Commit;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.Providers;
using MarkdownRenderer.Controls.Internal.Readiness;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.Controls.Internal.Rendering;
using MarkdownRenderer.Controls.Internal.Selection;
using MarkdownRenderer.Controls.Internal.Selection.Adorner;
using MarkdownRenderer.Controls.Internal.Selection.ContextMenu;
using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;

namespace MarkdownRenderer.Controls.Internal.Composition;

/// <summary>
/// The owners of one <see cref="MarkdownRendererControl"/>, built in
/// dependency order and connected to the hosts that let earlier owners reach
/// later ones. The control keeps its public surface and reads these owners
/// from its property callbacks, public members and disposal.
/// </summary>
internal sealed class MarkdownRendererComposition
{
    internal MarkdownViewLifetime Lifetime { get; } = new();

    internal PublishedLayout Published { get; } = new();

    internal SnapshotRetirementTracker SnapshotRetirements { get; } = new(
        static exception => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] retirement failed: {exception.GetType().Name} " +
            $"(0x{exception.HResult:X8})."));

    internal MarkdownAutomationDocumentGeneration AutomationDocumentGenerations { get; } = new();

    internal LayoutCommitState LayoutCommit { get; } = new();

    internal PointerHoverState Hover { get; } = new();

    internal SelectionController Selection { get; } = new();

    internal MarkdownTouchSelectionSession TouchSelection { get; } = new();

    internal TouchTapSuppressionPolicy TouchTapSuppression { get; } = new();

    internal MarkdownViewSurface Surface { get; }

    internal MarkdownViewport Viewport { get; }

    internal WindowDestroyingWatch WindowDestroying { get; }

    internal RendererLocalization Localization { get; }

    internal RendererAutomation Automation { get; }

    internal AutomationScrolling AutomationScrolling { get; }

    internal RebuildScheduler Rebuild { get; }

    internal RebuildPipeline Pipeline { get; }

    internal DocumentInputs DocumentInputs { get; }

    internal CanvasTilePainter Painter { get; }

    internal CanvasEvents CanvasEvents { get; }

    internal CanvasInvalidation CanvasInvalidation { get; }

    internal CanvasDeviceRecovery DeviceRecovery { get; }

    internal SyntheticDeviceReset DeviceReset { get; }

    internal RecoveryClockWork RecoveryWork { get; }

    internal VisibleWorkScheduler VisibleWork { get; }

    internal ScrollViewTracking Scrolling { get; }

    internal LazyLayoutRealization LazyLayout { get; }

    internal BlockScrollNavigation BlockScroll { get; }

    internal VisibleImageReadiness ImageReadiness { get; }

    internal DeferredSceneReadiness DeferredScenes { get; }

    internal EmbedRealization Embeds { get; }

    internal CodeBlockHighlighting Highlighting { get; }

    internal CodeBlockClipboard CodeBlockClipboard { get; }

    internal PresentationInputChanges InputChanges { get; }

    internal SelectionAdornerPresenter Adorner { get; }

    internal SelectionCommands SelectionCommands { get; }

    internal SelectionDragShield DragShield { get; }

    internal SelectionHandlesPresenter Handles { get; }

    internal SelectionChangeHandling SelectionChanges { get; }

    internal TouchSelectionRestore TouchRestore { get; }

    internal InteractionReset InteractionReset { get; }

    internal KeyboardInput Keyboard { get; }

    internal RendererFocusEvents FocusEvents { get; }

    internal HorizontalOverflowScrollVisuals OverflowScroll { get; }

    internal PerformanceSessionSubscription PerformanceSession { get; }

    internal SvgRendererSubscription SvgRenderer { get; }

    internal ThemeSubscription Theme { get; }

    internal EnvironmentSubscription Environment { get; }

    internal ViewAttachment Attachment { get; }

    /// <summary>Builds every owner of <paramref name="control"/> and connects their hosts.</summary>
    /// <param name="control">The renderer the owners serve.</param>
    /// <param name="ownsScrollViewport">True when the renderer creates its own scroll viewer.</param>
    /// <param name="diagnostics">The diagnostics the renderer runs with for its lifetime.</param>
    /// <param name="applyCursor">Sets the renderer's protected pointer cursor.</param>
    /// <remarks>This constructor stays whole because it is the composition root: one line per
    /// owner in dependency order, and readonly owners can be set only in a constructor.</remarks>
    internal MarkdownRendererComposition(
        MarkdownRendererControl control,
        bool ownsScrollViewport,
        MarkdownRendererDiagnosticOptions diagnostics,
        Action<InputCursor?> applyCursor)
    {
        IMarkdownUiScheduler? uiScheduler = DispatcherQueueUiScheduler.For(control.DispatcherQueue);
        var events = new RendererEvents(control, Lifetime, control.DispatcherQueue);
        var pipelineHosts = new PipelineHosts(control);
        var layoutHosts = new LayoutHosts(control, uiScheduler);
        var imageHosts = new ImageHosts(control);
        var focusHosts = new FocusHosts(control);
        var embedHosts = new EmbedHosts(control, uiScheduler);
        var pointerHosts = new PointerHosts(control);
        var selectionHosts = new SelectionHosts(control);
        var imagePrefetch = new ImageSourcePrefetch();
        var completedImageCarry = new CompletedImageCarryLedger();
        var imagePreparationOwnerToken = new object();
        // Hosted-focus readers consume only realization plans published for the
        // exact snapshot generation and layout revision. The attempt epoch also
        // invalidates a plan while a same-snapshot rebuild is being transferred.
        var hostedFocusPlan = new HostedFocusPlanPublication();
        var cancellationRetirement = new PipelineCancellationRetirement(SnapshotRetirements);
        var imageLifetime = new ImageLifetimeScope(cancellationRetirement);
        TouchRestore = new TouchSelectionRestore(Published, Selection, TouchSelection);
        Localization = new RendererLocalization(pipelineHosts, Published);
        Surface = new MarkdownViewSurface(ownsScrollViewport);
        WindowDestroying = new WindowDestroyingWatch(control, Lifetime);
        var recoveryClock = new RecoveryClock(layoutHosts, Lifetime, uiScheduler);
        Viewport = new MarkdownViewport(control, Surface);
        var vectorCapacity = new VectorCapacityRetry(layoutHosts, Lifetime, Published, Viewport);
        var cursor = new PointerCursorPresenter(applyCursor);
        var abbreviationTooltip = new AbbreviationTooltipPresenter(control, Surface, Lifetime, Hover);
        Adorner = new SelectionAdornerPresenter(selectionHosts, control, Surface, Lifetime, Published, Viewport, Selection, Hover);
        DeviceRecovery = new CanvasDeviceRecovery(layoutHosts, Lifetime, Published, Surface, Adorner, control.DispatcherQueue);
        CanvasInvalidation = new CanvasInvalidation(control, Lifetime, Surface, DeviceRecovery);
        SelectionCommands = new SelectionCommands(selectionHosts, Published, Selection);
        var selectionDismissal = new SelectionDismissalHook(selectionHosts, control, Surface, Lifetime, Published, Selection, SelectionCommands);
        var autoScroll = new SelectionDragAutoScroll(Surface, Viewport);
        DragShield = new SelectionDragShield(Surface, Viewport, pointerHosts);
        Handles = new SelectionHandlesPresenter(selectionHosts, pointerHosts, control, Surface, Lifetime, Published, Viewport, Selection, TouchSelection, TouchTapSuppression, Adorner, autoScroll);
        var contextMenu = new SelectionContextMenuPresenter(selectionHosts, Surface, Lifetime, Published, Viewport, Selection, TouchSelection, TouchTapSuppression, Handles, Adorner, SelectionCommands);
        var horizontalOverflowDrag = new HorizontalOverflowPointerDrag(pointerHosts, Surface, Published, TouchTapSuppression);
        var document = new DocumentPointerInteraction(pointerHosts, Surface, Published, Viewport, Selection, TouchSelection, TouchTapSuppression, horizontalOverflowDrag, Handles, autoScroll, DragShield, Adorner, contextMenu, new PointerHoverTracker(pointerHosts, Published, Hover, cursor, abbreviationTooltip), cursor, abbreviationTooltip);
        var extent = new SnapshotExtent(control, Surface, DragShield, document);
        var touchGestures = new TouchGestureInteraction(pointerHosts, control, Surface, Published, Viewport, Selection, TouchSelection, TouchTapSuppression, horizontalOverflowDrag, document);
        var pointerInput = new PointerInputRouter(pointerHosts, pointerHosts, control.DispatcherQueue, Surface, Lifetime, Published, Viewport, Selection, TouchSelection, TouchTapSuppression, Handles, document, horizontalOverflowDrag, touchGestures, contextMenu, Adorner, DragShield);
        Rebuild = new RebuildScheduler(pipelineHosts, Lifetime, Surface, pointerInput, cancellationRetirement);
        var canvasPointerEvents = new CanvasPointerEvents(Surface, pointerInput);
        Embeds = new EmbedRealization(embedHosts, embedHosts, Surface, Lifetime, Published, Viewport, control.DispatcherQueue, OnHostedEmbedKeyDown);
        // Lazy-load queue: all ImageBox instances in the current snapshot.
        // EnsureLoading() is called for each when its bounds enter the
        // viewport + LazyImageOverscanPx band.  Images already in the
        // in-memory cache start loading (no-op) immediately after build.
        var imagePlans = new ImagePlanSet(Lifetime, Published);
        var imageSubscriptions = new ImageSubscriptions(imageHosts, Lifetime, Published, imagePlans);
        var imageResidency = new ImageResidency(imageHosts, Lifetime, Published, imagePlans, Embeds);
        Highlighting = new CodeBlockHighlighting(embedHosts, Lifetime, Published);
        InputChanges = new PresentationInputChanges(Lifetime, Rebuild, Embeds, Highlighting, Handles);
        var focus = new KeyboardFocusModel(Lifetime, Published);
        var focusGeometry = new FocusGeometryReader(Lifetime, Published);
        var focusRing = new FocusRingPresenter(focusHosts, focus, focusGeometry, Surface, Lifetime, Published, control.DispatcherQueue);
        InteractionReset = new InteractionReset(Surface, Selection, TouchSelection, TouchRestore, TouchTapSuppression, pointerInput, document, Handles, autoScroll, DragShield, contextMenu, Adorner, Hover, abbreviationTooltip, cursor, focus, focusRing);
        var rasterPlacement = new RasterPlacementPublisher(imageHosts, Lifetime, Published, LayoutCommit, Surface, pointerInput, imageSubscriptions, imagePlans, imageResidency, Embeds, focus, focusRing, hostedFocusPlan, SnapshotRetirements);
        // A deferred focus-plan refresh is value-only: it never keeps a snapshot,
        // box, or native resource alive while a geometry lease is busy.
        var focusRefresh = new FocusableItemsRefreshRequest(Published);
        var focusableItems = new FocusableItemsPublication(layoutHosts, Lifetime, Published, focusRefresh, focus, recoveryClock);
        // Used when neither the control, document nor engine supplies a registry.
        var fallbackRegistry = new MarkdownExtensionRegistry();
        var layoutSetup = new RebuildLayoutSetup(pipelineHosts, diagnostics, SnapshotRetirements, imagePrefetch, completedImageCarry, imagePlans, Embeds, focusableItems, imagePreparationOwnerToken, fallbackRegistry);
        var hostedFocus = new HostedElementFocus(focusHosts, Lifetime, Published, focus, hostedFocusPlan, Embeds, focusRing);
        OverflowScroll = new HorizontalOverflowScrollVisuals(focusHosts, Surface, Lifetime, Published, Selection, focus, Embeds, control.DispatcherQueue);
        var reveal = new FocusedItemReveal(focusHosts, Surface, Lifetime, Published, Viewport, focus, OverflowScroll, control.DispatcherQueue);
        AutomationScrolling = new AutomationScrolling(Surface, reveal);
        var focusCommit = new FocusedItemCommit(focusHosts, Lifetime, Published, focus, focusRing, reveal, hostedFocus, Embeds, control.DispatcherQueue);
        var queuedFocusKeys = new SpatialFocusKeyQueue();
        var navigator = new KeyboardFocusNavigator(focusHosts, Lifetime, Published, focus, focusGeometry, focusRing, OverflowScroll, Embeds, focusCommit, queuedFocusKeys, control.DispatcherQueue);
        var focusGeometryWork = new PendingFocusGeometryWork(focusHosts, Lifetime, Published, focus, focusGeometry, focusRing, reveal, queuedFocusKeys, navigator);
        var focusEntry = new RendererFocusEntry(focusHosts, Lifetime, focus, focusRing, focusCommit, navigator, control.DispatcherQueue);
        var layoutPublisher = new LayoutCommitPublisher(layoutHosts, Lifetime, Published, LayoutCommit, Surface, recoveryClock, pointerInput, Embeds, imageSubscriptions, imagePlans, imageResidency, focus, focusRing, focusCommit, focusRefresh, hostedFocusPlan);
        LazyLayout = new LazyLayoutRealization(layoutHosts, Lifetime, Published, LayoutCommit, layoutPublisher, Surface, Viewport, recoveryClock, vectorCapacity, pointerInput, focus, focusCommit);
        BlockScroll = new BlockScrollNavigation(Lifetime, Published, Surface, Viewport, LazyLayout, reveal);
        var links = new LinkActivation(pipelineHosts, BlockScroll);
        Automation = new RendererAutomation(control, Lifetime, Surface, Viewport, Published, AutomationDocumentGenerations, Localization, Selection, focus, focusRing, focusCommit, focusEntry, links, Handles, OverflowScroll);
        DeferredScenes = new DeferredSceneReadiness(pipelineHosts, Lifetime, Published, Viewport, Rebuild);
        ImageReadiness = new VisibleImageReadiness(Published, imagePlans, Viewport);
        Painter = new CanvasTilePainter(layoutHosts, Lifetime, Published, Surface, LazyLayout, DeviceRecovery, Rebuild, DeferredScenes);
        DeviceReset = new SyntheticDeviceReset(Published, SnapshotRetirements, AutomationDocumentGenerations, LayoutCommit, Automation, Painter, LazyLayout, vectorCapacity);
        CanvasEvents = new CanvasEvents(Lifetime, Surface, canvasPointerEvents, Painter, Rebuild);
        var imageRelayout = new ImageRelayout(imageHosts, Lifetime, Published, LayoutCommit, imageSubscriptions, recoveryClock, uiScheduler);
        var imageLoadCompletion = new ImageLoadCompletion(imageHosts, Lifetime, Published, LayoutCommit, imageSubscriptions, imageRelayout, uiScheduler);
        imageSubscriptions.LoadCompleted += imageLoadCompletion.OnLoadCompleted;
        var geometryRecovery = new GeometryRecovery(Lifetime, Published, Surface, pointerInput, vectorCapacity, horizontalOverflowDrag, focusGeometryWork, OverflowScroll, focusRing, Adorner, LayoutCommit, imageSubscriptions, LazyLayout, imageRelayout, InteractionReset, DragShield, contextMenu, abbreviationTooltip, cursor, Handles, Automation, DeviceRecovery, Rebuild);
        VisibleWork = new VisibleWorkScheduler(control, Published, Highlighting, geometryRecovery);
        DocumentInputs = new DocumentInputs(pipelineHosts, uiScheduler, SnapshotRetirements);
        Keyboard = new KeyboardInput(control, Published, Selection, SelectionCommands, contextMenu, focus, focusRing, focusCommit, hostedFocus, navigator, geometryRecovery);
        RecoveryWork = new RecoveryClockWork(layoutHosts, Lifetime, Published, recoveryClock, LayoutCommit, imageSubscriptions, imageRelayout, focusRefresh);
        var textAdorner = new InteractiveTextAdorner(Surface, Published, Selection, TouchSelection, Hover, focus, Adorner, geometryRecovery);
        Scrolling = new ScrollViewTracking(Surface, Lifetime, Viewport, LazyLayout, Adorner, textAdorner, Embeds, VisibleWork);
        PerformanceSession = new PerformanceSessionSubscription(control, Lifetime, SnapshotRetirements, Rebuild, Selection, Adorner, textAdorner);
        SvgRenderer = new SvgRendererSubscription(control, Lifetime, Rebuild, CanvasInvalidation);
        Theme = new ThemeSubscription(control, Lifetime, Rebuild);
        Environment = new EnvironmentSubscription(control, Lifetime, Rebuild);
        var replacementPublisher = new ReplacementPublisher(control, Published, Surface, Rebuild, TouchRestore, AutomationDocumentGenerations, Selection, LazyLayout, LayoutCommit, Scrolling, Painter, Localization, focusRefresh, vectorCapacity, geometryRecovery, imageSubscriptions, imageRelayout, BlockScroll, imagePlans, focus);
        var width = new LayoutWidthWatch(control, Rebuild);
        var inputCapture = new RebuildInputCapture(control, Published, imagePreparationOwnerToken, DocumentInputs, Localization, Environment, fallbackRegistry);
        var replacementPresentation = new ReplacementPresentation(Surface, SnapshotRetirements, Rebuild, LayoutCommit, layoutPublisher, DocumentInputs, extent, Scrolling, Hover, Selection, TouchRestore, contextMenu, DragShield, Adorner, textAdorner, Handles, Embeds, imageSubscriptions, imagePlans, imageResidency, focus, focusRing, focusCommit, Highlighting, VisibleWork, CanvasInvalidation, Automation, DeferredScenes);
        Pipeline = new RebuildPipeline(control, control, Lifetime, Surface, Viewport, Rebuild, inputCapture, layoutSetup, width, imagePrefetch, Automation, replacementPublisher, replacementPresentation);
        CodeBlockClipboard = new CodeBlockClipboard(control, Embeds, events);
        var visibleImages = new VisibleImageLoading(control, LazyLayout, imageResidency, imagePlans, Embeds);
        var pointerResume = new PointerInteractionResume(Lifetime, Rebuild, imageRelayout);
        selectionHosts.Connect(new SelectionHosts.Parts(
            events, Published, DeviceRecovery, Handles, pointerInput, Embeds, Rebuild, geometryRecovery,
            textAdorner, TouchRestore, Localization, InteractionReset, pointerResume, focus, focusGeometry,
            focusEntry));
        pointerHosts.Connect(new PointerHosts.Parts(
            Published, pointerInput, Embeds, geometryRecovery, textAdorner, BlockScroll, links, LayoutCommit,
            LazyLayout, TouchRestore, InteractionReset, pointerResume, focus, OverflowScroll,
            focusGeometryWork, focusEntry, focusRing));
        embedHosts.Connect(new EmbedHosts.Parts(
            events, Published, cancellationRetirement, Rebuild, CanvasInvalidation, VisibleWork, Automation,
            layoutPublisher, rasterPlacement, LazyLayout, hostedFocusPlan, Localization, CodeBlockClipboard,
            visibleImages, hostedFocus, imagePlans, imageSubscriptions, Embeds, Viewport));
        focusHosts.Connect(new FocusHosts.Parts(
            Surface, Published, Viewport, recoveryClock, Rebuild, geometryRecovery, textAdorner, links,
            Automation, LayoutCommit, LazyLayout, focusGeometryWork, focusRing));
        imageHosts.Connect(new ImageHosts.Parts(
            events, Hover, cursor, abbreviationTooltip, Adorner, DeviceRecovery, recoveryClock,
            SnapshotRetirements, cancellationRetirement, Rebuild, CanvasInvalidation, geometryRecovery,
            textAdorner, RecoveryWork, VisibleWork, Scrolling, Automation, imageLifetime, LayoutCommit,
            imagePreparationOwnerToken, inputCapture, extent, Surface, pointerInput, layoutPublisher,
            rasterPlacement));
        layoutHosts.Connect(new LayoutHosts.Parts(
            DeviceRecovery, cancellationRetirement, Rebuild, CanvasInvalidation, geometryRecovery, textAdorner,
            RecoveryWork, VisibleWork, Scrolling, DeferredScenes, BlockScroll, Automation, LazyLayout, extent,
            layoutPublisher, OverflowScroll, focusGeometryWork, focusableItems, pointerInput));
        pipelineHosts.Connect(new PipelineHosts.Parts(
            events, DocumentInputs, Automation, imageLifetime, imageRelayout, LazyLayout, Pipeline,
            TouchRestore, InteractionReset, PerformanceSession, Environment, Rebuild, Embeds, Highlighting));
        FocusEvents = new RendererFocusEvents(control, contextMenu, document, focus, focusEntry, focusRing);
        SelectionChanges = new SelectionChangeHandling(selectionDismissal, cursor, Rebuild, textAdorner, Automation, Selection, TouchRestore, TouchTapSuppression, InteractionReset);
        Attachment = new ViewAttachment(control, Surface, Lifetime, Published, cursor, abbreviationTooltip, Adorner, DeviceRecovery, selectionDismissal, DragShield, Handles, contextMenu, horizontalOverflowDrag, pointerInput, Embeds, WindowDestroying, SnapshotRetirements, Rebuild, Painter, CanvasEvents, geometryRecovery, RecoveryWork, Scrolling, DeferredScenes, BlockScroll, Automation, imageLifetime, AutomationDocumentGenerations, LayoutCommit, imageRelayout, vectorCapacity, LazyLayout, width, TouchSelection, TouchRestore, TouchTapSuppression, OverflowScroll, focusRing, PerformanceSession, SvgRenderer, Theme, Environment, imagePlans, imageSubscriptions, imageResidency, imagePrefetch, Highlighting);
    }

    // Embeds subscribe hosted elements before the keyboard owner exists.
    private void OnHostedEmbedKeyDown(object sender, KeyRoutedEventArgs e) => Keyboard.OnHostedEmbedKeyDown(sender, e);
}
