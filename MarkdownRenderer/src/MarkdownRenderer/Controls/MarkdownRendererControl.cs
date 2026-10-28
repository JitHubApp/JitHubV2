using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Markdig;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The native Win2D + DirectWrite markdown renderer. Hosts a
/// <see cref="CanvasVirtualControl"/> for paint and a sibling <see cref="Canvas"/>
/// overlay for hosted WinUI embeds.
/// </summary>
[Obsolete(
    "Use MarkdownScrollView for an owned viewport or MarkdownDocumentView for an ancestor-owned viewport.",
    DiagnosticId = "MR1001",
    UrlFormat = "https://github.com/JitHubApp/JitHubV2/tree/main/docs/markdown-renderer#{0}")]
public partial class MarkdownRendererControl : UserControl, IDisposable, IMarkdownEnvironmentListener,
    IMarkdownPerformanceLegacyParser
{
    private readonly bool _ownsScrollViewport;
    private AppWindow? _windowLifecycleAppWindow;
    private WindowLifecycleSubscription? _windowLifecycleSubscription;
    private CanvasVirtualControl? _canvas;
    private Canvas? _overlay;
    private ScrollViewer? _scroll;
    private bool _scrollViewIntermediate;
    private Grid? _root;
    private MarkdownEngine? _ownedEngine;
    private bool _synchronizingCodeHighlighter;
    private LegacyCodeHighlighterAdapter? _legacyCodeHighlighterAdapter;
    private ICodeHighlighter? _ownedCodeHighlighterService;
    private IDisposable? _ownedCodeHighlighter;
    private Rect _externalViewport;
    private bool _hasExternalViewport;

    private volatile LayoutSnapshot? _snapshot;
    private ImagePlanPublication? _imagePlanPublication;
    private readonly SnapshotRetirementTracker _snapshotRetirements = new(
        static exception => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] retirement failed: {exception.GetType().Name} " +
            $"(0x{exception.HResult:X8})."));
    private volatile CancellationTokenSource? _pipelineCts;
    private CancellationTokenSource? _imageLifetimeCts;
    private Task? _pipelineTask;
    private long _pipelineGeneration;
    private long _activePipelineGeneration;
    private long _pendingRebuildRequestTimestamp;
    private long _snapshotGeneration;
    private readonly MarkdownAutomationDocumentGeneration _automationDocumentGenerations = new();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    // Opt-in used only by the standalone quick-profile harness. This does not
    // attach a PerformanceSession or alter normal layout scheduling; it merely
    // enables the existing build-stage counters for this control instance.
    internal bool EnableStandaloneLayoutDiagnostics { get; set; }
    private bool _hasInitialLayoutDispatcherDiagnostics;
    private MarkdownInitialLayoutDispatcherDiagnosticsSnapshot _initialLayoutDispatcherDiagnostics;
    private MarkdownLayoutDiagnostics? _initialLayoutDispatcherDiagnosticsSource;
#endif
    private long _snapshotPipelineStartTimestamp;
    private long _snapshotSourceUtf16Bytes;
    private int _deferredSceneStatusRefreshAfterPaint;
    // Set when the visible deferred-scene probe could not verify its band
    // (busy or not yet measured). The next successful paint, which follows
    // band measurement or scene preparation, re-runs the probe once.
    private int _deferredSceneProbeAfterPaint;
    private MarkdownPipelineTimingSnapshot _lastPipelineTiming;
    private CancellationTokenSource? _lazyLayoutCts;
    private Task? _lazyLayoutTask;
    private LayoutSnapshot? _pendingLazyLayoutSnapshot;
    private LayoutSnapshot? _activeLazyLayoutSnapshot;
    private long _activeLazyLayoutRevision = -1;
    private LazyLayoutBand _pendingLazyLayoutBand;
    private LazyLayoutBand _activeLazyLayoutBand;
    private LazyLayoutBand _pendingLazyLayoutVisibleVectorBand;
    private LazyLayoutBand _activeLazyLayoutVisibleVectorBand;
    private long _activeLazyLayoutCapacityGeneration;
    private double? _activeLazyLayoutAnchorOffset;
    private bool _hasPendingLazyLayoutBand;
    private bool _lazyLayoutDispatchQueued;
    private bool _pendingLazyLayoutPreserveScrollAnchor;
    private long _lazyLayoutGeneration;
    private long _lastLazyLayoutDispatchTicket;
    private readonly WeakSnapshotPublicationDrainFence<LayoutSnapshot> _lazyLayoutPublicationHandoff = new();
    private long _appliedLazyLayoutRevision;
    // Hosted-focus readers consume only realization plans published for the
    // exact snapshot generation and layout revision. The attempt epoch also
    // invalidates a plan while a same-snapshot rebuild is being transferred.
    private long _hostedFocusPlanEpoch;
    private long _publishedHostedFocusPlanEpoch = -1;
    private long _publishedHostedFocusPlanGeneration = -1;
    private long _publishedHostedFocusPlanRevision = -1;
    private (LayoutSnapshot Snapshot, long SnapshotGeneration, int BlockIndex)? _pendingScrollToBlock;
    private double _lastLazyViewportTop;
    private long _lastLazyViewportTimestamp;
    private double _lazyScrollVelocityPixelsPerSecond;
    private MarkdownRebuildDispatchState _rebuildDispatchState;
    private long _lastRebuildDispatchTicket;
    private readonly MarkdownRebuildCoordinator _rebuildCoordinator = new();
    private int _rejectedCrossThreadRebuildReason = -1;
    private bool _imageRelayoutQueued;
    private bool _imageRelayoutActive;
    private long _lastImageRelayoutDispatchTicket;
    private CancellationTokenSource? _imageRelayoutCts;
    private Task? _imageRelayoutTask;
    private ImageRelayoutWorkPayload<LayoutSnapshot>? _imageRelayoutPayload;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _imageRelayoutRecoveryTimer;
    private WeakRecoveryTimer<MarkdownRendererControl>? _managedImageRelayoutRecoveryTimer;
    private bool HasImageRelayoutRecoveryClock => _imageRelayoutRecoveryTimer is not null ||
        _managedImageRelayoutRecoveryTimer is not null;
    private (long Generation, (int BlockIndex, double OffsetFromTop)? Anchor, double? ScrollOffset)? _pendingLayoutCommit;
    private bool _publishingLayoutCommit;
    private long _lastLayoutCommitTicket;
    // A sparse HashSet can scan an old storm's entire retained slot array to
    // capture just 32 live indices. A balanced live-only tree keeps capture
    // O(log N + batch size), and naturally prioritizes earlier document roots.
    private readonly SortedSet<int> _pendingImageRelayoutBlockIndices = [];
    private bool _selectionAutomationEventQueued;
    private bool _horizontalOverflowAutomationEventQueued;
    private readonly Dictionary<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>
        _pendingHorizontalOverflowAutomationChanges = new(ReferenceEqualityComparer.Instance);
    private float _lastWidth;
    private static readonly MarkdownTheme _defaultTheme = new();
    private static readonly MarkdownExtensionRegistry _defaultRegistry = new();
    private readonly object _parseCacheGate = new();
    private readonly Dictionary<string, bool> _disclosureStates = new(StringComparer.Ordinal);
    private readonly HashSet<HostedElementFallbackKey> _hostedElementFallbacks = new();
    private ParsedMarkdown? _parseCache;
    private string? _parseCacheSource;
    private MarkdownExtensionRegistry? _parseCacheRegistry;
    private int _parseCacheRegistryRevision = -1;
    private bool _isUpdatingDocumentProperty;
    private bool _hasExplicitDocument;
    private SizeChangedEventHandler? _sizeChangedHandler;
    private readonly SelectionController _selection = new();
    private DocumentPosition? _selectionAnchor;
    private double _lastSelectionPointerViewportY = double.NaN;

    // Selection rectangles are retained for viewport and diagnostics work. The
    // selected background and foreground are painted into one viewport-bounded
    // composition surface layered above the immutable document canvas. This keeps
    // selection drags from invalidating/repainting document tiles while avoiding
    // a second Win2D XAML control (and its independent XamlRoot lifetime).
    private List<Rect> _selectionAdornerRects = new();
    private List<Rect> _selectionAdornerRectScratch = new();
    private List<SelectionBackgroundGeometry> _selectionAdornerBackgrounds = new();
    private List<SelectionBackgroundGeometry> _selectionAdornerBackgroundScratch = new();
    private readonly SelectionAdornerRetryState _selectionAdornerRetryState = new();
    private bool _selectionAdornerRetryFrameSubscribed;
    private EventHandler<object>? _selectionAdornerRetryFrameHandler;
    private bool _selectionAdornerRefreshInProgress;
    private long _selectionAdornerRequestVersion;
    private bool _selectionAdornerGeometryDirty = true;
    private bool _selectionAdornerVisualDirty = true;
    private long _selectionAdornerSnapshotGeneration = -1;
    private long _selectionAdornerLayoutRevision = -1;
    private double _selectionAdornerViewportTop = double.NaN;
    private double _selectionAdornerViewportWidth = double.NaN;
    private double _selectionAdornerViewportHeight = double.NaN;
    private double _selectionAdornerRasterizationScale = double.NaN;
    private Point _selectionStartHandleEndpoint;
    private Point _selectionEndHandleEndpoint;
    private bool _selectionStartHandleEndpointAvailable;
    private bool _selectionEndHandleEndpointAvailable;
    // The adorner is an inert placeholder element hosting a composition
    // child visual. A XAML Image showing Win2D's CanvasImageSource is unsafe:
    // XAML's reference to the aggregated inner SurfaceImageSource does not keep
    // Win2D's outer object alive, so the .NET finalizer could destroy it while
    // XAML still released the Image. Composition surfaces have no XAML peer and
    // are disposed deterministically.
    private Border? _selectionAdorner;
    private SelectionAdornerVisualHost? _selectionAdornerHost;
    // One long-lived, grow-only surface per adorner. Relayout, source
    // replacement and resize reuse it.
    private readonly SelectionAdornerSurfaceLifetime<SelectionAdornerCompositionSurface, CompositionGraphicsDevice> _selectionAdornerSurfaces =
        new(CreateSelectionAdornerSurface, ReportSelectionAdornerSurfaceReleaseFailure, DisposeSelectionAdornerSurface);
    // The composition graphics device that draws adorner surfaces, and the
    // Win2D device it currently renders with.
    private CompositionGraphicsDevice? _selectionAdornerGraphicsDevice;
    private CanvasDevice? _selectionAdornerCanvasDevice;
    // The Win2D device that created the committed snapshot's bitmaps.
    private CanvasDevice? _snapshotLayoutDevice;
    private double _selectionAdornerOffsetY;
    private Border? _selectionDragShield;
    private Button? _selectionStartHandle;
    private Button? _selectionEndHandle;
    private SelectionHandleEndpoint _activeSelectionHandle;
    private uint _selectionHandlePointerId;
    private Button? _selectionHandleCaptureTarget;
    private DocumentPosition _selectionHandleFixedPosition;
    private readonly MarkdownTouchSelectionSession _touchSelection = new();
    private TouchSelectionPresentationRestoreIntent? _pendingTouchSelectionPresentationRestore;
    private bool _selectionHandleFrameSubscribed;
    private long _selectionHandleLastFrameTimestamp;
    private Point _selectionHandleLatestPoint;
    private double _selectionHandleLatestViewportY;
    private Point _selectionHandleCaretOffset;
    private bool _selectionHandleMovePending;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression = new();

    // Background color used to clear each canvas tile before painting. Published
    // atomically with the layout/theme snapshot so OnRegionsInvalidated (UI
    // thread, but outside the rebuild flow) never mixes presentation generations.
    // Relying on ds.Clear(Colors.Transparent) doesn't work reliably because
    // CanvasVirtualControl may not alpha-composite with the XAML compositor
    // depending on the platform's DirectX swap-chain configuration.
    private Windows.UI.Color _canvasBackground = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    // Last committed theme snapshot. Used by UI operations (focus ring, etc.)
    // that need theme colors after the rebuild is complete.
    private Theming.ThemeSnapshot? _themeSnapshot;
    private MarkdownLocalizationSnapshot? _committedLocalization;
    private string? _committedImageDocumentId;
    private MarkdownEnvironmentMonitor.Subscription? _environmentSubscription;
    private MarkdownEnvironmentSnapshot _environmentSnapshot;
    private bool _canvasDeviceRecoveryQueued;
    private int _canvasDeviceRecoveryAttempt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasDeviceRecoveryTimer;

    // ---- Keyboard navigation ----
    // Ordered list of focusable items (LinkRuns + InlineEmbedRuns) in the current
    // snapshot.  Rebuilt after each snapshot commit.  -1 means "nothing focused".
    private System.Collections.Generic.IReadOnlyList<Layout.FocusableItem>? _focusableItems;
    private int _focusedItemIndex = -1;
    private int _focusResumeItemIndex = -1;
    private bool _taskCommandAvailabilityRefreshQueued;
    // A deferred focus-plan refresh is value-only: it never keeps a snapshot,
    // box, or native resource alive while a geometry lease is busy.
    private (long SnapshotGeneration, long AutomationDocumentGeneration,
        long LayoutRevision, long RequestVersion, bool IsPending) _pendingFocusableItemsRefresh;
    private long _focusableItemsRefreshRequestVersion;
    private PendingLazyFocus? _pendingLazyFocus;
    private const int MaxPendingSpatialFocusKeys = 64;
    private readonly Queue<VirtualKey> _pendingSpatialFocusKeys = new();
    private long _pendingSpatialFocusQueueEpoch;
    private bool _processingPendingFocusGeometry;
    private bool _hasPendingSpatialFocusOrigin;
    private Layout.FocusableItem _pendingSpatialFocusOrigin;
    private long _pendingSpatialFocusGeneration;
    private bool _pendingFocusGeometryRefresh;
    // GotFocus is raised asynchronously, after the dispatcher may have
    // cleared _suppressNextFocusPromotion. GettingFocus captures it in time.
    private bool _focusEntrySuppressed;
    // Counts explicit scroll requests so a delayed focus reveal
    // never overrides a newer one, even before its view change applies.
    private long _viewportRequestSerial;
    private FocusRevealOrigin? _focusReveal;
    private bool _hasPendingFocusResumePoint;
    private Point _pendingFocusResumePoint;
    private long _pendingFocusResumeGeneration;
    private bool _lastFocusNavigationHandled;
    private long _focusRingPublicationTicket;
    private bool _hasPendingHorizontalOverflowRefresh;
    private long _pendingHorizontalOverflowRefreshGeneration;
    private int _pendingHorizontalOverflowRefreshBlockIndex;
    private double _pendingHorizontalOverflowRefreshOldOffset;
    // XAML Border element used to show a focus ring around the focused item.
    // Lives on _overlay at ZIndex 1 (above selection at -1, below embeds at 0).
    private Microsoft.UI.Xaml.Controls.Border? _focusRing;
    private Windows.UI.Color _focusRingBrushColor; // cached to avoid per-keystroke SolidColorBrush allocations

    // ---- Multi-click tracking (double/triple click selection) ----
    private long _lastPressTickMs;
    private Point _lastPressPoint;
    private int _consecutiveClickCount;
    // Tracks the currently captured primary-pointer gesture. Keeping the pointer
    // id with the state makes release/cancel/capture-lost handling idempotent
    // when events arrive out of order or are routed through the drag shield.
    private PointerSession _pointerSession;
    // Set in OnUnloaded; checked in dispatcher lambdas to guard against post-unload execution.
    private bool _isUnloaded = true;
    private bool _isDisposed;
    // The provider may raise CacheInvalidated from a worker or system-event
    // callback. Keep the subscribed instance outside DependencyObject storage
    // so the callback never reads a thread-affine dependency property.
    private IMarkdownSvgRenderer? _subscribedSvgRenderer;
    private IMarkdownPerformanceSessionInternal? _subscribedPerformanceSession;
    private bool _canvasRenderHandlersAttached;
    private readonly PointerEventHandler _canvasPointerMovedHandler;
    private readonly PointerEventHandler _canvasPointerReleasedHandler;
    private readonly PointerEventHandler _canvasPointerCanceledHandler;
    private readonly PointerEventHandler _canvasPointerCaptureLostHandler;
    // System double-click time; read from the Win32 API at first use.
    private static readonly int _doubleClickTimeMs = GetSystemDoubleClickTimeMs();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static int GetSystemDoubleClickTimeMs()
    {
        try { return (int)Math.Min(GetDoubleClickTime(), (uint)int.MaxValue); } catch { return 500; }
    }

    // Click mode for the current press; governs drag-extension behaviour.
    private enum ClickMode { Single, Word, Block }
    private enum SelectionHandleEndpoint { None, Start, End }
    private enum FocusGeometryReadStatus { Ready, Busy, Stale }
    private ClickMode _clickMode;
    private readonly struct PointerSession(uint PointerId, bool IsPrimary)
    {
        public readonly uint PointerId = PointerId;
        public readonly bool IsPrimary = IsPrimary;
        public bool IsActive => PointerId != 0;
    }
    private readonly struct PendingLazyFocus(
        LayoutSnapshot Snapshot,
        long SnapshotGeneration,
        Layout.FocusableItem Item,
        bool Reverse)
    {
        public readonly LayoutSnapshot Snapshot = Snapshot;
        public readonly long SnapshotGeneration = SnapshotGeneration;
        public readonly Layout.FocusableItem Item = Item;
        public readonly bool Reverse = Reverse;
    }
    private enum HostedFocusPlanStatus { Current, LayoutPending, Stale }
    private enum HostedFocusResult { Focused, NotRealized, Deferred, Stale }
    private readonly struct HostedFocusTuple(
        LayoutSnapshot Snapshot,
        long SnapshotGeneration,
        long LayoutRevision,
        long PlanEpoch,
        System.Collections.Generic.IReadOnlyList<Layout.FocusableItem> Items)
    {
        public readonly LayoutSnapshot Snapshot = Snapshot;
        public readonly long SnapshotGeneration = SnapshotGeneration;
        public readonly long LayoutRevision = LayoutRevision;
        public readonly long PlanEpoch = PlanEpoch;
        public readonly System.Collections.Generic.IReadOnlyList<Layout.FocusableItem> Items = Items;
    }
    private readonly struct PendingHorizontalOverflowAutomationChange(
        LayoutSnapshot Snapshot,
        double OldPhysicalPercent,
        double NewPhysicalPercent)
    {
        public readonly LayoutSnapshot Snapshot = Snapshot;
        public readonly double OldPhysicalPercent = OldPhysicalPercent;
        public readonly double NewPhysicalPercent = NewPhysicalPercent;
    }
    private readonly struct InlineEmbedPlacementUpdate(
        WeakReference<InlineEmbedPlan> Plan,
        Rect Rect)
    {
        public readonly WeakReference<InlineEmbedPlan> Plan = Plan;
        public readonly Rect Rect = Rect;
    }
    private readonly struct HorizontalOverflowVisualState(
        WeakReference<LayoutSnapshot> Snapshot,
        long SnapshotGeneration,
        long LayoutRevision,
        int BlockIndex,
        WeakReference<IHorizontalOverflowBox> Target,
        double OldOffset,
        double NewOffset,
        double Extent,
        double Viewport,
        bool IsRightToLeft,
        Rect ViewportBounds,
        Rect TrackBounds,
        InlineEmbedPlacementUpdate[] EmbedPlacements)
    {
        public readonly WeakReference<LayoutSnapshot> Snapshot = Snapshot;
        public readonly long SnapshotGeneration = SnapshotGeneration;
        public readonly long LayoutRevision = LayoutRevision;
        public readonly int BlockIndex = BlockIndex;
        public readonly WeakReference<IHorizontalOverflowBox> Target = Target;
        public readonly double OldOffset = OldOffset;
        public readonly double NewOffset = NewOffset;
        public readonly double Extent = Extent;
        public readonly double Viewport = Viewport;
        public readonly bool IsRightToLeft = IsRightToLeft;
        public readonly Rect ViewportBounds = ViewportBounds;
        public readonly Rect TrackBounds = TrackBounds;
        public readonly InlineEmbedPlacementUpdate[] EmbedPlacements = EmbedPlacements;
    }
    private uint _horizontalOverflowPointerId;
    private LayoutSnapshot? _horizontalOverflowSnapshot;
    private IHorizontalOverflowBox? _horizontalOverflowTarget;
    private Point _horizontalOverflowPressPoint;
    private double _horizontalOverflowInitialOffset;
    private double _horizontalOverflowThumbGrabOffset;
    private bool _horizontalOverflowCaptured;
    private bool _horizontalOverflowThumbDrag;
    // When _clickMode is Word or Block, these hold the start/end of the initially
    // selected word/block so that backward drag can correctly extend to the
    // start of the word/block under the pointer rather than always to the end.
    private DocumentPosition _dragAnchorStart;
    private DocumentPosition _dragAnchorEnd;

    // Cached cursor instances (created once, reused; disposed on Unload).
    private Microsoft.UI.Input.InputSystemCursor? _cursorHand;
    private Microsoft.UI.Input.InputSystemCursor? _cursorIBeam;

    // Cache of inline-embed rectangles (in canvas/document coordinates) plus
    // the InlineRun + owning InlineContainerBox.  Built during PlaceEmbeds
    // and consulted from pointer handlers so we can:
    //   (a) suppress our own ProtectedCursor / link-hover work over an embed
    //       so the embedded XAML element's own cursor (Hand for Button, IBeam
    //       for TextBox, …) wins;
    //   (b) avoid starting a selection on PointerPressed inside an embed so
    //       that click routes to the embed normally;
    //   (c) snap drag-through positions atomically to the run start or end,
    //       so an embed is included or excluded as a single unit.
    private readonly List<(Layout.Boxes.InlineContainerBox Box, InlineEmbedRun Run, Rect Rect)> _embedRects = new();

    // Cache of block-embed bounding rects (in document coordinates).
    // Mirrors _embedRects but for EmbedBox block elements (e.g. hosted Buttons).
    // Used by IsPointOverEmbed so that hovering a block-embed button correctly
    // suppresses link-hover and IBeam-cursor work, just like inline embeds.
    private readonly List<(BlockBox Box, Rect Rect)> _blockEmbedRects = new();

    // Code block copy buttons are overlay-hosted native controls, but they are
    // renderer chrome rather than user-authored embeds. Keep them on a separate
    // realization track so RealizedEmbedCount remains embed-only.
    private enum CodeBlockHostedElementKind
    {
        Copy,
    }

    private List<(Layout.Boxes.CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> _codeBlockActionRects = new();

    // Embed virtualisation. Plans capture each embed's position + factory
    // delegate up front; realisation happens lazily as scrolling brings them
    // into the viewport (plus an overscan band). Off-screen embeds beyond a
    // wider derealisation band are removed from the visual tree and their
    // factory's RecycleBlock hook is called. This keeps memory usage bounded
    // for very long documents with many hosted controls.
    private abstract class EmbedPlan
    {
        public Rect Rect;
        public FrameworkElement? Realized;
        public Layout.FocusableItem? FocusIdentity;
        public virtual bool HasActiveWork => Realized is not null;
        public abstract object LogicalOwner { get; }
        public abstract void Realize(MarkdownRendererControl owner);
        public abstract void Derealize(MarkdownRendererControl owner);
        public abstract bool IsSameLogicalEmbed(EmbedPlan other);
        public virtual void UpdatePlacement() => UpdatePlacement(snapEdges: true);

        protected void UpdatePlacement(bool snapEdges)
        {
            FrameworkElement? element = Realized;
            if (element is null) return;
            // Rect was copied under the snapshot read lease. A hosted element
            // can synchronously recycle/unload us from any DP callback.
            Rect rect = Rect;
            double left = Math.Round(rect.X);
            double top = Math.Round(rect.Y);
            double width = snapEdges ? Math.Round(rect.Right) - left : Math.Round(rect.Width);
            double height = snapEdges ? Math.Round(rect.Bottom) - top : Math.Round(rect.Height);
            element.Width = Math.Max(0, width);
            if (!ReferenceEquals(Realized, element)) return;
            element.Height = Math.Max(0, height);
            if (!ReferenceEquals(Realized, element)) return;
            Canvas.SetLeft(element, left);
            if (!ReferenceEquals(Realized, element)) return;
            Canvas.SetTop(element, top);
            if (!ReferenceEquals(Realized, element)) return;
            Canvas.SetZIndex(element, 2);
        }
    }
    private sealed class BlockEmbedPlan : EmbedPlan
    {
        public Layout.Boxes.EmbedBox Box = null!;
        public override object LogicalOwner => Box;
        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;
            long generation = owner._snapshotGeneration;
            try
            {
                var fe = Box.Factory.CreateBlock(Box.SourceBlock);
                if (owner._isDisposed || owner._isUnloaded || generation != owner._snapshotGeneration)
                {
                    try { Box.Factory.RecycleBlock(Box.SourceBlock, fe); } catch { }
                    return;
                }
                Realized = fe;
                Box.RealizedElement = fe;
                UpdatePlacement();
                if (!ReferenceEquals(Realized, fe) || owner._isDisposed || owner._isUnloaded)
                    return;
                fe.KeyDown += owner.OnHostedEmbedKeyDown;
                owner._overlay!.Children.Add(fe);
                // _blockEmbedRects rebuilt in RealizeVisibleEmbeds.
            }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] EmbedBox factory threw: {ex.Message}");
            }
        }
        public override void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            Realized = null;
            Box.RealizedElement = null;
            fe.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Box.Factory.RecycleBlock(Box.SourceBlock, fe); } catch { }
            // Refresh _blockEmbedRects defensively (rebuilt fully each Realize cycle from realised plans).
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is BlockEmbedPlan block && ReferenceEquals(Box, block.Box);

        public override void UpdatePlacement() => UpdatePlacement(snapEdges: false);
    }
    private sealed class InlineEmbedPlan : EmbedPlan
    {
        public Layout.Boxes.InlineContainerBox Icb = null!;
        public InlineEmbedRun Run = null!;
        public override object LogicalOwner => Icb;
        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;
            long generation = owner._snapshotGeneration;
            try
            {
                var fe = Run.ElementFactory();
                if (owner._isDisposed || owner._isUnloaded || generation != owner._snapshotGeneration)
                {
                    try { Run.Recycle?.Invoke(fe); } catch { }
                    return;
                }
                Realized = fe;
                Run.RealizedElement = fe;
                UpdatePlacement();
                if (!ReferenceEquals(Realized, fe) || owner._isDisposed || owner._isUnloaded)
                    return;
                fe.KeyDown += owner.OnHostedEmbedKeyDown;
                owner._overlay!.Children.Add(fe);
                // _embedRects rebuilt in RealizeVisibleEmbeds.
            }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] inline embed factory threw: {ex.Message}");
            }
        }
        public override void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            Realized = null;
            Run.RealizedElement = null;
            fe.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Run.Recycle?.Invoke(fe); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] inline embed Recycle threw: {ex.Message}");
            }
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is InlineEmbedPlan inline &&
               ReferenceEquals(Icb, inline.Icb) &&
               ReferenceEquals(Run, inline.Run);

    }
    private readonly List<EmbedPlan> _embedPlans = new();
    private ViewportBandIndex? _embedPlanIndex;
    // Document ordinals stay sorted so hit-test rectangles retain their source
    // order while scroll work visits only the small active realization band.
    private readonly List<int> _activeEmbedOrdinals = new();
    private sealed class CodeBlockActionPlan
    {
        public Layout.Boxes.CodeBlockBox Box = null!;
        public Rect Rect;
        public CodeBlockHostedElementKind Kind;
        public Layout.FocusableItem? FocusIdentity;
        public FrameworkElement? Realized;
        private RoutedEventHandler? _clickHandler;
        private int _feedbackVersion;
        private bool _showsCopiedFeedback;

        public void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;
            long generation = owner._snapshotGeneration;
            var (button, alreadyAttached) = owner.RentCodeBlockCopyButton();
            if (owner._isDisposed || owner._isUnloaded || generation != owner._snapshotGeneration)
                return;
            AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
            if (owner._isDisposed || owner._isUnloaded || generation != owner._snapshotGeneration)
                return;
            AttachHandlers(button, owner);
            button.KeyDown += owner.OnHostedEmbedKeyDown;
            Realized = button;
            Box.RealizedCopyButton = button;
            UpdatePlacement();
            if (!ReferenceEquals(Realized, button) || owner._isDisposed || owner._isUnloaded)
                return;
            if (!alreadyAttached)
                owner._overlay!.Children.Add(button);
            button.Visibility = Visibility.Visible;
            owner.RestoreFocusedCodeBlockActionIfNeeded(this);
        }

        public void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            bool resetFeedback = _showsCopiedFeedback;
            Box.RealizedCopyButton = null;
            Realized = null;
            _showsCopiedFeedback = false;
            _feedbackVersion++;
            if (fe is Button button)
            {
                if (_clickHandler is not null)
                    button.Click -= _clickHandler;
                button.KeyDown -= owner.OnHostedEmbedKeyDown;
            }

            owner.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(fe);
            if (resetFeedback && fe is Button copiedButton)
                owner.SetCodeBlockCopyButtonState(copiedButton, copied: false);
            if (fe is Button pooledButton)
                owner.ReturnCodeBlockCopyButton(pooledButton);
            else
            {
                try { owner._overlay!.Children.Remove(fe); } catch { }
            }
        }

        public void AdoptRealizedFrom(CodeBlockActionPlan oldPlan, MarkdownRendererControl owner)
        {
            if (oldPlan.Realized is null) return;

            var fe = oldPlan.Realized;
            RoutedEventHandler? oldClickHandler = oldPlan._clickHandler;
            oldPlan._clickHandler = null;
            oldPlan._feedbackVersion++;
            oldPlan._showsCopiedFeedback = false;
            oldPlan.Box.RealizedCopyButton = null;
            oldPlan.Realized = null;
            Realized = fe;
            Box.RealizedCopyButton = fe;
            if (fe is Button button)
            {
                if (oldClickHandler is not null)
                    button.Click -= oldClickHandler;
                button.KeyDown -= owner.OnHostedEmbedKeyDown;
                AttachHandlers(button, owner);
                button.KeyDown += owner.OnHostedEmbedKeyDown;
                AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
                if (!ReferenceEquals(Realized, fe) || owner._isDisposed || owner._isUnloaded)
                    return;
                owner.SetCodeBlockCopyButtonState(button, copied: false);
                if (!ReferenceEquals(Realized, fe) || owner._isDisposed || owner._isUnloaded)
                    return;
            }
            UpdatePlacement();
        }

        public bool AdoptRealizedFromRasterCandidate(
            CodeBlockActionPlan oldPlan,
            MarkdownRendererControl owner,
            LayoutSnapshot candidate,
            long generation,
            long layoutRevision,
            long hostedFocusPlanEpoch)
        {
            if (!owner.IsCurrentRasterCodeActionPublication(
                    candidate, generation, layoutRevision, hostedFocusPlanEpoch) ||
                oldPlan.Realized is not Button button)
                return false;

            FrameworkElement fe = button;
            RoutedEventHandler? oldClickHandler = oldPlan._clickHandler;
            oldPlan._clickHandler = null;
            oldPlan._feedbackVersion++;
            oldPlan._showsCopiedFeedback = false;
            oldPlan.Box.RealizedCopyButton = null;
            oldPlan.Realized = null;
            Realized = fe;
            Box.RealizedCopyButton = fe;

            if (oldClickHandler is not null)
                button.Click -= oldClickHandler;
            button.KeyDown -= owner.OnHostedEmbedKeyDown;
            AttachHandlers(button, owner);
            button.KeyDown += owner.OnHostedEmbedKeyDown;

            AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
            if (!IsCurrentRasterActionOwner(owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, fe))
                return false;

            if (!owner.SetCodeBlockCopyButtonStateForRasterCandidate(
                    this, button, fe, candidate, generation, layoutRevision, hostedFocusPlanEpoch))
                return false;

            return UpdatePlacementForRasterCandidate(
                owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, fe);
        }

        private void AttachHandlers(Button button, MarkdownRendererControl owner)
        {
            _clickHandler ??= (_, _) => owner.CopyCodeBlockToClipboard(this);
            button.Click += _clickHandler;
        }

        public bool IsSameLogicalAction(CodeBlockActionPlan other)
            => ReferenceEquals(Box, other.Box) && Kind == other.Kind;

        public void UpdatePlacement()
        {
            FrameworkElement? element = Realized;
            if (element is null) return;
            double left = Math.Round(Rect.X);
            double top = Math.Round(Rect.Y);
            double width = Math.Round(Rect.X + Rect.Width) - left;
            double height = Math.Round(Rect.Y + Rect.Height) - top;
            if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.1)
                element.Width = width;
            if (!ReferenceEquals(Realized, element)) return;
            if (double.IsNaN(element.Height) || Math.Abs(element.Height - height) > 0.1)
                element.Height = height;
            if (!ReferenceEquals(Realized, element)) return;
            double currentLeft = Canvas.GetLeft(element);
            if (double.IsNaN(currentLeft) || Math.Abs(currentLeft - left) > 0.1)
                Canvas.SetLeft(element, left);
            if (!ReferenceEquals(Realized, element)) return;
            double currentTop = Canvas.GetTop(element);
            if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
                Canvas.SetTop(element, top);
            if (!ReferenceEquals(Realized, element)) return;
            if (Canvas.GetZIndex(element) != 2)
                Canvas.SetZIndex(element, 2);
        }

        private bool UpdatePlacementForRasterCandidate(
            MarkdownRendererControl owner,
            LayoutSnapshot candidate,
            long generation,
            long layoutRevision,
            long hostedFocusPlanEpoch,
            FrameworkElement element)
        {
            if (!IsCurrentRasterActionOwner(owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;

            double left = Math.Round(Rect.X);
            double top = Math.Round(Rect.Y);
            double width = Math.Round(Rect.X + Rect.Width) - left;
            double height = Math.Round(Rect.Y + Rect.Height) - top;
            if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.1)
            {
                element.Width = width;
                if (!IsCurrentRasterActionOwner(owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                    return false;
            }
            if (double.IsNaN(element.Height) || Math.Abs(element.Height - height) > 0.1)
            {
                element.Height = height;
                if (!IsCurrentRasterActionOwner(owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                    return false;
            }

            double currentLeft = Canvas.GetLeft(element);
            if (double.IsNaN(currentLeft) || Math.Abs(currentLeft - left) > 0.1)
            {
                Canvas.SetLeft(element, left);
                if (!IsCurrentRasterActionOwner(owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                    return false;
            }
            double currentTop = Canvas.GetTop(element);
            if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
            {
                Canvas.SetTop(element, top);
                if (!IsCurrentRasterActionOwner(owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                    return false;
            }
            if (Canvas.GetZIndex(element) != 2)
            {
                Canvas.SetZIndex(element, 2);
                if (!IsCurrentRasterActionOwner(owner, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                    return false;
            }
            return true;
        }

        public bool IsCurrentRasterActionOwner(
            MarkdownRendererControl owner,
            LayoutSnapshot candidate,
            long generation,
            long layoutRevision,
            long hostedFocusPlanEpoch,
            FrameworkElement element)
            => ReferenceEquals(Realized, element) &&
               ReferenceEquals(Box.RealizedCopyButton, element) &&
               owner.IsCurrentRasterCodeActionPublication(
                   candidate, generation, layoutRevision, hostedFocusPlanEpoch);

        public void ShowCopiedFeedback(MarkdownRendererControl owner)
        {
            if (Realized is not Button button)
                return;

            int version = ++_feedbackVersion;
            _showsCopiedFeedback = true;
            owner.SetCodeBlockCopyButtonState(button, copied: true);
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(1500).ConfigureAwait(false); }
                catch { return; }

                owner.DispatcherQueue.TryEnqueue(() =>
                {
                    if (owner._isUnloaded || _feedbackVersion != version)
                        return;
                    if (Realized is Button realizedButton)
                    {
                        owner.SetCodeBlockCopyButtonState(realizedButton, copied: false);
                        _showsCopiedFeedback = false;
                    }
                });
            });
        }
    }

    private List<CodeBlockActionPlan> _codeBlockActionPlans = new();
    private bool _hasMeasuredCodeBlocks;
    private ViewportBandIndex? _codeBlockActionPlanIndex;
    private List<int> _activeCodeBlockActionOrdinals = new();
    private readonly Stack<(Button Button, bool Attached)> _codeBlockCopyButtonPool = new();
    private const int CodeBlockCopyButtonPoolCapacity = 8;
    private bool _derealizingAllEmbeds;
    private bool _realizingVisibleEmbeds;
    private bool _realizeVisibleEmbedsRequested;
    private bool _realizeVisibleEmbedsContinuationQueued;
    private long _realizeVisibleEmbedsContinuationTicket;
    private long _realizationPassEpoch;
    private readonly BoundedCodeBlockHighlightCache<CodeBlockHighlightCacheKey> _codeBlockHighlightCache = new(
        CodeBlockHighlightCacheMaxEntries,
        CodeBlockHighlightCacheBudgetBytes,
        static key => 64L + ((key.Language?.Length ?? 0) * sizeof(char)));
    private readonly HashSet<CodeBlockHighlightCacheKey> _codeBlockHighlightInFlight = new();
    private readonly SemaphoreSlim _codeBlockHighlightSemaphore = new(2, 2);
    private readonly object _codeBlockHighlightTasksGate = new();
    private readonly HashSet<Task> _codeBlockHighlightTasks = new();
    private CancellationTokenSource? _codeBlockHighlightCts;
    private Task _codeBlockHighlightRetirement = Task.CompletedTask;
    private int _codeBlockHighlightGeneration;
    private bool _promotingKeyboardFocusEntry;
    private bool _lastFocusEntryWasKeyboardTraversal;
    private bool _lastFocusEntryWasKeyboardInput;
    private bool _lastFocusEntryWasReverse;
    private bool _suppressNextFocusPromotion;
    private bool _contextMenuOpen;
    private MenuFlyout? _selectionContextMenu;
    private UIElement? _selectionDismissalRoot;
    private PointerEventHandler? _selectionDismissalPointerPressedHandler;
    private KeyEventHandler? _selectionCopyKeyDownHandler;

    // Lazy-load queue: all ImageBox instances in the current snapshot.
    // EnsureLoading() is called for each when its bounds enter the
    // viewport + LazyImageOverscanPx band.  Images already in the
    // in-memory cache start loading (no-op) immediately after build.
    private List<Layout.Boxes.ImageBox> _imagePlans = new();
    private const int MaximumCompletedImageCarryEntries = 4096;
    private readonly Dictionary<string, Layout.Boxes.CompletedImageCarry> _completedImageCarry =
        new(StringComparer.Ordinal);
    private Layout.Boxes.MarkdownImageCarryIdentity? _completedImageCarryIdentity;
    private ViewportBandIndex? _imagePlanIndex;
    private HashSet<Layout.Boxes.ImageBox> _subscribedImages = new();
    private string? _prefetchedMarkdownSource;
    private IMarkdownImagePrefetcher? _activeImagePrefetcher;
    private MarkdownImageResolveContext? _activeImagePrefetchContext;
    private int _prefetchedImageRegistryRevision = -1;
    private PendingImagePrefetch? _pendingImagePrefetch;

    private sealed class PendingImagePrefetch(
        Markdig.Syntax.MarkdownDocument Document,
        SafeHtmlRenderPolicy? SafeHtmlPolicy,
        IMarkdownImagePrefetcher Prefetcher,
        MarkdownImageResolveContext Context,
        CancellationToken CancellationToken)
    {
        public readonly Markdig.Syntax.MarkdownDocument Document = Document;
        public readonly SafeHtmlRenderPolicy? SafeHtmlPolicy = SafeHtmlPolicy;
        public readonly IMarkdownImagePrefetcher Prefetcher = Prefetcher;
        public readonly MarkdownImageResolveContext Context = Context;
        public readonly CancellationToken CancellationToken = CancellationToken;
    }

    /// <summary>
    /// Immutable membership/index publication for automation readiness. ImageBox
    /// objects are borrowed from the stated snapshot and read only while that
    /// snapshot's interaction lease is held.
    /// </summary>
    private sealed class ImagePlanPublication(
        LayoutSnapshot Snapshot,
        long SnapshotGeneration,
        long LayoutRevision,
        Layout.Boxes.ImageBox[] Images,
        Rect[] ImageBounds,
        ViewportBandIndex? Index)
    {
        internal readonly LayoutSnapshot Snapshot = Snapshot;
        internal readonly long SnapshotGeneration = SnapshotGeneration;
        internal readonly long LayoutRevision = LayoutRevision;
        internal readonly Layout.Boxes.ImageBox[] Images = Images;
        internal readonly Rect[] ImageBounds = ImageBounds;
        internal readonly ViewportBandIndex? Index = Index;
    }

    private const int CodeBlockHighlightCacheMaxEntries = 128;
    private const long CodeBlockHighlightCacheBudgetBytes = 8L * 1024 * 1024;
    private const double CodeBlockHighlightOverscanPx = 1600;

    /// <summary>
    /// Overscan band (pixels, each direction) within which off-screen images
    /// are preemptively loaded.  Wider than the embed virtualisation overscan
    /// so images appear before they scroll into view.
    /// </summary>
    public const double LazyImageOverscanPx = 800;

    // Stable per-LinkRun automation peer cache. UIA traversals re-request the
    // children of a block frequently; returning fresh peers each time loses
    // identity tracking (Narrator focus, "where am I"). Tied to the LinkRun
    // weakly so peers are released alongside the layout snapshot.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LinkRun, MarkdownLinkPeer> _linkPeerCache = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<InlineImageRun, MarkdownLinkedImagePeer> _linkedImagePeerCache = new();

    // Previous realised embed count — used to suppress redundant
    // EmbedsRealizationChanged fires on every scroll tick (UIA-friendly).
    private int _lastFiredRealizedCount = -1;

    /// <summary>
    /// Viewport over-scan band, in pixels, in either direction. Embeds whose
    /// bounds intersect [viewport.Top - Overscan, viewport.Bottom + Overscan]
    /// stay realised; everything else is virtualised away.
    /// </summary>
    public const double EmbedVirtualizationOverscanPx = 400;
    /// <summary>
    /// Wider band beyond which a realised embed is derealised. The gap between
    /// realisation and derealisation prevents rapid create/destroy thrash when
    /// the user scrolls along the edge of an embed.
    /// </summary>
    public const double EmbedVirtualizationDerealizeOverscanPx = 1200;

    /// <summary>
    /// Extra document pixels measured around the viewport in lazy layout mode.
    /// Wider than embed realization so text, inline image geometry, and
    /// accessibility ranges are ready before the user reaches them.
    /// </summary>
    internal const double LazyLayoutOverscanPx = 2400;

    // ---- Dependency properties ----

    /// <summary>Dependency property backing <see cref="Markdown"/>.</summary>
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownRendererControl),
            new PropertyMetadata(string.Empty, (d, _) => ((MarkdownRendererControl)d).OnMarkdownChanged()));

    /// <summary>Gets or sets the markdown source text to render.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private sealed class DeclarativeHostedElementPlan : EmbedPlan
    {
        private readonly HostedElementRealizationGate<MarkdownHostedElementRequest> _realizationGate = new();
        private MarkdownHostedElementRequest? _realizedRequest;
        private bool _fallbackRequested;

        public Layout.Boxes.DeclarativeHostedElementBox Box = null!;
        public IMarkdownHostedElementFactory Factory = null!;
        public override object LogicalOwner => Box;
        public override bool HasActiveWork => Realized is not null || _realizationGate.HasActive;

        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null || _fallbackRequested)
                return;

            var request = owner.CreateHostedElementRequest(Box, Rect);
            var pipelineToken = owner._pipelineCts?.Token ?? CancellationToken.None;
            long pipelineGeneration = owner._pipelineGeneration;
            if (!_realizationGate.TryBegin(request, pipelineToken, out var operation))
                return;

            ValueTask<FrameworkElement?> pending;
            try
            {
                // RealizeVisibleEmbeds is UI-thread only. Calling the public
                // factory here keeps creation of thread-affine WinUI objects on
                // the owning dispatcher while still permitting async loading.
                pending = Factory.CreateAsync(operation.Request, operation.CancellationToken);
            }
            catch (Exception ex)
            {
                CompleteFailure(owner, operation, ex, pipelineGeneration);
                return;
            }

            if (pending.IsCompletedSuccessfully)
            {
                CompleteOnUiThread(owner, operation, pending.Result, pipelineGeneration);
                return;
            }

            _ = CompleteAsync(owner, operation, pending, pipelineGeneration);
        }

        private async Task CompleteAsync(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            ValueTask<FrameworkElement?> pending,
            long pipelineGeneration)
        {
            FrameworkElement? element;
            try
            {
                element = await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
            {
                QueueCompletion(
                    owner,
                    operation,
                    element: null,
                    () => CompleteOnUiThread(owner, operation, null, pipelineGeneration));
                return;
            }
            catch (Exception ex)
            {
                QueueCompletion(
                    owner,
                    operation,
                    element: null,
                    () => CompleteFailure(owner, operation, ex, pipelineGeneration));
                return;
            }

            QueueCompletion(
                owner,
                operation,
                element,
                () => CompleteOnUiThread(owner, operation, element, pipelineGeneration));
        }

        private void QueueCompletion(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            FrameworkElement? element,
            Action completion)
        {
            var dispatcher = owner.DispatcherQueue;
            bool queued = false;
            try { queued = dispatcher is not null && dispatcher.TryEnqueue(() => completion()); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element completion dispatch failed: {ex.Message}");
            }
            if (queued)
                return;

            // Dispatcher shutdown means the control cannot attach the result.
            // Dispose host state as a last resort rather than leaking it.
            _realizationGate.TryClaimCompletion(operation);
            operation.Cancel();
            operation.Dispose();
            RecycleResult(operation.Request, element);
        }

        private void CompleteFailure(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            Exception exception,
            long pipelineGeneration)
        {
            bool wasCurrent = _realizationGate.TryClaimCompletion(operation);
            operation.Dispose();
            if (wasCurrent && owner.CanActivateHostedElementFallback(this, pipelineGeneration))
            {
                _fallbackRequested = true;
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element factory threw: {exception.Message}");
                owner.ActivateHostedElementFallback(this);
            }
        }

        private void CompleteOnUiThread(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            FrameworkElement? element,
            long pipelineGeneration)
        {
            bool isCurrent = _realizationGate.TryClaimCompletion(operation);
            bool canAttach = isCurrent &&
                !operation.CancellationToken.IsCancellationRequested &&
                owner.CanAttachHostedElement(this, pipelineGeneration);
            operation.Dispose();

            if (!canAttach)
            {
                RecycleResult(operation.Request, element);
                return;
            }

            if (element is null)
            {
                _fallbackRequested = true;
                owner.ActivateHostedElementFallback(this);
                return;
            }

            bool ownershipEstablished = false;
            try
            {
                MarkdownRendererControl.ApplyHostedElementAutomationProperties(Box, element);
                if (owner._isDisposed || !owner.CanAttachHostedElement(this, pipelineGeneration))
                {
                    RecycleResult(operation.Request, element);
                    return;
                }
                _realizedRequest = operation.Request;
                Realized = element;
                ownershipEstablished = true;
                Box.RealizedElement = element;
                UpdatePlacement();
                if (!ReferenceEquals(Realized, element))
                    return; // Reentrant teardown already recycled this result.
                if (owner._isDisposed || !owner.CanAttachHostedElement(this, pipelineGeneration))
                {
                    Derealize(owner);
                    return;
                }
                element.KeyDown += owner.OnHostedEmbedKeyDown;
                owner._overlay!.Children.Add(element);
            }
            catch (Exception ex)
            {
                if (ownershipEstablished && !ReferenceEquals(Realized, element))
                    return; // Never mutate or recycle an already-transferred result.
                element.KeyDown -= owner.OnHostedEmbedKeyDown;
                try { owner._overlay?.Children.Remove(element); } catch { }
                _realizedRequest = null;
                Realized = null;
                Box.RealizedElement = null;
                RecycleResult(operation.Request, element);
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element attachment failed: {ex.Message}");
                _fallbackRequested = true;
                owner.ActivateHostedElementFallback(this);
                return;
            }

            if (!ReferenceEquals(Realized, element) || owner._isDisposed ||
                !owner.CanAttachHostedElement(this, pipelineGeneration))
                return;
            owner.RealizeVisibleEmbeds();
            owner.RestoreFocusedHostedElementIfNeeded(this);
        }

        public override void Derealize(MarkdownRendererControl owner)
        {
            // Clear the active slot before cancellation so a quick scroll back
            // into view can start a fresh attempt even if the old factory does
            // not observe cancellation promptly.
            _realizationGate.CancelActive();

            if (Realized is null)
                return;

            var element = Realized;
            var request = _realizedRequest;
            _realizedRequest = null;
            Realized = null;
            Box.RealizedElement = null;
            element.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay?.Children.Remove(element); } catch { }
            if (request is not null)
                RecycleResult(request, element);
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is DeclarativeHostedElementPlan declarative &&
               ReferenceEquals(Box, declarative.Box) &&
               ReferenceEquals(Factory, declarative.Factory);

        internal void AdoptRealizedFrom(DeclarativeHostedElementPlan oldPlan)
        {
            _realizedRequest = oldPlan._realizedRequest;
            Realized = oldPlan.Realized;
            oldPlan._realizedRequest = null;
            oldPlan.Realized = null;
            oldPlan.Box.RealizedElement = null;
            Box.RealizedElement = Realized;
        }

        private void RecycleResult(
            MarkdownHostedElementRequest request,
            FrameworkElement? element)
        {
            if (element is null)
                return;

            try { Factory.Recycle(request, element); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element Recycle threw: {ex.Message}");
            }
        }

    }

    /// <summary>Dependency property backing <see cref="Document"/>.</summary>
    public static readonly DependencyProperty DocumentProperty =
        DependencyProperty.Register(
            nameof(Document),
            typeof(MarkdownRenderer.Document.MarkdownDocument),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnDocumentChanged(e)));

    /// <summary>
    /// Gets or sets an immutable, reusable parsed document. When set, the
    /// document takes precedence over <see cref="Markdown"/>. When markdown is
    /// parsed by this control, the committed snapshot is published here.
    /// </summary>
    public MarkdownRenderer.Document.MarkdownDocument? Document
    {
        get => (MarkdownRenderer.Document.MarkdownDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Engine"/>.</summary>
    public static readonly DependencyProperty EngineProperty =
        DependencyProperty.Register(
            nameof(Engine),
            typeof(MarkdownEngine),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnEngineChanged(e)));

    /// <summary>
    /// Gets or sets the immutable parser engine used for markdown replacements.
    /// A null value retains the legacy <see cref="ExtensionRegistry"/> pipeline.
    /// </summary>
    public MarkdownEngine? Engine
    {
        get => (MarkdownEngine?)GetValue(EngineProperty);
        set => SetValue(EngineProperty, value);
    }

    /// <summary>
    /// Assigns an engine created specifically for this control. Public Engine
    /// assignments are borrowed; only engines assigned through this internal
    /// ownership boundary are disposed with the control.
    /// </summary>
    internal void SetOwnedEngine(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        MarkdownEngine? previous = _ownedEngine;
        _ownedEngine = null;
        try
        {
            Engine = engine;
            _ownedEngine = engine;
        }
        catch
        {
            _ownedEngine = previous;
            RetireOwnedEngine(engine);
            throw;
        }

        if (previous is not null && !ReferenceEquals(previous, engine))
            RetireOwnedEngine(previous);
    }

    /// <summary>
    /// Assigns a highlighter created specifically for this control. Direct
    /// public highlighter assignments remain borrowed.
    /// </summary>
    internal void SetOwnedCodeHighlighter(ICodeHighlighter highlighter, IDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(highlighter);
        ArgumentNullException.ThrowIfNull(owner);

        // Validate before changing the dependency property or ownership fields.
        // A rejected transfer leaves both the active assignment and the passed
        // owner untouched; ownership transfers only after successful assignment.
        OwnedCodeHighlighterAssignment.ValidateTransfer(
            _ownedCodeHighlighterService,
            _ownedCodeHighlighter,
            highlighter,
            owner);

        IDisposable? previousOwner = _ownedCodeHighlighter;
        ICodeHighlighter? previousService = _ownedCodeHighlighterService;
        _ownedCodeHighlighter = null;
        _ownedCodeHighlighterService = null;
        try
        {
            CodeHighlighter = highlighter;
            _ownedCodeHighlighter = owner;
            _ownedCodeHighlighterService = highlighter;
        }
        catch
        {
            _ownedCodeHighlighter = previousOwner;
            _ownedCodeHighlighterService = previousService;
            throw;
        }

        if (previousOwner is not null && !ReferenceEquals(previousOwner, owner))
            RetireOwnedCodeHighlighter(previousOwner, _codeBlockHighlightRetirement);
    }

    /// <summary>Dependency property backing <see cref="StyleSheet"/>.</summary>
    public static readonly DependencyProperty StyleSheetProperty =
        DependencyProperty.Register(
            nameof(StyleSheet),
            typeof(MarkdownStyleSheet),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Restyle)));

    /// <summary>Gets or sets immutable semantic style rules layered over the active theme.</summary>
    public MarkdownStyleSheet? StyleSheet
    {
        get => (MarkdownStyleSheet?)GetValue(StyleSheetProperty);
        set => SetValue(StyleSheetProperty, value);
    }

    /// <summary>Dependency property backing <see cref="StringProvider"/>.</summary>
    public static readonly DependencyProperty StringProviderProperty =
        DependencyProperty.Register(
            nameof(StringProvider),
            typeof(IMarkdownStringProvider),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnStringProviderChanged()));

    /// <summary>Gets or sets a host provider for localized visible and automation strings.</summary>
    public IMarkdownStringProvider? StringProvider
    {
        get => (IMarkdownStringProvider?)GetValue(StringProviderProperty);
        set => SetValue(StringProviderProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CommandProvider"/>.</summary>
    public static readonly DependencyProperty CommandProviderProperty =
        DependencyProperty.Register(
            nameof(CommandProvider),
            typeof(IMarkdownCommandProvider),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets a host provider for target-aware markdown commands.</summary>
    public IMarkdownCommandProvider? CommandProvider
    {
        get => (IMarkdownCommandProvider?)GetValue(CommandProviderProperty);
        set => SetValue(CommandProviderProperty, value);
    }

    /// <summary>Dependency property backing <see cref="HostedElementFactory"/>.</summary>
    public static readonly DependencyProperty HostedElementFactoryProperty =
        DependencyProperty.Register(
            nameof(HostedElementFactory),
            typeof(IMarkdownHostedElementFactory),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnHostedElementFactoryChanged()));

    /// <summary>Gets or sets the viewport-aware factory for declarative hosted content.</summary>
    public IMarkdownHostedElementFactory? HostedElementFactory
    {
        get => (IMarkdownHostedElementFactory?)GetValue(HostedElementFactoryProperty);
        set => SetValue(HostedElementFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Theme"/>.</summary>
    public static readonly DependencyProperty ThemeProperty =
        DependencyProperty.Register(nameof(Theme), typeof(MarkdownTheme), typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnThemeDpChanged(e)));

    /// <summary>Gets or sets the renderer theme.</summary>
    public MarkdownTheme? Theme
    {
        get => (MarkdownTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    internal static readonly DependencyProperty ExtensionRegistryProperty =
        DependencyProperty.Register(nameof(ExtensionRegistry), typeof(MarkdownExtensionRegistry),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnParserConfigurationChanged()));

    internal MarkdownExtensionRegistry? ExtensionRegistry
    {
        get => (MarkdownExtensionRegistry?)GetValue(ExtensionRegistryProperty);
        set => SetValue(ExtensionRegistryProperty, value);
    }

    internal static readonly DependencyProperty EmbedFactoryProperty =
        DependencyProperty.Register(nameof(EmbedFactory), typeof(IMarkdownEmbedFactory),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    internal IMarkdownEmbedFactory? EmbedFactory
    {
        get => (IMarkdownEmbedFactory?)GetValue(EmbedFactoryProperty);
        set => SetValue(EmbedFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageResolver"/>.</summary>
    public static readonly DependencyProperty ImageResolverProperty =
        DependencyProperty.Register(nameof(ImageResolver), typeof(IMarkdownImageResolver),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets the host-specific resolver consulted before public image loading.</summary>
    public IMarkdownImageResolver? ImageResolver
    {
        get => (IMarkdownImageResolver?)GetValue(ImageResolverProperty);
        set => SetValue(ImageResolverProperty, value);
    }

    /// <summary>Dependency property backing <see cref="SvgRenderer"/>.</summary>
    public static readonly DependencyProperty SvgRendererProperty =
        DependencyProperty.Register(nameof(SvgRenderer), typeof(IMarkdownSvgRenderer),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, static (d, e) =>
                ((MarkdownRendererControl)d).OnSvgRendererChanged(e)));

    /// <summary>
    /// Gets or sets the shared provider used to render admitted static SVG
    /// images. The control borrows this service and never disposes it.
    /// </summary>
    public IMarkdownSvgRenderer? SvgRenderer
    {
        get => (IMarkdownSvgRenderer?)GetValue(SvgRendererProperty);
        set => SetValue(SvgRendererProperty, value);
    }

    /// <summary>Dependency property backing <see cref="RasterDecoder"/>.</summary>
    public static readonly DependencyProperty RasterDecoderProperty =
        DependencyProperty.Register(nameof(RasterDecoder), typeof(IMarkdownRasterDecoder),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, static (d, _) =>
                ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>
    /// Gets or sets the optional shared provider used to decode admitted static
    /// raster formats. The control borrows this service and never disposes it.
    /// </summary>
    public IMarkdownRasterDecoder? RasterDecoder
    {
        get => (IMarkdownRasterDecoder?)GetValue(RasterDecoderProperty);
        set => SetValue(RasterDecoderProperty, value);
    }

    /// <summary>Dependency property backing <see cref="PerformanceSession"/>.</summary>
    public static readonly DependencyProperty PerformanceSessionProperty =
        DependencyProperty.Register(nameof(PerformanceSession), typeof(IMarkdownPerformanceSession),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) =>
                ((MarkdownRendererControl)d).OnPerformanceSessionChanged(e)));

    /// <summary>
    /// Gets or sets the optional, host-owned resource preparation session. The
    /// control borrows this session and never disposes it.
    /// </summary>
    public IMarkdownPerformanceSession? PerformanceSession
    {
        get => (IMarkdownPerformanceSession?)GetValue(PerformanceSessionProperty);
        set => SetValue(PerformanceSessionProperty, value);
    }

    private void OnPerformanceSessionChanged(DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not null and not IMarkdownPerformanceSessionInternal)
        {
            SetValue(PerformanceSessionProperty, args.OldValue);
            throw new ArgumentException(
                "The performance session must be supplied by the optional MarkdownRenderer.Performance package.",
                nameof(PerformanceSession));
        }
        DetachPerformanceSessionDisposalNotification();
        if (args.OldValue is IMarkdownPerformanceSessionInternal oldSession)
        {
            _snapshotRetirements.Track(oldSession.ReleaseDeferredSceneDocument(this));
            oldSession.ReleaseDocumentScope(this);
        }
        AttachPerformanceSessionDisposalNotification();
        RequestRebuild(RebuildReason.Full);
        if (_selection.IsActive)
            UpdateSelectionOverlay();
        else if (HasInteractiveTextAdornerContent())
            UpdateSelectionAdornerViewport();
    }

    private void AttachPerformanceSessionDisposalNotification()
    {
        if (_isDisposed || _isUnloaded ||
            PerformanceSession is not IMarkdownPerformanceSessionInternal session)
        {
            return;
        }

        session.Disposed -= OnPerformanceSessionDisposed;
        Volatile.Write(ref _subscribedPerformanceSession, session);
        session.Disposed += OnPerformanceSessionDisposed;
    }

    private void DetachPerformanceSessionDisposalNotification()
    {
        IMarkdownPerformanceSessionInternal? previous =
            Interlocked.Exchange(ref _subscribedPerformanceSession, null);
        if (previous is not null)
            previous.Disposed -= OnPerformanceSessionDisposed;
    }

    private void OnPerformanceSessionDisposed(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _subscribedPerformanceSession)) ||
            Volatile.Read(ref _isDisposed) ||
            Volatile.Read(ref _isUnloaded))
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, Volatile.Read(ref _subscribedPerformanceSession)) &&
                !_isDisposed && !_isUnloaded)
            {
                // RebuildInternalAsync detects the disposed borrowed session
                // and uses the eager engine/legacy parser path, replacing any
                // committed code fallback with its native scene.
                RequestRebuild(RebuildReason.Full);
            }
        });
    }

    private void OnSvgRendererChanged(DependencyPropertyChangedEventArgs args)
    {
        IMarkdownSvgRenderer? previous = Interlocked.Exchange(ref _subscribedSvgRenderer, null);
        if (previous is not null)
            previous.CacheInvalidated -= OnSvgRendererCacheInvalidated;
        if (!_isUnloaded && args.NewValue is IMarkdownSvgRenderer current)
        {
            current.CacheInvalidated -= OnSvgRendererCacheInvalidated;
            Volatile.Write(ref _subscribedSvgRenderer, current);
            current.CacheInvalidated += OnSvgRendererCacheInvalidated;
        }
        RequestRebuild(RebuildReason.Full);
    }

    private void OnSvgRendererCacheInvalidated(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _subscribedSvgRenderer)) ||
            Volatile.Read(ref _isDisposed) ||
            Volatile.Read(ref _isUnloaded))
            return;

        void InvalidateProviderRasters()
        {
            if (!ReferenceEquals(sender, Volatile.Read(ref _subscribedSvgRenderer)) ||
                _isDisposed ||
                _isUnloaded)
                return;
            RequestRebuild(RebuildReason.Full);
            InvalidateCanvas();
        }

        if (DispatcherQueue.HasThreadAccess)
            InvalidateProviderRasters();
        else
            _ = DispatcherQueue.TryEnqueue(InvalidateProviderRasters);
    }

    /// <summary>Dependency property backing <see cref="ImageBaseUri"/>.</summary>
    public static readonly DependencyProperty ImageBaseUriProperty =
        DependencyProperty.Register(nameof(ImageBaseUri), typeof(Uri),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets the base URI used to resolve relative image sources.</summary>
    public Uri? ImageBaseUri
    {
        get => (Uri?)GetValue(ImageBaseUriProperty);
        set => SetValue(ImageBaseUriProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageDocumentPath"/>.</summary>
    public static readonly DependencyProperty ImageDocumentPathProperty =
        DependencyProperty.Register(nameof(ImageDocumentPath), typeof(string),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets the source document path used by host image resolvers.</summary>
    public string? ImageDocumentPath
    {
        get => (string?)GetValue(ImageDocumentPathProperty);
        set => SetValue(ImageDocumentPathProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageDocumentSource"/>.</summary>
    public static readonly DependencyProperty ImageDocumentSourceProperty =
        DependencyProperty.Register(nameof(ImageDocumentSource), typeof(MarkdownDocumentSource),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets the stable identity and repository context of this document.</summary>
    public MarkdownDocumentSource? ImageDocumentSource
    {
        get => (MarkdownDocumentSource?)GetValue(ImageDocumentSourceProperty);
        set => SetValue(ImageDocumentSourceProperty, value);
    }

    /// <summary>Dependency property backing <see cref="AllowThirdPartyRemoteImages"/>.</summary>
    public static readonly DependencyProperty AllowThirdPartyRemoteImagesProperty =
        DependencyProperty.Register(
            nameof(AllowThirdPartyRemoteImages),
            typeof(bool),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(false, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>
    /// Gets or sets whether the host's image policy permits third-party HTTPS images.
    /// The default is <see langword="false"/>. A host can set this once as an application
    /// policy or enable it after obtaining per-document consent. Insecure HTTP images
    /// remain blocked regardless of this value.
    /// </summary>
    public bool AllowThirdPartyRemoteImages
    {
        get => (bool)GetValue(AllowThirdPartyRemoteImagesProperty);
        set => SetValue(AllowThirdPartyRemoteImagesProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsSelectionEnabled"/>.</summary>
    public static readonly DependencyProperty IsSelectionEnabledProperty =
        DependencyProperty.Register(nameof(IsSelectionEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, static (d, e) =>
                ((MarkdownRendererControl)d).OnSelectionEnabledChanged((bool)e.NewValue)));

    /// <summary>
    /// Gets or sets whether read-only text selection is available to mouse,
    /// keyboard, pen, touch, and UI Automation clients.
    /// </summary>
    public bool IsSelectionEnabled
    {
        get => (bool)GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsCodeBlockCopyEnabled"/>.</summary>
    public static readonly DependencyProperty IsCodeBlockCopyEnabledProperty =
        DependencyProperty.Register(nameof(IsCodeBlockCopyEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets whether code blocks include an always-visible copy button.</summary>
    public bool IsCodeBlockCopyEnabled
    {
        get => (bool)GetValue(IsCodeBlockCopyEnabledProperty);
        set => SetValue(IsCodeBlockCopyEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockCopyButtonLabel"/>.</summary>
    public static readonly DependencyProperty CodeBlockCopyButtonLabelProperty =
        DependencyProperty.Register(nameof(CodeBlockCopyButtonLabel), typeof(string),
            typeof(MarkdownRendererControl), new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).UpdateCodeBlockCopyButtonLabels()));

    /// <summary>
    /// Gets or sets the accessible label and tooltip text used for code-block copy buttons.
    /// A null or whitespace value uses the renderer's localized default.
    /// </summary>
    public string? CodeBlockCopyButtonLabel
    {
        get => (string?)GetValue(CodeBlockCopyButtonLabelProperty);
        set => SetValue(CodeBlockCopyButtonLabelProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockCopiedButtonLabel"/>.</summary>
    public static readonly DependencyProperty CodeBlockCopiedButtonLabelProperty =
        DependencyProperty.Register(nameof(CodeBlockCopiedButtonLabel), typeof(string),
            typeof(MarkdownRendererControl), new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).UpdateCodeBlockCopyButtonLabels()));

    /// <summary>
    /// Gets or sets the accessible label and tooltip text announced briefly after a code block is copied.
    /// A null or whitespace value uses the renderer's localized default.
    /// </summary>
    public string? CodeBlockCopiedButtonLabel
    {
        get => (string?)GetValue(CodeBlockCopiedButtonLabelProperty);
        set => SetValue(CodeBlockCopiedButtonLabelProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockCopyButtonStyle"/>.</summary>
    public static readonly DependencyProperty CodeBlockCopyButtonStyleProperty =
        DependencyProperty.Register(
            nameof(CodeBlockCopyButtonStyle),
            typeof(Style),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).UpdateCodeBlockCopyButtonStyles()));

    /// <summary>Gets or sets the host-provided style for code-block copy buttons.</summary>
    public Style? CodeBlockCopyButtonStyle
    {
        get => (Style?)GetValue(CodeBlockCopyButtonStyleProperty);
        set => SetValue(CodeBlockCopyButtonStyleProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsCodeBlockSyntaxHighlightingEnabled"/>.</summary>
    public static readonly DependencyProperty IsCodeBlockSyntaxHighlightingEnabledProperty =
        DependencyProperty.Register(nameof(IsCodeBlockSyntaxHighlightingEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets whether code blocks may request syntax highlighting from a configured provider.</summary>
    public bool IsCodeBlockSyntaxHighlightingEnabled
    {
        get => (bool)GetValue(IsCodeBlockSyntaxHighlightingEnabledProperty);
        set => SetValue(IsCodeBlockSyntaxHighlightingEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockSyntaxHighlighter"/>.</summary>
    [Obsolete("Use CodeHighlighterProperty. This compatibility alias shares the same provider backing.")]
    public static readonly DependencyProperty CodeBlockSyntaxHighlighterProperty =
        DependencyProperty.Register(nameof(CodeBlockSyntaxHighlighter), typeof(ICodeBlockSyntaxHighlighter),
            typeof(MarkdownRendererControl), new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnLegacyCodeHighlighterChanged(e)));

    /// <summary>
    /// Gets or sets the preview syntax-highlighting provider. This compatibility
    /// alias is adapted to <see cref="CodeHighlighter"/> and does not create a
    /// second highlighting pipeline.
    /// </summary>
    [Obsolete("Use CodeHighlighter for explicit cancellation support.")]
    public ICodeBlockSyntaxHighlighter? CodeBlockSyntaxHighlighter
    {
        get => (ICodeBlockSyntaxHighlighter?)GetValue(CodeBlockSyntaxHighlighterProperty);
        set => SetValue(CodeBlockSyntaxHighlighterProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsTaskListEditingEnabled"/>.</summary>
    public static readonly DependencyProperty IsTaskListEditingEnabledProperty =
        DependencyProperty.Register(
            nameof(IsTaskListEditingEnabled),
            typeof(bool),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(false, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>
    /// Gets or sets whether task-list markers may invoke host-provided
    /// <see cref="MarkdownCommandKind.ToggleTask"/> commands. The default is
    /// false and exposes read-only task semantics without UIA TogglePattern.
    /// </summary>
    public bool IsTaskListEditingEnabled
    {
        get => (bool)GetValue(IsTaskListEditingEnabledProperty);
        set => SetValue(IsTaskListEditingEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeHighlighter"/>.</summary>
    public static readonly DependencyProperty CodeHighlighterProperty =
        DependencyProperty.Register(
            nameof(CodeHighlighter),
            typeof(ICodeHighlighter),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, static (d, e) =>
                ((MarkdownRendererControl)d).OnCodeHighlighterChanged(e)));

    /// <summary>
    /// Gets or sets the stable asynchronous, cancellation-aware code highlighter.
    /// </summary>
    public ICodeHighlighter? CodeHighlighter
    {
        get => (ICodeHighlighter?)GetValue(CodeHighlighterProperty);
        set => SetValue(CodeHighlighterProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockLineNumberMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockLineNumberModeProperty =
        DependencyProperty.Register(nameof(CodeBlockLineNumberMode), typeof(CodeBlockLineNumberMode),
            typeof(MarkdownRendererControl), new PropertyMetadata(CodeBlockLineNumberMode.AutoMultiline, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets when code blocks show line numbers.</summary>
    public CodeBlockLineNumberMode CodeBlockLineNumberMode
    {
        get => (CodeBlockLineNumberMode)GetValue(CodeBlockLineNumberModeProperty);
        set => SetValue(CodeBlockLineNumberModeProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockChromeMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockChromeModeProperty =
        DependencyProperty.Register(
            nameof(CodeBlockChromeMode),
            typeof(CodeBlockChromeMode),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(
                CodeBlockChromeMode.Enhanced,
                (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>Gets or sets whether code blocks use enhanced headers or plain Markdown chrome.</summary>
    public CodeBlockChromeMode CodeBlockChromeMode
    {
        get => (CodeBlockChromeMode)GetValue(CodeBlockChromeModeProperty);
        set => SetValue(CodeBlockChromeModeProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockWrappingMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockWrappingModeProperty =
        DependencyProperty.Register(
            nameof(CodeBlockWrappingMode),
            typeof(CodeBlockWrappingMode),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(
                CodeBlockWrappingMode.NoWrap,
                (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full)));

    /// <summary>
    /// Gets or sets whether long code lines remain unwrapped with a local
    /// horizontal viewport or wrap to the available width.
    /// </summary>
    public CodeBlockWrappingMode CodeBlockWrappingMode
    {
        get => (CodeBlockWrappingMode)GetValue(CodeBlockWrappingModeProperty);
        set => SetValue(CodeBlockWrappingModeProperty, value);
    }

    /// <summary>
    /// Creates a renderer configured for the core CommonMark feature set.
    /// </summary>
    /// <param name="markdown">Initial markdown source text.</param>
    /// <param name="theme">Theme to assign, or null to use the renderer default.</param>
    /// <param name="isSelectionEnabled">True to enable text selection.</param>
    /// <param name="isCodeBlockCopyEnabled">True to show copy buttons on code blocks.</param>
    /// <param name="codeBlockSyntaxHighlighter">Optional code-block syntax-highlighting provider.</param>
    /// <param name="codeBlockCopyButtonLabel">Accessible label and tooltip for code-block copy buttons, or null for the localized default.</param>
    /// <param name="codeBlockCopiedButtonLabel">Accessible label and tooltip after copy succeeds, or null for the localized default.</param>
    /// <param name="imageResolver">Optional host-specific image resolver used before public URI loading.</param>
    /// <param name="imageBaseUri">Optional base URI used to resolve relative image sources.</param>
    /// <param name="imageDocumentPath">Optional source document path used by host image resolvers.</param>
    /// <param name="codeBlockWrappingMode">Long-line wrapping policy. The default preserves lines and enables local horizontal scrolling.</param>
    /// <returns>A new configured viewport-owning renderer.</returns>
    public static MarkdownScrollView CreateDefault(
        string? markdown = null,
        MarkdownTheme? theme = null,
        bool isSelectionEnabled = true,
        bool isCodeBlockCopyEnabled = true,
        ICodeBlockSyntaxHighlighter? codeBlockSyntaxHighlighter = null,
        string? codeBlockCopyButtonLabel = null,
        string? codeBlockCopiedButtonLabel = null,
        IMarkdownImageResolver? imageResolver = null,
        Uri? imageBaseUri = null,
        string? imageDocumentPath = null,
        CodeBlockWrappingMode codeBlockWrappingMode = CodeBlockWrappingMode.NoWrap)
        => new MarkdownRendererControlBuilder()
            .WithMarkdown(markdown)
            .WithTheme(theme)
            .WithImageResolver(imageResolver)
            .WithImageBaseUri(imageBaseUri)
            .WithImageDocumentPath(imageDocumentPath)
            .WithSelectionEnabled(isSelectionEnabled)
            .WithCodeBlockCopyEnabled(isCodeBlockCopyEnabled)
            .WithCodeBlockCopyButtonLabel(codeBlockCopyButtonLabel)
            .WithCodeBlockCopiedButtonLabel(codeBlockCopiedButtonLabel)
            .WithCodeBlockSyntaxHighlighter(codeBlockSyntaxHighlighter)
            .WithCodeBlockWrappingMode(codeBlockWrappingMode)
            .BuildScrollView();

    internal MarkdownLinkPeer GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run)
    {
        // GetValue is atomic for concurrent UIA callers and avoids the
        // TryGetValue+Add race. These caches are cleared when the root peer
        // observes a new semantic snapshot; externally retained old peers are
        // fenced by their captured projection lifetime.
        return _linkPeerCache.GetValue(run, r => new MarkdownLinkPeer(this, parent, r));
    }

    internal void InvalidateAutomationChildPeerCaches()
    {
        _linkPeerCache.Clear();
        _linkedImagePeerCache.Clear();
    }

    private void NotifyAutomationSnapshotChanged()
    {
        // Do not create an automation peer just to release its previous
        // semantic projection; if a peer exists, reconcile it with the
        // already-published snapshot immediately.
        (FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer)?.NotifySnapshotChanged();
    }

    internal MarkdownLinkedImagePeer GetOrCreateLinkedImagePeer(
        MarkdownBlockPeer parent,
        InlineImageRun run) =>
        _linkedImagePeerCache.GetValue(run, r => new MarkdownLinkedImagePeer(this, parent, r));

    internal bool TryGetLinkedImagePeer(
        InlineImageRun run,
        out MarkdownLinkedImagePeer peer)
    {
        if (_linkedImagePeerCache.TryGetValue(run, out MarkdownLinkedImagePeer? cached) &&
            cached is not null)
        {
            peer = cached;
            return true;
        }

        peer = null!;
        return false;
    }

    internal bool IsKeyboardFocusOnLink(LinkRun run)
    {
        return TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
               linkedRun is LinkRun focusedRun &&
               AreEquivalentLinks(focusedRun, run);
    }

    internal bool IsKeyboardFocusOnLinkedImage(InlineImageRun run) =>
        TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
        linkedRun is InlineImageRun focusedRun &&
        ReferenceEquals(focusedRun, run);

    internal bool IsKeyboardFocusOnLinkedInlineVectorScene(InlineVectorSceneRun run) =>
        TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
        linkedRun is InlineVectorSceneRun focusedRun &&
        ReferenceEquals(focusedRun, run);

    internal bool IsKeyboardFocusOnVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex) =>
        FocusState != FocusState.Unfocused &&
        TryGetFocusedVectorSemantic(out var focusedBox, out var focusedSemanticIndex) &&
        ReferenceEquals(focusedBox, box) &&
        focusedSemanticIndex == semanticIndex;

    internal bool IsKeyboardFocusOnHorizontalOverflow(IHorizontalOverflowBox overflow) =>
        FocusState != FocusState.Unfocused &&
        TryGetFocusedHorizontalOverflow(out var focusedOverflow) &&
        ReferenceEquals(focusedOverflow, overflow);

    internal bool IsHorizontalOverflowKeyboardFocusable(IHorizontalOverflowBox overflow) =>
        TryGetFocusableIndexForHorizontalOverflow(overflow, out _);

    internal bool HasKeyboardFocusOnVirtualChild =>
        FocusState != FocusState.Unfocused &&
        (TryGetFocusedLinkedAtomic(out _, out _) ||
         TryGetFocusedVectorSemantic(out _, out _) ||
         TryGetFocusedHorizontalOverflow(out _));

    internal bool FocusLinkFromAutomation(LinkRun run) =>
        FocusLinkedAtomicFromAutomation(run);

    internal bool FocusLinkedImageFromAutomation(InlineImageRun run) =>
        FocusLinkedAtomicFromAutomation(run);

    internal bool FocusInlineVectorSceneFromAutomation(InlineVectorSceneRun run) =>
        FocusLinkedAtomicFromAutomation(run);

    private bool FocusLinkedAtomicFromAutomation(InlineRun run)
    {
        if (!TryGetFocusableIndexForLinkedAtomic(run, out var index))
            return Focus(FocusState.Programmatic);

        _pendingLazyFocus = null;
        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        BeginFocusedItemReveal();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusVectorSemanticFromAutomation(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex)
    {
        if (!TryGetFocusableIndexForVectorSemantic(box, semanticIndex, out int index))
            return false;

        _pendingLazyFocus = null;
        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        BeginFocusedItemReveal();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusHorizontalOverflowFromAutomation(IHorizontalOverflowBox overflow)
    {
        if (!TryGetFocusableIndexForHorizontalOverflow(overflow, out int index))
            return false;

        _pendingLazyFocus = null;
        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        BeginFocusedItemReveal();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusDocumentFromAutomation()
    {
        _suppressNextFocusPromotion = true;
        _pendingLazyFocus = null;
        _focusedItemIndex = -1;
        UpdateFocusRing();
        bool focused = Focus(FocusState.Programmatic);
        if (!focused)
            _suppressNextFocusPromotion = false;
        else
            DispatcherQueue.TryEnqueue(() => _suppressNextFocusPromotion = false);
        return focused;
    }

    /// <summary>Invoked by <see cref="MarkdownLinkPeer.Invoke"/> so UIA clients
    /// can activate a link the same way as pointer interaction. UIA callers
    /// can arrive on the RPC thread, so marshal back to the UI dispatcher
    /// before raising the public event.</summary>
    internal void RaiseLinkClickFromAutomation(LinkRun run)
    {
        RaiseLinkedAtomicClickFromAutomation(run);
    }

    internal void RaiseLinkedImageClickFromAutomation(InlineImageRun run)
    {
        RaiseLinkedAtomicClickFromAutomation(run);
    }

    internal void RaiseInlineVectorSceneClickFromAutomation(InlineVectorSceneRun run)
    {
        RaiseLinkedAtomicClickFromAutomation(run);
    }

    internal void RaiseLinkedAtomicClickFromAutomation(InlineRun run)
    {
        if (TryCreateLinkedAtomicTarget(run, out LinkTarget target))
            DispatchAutomationLinkActivation(target);
    }

    internal static bool TryCreateLinkedAtomicTarget(InlineRun run, out LinkTarget target)
    {
        ArgumentNullException.ThrowIfNull(run);
        switch (run)
        {
            case LinkRun link:
                target = new LinkTarget(
                    link.Url,
                    link.Title,
                    link.SourceSpan,
                    link);
                return true;
            case InlineImageRun { IsLinked: true, LinkUrl: { } imageUrl } image:
                target = new LinkTarget(
                    imageUrl,
                    image.LinkTitle,
                    image.SourceSpan,
                    null);
                return true;
            case InlineVectorSceneRun { IsLinked: true, LinkUrl: { } sceneUrl } scene:
                target = new LinkTarget(
                    sceneUrl,
                    scene.LinkTitle,
                    scene.SourceSpan,
                    null);
                return true;
            default:
                target = default;
                return false;
        }
    }

    internal void SetDisclosureExpanded(LinkRun run, bool expanded)
    {
        if (run is not { IsDisclosure: true, DisclosureId: { Length: > 0 } id } ||
            run.IsExpanded == expanded)
        {
            return;
        }

        SetDisclosureExpanded(id, expanded);
    }

    private void SetDisclosureExpanded(string id, bool expanded)
    {
        void Apply()
        {
            _disclosureStates[id] = expanded;
            DisclosureToggled?.Invoke(this, new MarkdownDisclosureToggledEventArgs(expanded));
            RequestRebuild(RebuildReason.Restyle);
        }

        if (DispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    /// <summary>Raised when the user activates a non-internal markdown link.</summary>
    public event EventHandler<MarkdownLinkClickEventArgs>? LinkClick;

    /// <summary>Raised after a safe HTML disclosure is expanded or collapsed.</summary>
    public event EventHandler<MarkdownDisclosureToggledEventArgs>? DisclosureToggled;

    /// <summary>Raised after a selection or code block clipboard copy is attempted.</summary>
    public event EventHandler<MarkdownCopyCompletedEventArgs>? CopyCompleted;

    /// <summary>Raised when an image source is deliberately blocked or unavailable.</summary>
    public event EventHandler<MarkdownImageUnavailableEventArgs>? ImageUnavailable;

    /// <summary>Raised after the current Markdown document is rendered successfully.</summary>
    public event EventHandler? RenderCompleted;

    /// <summary>Raised when the current Markdown document cannot be rendered.</summary>
    public event EventHandler<MarkdownRenderFailedEventArgs>? RenderFailed;

    private void RaiseImageUnavailable(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason)
    {
        if (_isDisposed || reason == MarkdownImageUnavailableReason.None)
            return;

        void Raise()
        {
            if (!_isDisposed)
                ImageUnavailable?.Invoke(this, new MarkdownImageUnavailableEventArgs(source, reason, svgFailureReason));
        }

        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            dispatcher.TryEnqueue(Raise);
        else
            Raise();
    }

    /// <summary>
    /// Raised after every embed realisation pass (initial layout commit,
    /// scroll, resize). Subscribers can use it to surface
    /// <see cref="RealizedEmbedCount"/> for diagnostics or UI-automation
    /// tests without polluting the control's UIA surface.
    /// </summary>
    public event EventHandler? EmbedsRealizationChanged;

    /// <summary>
    /// Current vertical offset owned by this control. An ancestor-owned
    /// effective viewport is already represented by the control's transform to
    /// screen coordinates and must not be subtracted a second time.
    /// </summary>
    internal double CurrentScrollOffsetY => _scroll?.VerticalOffset ?? 0;

    internal bool OwnsScrollViewport => _ownsScrollViewport;

    /// <summary>
    /// Internal gate-harness signal used to ensure a warm scroll trace starts
    /// only after queued/background band realization has retired.
    /// </summary>
    internal bool HasPendingLazyLayoutWork =>
        _lazyLayoutDispatchQueued ||
        _hasPendingLazyLayoutBand ||
        _lazyLayoutPublicationHandoff.HasPendingPublication ||
        _pendingLayoutCommit is not null ||
        _lazyLayoutTask is { IsCompleted: false };

    /// <summary>
    /// Returns a completion task for every renderer-owned resource or work item
    /// retired before this call. Failures from that drain epoch are surfaced once; later drains
    /// are not permanently poisoned by an earlier failed retirement.
    /// Production teardown remains nonblocking; the performance harness uses
    /// this internal seam to prevent disposal work from contaminating the next
    /// independent sample.
    /// </summary>
    internal Task DrainRetiredSnapshotsAsync()
        => _snapshotRetirements.DrainAsync();

    /// <summary>
    /// Releases device-bound snapshot resources before the performance harness
    /// deliberately replaces Win2D's shared device. Real device-loss HRESULTs
    /// are handled by the normal paint recovery path; this avoids a synthetic
    /// Dispose race producing an unrelated cross-factory E_FAIL.
    /// </summary>
    internal void PrepareForSyntheticDeviceReset()
    {
        ResetVectorCapacityObserver();
        CancelLazyLayoutRealization();
        _pendingLayoutCommit = null;
        LayoutSnapshot? snapshot = _snapshot;
        _snapshot = null;
        _automationDocumentGenerations.Invalidate();
        NotifyAutomationSnapshotChanged();
        _snapshotPipelineStartTimestamp = 0;
        RetireSnapshot(snapshot);
    }

    /// <summary>Raises a host-approved vector-scene link/action from UI Automation.</summary>
    internal void RaiseVectorSceneLinkClickFromAutomation(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex)
    {
        if (box is null || !box.TryGetLink(semanticIndex, out var link) || link is null)
            return;

        var semantic = box.GetSemanticItem(semanticIndex);
        DispatchAutomationLinkActivation(new LinkTarget(
            link.Target ?? string.Empty,
            semantic?.Description,
            semantic?.SourceSpan ?? box.SourceRange,
            null,
            link.Action,
            link.External));
    }

    private void DispatchAutomationLinkActivation(LinkTarget target)
    {
        void Activate() => ActivateLinkTarget(
            target,
            MarkdownLinkInputKind.Automation,
            MarkdownInputModifiers.None);

        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            dispatcher.TryEnqueue(Activate);
        else
            Activate();
    }

    internal bool TryGetAutomationScrollMetrics(
        out double verticalOffset,
        out double viewportHeight,
        out double extentHeight)
    {
        if (_scroll is null)
        {
            verticalOffset = 0;
            viewportHeight = 0;
            extentHeight = 0;
            return false;
        }

        verticalOffset = _scroll.VerticalOffset;
        viewportHeight = _scroll.ViewportHeight;
        extentHeight = _scroll.ExtentHeight;
        return true;
    }

    internal void ScrollFromAutomation(ScrollAmount amount)
    {
        if (_scroll is null || amount == ScrollAmount.NoAmount)
            return;
        NoteViewportRequest();

        double delta = amount switch
        {
            ScrollAmount.LargeDecrement => -Math.Max(48, _scroll.ViewportHeight * 0.8),
            ScrollAmount.SmallDecrement => -48,
            ScrollAmount.SmallIncrement => 48,
            ScrollAmount.LargeIncrement => Math.Max(48, _scroll.ViewportHeight * 0.8),
            _ => 0,
        };
        double maximum = Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight);
        double target = Math.Clamp(_scroll.VerticalOffset + delta, 0, maximum);
        _scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    internal void SetAutomationVerticalScrollPercent(double percent)
    {
        if (_scroll is null)
            return;
        NoteViewportRequest();

        double maximum = Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight);
        double target = maximum * (Math.Clamp(percent, 0, 100) / 100);
        _scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    /// <summary>
    /// Offset applied between the scroll viewport and document content. The
    /// renderer pins short documents to the top of the viewport, so this is
    /// normally zero.
    /// </summary>
    internal double CurrentContentOffsetY => 0;

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new MarkdownAutomationPeer(this);

    /// <summary>
    /// Internal accessor for the current layout snapshot. Used by the
    /// automation peer to walk the document structure (headings, links, etc.).
    /// May be null before the first layout completes.
    /// </summary>
    internal LayoutSnapshot? CurrentSnapshot => _snapshot;
    internal long CurrentAutomationDocumentGeneration =>
        _automationDocumentGenerations.CurrentGeneration;

    /// <summary>
    /// Last committed theme snapshot. Used by automation peers to expose UIA
    /// text attributes that match the pixels currently painted on the canvas.
    /// </summary>
    internal Theming.ThemeSnapshot? CurrentThemeSnapshot => _themeSnapshot;

    /// <summary>Effective document-language LCID exposed through UI Automation.</summary>
    internal int AutomationCultureLcid
    {
        get
        {
            return AutomationCulture.LCID;
        }
    }

    /// <summary>Culture resolved from the same language snapshot used by the committed layout.</summary>
    internal System.Globalization.CultureInfo AutomationCulture =>
        GetEffectiveLocalization().Culture;

    /// <summary>
    /// Number of currently-realised hosted embed elements (block + inline).
    /// Exposed for UI-automation tests that want to validate virtualisation
    /// without relying on heuristic descendant counts.
    /// </summary>
    public int RealizedEmbedCount
    {
        get
        {
            int n = 0;
            foreach (int ordinal in _activeEmbedOrdinals)
            {
                if (_embedPlans[ordinal].Realized is not null)
                    n++;
            }
            return n;
        }
    }

    private Button CreateCodeBlockCopyButton()
    {
        var button = new Button
        {
            Content = new SymbolIcon(Symbol.Copy),
            Style = CodeBlockCopyButtonStyle,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(button, "MarkdownCodeBlockCopyButton");
        SetCodeBlockCopyButtonState(button, copied: false);
        return button;
    }

    private (Button Button, bool Attached) RentCodeBlockCopyButton()
    {
        if (_codeBlockCopyButtonPool.Count == 0)
            return (CreateCodeBlockCopyButton(), false);

        var pooled = _codeBlockCopyButtonPool.Pop();
        Button button = pooled.Button;
        if (!ReferenceEquals(button.Style, CodeBlockCopyButtonStyle))
            button.Style = CodeBlockCopyButtonStyle;
        return (button, pooled.Attached);
    }

    private void ReturnCodeBlockCopyButton(Button button)
    {
        bool keepAttached = !_derealizingAllEmbeds &&
                            _codeBlockCopyButtonPool.Count < CodeBlockCopyButtonPoolCapacity;
        button.Visibility = Visibility.Collapsed;
        if (_isDisposed || _isUnloaded)
        {
            try { _overlay?.Children.Remove(button); } catch { }
            return;
        }
        if (_codeBlockCopyButtonPool.Count < CodeBlockCopyButtonPoolCapacity)
        {
            _codeBlockCopyButtonPool.Push((button,
                keepAttached && ReferenceEquals(VisualTreeHelper.GetParent(button), _overlay)));
            return;
        }

        try { _overlay?.Children.Remove(button); } catch { }
    }

    internal string ResolvedCodeBlockCopyButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopyButtonLabel)
            ? ResolveLocalizedString(MarkdownStringKeys.CopyCode, MarkdownLocalizedStrings.CodeBlockCopyAutomationName)
            : CodeBlockCopyButtonLabel!;

    private string ResolvedCodeBlockCopiedButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopiedButtonLabel)
            ? ResolveLocalizedString(MarkdownStringKeys.CodeCopied, MarkdownLocalizedStrings.CodeBlockCopied)
            : CodeBlockCopiedButtonLabel!;

    internal string ResolveLocalizedString(string key, string fallback)
    {
        MarkdownLocalizationSnapshot localization = GetEffectiveLocalization();
        return ResolveLocalizedString(key, fallback, localization);
    }

    private static string ResolveLocalizedString(
        string key,
        string fallback,
        MarkdownLocalizationSnapshot localization)
    {
        var culture = localization.Culture;
        string localizedFallback = MarkdownLocalizedStrings.Resolve(key, fallback, culture);
        try
        {
            string? value = localization.StringProvider?.GetString(key, culture);
            return string.IsNullOrWhiteSpace(value) ? localizedFallback : value;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] String provider failed for '{key}': {ex.Message}");
            return localizedFallback;
        }
    }

    internal string ResolveFormattedLocalizedString(
        string key,
        string fallbackFormat,
        params object?[] arguments)
    {
        MarkdownLocalizationSnapshot localization = GetEffectiveLocalization();
        string format = ResolveLocalizedString(key, fallbackFormat, localization);
        try
        {
            return string.Format(localization.Culture, format, arguments);
        }
        catch (FormatException)
        {
            return string.Format(
                localization.Culture,
                fallbackFormat,
                arguments);
        }
    }

    private MarkdownLocalizationSnapshot GetEffectiveLocalization()
    {
        MarkdownLocalizationSnapshot? committed =
            MarkdownLocalizationSnapshot.SelectCommitted(
                _snapshot is not null,
                _committedLocalization);
        if (committed is not null)
            return committed;

        return CaptureRequestedLocalization();
    }

    private MarkdownLocalizationSnapshot CaptureRequestedLocalization() =>
        MarkdownLocalizationSnapshot.Capture(
            Language,
            _environmentSnapshot.Language,
            StringProvider,
            System.Globalization.CultureInfo.CurrentUICulture);

    private void OnStringProviderChanged()
    {
        if (_selectionStartHandle is not null)
            UpdateSelectionHandleLocalization(_selectionStartHandle, SelectionHandleEndpoint.Start);
        if (_selectionEndHandle is not null)
            UpdateSelectionHandleLocalization(_selectionEndHandle, SelectionHandleEndpoint.End);
        RequestRebuild(RebuildReason.Restyle);
    }

    private void SetCodeBlockCopyButtonState(Button button, bool copied)
    {
        string label = copied ? ResolvedCodeBlockCopiedButtonLabel : ResolvedCodeBlockCopyButtonLabel;
        if (button.Content is SymbolIcon icon)
            icon.Symbol = copied ? Symbol.Accept : Symbol.Copy;
        else
            button.Content = new SymbolIcon(copied ? Symbol.Accept : Symbol.Copy);

        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private bool SetCodeBlockCopyButtonStateForRasterCandidate(
        CodeBlockActionPlan plan,
        Button button,
        FrameworkElement element,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
    {
        if (!ReferenceEquals(button, element) ||
            !plan.IsCurrentRasterActionOwner(
                this, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        // Localization can call a host StringProvider. Resolve it before the
        // first visual mutation, then revalidate before touching this recycled
        // element. Every subsequent XAML property update is independently
        // fenced because dependency-property callbacks may reenter the host.
        string label = ResolvedCodeBlockCopyButtonLabel;
        if (!plan.IsCurrentRasterActionOwner(
                this, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        if (button.Content is SymbolIcon icon)
        {
            icon.Symbol = Symbol.Copy;
            if (!plan.IsCurrentRasterActionOwner(
                    this, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }
        else
        {
            button.Content = new SymbolIcon(Symbol.Copy);
            if (!plan.IsCurrentRasterActionOwner(
                    this, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }

        AutomationProperties.SetName(button, label);
        if (!plan.IsCurrentRasterActionOwner(
                this, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        ToolTipService.SetToolTip(button, label);
        return plan.IsCurrentRasterActionOwner(
            this, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element);
    }

    private void UpdateCodeBlockCopyButtonLabels()
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is Button button)
                SetCodeBlockCopyButtonState(button, copied: false);
        }
    }

    private void UpdateCodeBlockCopyButtonStyles()
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is Button button)
                button.Style = CodeBlockCopyButtonStyle;
        }
    }

    private void CopyCodeBlockToClipboard(CodeBlockActionPlan plan)
    {
        bool succeeded = false;
        try
        {
            var package = new DataPackage();
            package.SetText(plan.Box.CodeText);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            plan.ShowCopiedFeedback(this);
            succeeded = true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block copy failed: {ex.Message}");
        }

        RaiseCopyCompleted(MarkdownCopyKind.CodeBlock, succeeded);
    }

    internal void CopyCodeBlockFromAutomation(Layout.Boxes.CodeBlockBox box)
    {
        if (CodeBlockChromeMode == CodeBlockChromeMode.PlainMarkdown || !box.IsCopyButtonEnabled)
            return;

        foreach (CodeBlockActionPlan plan in _codeBlockActionPlans)
        {
            if (ReferenceEquals(plan.Box, box))
            {
                CopyCodeBlockToClipboard(plan);
                return;
            }
        }

        // UI Automation can invoke an off-screen code block before its
        // viewport-owned XAML button is realized. Copy directly without
        // realizing an overlay element or mutating the UIA child tree.
        CopyCodeBlockToClipboard(new CodeBlockActionPlan { Box = box });
    }

    private void RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
    {
        try
        {
            CopyCompleted?.Invoke(this, new MarkdownCopyCompletedEventArgs(kind, succeeded));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Copy completion subscriber failed: {ex.Message}");
        }
    }

    private void OnHostedEmbedKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (!TrySetFocusedItemForHostedElement(element)) return;

        if (e.Key == VirtualKey.Tab)
        {
            if (TryMoveFocusWithinHostedEmbed(element, e.OriginalSource, shift))
            {
                e.Handled = true;
                return;
            }

            e.Handled = MoveFocus(reverse: shift);
            return;
        }

        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down &&
            element is not TextBox)
        {
            e.Handled = MoveFocusSpatial(e.Key);
        }
    }

    /// <summary>
    /// The raw markdown source the renderer was last given. Useful for
    /// assistive technologies that want a flat textual representation when
    /// structural traversal isn't available.
    /// </summary>
    internal string CurrentMarkdownSource => Markdown ?? string.Empty;

    internal DocumentRange CurrentSelectionRange => _selection.Range;

    internal void SelectAutomationRange(DocumentRange range)
    {
        if (!IsSelectionEnabled) return;
        var normalized = range.Normalized();
        _selection.SetAnchor(normalized.Start);
        _selection.ExtendTo(normalized.End);
    }

    internal void ClearAutomationSelection() => _selection.Clear();

    internal bool TryGetVisibleDocumentRect(out Windows.Foundation.Rect rect)
    {
        bool hasViewport = TryGetViewport(out double top, out double height, out double width);
        rect = new Windows.Foundation.Rect(0, top, width, height);
        return hasViewport;
    }

    internal bool HasVisibleLoadingImagesForAutomation()
    {
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null)
            return false;

        bool hasViewport = TryGetVisibleDocumentRect(out Windows.Foundation.Rect viewport);
        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        ImagePlanPublication? publication = Volatile.Read(ref _imagePlanPublication);
        if (publication is null ||
            !IsCurrentImagePlanPublication(snapshot, snapshotGeneration, publication))
        {
            // A snapshot or its realization plan is between publications. Do
            // not let UIA announce readiness from a previous document/plan.
            return true;
        }

        // A readiness query must never wait behind DirectWrite/layout work. A
        // busy snapshot is conservatively reported as still loading and the
        // existing layout/paint status notifications will retry the query.
        if (!snapshot.TryBeginInteraction())
            return true;

        bool hasVisibleLoadingImage = true;
        long layoutRevision = -1;
        try
        {
            if (!IsCurrentImagePlanPublication(snapshot, snapshotGeneration, publication))
                return true;

            layoutRevision = snapshot.LayoutRevision;
            if (layoutRevision != publication.LayoutRevision)
            {
                // A lazy-measure or image-size commit invalidated the indexed
                // bounds. The replacement plan is published after the layout
                // lease is released; until then avoid mixing its bounds with
                // the previous index or membership.
                return true;
            }

            Layout.Boxes.ImageBox[] images = publication.Images;
            Rect[] imageBounds = publication.ImageBounds;
            if (imageBounds.Length != images.Length)
                return true;
            if (images.Length == 0)
            {
                hasVisibleLoadingImage = false;
            }
            else
            {
                ViewportBandIndex? imageIndex = hasViewport ? publication.Index : null;
                ViewportRange range = imageIndex is not null
                    ? imageIndex.Find(viewport.Top, viewport.Bottom)
                    : new ViewportRange(0, images.Length);
                hasVisibleLoadingImage = false;
                for (int index = range.Start; index < range.End; index++)
                {
                    int ordinal = imageIndex is not null
                        ? imageIndex.GetBlockOrdinal(index)
                        : index;
                    if ((uint)ordinal >= (uint)images.Length)
                    {
                        hasVisibleLoadingImage = true;
                        break;
                    }

                    Layout.Boxes.ImageBox image = images[ordinal];
                    if (!image.IsAwaitingVisiblePixelsForAutomation)
                        continue;
                    Rect bounds = imageBounds[ordinal];

                    if (!hasViewport ||
                        (bounds.Width > 0 &&
                         bounds.Height > 0 &&
                         bounds.Right > viewport.Left &&
                         bounds.Left < viewport.Right &&
                         bounds.Bottom > viewport.Top &&
                         bounds.Top < viewport.Bottom))
                    {
                        hasVisibleLoadingImage = true;
                        break;
                    }
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        // Snapshot replacement and plan publication are independent of the
        // layout lock. Fence once more after releasing it so a detached result
        // from a superseded page cannot become a false-ready UIA answer.
        return !IsCurrentImagePlanPublication(snapshot, snapshotGeneration, publication) ||
            snapshot.LayoutRevision != layoutRevision ||
            hasVisibleLoadingImage;
    }

    private bool IsCurrentImagePlanPublication(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        ImagePlanPublication publication) =>
        !_isDisposed && !_isUnloaded &&
        ReferenceEquals(snapshot, _snapshot) &&
        snapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
        ReferenceEquals(publication, Volatile.Read(ref _imagePlanPublication)) &&
        ReferenceEquals(snapshot, publication.Snapshot) &&
        snapshotGeneration == publication.SnapshotGeneration &&
        snapshot.LayoutRevision == publication.LayoutRevision;

    internal MarkdownVisibleSceneReadiness GetVisibleDeferredSceneStatusForAutomation()
    {
        IMarkdownPerformanceSessionInternal? session =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null)
            return session?.HasRegisteredDeferredSceneWork(this) == true
                ? MarkdownVisibleSceneReadiness.NotReady : MarkdownVisibleSceneReadiness.Ready;

        long generation = _pipelineGeneration;
        long revision = snapshot.LayoutRevision;
        if (!TryGetVisibleDocumentRect(out Rect viewport) || !IsFiniteNonEmptyAutomationRect(viewport))
        {
            return _snapshotGeneration != generation ||
                session?.HasRegisteredDeferredSceneWork(this) == true
                ? MarkdownVisibleSceneReadiness.NotReady : MarkdownVisibleSceneReadiness.Ready;
        }

        return GetVisibleDeferredSceneReadinessForAutomation(
            snapshot,
            generation,
            revision,
            viewport);
    }

    internal void NotifyVisibleDeferredSceneItemStatusMayHaveChanged()
    {
        if (_isDisposed || _isUnloaded)
            return;

        var peer = FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer;
        peer?.NotifyLoadingStatusMayHaveChanged();
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null || !snapshot.TryBeginInteraction())
        {
            if (snapshot is not null)
                Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 1);
            return;
        }
        try
        {
            if (snapshot.VectorSceneResidencyPolicy is { } policy)
            {
                try
                {
                    foreach (VectorSceneBox box in policy.GraphStatusChanges)
                        peer?.NotifyVectorSceneStatusChanged(box);
                }
                finally
                {
                    // Drain even without a UIA listener: completed changes must
                    // not become a second document-sized owner list.
                    policy.ClearGraphStatusChanges();
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private void RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded()
    {
        if (Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 0) == 0 ||
            _isDisposed ||
            _isUnloaded)
        {
            return;
        }

        NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
    }

    internal MarkdownVisibleSceneReadiness GetVisibleDeferredSceneReadinessForAutomation(
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision,
        Rect viewport)
    {
        if (expectedSnapshot is null ||
            expectedGeneration <= 0 ||
            !IsFiniteNonEmptyAutomationRect(viewport) ||
            !IsDeferredSceneSnapshotCurrent(
                expectedSnapshot,
                expectedGeneration,
                expectedLayoutRevision))
        {
            return MarkdownVisibleSceneReadiness.NotReady;
        }

        IMarkdownPerformanceSessionInternal? session =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
        if (session is not null)
        {
            MarkdownVisibleSceneReadiness readiness = session.GetVisibleDeferredSceneReadiness(
                this,
                expectedSnapshot,
                expectedGeneration,
                expectedLayoutRevision,
                viewport);
            if (readiness == MarkdownVisibleSceneReadiness.Busy)
                Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 1);
            return readiness;
        }

        // Without the optional session there are no deferred markers. Keep the
        // native-resource measured-band gate in the base renderer so eager
        // vector scenes remain covered for every consumer.
        if (!expectedSnapshot.TryBeginPaint())
        {
            Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 1);
            return MarkdownVisibleSceneReadiness.Busy;
        }

        try
        {
            if (!IsDeferredSceneSnapshotCurrent(
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision) ||
                !ReferenceEquals(PerformanceSession, session))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }

            LazyLayoutBand visibleBand = LazyLayoutBand.FromViewport(
                viewport.Top,
                viewport.Height,
                overscan: 0);
            return expectedSnapshot.IsBandMeasured(visibleBand) &&
                IsDeferredSceneSnapshotCurrent(
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision) &&
                ReferenceEquals(PerformanceSession, session)
                ? MarkdownVisibleSceneReadiness.Ready : MarkdownVisibleSceneReadiness.NotReady;
        }
        finally
        {
            expectedSnapshot.EndPaint();
        }
    }

    internal bool IsDeferredSceneSnapshotCurrent(
        LayoutSnapshot snapshot,
        long generation,
        long layoutRevision) =>
        ReferenceEquals(_snapshot, snapshot) &&
        _pipelineGeneration == generation &&
        _snapshotGeneration == generation &&
        snapshot.LayoutRevision == layoutRevision;

    private static bool IsFiniteNonEmptyAutomationRect(Rect rect) =>
        IsFiniteAutomationRect(rect) && rect.Width > 0 && rect.Height > 0;

    private static bool IsFiniteAutomationRect(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);

    internal long AutomationPipelineGeneration => _pipelineGeneration;

    internal CancellationToken GetAutomationPipelineCancellationToken(long generation)
    {
        CancellationTokenSource? cancellation = _pipelineCts;
        return generation > 0 &&
            generation == _activePipelineGeneration &&
            cancellation is not null
                ? cancellation.Token
                : new CancellationToken(canceled: true);
    }

    internal void ScrollDocumentRectIntoView(Windows.Foundation.Rect rect, bool alignToTop)
    {
        NoteViewportRequest();
        if (_scroll is null)
        {
            BringDocumentRectIntoExternalViewport(rect, animate: false, alignToTop ? 0 : 0.5);
            return;
        }
        const double margin = 24.0;
        double target = alignToTop
            ? rect.Top
            : rect.Top < _scroll.VerticalOffset + margin
                ? rect.Top - margin
                : rect.Bottom > _scroll.VerticalOffset + _scroll.ViewportHeight - margin
                    ? rect.Bottom - _scroll.ViewportHeight + margin
                    : _scroll.VerticalOffset;
        // UI Automation's ScrollIntoView contract is immediate. An animated
        // change can be superseded by lazy-layout realization before its first
        // frame, leaving the requested range offscreen.
        _scroll.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }

    /// <summary>Initializes a new markdown renderer control.</summary>
    public MarkdownRendererControl()
        : this(ownsScrollViewport: true)
    {
    }

    /// <summary>
    /// Initializes a renderer with either an owned scroll viewport or an
    /// externally supplied effective viewport.
    /// </summary>
    /// <param name="ownsScrollViewport">
    /// True to create the compatibility scroll viewer; false to participate in
    /// the nearest ancestor viewport through EffectiveViewportChanged.
    /// </param>
    protected MarkdownRendererControl(bool ownsScrollViewport)
    {
        _ownsScrollViewport = ownsScrollViewport;
        _canvasPointerMovedHandler = OnPointerMoved;
        _canvasPointerReleasedHandler = OnPointerReleased;
        _canvasPointerCanceledHandler = OnPointerCanceledOrCaptureLost;
        _canvasPointerCaptureLostHandler = OnPointerCanceledOrCaptureLost;
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += (_, _) => OnLoadedInternal();
        Unloaded += (_, _) => OnUnloaded();
        ActualThemeChanged += (_, _) => OnThemeChanged();
        // Selection changes update the XAML overlay (not the DirectWrite canvas),
        // so tiles are never invalidated during a drag.
        _selection.Changed += (_, _) => OnSelectionChanged();
        // FlowDirection has no DP-changed callback we can register at the
        // class level (it's defined by FrameworkElement). Listen for live
        // changes via the dependency-property-changed callback API so RTL
        // toggling at runtime rebuilds the layout instead of leaving stale
        // CanvasTextLayouts in the previous direction. Gate on IsLoaded so
        // the inheritance pass during initial visual-tree attach doesn't
        // race against the Loaded handler's own RequestRebuild.
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) =>
        {
            if (IsLoaded) RequestRebuild(RebuildReason.Full);
        });
        RegisterPropertyChangedCallback(LanguageProperty, (_, _) =>
        {
            if (IsLoaded) RequestRebuild(RebuildReason.Restyle);
        });
        BuildVisualTree();
    }

    private void BuildVisualTree()
    {
        if (_ownsScrollViewport)
        {
            _scroll = new ScrollViewer
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                ZoomMode = ZoomMode.Disabled,
            };
        }
        _root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _overlay = new Canvas
        {
            // Background null so the empty overlay doesn't capture pointer
            // events. Hosted embeds are individually hit-testable.
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root.Children.Add(_overlay);
        // Selection, hover and drag chrome is created only when used. A new
        // document does not need an Image, Border and two Button handles in
        // its XAML tree merely to paint its first viewport.
        if (_scroll is not null)
            _scroll.Content = _root;

        EnsureCanvas();
        GettingFocus += OnGettingFocus;
        GotFocus += OnGotFocus;
        LosingFocus += OnLosingFocus;
        KeyDown += OnKeyDown;

        if (_scroll is not null)
        {
            Content = _scroll;
        }
        else
        {
            EffectiveViewportChanged += OnEffectiveViewportChanged;
            Content = _root;
        }
    }

    private void EnsureCanvas()
    {
        if (_canvas is not null || _root is null || _isDisposed)
            return;
        _canvas = new CanvasVirtualControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDoubleTapEnabled = true,
            IsHoldingEnabled = true,
            IsTapEnabled = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root.Children.Insert(0, _canvas);
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerWheelChanged += OnPointerWheelChanged;
        _canvas.PointerExited += OnPointerExited;
        _canvas.Tapped += OnTapped;
        _canvas.DoubleTapped += OnDoubleTapped;
        _canvas.Holding += OnHolding;
        _canvas.RightTapped += OnRightTapped;
        _canvas.AddHandler(UIElement.PointerMovedEvent, _canvasPointerMovedHandler, handledEventsToo: true);
        _canvas.AddHandler(UIElement.PointerReleasedEvent, _canvasPointerReleasedHandler, handledEventsToo: true);
        _canvas.AddHandler(UIElement.PointerCanceledEvent, _canvasPointerCanceledHandler, handledEventsToo: true);
        _canvas.AddHandler(UIElement.PointerCaptureLostEvent, _canvasPointerCaptureLostHandler, handledEventsToo: true);
        AttachCanvasRenderHandlers();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (_ownsScrollViewport || _isDisposed)
            return;

        Rect viewport = args.EffectiveViewport;
        if (!double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) ||
            !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        bool materiallyChanged = !_hasExternalViewport ||
            Math.Abs(_externalViewport.X - viewport.X) >= 0.5 ||
            Math.Abs(_externalViewport.Y - viewport.Y) >= 0.5 ||
            Math.Abs(_externalViewport.Width - viewport.Width) >= 0.5 ||
            Math.Abs(_externalViewport.Height - viewport.Height) >= 0.5;
        _externalViewport = viewport;
        _hasExternalViewport = true;
        if (!materiallyChanged || _isUnloaded)
            return;

        QueueLazyLayoutForViewport();
        UpdateSelectionAdornerViewport();
        InvalidateSelectionAdorner();
        RealizeVisibleEmbeds();
        ScheduleVisibleCodeBlockHighlighting();
    }

    internal bool TryGetViewport(out double top, out double height, out double width)
    {
        if (_scroll is not null)
        {
            top = _scroll.VerticalOffset;
            height = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : Math.Max(ActualHeight, 1);
            width = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : Math.Max(ActualWidth, 1);
            return true;
        }

        if (_hasExternalViewport)
        {
            top = Math.Max(0, _externalViewport.Top);
            height = Math.Max(1, _externalViewport.Height);
            width = Math.Max(1, _externalViewport.Width);
            return true;
        }

        top = 0;
        height = Math.Max(ActualHeight, 600);
        width = Math.Max(ActualWidth, 1);
        return false;
    }

    private bool BringDocumentRectIntoExternalViewport(Rect rect, bool animate, double alignmentRatio = 0)
    {
        if (_ownsScrollViewport || _root is null)
            return false;

        try
        {
            _root.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = animate,
                TargetRect = rect,
                VerticalAlignmentRatio = Math.Clamp(alignmentRatio, 0, 1),
                VerticalOffset = 0,
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        if (!ReferenceEquals(e.NewFocusedElement, this))
            return;

        _focusEntrySuppressed = _suppressNextFocusPromotion;

        var direction = e.Direction;
        _lastFocusEntryWasKeyboardInput = e.InputDevice == FocusInputDeviceKind.Keyboard;
        _lastFocusEntryWasKeyboardTraversal =
            direction is Microsoft.UI.Xaml.Input.FocusNavigationDirection.Next
                      or Microsoft.UI.Xaml.Input.FocusNavigationDirection.Previous;
        _lastFocusEntryWasReverse = direction == Microsoft.UI.Xaml.Input.FocusNavigationDirection.Previous;
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_promotingKeyboardFocusEntry || _isUnloaded)
            return;

        if (!ReferenceEquals(e.OriginalSource, this) || FocusState == FocusState.Pointer)
            return;

        if (_focusEntrySuppressed || _suppressNextFocusPromotion)
        {
            _focusEntrySuppressed = false;
            _suppressNextFocusPromotion = false;
            _lastFocusEntryWasKeyboardTraversal = false;
            _lastFocusEntryWasKeyboardInput = false;
            return;
        }

        if (!_lastFocusEntryWasKeyboardTraversal &&
            !_lastFocusEntryWasKeyboardInput &&
            FocusState != FocusState.Keyboard)
            return;

        if (_pendingLazyFocus is not null)
        {
            HideFocusRing();
            _lastFocusEntryWasKeyboardTraversal = false;
            _lastFocusEntryWasKeyboardInput = false;
            return;
        }

        if (_focusedItemIndex >= 0)
        {
            UpdateFocusRing();
            NotifyFocusedItemAutomation();
            return;
        }

        _promotingKeyboardFocusEntry = true;
        try
        {
            MoveFocus(_lastFocusEntryWasReverse);
        }
        finally
        {
            _promotingKeyboardFocusEntry = false;
            _lastFocusEntryWasKeyboardTraversal = false;
            _lastFocusEntryWasKeyboardInput = false;
        }
    }

    private void OnLosingFocus(UIElement sender, LosingFocusEventArgs e)
    {
        _suppressNextFocusPromotion = false;

        if (e.NewFocusedElement is DependencyObject next && IsElementWithinRenderer(next))
            return;

        _pendingLazyFocus = null;
        _focusedItemIndex = -1;
        UpdateFocusRing();
        if (!_contextMenuOpen)
        {
            _selectionAnchor = null;
            _clickMode = ClickMode.Single;
        }
    }

    private void OnMarkdownChanged()
    {
        ResetInputForDocumentReplacement();
        // Document has documented precedence over Markdown. A source change
        // invalidates only the document that this control published itself;
        // it must never clear a host-supplied/bound document based on DP
        // notification order. Hosts switch back to source parsing by clearing
        // Document explicitly.
        if (!_hasExplicitDocument && !_isUpdatingDocumentProperty && Document is not null)
        {
            _isUpdatingDocumentProperty = true;
            try { ClearValue(DocumentProperty); }
            finally { _isUpdatingDocumentProperty = false; }
        }
        _disclosureStates.Clear();
        _hostedElementFallbacks.Clear();
        ClearCodeBlockHighlightCache();
        RequestRebuild(RebuildReason.Full);
    }

    private void OnDocumentChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_isUpdatingDocumentProperty)
            return;

        ResetInputForDocumentReplacement();
        _hasExplicitDocument = e.NewValue is MarkdownRenderer.Document.MarkdownDocument;
        _disclosureStates.Clear();
        _hostedElementFallbacks.Clear();
        ClearCodeBlockHighlightCache();
        RequestRebuild(RebuildReason.Full);
    }

    private void ResetInputForDocumentReplacement()
    {
        // Prevent a queued terminal from applying old-document cancellation
        // semantics while its input queue is being discarded. The committed
        // document selection remains governed by the normal rebuild commit;
        // only touch ownership, handles, flyouts, and pending input are reset.
        _pendingTouchSelectionPresentationRestore = null;
        _touchTapSuppression.Reset();
        ResetDeferredPointerInput();
        ResetTransientInteractionState(clearSelection: false);
        _touchTapSuppression.RequireAcceptedContactForRecognizedGesture();
        UpdateSelectionHandles();
    }

    private void OnEngineChanged(DependencyPropertyChangedEventArgs e)
    {
        MarkdownEngine? retiredOwnedEngine = null;
        if (_ownedEngine is not null &&
            ReferenceEquals(e.OldValue, _ownedEngine) &&
            !ReferenceEquals(e.NewValue, _ownedEngine))
        {
            retiredOwnedEngine = _ownedEngine;
            _ownedEngine = null;
        }

        try
        {
            // Supersede the current parse before disposing the previous owner.
            // The retirement helper also snapshots other already-pending work.
            OnParserConfigurationChanged();
        }
        finally
        {
            if (retiredOwnedEngine is not null)
                RetireOwnedEngine(retiredOwnedEngine);
        }
    }

    private void OnParserConfigurationChanged()
    {
        // Document is both an input and the published output of Markdown
        // parsing. Only a document supplied by the host is authoritative.
        // Clear an internally published value so changing Engine or the legacy
        // registry cannot silently reuse an AST produced by old settings.
        if (!_hasExplicitDocument && Document is not null)
        {
            _isUpdatingDocumentProperty = true;
            try { ClearValue(DocumentProperty); }
            finally { _isUpdatingDocumentProperty = false; }
        }

        _disclosureStates.Clear();
        _hostedElementFallbacks.Clear();
        ClearCodeBlockHighlightCache();
        RequestRebuild(RebuildReason.Full);
    }

    private void OnHostedElementFactoryChanged()
    {
        _hostedElementFallbacks.Clear();
        RequestRebuild(RebuildReason.Full);
    }

    private void OnLegacyCodeHighlighterChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_synchronizingCodeHighlighter)
            return;

        ICodeHighlighter? previous = CodeHighlighter;
        _synchronizingCodeHighlighter = true;
        try
        {
#pragma warning disable CS0618 // Synchronizes the preview compatibility alias.
            if (e.NewValue is null)
            {
                _legacyCodeHighlighterAdapter = null;
                CodeHighlighter = null;
            }
            else if (e.NewValue is ICodeHighlighter stable)
            {
                _legacyCodeHighlighterAdapter = null;
                CodeHighlighter = stable;
            }
            else if (e.NewValue is ICodeBlockSyntaxHighlighter legacy)
            {
                if (_legacyCodeHighlighterAdapter is null ||
                    !ReferenceEquals(_legacyCodeHighlighterAdapter.Inner, legacy))
                {
                    _legacyCodeHighlighterAdapter = new LegacyCodeHighlighterAdapter(legacy);
                }

                CodeHighlighter = _legacyCodeHighlighterAdapter;
            }
#pragma warning restore CS0618
        }
        finally
        {
            _synchronizingCodeHighlighter = false;
        }

        OnEffectiveCodeHighlighterChanged(previous, CodeHighlighter);
    }

    private void OnCodeHighlighterChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_synchronizingCodeHighlighter)
            return;

        _synchronizingCodeHighlighter = true;
        try
        {
            _legacyCodeHighlighterAdapter = e.NewValue as LegacyCodeHighlighterAdapter;
#pragma warning disable CS0618 // Keep the compatibility alias bidirectionally synchronized.
            CodeBlockSyntaxHighlighter = e.NewValue as ICodeBlockSyntaxHighlighter;
#pragma warning restore CS0618
        }
        finally
        {
            _synchronizingCodeHighlighter = false;
        }

        OnEffectiveCodeHighlighterChanged(
            e.OldValue as ICodeHighlighter,
            e.NewValue as ICodeHighlighter);
    }

    private void OnEffectiveCodeHighlighterChanged(
        ICodeHighlighter? previous,
        ICodeHighlighter? current)
    {
        if (ReferenceEquals(previous, current))
            return;

        IDisposable? replacedOwner = null;
        if (_ownedCodeHighlighterService is not null &&
            ReferenceEquals(previous, _ownedCodeHighlighterService))
        {
            replacedOwner = _ownedCodeHighlighter;
            _ownedCodeHighlighter = null;
            _ownedCodeHighlighterService = null;
        }

        Task retirement = RetireCodeBlockHighlighting(restart: !_isUnloaded && !_isDisposed);
        if (replacedOwner is not null)
            RetireOwnedCodeHighlighter(replacedOwner, retirement);
        ClearCodeBlockHighlightCache();
        RequestRebuild(RebuildReason.Full);
    }

    private bool IsElementWithinRenderer(DependencyObject element)
        => IsElementWithin(element, this);

    private bool IsElementWithinCodeBlockAction(DependencyObject element)
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is DependencyObject realized && IsElementWithin(element, realized))
                return true;
        }

        return false;
    }

    private bool IsElementWithinSelectionHandle(DependencyObject element)
    {
        return (_selectionStartHandle is not null && IsElementWithin(element, _selectionStartHandle)) ||
               (_selectionEndHandle is not null && IsElementWithin(element, _selectionEndHandle));
    }

    private static bool IsElementWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;)
        {
            if (ReferenceEquals(current, ancestor))
                return true;

            try { current = VisualTreeHelper.GetParent(current); }
            catch { return false; }
        }

        return false;
    }

    private void RegisterSelectionDismissalHook()
    {
        if (_selectionDismissalRoot is not null)
            return;

        var root = XamlRoot?.Content as UIElement;
        if (root is null)
            return;

        _selectionDismissalPointerPressedHandler = OnAppRootPointerPressed;
        root.AddHandler(UIElement.PointerPressedEvent, _selectionDismissalPointerPressedHandler, handledEventsToo: true);
        _selectionCopyKeyDownHandler = OnAppRootKeyDown;
        root.AddHandler(UIElement.KeyDownEvent, _selectionCopyKeyDownHandler, handledEventsToo: true);
        _selectionDismissalRoot = root;
    }

    private void UnregisterSelectionDismissalHook()
    {
        if (_selectionDismissalRoot is not null && _selectionDismissalPointerPressedHandler is not null)
        {
            try { _selectionDismissalRoot.RemoveHandler(UIElement.PointerPressedEvent, _selectionDismissalPointerPressedHandler); }
            catch { }
        }

        if (_selectionDismissalRoot is not null && _selectionCopyKeyDownHandler is not null)
        {
            try { _selectionDismissalRoot.RemoveHandler(UIElement.KeyDownEvent, _selectionCopyKeyDownHandler); }
            catch { }
        }

        _selectionDismissalRoot = null;
        _selectionDismissalPointerPressedHandler = null;
        _selectionCopyKeyDownHandler = null;
    }

    private void OnAppRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded)
            return;

        var point = e.GetCurrentPoint(sender as UIElement ?? this);
        var properties = point.Properties;
        if (properties.IsRightButtonPressed || properties.IsMiddleButtonPressed || properties.IsXButton1Pressed || properties.IsXButton2Pressed)
            return;

        if (e.OriginalSource is DependencyObject source && IsElementWithinRenderer(source))
        {
            MarkdownSelectionCoordinator.ClearSelectionsExcept(this);
            if (IsElementWithinCodeBlockAction(source))
                return;
            if (IsElementWithinSelectionHandle(source))
                return;

            bool overCanvas = IsPointerOverCanvasTextSurface(e, out var canvasPoint);
            bool overEmbed = overCanvas && IsPointOverEmbed(canvasPoint);
            if (!overCanvas || overEmbed)
                ClearSelectionForExternalInteraction();
            return;
        }

        ClearSelectionForExternalInteraction();
    }

    private void OnAppRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isUnloaded || _snapshot is null || !_selection.IsActive)
            return;

        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (!ctrl || e.Key != VirtualKey.C)
            return;

        if (ExecuteSelectionCommand(MarkdownCommandKind.CopyRendered))
            e.Handled = true;
    }

    /// <summary>
    /// Copies the active selection to the clipboard.
    /// </summary>
    /// <param name="options">Optional copy format options. Defaults use rendered plain text and add CF_HTML.</param>
    /// <returns>True when a selection was copied successfully.</returns>
    public bool CopySelectionToClipboard(MarkdownCopyOptions? options = null)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !_selection.IsActive)
            return false;

        bool succeeded;
        try
        {
            var effectiveOptions = options ?? MarkdownCopyOptions.Default;
            string? renderedText = null;
            string? renderedHtml = null;
            if (effectiveOptions.PlainTextMode == MarkdownPlainTextCopyMode.RenderedText ||
                effectiveOptions.IncludeHtml)
            {
                var rendered = GetRenderedSelectionPayload(snapshot, _selection.Range);
                if (effectiveOptions.PlainTextMode == MarkdownPlainTextCopyMode.RenderedText)
                    renderedText = rendered.Text;
                if (effectiveOptions.IncludeHtml)
                    renderedHtml = rendered.Html;
            }

            succeeded = MarkdownClipboardWriter.Copy(
                snapshot.SourceMap,
                _selection.Range,
                effectiveOptions,
                renderedText,
                renderedHtml);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Selection copy failed: {ex.Message}");
            succeeded = false;
        }

        RaiseCopyCompleted(MarkdownCopyKind.Selection, succeeded);
        return succeeded;
    }

    /// <summary>Copies the active selection as exact markdown source plus CF_HTML.</summary>
    public bool CopySelectionAsMarkdown()
        => CopySelectionToClipboard(new MarkdownCopyOptions
        {
            PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
            IncludeHtml = true,
        });

    private bool ExecuteSelectionCommand(MarkdownCommandKind kind)
    {
        try
        {
            if (TryResolveSelectionCommand(kind, out var command, out var context))
            {
                if (!command.CanExecute(context))
                    return false;

                command.Execute(context);
                return true;
            }

            return kind == MarkdownCommandKind.CopyMarkdown
                ? CopySelectionAsMarkdown()
                : CopySelectionToClipboard();
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{kind}' failed: {ex.Message}");
            return false;
        }
    }

    private bool CanExecuteSelectionCommandOrDefault(MarkdownCommandKind kind)
    {
        try
        {
            return !TryResolveSelectionCommand(kind, out var command, out var context) ||
                   command.CanExecute(context);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{kind}' query failed: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveSelectionCommand(
        MarkdownCommandKind kind,
        out System.Windows.Input.ICommand command,
        out MarkdownCommandContext context)
    {
        SourceSpan sourceRange = SourceSpan.Empty;
        if (_snapshot is { } snapshot && _selection.IsActive)
            snapshot.SourceMap.TryMapRange(_selection.Range, out sourceRange);

        context = new MarkdownCommandContext(kind, sourceRange);
        command = CommandProvider?.GetCommand(context)!;
        return command is not null;
    }

    private static RenderedSelectionPayload GetRenderedSelectionPayload(
        LayoutSnapshot snapshot,
        DocumentRange range)
    {
        var semantic = snapshot.SemanticDocument;
        var normalized = range.Normalized();
        int start = semantic.TextOffsetFromDocumentPosition(normalized.Start);
        int end = semantic.TextOffsetFromDocumentPosition(normalized.End);
        if (end < start)
            (start, end) = (end, start);

        start = Math.Clamp(start, 0, semantic.Text.Length);
        end = Math.Clamp(end, start, semantic.Text.Length);
        return new RenderedSelectionPayload(
            semantic.Text.Substring(start, end - start),
            MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, end));
    }

    private readonly struct RenderedSelectionPayload
    {
        internal readonly string Text;
        internal readonly string Html;

        internal RenderedSelectionPayload(string text, string html)
        {
            Text = text;
            Html = html;
        }
    }

    internal void ClearSelectionFromCoordinator()
    {
        ClearSelectionForExternalInteraction();
    }

    private void ClearSelectionForExternalInteraction(
        bool resetClickTracking = true,
        bool resetDeferredPointerInput = true)
    {
        _pendingTouchSelectionPresentationRestore = null;
        _touchTapSuppression.Reset();
        CloseSelectionContextMenu();
        if (resetDeferredPointerInput)
            ResetDeferredPointerInput();
        _pointerSession = default;
        ResetSelectionHandleDrag();
        _touchSelection.Reset();
        _selectionAnchor = null;
        _clickMode = ClickMode.Single;
        SetSelectionDragShieldActive(false);
        if (resetClickTracking)
        {
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint = default;
        }

        if (_selection.IsActive || !_selection.Range.IsEmpty)
            _selection.Clear();

        if (_focusedItemIndex >= 0)
        {
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            UpdateFocusRing();
        }
    }

    private bool IsPointerOverCanvasTextSurface(PointerRoutedEventArgs e, out Point canvasPoint)
    {
        canvasPoint = default;
        if (_canvas is null)
            return false;

        try
        {
            canvasPoint = e.GetCurrentPoint(_canvas).Position;
            return canvasPoint.X >= 0 &&
                   canvasPoint.Y >= 0 &&
                   canvasPoint.X <= _canvas.ActualWidth &&
                   canvasPoint.Y <= _canvas.ActualHeight;
        }
        catch
        {
            canvasPoint = default;
            return false;
        }
    }

    private void OnSelectionChanged()
    {
        // SelectionController may publish a change notification for a
        // presentation-only update. Keep a pending restore when the exact
        // range is unchanged; a real range change invalidates it. Generation
        // is intentionally checked at commit, because snapshot publication can
        // notify automation before the restore phase finishes.
        if (_pendingTouchSelectionPresentationRestore is { } restoreIntent &&
            !restoreIntent.MatchesRange(_selection.Range))
        {
            _pendingTouchSelectionPresentationRestore = null;
        }

        if (_selection.IsActive)
            MarkdownSelectionCoordinator.ClearSelectionsExcept(this);

        UpdateSelectionOverlay();
        QueueSelectionAutomationEvent();
    }

    private void QueueSelectionAutomationEvent()
    {
        if (_selectionAutomationEventQueued || _isDisposed || _isUnloaded)
            return;

        _selectionAutomationEventQueued = true;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
            {
                _selectionAutomationEventQueued = false;
                if (_isDisposed || _isUnloaded)
                    return;

                (FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer)?
                    .NotifySelectionChanged();
            }))
        {
            _selectionAutomationEventQueued = false;
        }
    }

    private void OnSelectionEnabledChanged(bool isEnabled)
    {
        if (!isEnabled)
        {
            _touchTapSuppression.Reset();
            ClearSelectionForExternalInteraction();
        }

        // TextPattern remains available for document reading, but its
        // SupportedTextSelection value changes between Single and None.
        // Recreate the peer-facing semantic snapshot and refresh cursor state.
        RequestRebuild(RebuildReason.Full);
        InvalidateAutomationLayout();
        SetCursorShape(null);
    }

    private void OnLoadedInternal()
    {
        if (_isDisposed)
            return;

        _isUnloaded = false;
        // RemoveFromVisualTree physically removes the Win2D child from _root.
        // Recreate it for a retained view; the old native child and its input
        // delegates must not retain the unloaded view through a WinRT cycle.
        EnsureCanvas();
        AttachWindowLifecycle();
        AttachCanvasRenderHandlers();
        if (_imageLifetimeCts is null || _imageLifetimeCts.IsCancellationRequested)
        {
            _imageLifetimeCts?.Dispose();
            _imageLifetimeCts = new CancellationTokenSource();
        }
        MarkdownSelectionCoordinator.Register(this);
        RegisterSelectionDismissalHook();
        _environmentSubscription?.Dispose();
        _environmentSubscription = MarkdownEnvironmentMonitor.Acquire(this, this);
        _environmentSnapshot = _environmentSubscription?.Snapshot ?? default;
        _sizeChangedHandler = (_, e) =>
        {
            if (Math.Abs(_lastWidth - (float)e.NewSize.Width) > 0.5f) RequestRebuild(RebuildReason.Restyle);
        };
        SizeChanged += _sizeChangedHandler;
        // Re-subscribe to Theme.Changed: OnUnloaded unhooks the handler, and
        // a Load→Unload→Load cycle (TabView reuse, navigation hide/show)
        // does not necessarily reassign the Theme DP, so without this the
        // control would silently stop reacting to theme.Invalidate() after
        // re-attach.  Unsubscribe first to avoid a duplicate handler if
        // Loaded fires before any Unload has occurred.
        if (Theme is { } t)
        {
            t.Changed -= OnThemeRevisionChanged;
            t.Changed += OnThemeRevisionChanged;
        }
        if (SvgRenderer is { } svgRenderer)
        {
            svgRenderer.CacheInvalidated -= OnSvgRendererCacheInvalidated;
            Volatile.Write(ref _subscribedSvgRenderer, svgRenderer);
            svgRenderer.CacheInvalidated += OnSvgRendererCacheInvalidated;
        }
        AttachPerformanceSessionDisposalNotification();
        RequestRebuild(RebuildReason.Full);
    }

    private void OnUnloaded()
    {
        if (_isUnloaded)
            return;

        // Set before any unsubscription so dispatcher-queued lambdas
        // (e.g. from OnImageLoadCompleted) that are already in-flight know
        // not to call RequestRebuild after we've torn down.
        _isUnloaded = true;
        _pendingTouchSelectionPresentationRestore = null;
        // Keep the recognizer barrier while the owner epoch is still intact.
        // A queued Cancel must apply its per-contact selection disposition
        // before that identity is discarded; a queued Release keeps selection.
        _touchTapSuppression.PrepareForReset();
        StopCanvasDeviceRecovery();
        StopImageRelayoutRecovery(force: true);
        DetachPerformanceSessionDisposalNotification();
        ResetDeferredPointerInput();
        _touchTapSuppression.Reset();
        // An unloaded view is reusable and no longer belongs to this window.
        // Do not let a former host destroy it, or retain weak-event wrappers
        // for every virtualized view until the window eventually closes.
        DetachWindowLifecycle();
        // Win2D can retain queued native draw/resource callbacks after XAML starts
        // removing this control. Stop those callbacks before releasing any snapshot
        // or DirectWrite resources they may reference.
        DetachCanvasRenderHandlers();
        DetachCanvasInputHandlers();
        // Win2D requires RemoveFromVisualTree from the containing control's
        // Unloaded boundary. At this point XAML has begun detaching the renderer,
        // so each Win2D child can unregister its XamlRoot callback without racing a
        // subsequent window resize.
        _canvas?.RemoveFromVisualTree();
        _canvas = null;
        ReleaseLoadedResources();
    }

    private void ReleaseLoadedResources()
    {
        ResetVectorCapacityObserver();
        ClearPendingFocusGeometryWork();
        Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 0);
        Interlocked.Exchange(ref _deferredSceneProbeAfterPaint, 0);
        CloseSelectionContextMenu();
        ClearHorizontalOverflowPointerState();
        HideAbbreviationTooltip();
        ReleaseAbbreviationTooltipTimers();
        if (_sizeChangedHandler is not null)
        {
            SizeChanged -= _sizeChangedHandler;
            _sizeChangedHandler = null;
        }
        if (Theme is { } t)
        {
            t.Changed -= OnThemeRevisionChanged;
        }
        if (Interlocked.Exchange(ref _subscribedSvgRenderer, null) is { } svgRenderer)
            svgRenderer.CacheInvalidated -= OnSvgRendererCacheInvalidated;
        _environmentSubscription?.Dispose();
        _environmentSubscription = null;
        UnregisterSelectionDismissalHook();
        MarkdownSelectionCoordinator.Unregister(this);
        var oldCts = _pipelineCts;
        var oldTask = _pipelineTask;
        _pipelineCts = null;
        _pipelineTask = null;
        _activePipelineGeneration = 0;
        RetireCancellationTokenSource(oldCts, oldTask);
        CancelLazyLayoutRealization();
        _pendingLayoutCommit = null;
        var oldImageLifetimeCts = _imageLifetimeCts;
        _imageLifetimeCts = null;
        RetireCancellationTokenSource(oldImageLifetimeCts, lifetime: null);
        _rebuildDispatchState = default;
        _rebuildCoordinator.Reset();
        Interlocked.Exchange(ref _rejectedCrossThreadRebuildReason, -1);
        _imageRelayoutQueued = false;
        _imageRelayoutActive = false;
        ++_lastImageRelayoutDispatchTicket;
        StopImageRelayoutRecovery(force: true);
        Interlocked.Exchange(ref _imageRelayoutPayload, null)?.DiscardPublication();
        RetireCancellationTokenSource(_imageRelayoutCts, _imageRelayoutTask);
        _imageRelayoutCts = null;
        _imageRelayoutTask = null;
        _lastLazyViewportTimestamp = 0;
        _lazyScrollVelocityPixelsPerSecond = 0;
        _pendingImageRelayoutBlockIndices.Clear();
        _pendingScrollToBlock = null;
        _selectionAutomationEventQueued = false;
        _horizontalOverflowAutomationEventQueued = false;
        _pendingHorizontalOverflowAutomationChanges.Clear();
        RetireCodeBlockHighlighting(restart: false);
        // Unsubscribe scroll handler so scroll-inertia events after visual-tree
        // removal don't fire OnScrollViewChanged on a partially-torn-down control.
        if (_scroll is not null) _scroll.ViewChanged -= OnScrollViewChanged;
        _scrollViewIntermediate = false;
        // Unsubscribe all in-flight image load handlers before clearing the plan
        // list; otherwise an async load completing after unload fires
        // OnImageLoadCompleted → RequestRebuild on a torn-down control, causing a
        // zombie rebuild cycle that re-subscribes handlers indefinitely.
        UnsubscribeAllImages();
        // Tear down embed plans before clearing the overlay so block embed
        // factories get RecycleBlock callbacks and inline embeds release
        // their Run.RealizedElement references — otherwise hosted controls
        // and their event handlers would leak past detach.
        DerealizeAllEmbeds();
        _embedPlans.Clear();
        _embedPlanIndex = null;
        _activeEmbedOrdinals.Clear();
        _codeBlockActionPlans.Clear();
        _hasMeasuredCodeBlocks = false;
        _codeBlockActionPlanIndex = null;
        _activeCodeBlockActionOrdinals.Clear();
        _codeBlockCopyButtonPool.Clear();
        _imagePlans.Clear();
        _imagePlanIndex = null;
        Volatile.Write(ref _imagePlanPublication, null);
        ResetImageResidency();
        _prefetchedMarkdownSource = null;
        _activeImagePrefetcher = null;
        _activeImagePrefetchContext = null;
        _prefetchedImageRegistryRevision = -1;
        _pendingImagePrefetch = null;
        if (PerformanceSession is IMarkdownPerformanceSessionInternal performanceSession)
        {
            _snapshotRetirements.Track(performanceSession.ReleaseDeferredSceneDocument(this));
            performanceSession.ReleaseDocumentScope(this);
        }
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        ResetSelectionHandleDrag();
        _touchTapSuppression.Reset();
        _touchSelection.Reset();
        SetSelectionDragShieldActive(false);
        // Release native resources & hosted embeds so re-attaching the
        // control to a new visual parent doesn't leak DirectWrite layouts
        // or keep stale FrameworkElements alive. A failed XAML detach must not
        // abort the remaining one-shot cleanup or snapshot retirement below.
        TryReleaseSelectionAdornerSurface();
        _overlay?.Children.Clear();
        // No refresh frame follows an unload, so dispose the detached adorner
        // surfaces, the child visual and the graphics device here.
        _selectionAdornerSurfaces.DropRetired();
        _selectionAdornerHost?.Dispose();
        ReleaseSelectionAdornerGraphicsDevice();
        // Release ProtectedCursor before disposing the native cursor objects so the
        // XAML compositor doesn't access a dangling handle during the same render frame.
        ProtectedCursor = null;
        _currentCursorShape = null;
        // Dispose cached cursor objects; they will be lazily re-created on next use.
        _cursorHand?.Dispose();
        _cursorIBeam?.Dispose();
        _cursorHand = null;
        _cursorIBeam = null;
        _selectionAdornerRects.Clear();
        _selectionAdornerRectScratch.Clear();
        _selectionAdornerBackgrounds.Clear();
        _selectionAdornerBackgroundScratch.Clear();
        if (_selectionAdorner is not null)
        {
            _selectionAdorner = null;
            _selectionAdornerHost = null;
        }
        DetachSelectionHandleHandlers();
        _selectionStartHandle = null;
        _selectionEndHandle = null;
        _focusRing = null; // evicted from overlay above; lazily re-created on re-attach
        var snap = _snapshot;
        _snapshot = null;
        _automationDocumentGenerations.Invalidate();
        NotifyAutomationSnapshotChanged();
        RetireSnapshot(snap);
    }

    private Task RetireSnapshot(LayoutSnapshot? snapshot)
    {
        if (snapshot is null)
            return Task.CompletedTask;

        Task retirement = snapshot.Retire();
        _snapshotRetirements.Track(retirement);
        return retirement;
    }

    /// <summary>
    /// Releases renderer resources and detaches Win2D/XAML event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _pendingTouchSelectionPresentationRestore = null;
        _touchTapSuppression.Reset();
        StopCanvasDeviceRecovery();
        ResetSelectionHandleDrag();
        _touchSelection.Dispose();
        StopSelectionHandleFrameClock();
        DetachWindowLifecycle();
        OnUnloaded();
        // Unload has already detached and disposed the adorner surfaces; this
        // covers a control disposed without ever unloading.
        TryReleaseSelectionAdornerSurface();
        _selectionAdornerSurfaces.DropRetired();
        ReleaseSelectionAdornerGraphicsDevice();
        _snapshotLayoutDevice = null;
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        DetachCanvasHandlers();
        DetachSelectionDragShieldHandlers();
        Content = null;
        _canvas = null;
        _overlay = null;
        _scroll = null;
        _root = null;
        MarkdownEngine? ownedEngine = _ownedEngine;
        _ownedEngine = null;
        if (ownedEngine is not null)
            RetireOwnedEngine(ownedEngine);
        IDisposable? ownedHighlighter = _ownedCodeHighlighter;
        _ownedCodeHighlighter = null;
        _ownedCodeHighlighterService = null;
        if (ownedHighlighter is not null)
            RetireOwnedCodeHighlighter(ownedHighlighter, _codeBlockHighlightRetirement);
        try { DisposalCompleted?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] disposal observer failed: " +
                $"{ex.GetType().Name} (0x{ex.HResult:X8}).");
        }
    }

    // Release tooling observes actual control teardown without expanding the
    // public API or treating process disappearance as proof of disposal.
    internal event EventHandler? DisposalCompleted;


    // WinUI does not guarantee a child UserControl receives Unloaded when its
    // top-level window is destroyed. Subscribe to the non-cancelable window
    // destruction event so native Canvas resources are released while the
    // WinUI dispatcher and COM apartment are still valid.
    private void AttachWindowLifecycle()
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } island)
            return;

        AppWindow appWindow;
        try
        {
            appWindow = AppWindow.GetFromWindowId(island.AppWindowId);
        }
        catch
        {
            // A host can transition between content islands while it is being
            // created or torn down. Its regular Unloaded path remains valid.
            return;
        }

        if (ReferenceEquals(_windowLifecycleAppWindow, appWindow))
            return;

        DetachWindowLifecycle();
        var subscription = new WindowLifecycleSubscription(this);
        _windowLifecycleAppWindow = appWindow;
        _windowLifecycleSubscription = subscription;
        appWindow.Destroying += subscription.OnDestroying;
    }

    private void DetachWindowLifecycle()
    {
        if (_windowLifecycleAppWindow is not { } appWindow)
            return;

        _windowLifecycleAppWindow = null;
        var subscription = _windowLifecycleSubscription;
        _windowLifecycleSubscription = null;
        if (subscription is null)
            return;

        try { appWindow.Destroying -= subscription.OnDestroying; }
        catch { /* The host may already be tearing down its WinRT event source. */ }
    }

    private sealed class WindowLifecycleSubscription
    {
        private readonly WeakReference<MarkdownRendererControl> _owner;

        internal WindowLifecycleSubscription(MarkdownRendererControl owner)
            => _owner = new WeakReference<MarkdownRendererControl>(owner);

        internal void OnDestroying(AppWindow sender, object args)
        {
            if (_owner.TryGetTarget(out var owner) &&
                ReferenceEquals(sender, owner._windowLifecycleAppWindow) &&
                !owner._isDisposed)
            {
                owner.Dispose();
            }
        }
    }

    private void DetachCanvasHandlers()
    {
        DetachCanvasRenderHandlers();
        DetachCanvasInputHandlers();
        GettingFocus -= OnGettingFocus;
        GotFocus -= OnGotFocus;
        LosingFocus -= OnLosingFocus;
        KeyDown -= OnKeyDown;
        DetachSelectionHandleHandlers();
    }

    private void DetachCanvasInputHandlers()
    {
        if (_canvas is not null)
        {
            _canvas.PointerPressed -= OnPointerPressed;
            _canvas.PointerWheelChanged -= OnPointerWheelChanged;
            _canvas.PointerExited -= OnPointerExited;
        _canvas.Tapped -= OnTapped;
        _canvas.DoubleTapped -= OnDoubleTapped;
        _canvas.Holding -= OnHolding;
            _canvas.RightTapped -= OnRightTapped;
            _canvas.RemoveHandler(UIElement.PointerMovedEvent, _canvasPointerMovedHandler);
            _canvas.RemoveHandler(UIElement.PointerReleasedEvent, _canvasPointerReleasedHandler);
            _canvas.RemoveHandler(UIElement.PointerCanceledEvent, _canvasPointerCanceledHandler);
            _canvas.RemoveHandler(UIElement.PointerCaptureLostEvent, _canvasPointerCaptureLostHandler);
        }

    }

    private void AttachCanvasRenderHandlers()
    {
        if (_canvas is null || _canvasRenderHandlersAttached)
            return;

        _canvas.RegionsInvalidated += OnRegionsInvalidated;
        _canvas.CreateResources += OnCanvasCreateResources;
        _canvasRenderHandlersAttached = true;
    }

    private void DetachCanvasRenderHandlers()
    {
        if (_canvas is null || !_canvasRenderHandlersAttached)
            return;

        _canvas.RegionsInvalidated -= OnRegionsInvalidated;
        _canvas.CreateResources -= OnCanvasCreateResources;
        _canvasRenderHandlersAttached = false;
    }

    private void DetachSelectionDragShieldHandlers()
    {
        if (_selectionDragShield is null)
            return;

        _selectionDragShield.PointerMoved -= OnPointerMoved;
        _selectionDragShield.PointerReleased -= OnPointerReleased;
        _selectionDragShield.PointerCanceled -= OnPointerCanceledOrCaptureLost;
        _selectionDragShield.PointerCaptureLost -= OnPointerCanceledOrCaptureLost;
        _selectionDragShield = null;
    }

    private void OnThemeChanged()
    {
        if (_isDisposed || _isUnloaded)
            return;

        // NotifyEnvironmentChanged fires Theme.Changed → OnThemeRevisionChanged → a restyle.
        // Do NOT call RequestRebuild() here again — that would start two simultaneous
        // builds and immediately cancel the first one on every theme change.
        if (Theme is { } t) t.NotifyEnvironmentChanged();
        else RequestRebuild(RebuildReason.Restyle); // no Theme object: must trigger rebuild directly
    }

    void IMarkdownEnvironmentListener.OnMarkdownEnvironmentChanged(
        in MarkdownEnvironmentSnapshot snapshot,
        MarkdownEnvironmentChange changes)
    {
        _environmentSnapshot = snapshot;
        if (_isDisposed || _isUnloaded)
            return;

        if ((changes & MarkdownEnvironmentChange.Relayout) != 0)
        {
            RequestRebuild(RebuildReason.Restyle);
            return;
        }

        // Layout boxes hold an immutable ThemeSnapshot. Until paint commands
        // carry semantic brush roles independently, a palette-only notification
        // must compile and commit a fresh snapshot; repainting the existing
        // boxes would retain stale accent or High Contrast colors.
        if (MarkdownEnvironmentSnapshot.IsBrushOnly(changes))
            RequestRebuild(RebuildReason.Restyle);
    }

    private void OnThemeDpChanged(DependencyPropertyChangedEventArgs e)
    {
        // Unsubscribe from the previous theme's Changed event so we don't leak
        // a reference to it (DependencyObjects don't auto-unsubscribe).
        if (e.OldValue is MarkdownTheme old)
        {
            old.Changed -= OnThemeRevisionChanged;
        }
        if (e.NewValue is MarkdownTheme @new)
        {
            @new.Changed += OnThemeRevisionChanged;
        }
        OnThemeChanged();
    }

    private void OnThemeRevisionChanged(object? sender, EventArgs e)
    {
        // A theme property, override, environment notification, or explicit
        // MarkdownTheme.Invalidate() raised Changed. Rebuild without notifying
        // the theme again, which would loop.
        // Guard against cross-thread calls: MarkdownTheme.Changed is a plain
        // .NET event and a consumer may call Invalidate() from a background thread.
        // Accessing DependencyObject members off the UI thread throws RPC_E_WRONG_THREAD.
        var dq = DispatcherQueue;
        if (dq is null) return;
        if (dq.HasThreadAccess) { if (!_isUnloaded) RequestRebuild(RebuildReason.Restyle); return; }
        dq.TryEnqueue(() => { if (!_isUnloaded) RequestRebuild(RebuildReason.Restyle); });
    }

    /// <summary>Kicks off (or re-kicks) the parse + layout pipeline.</summary>
    public void RequestRebuild()
        => RequestRebuild(RebuildReason.Full);

    // Internal requests call the reason-taking overload directly so the
    // opt-in diagnostic observer retains their actual caller and line number.

    /// <summary>
    /// Clears cached WinUI resource-key discovery and rebuilds the current
    /// presentation. Call this after replacing resource dictionary keys without
    /// changing the dictionary's count; ordinary value replacement, theme
    /// properties, and override mutations do not require it.
    /// </summary>
    public void InvalidateThemeResources()
    {
        if (_isDisposed)
            return;

        ThemeResolver.InvalidateResourceKeyCache();
        RequestRebuild(RebuildReason.Restyle);
    }

    private void RequestRebuild(
        RebuildReason reason)
    {
        if (_isDisposed)
            return;

        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            bool enqueued;
            try
            {
                enqueued = dispatcher.TryEnqueue(() => RequestRebuild(reason));
            }
            catch (Exception exception)
            {
                enqueued = false;
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] Dispatcher rejected cross-thread rebuild request: {exception.Message}");
            }

            if (!enqueued)
            {
                if (!Volatile.Read(ref _isDisposed) && !Volatile.Read(ref _isUnloaded))
                {
                    RecordRejectedCrossThreadRebuildRequest(reason);
                    MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Dispatcher rejected cross-thread rebuild request.");
                }
            }
            return;
        }

        if (_isUnloaded)
            return;

        DrainRejectedCrossThreadRebuildRequest();

        MarkdownRebuildRequestDisposition disposition = _rebuildCoordinator.Request(reason);

        var performance = MarkdownPerformanceEventSource.Log;
        if (_pendingRebuildRequestTimestamp == 0 && performance.IsMeasurementEnabled())
            _pendingRebuildRequestTimestamp = Stopwatch.GetTimestamp();

        // A materialized scene is already immutable and has passed its plan
        // generation fence. While another pipeline is active, remember one
        // follow-up only; do not revoke its probes, image/lazy work, generation,
        // or cancellation scope.
        if (disposition == MarkdownRebuildRequestDisposition.Deferred)
        {
            // Usually the active build owns this follow-up until completion.
            // If a genuine Full/Restyle request is already pending (for
            // example after a rejected dispatch), let it retry without turning
            // this scene event into a supersession.
            QueuePendingRebuild();
            return;
        }

        // When no pipeline is active, a scene-only request can wait in the
        // ordinary coalescing queue too. Do not supersede the currently
        // published snapshot until the replacement pipeline is actually
        // allowed to start (for example, after an active pointer gesture).
        if (reason == RebuildReason.SceneMaterialized)
        {
            QueuePendingRebuild();
            return;
        }

        // Source, configuration, width, and theme requests invalidate the
        // progressive scene-plan generation. Scene-only work returned above.
        if (MarkdownRebuildDispatchPolicy.AdvancesDeferredSceneGeneration(
                reason == RebuildReason.SceneMaterialized))
        {
            if (PerformanceSession is IMarkdownPerformanceSessionInternal performanceSession)
            {
                Task sceneRetirement = performanceSession.AdvanceDeferredSceneGeneration(this);
                _snapshotRetirements.Track(sceneRetirement);
            }
        }

        // Fence the currently active generation at request time, before the
        // queued rebuild callback can be delayed by UI work. This makes a
        // superseded pipeline unable to publish even while its parse worker is
        // still unwinding, and signals cancellation with request-time latency.
        ++_pipelineGeneration;
        var supersededCts = _pipelineCts;
        var supersededTask = _pipelineTask;
        long supersededGeneration = _activePipelineGeneration;
        _pipelineCts = null;
        _pipelineTask = null;
        _activePipelineGeneration = 0;

        long supersessionTimestamp = 0;
        if (supersededCts is not null &&
            supersededTask is { IsCompleted: false } &&
            supersededGeneration > 0 &&
            performance.IsMeasurementEnabled())
        {
            supersessionTimestamp = Stopwatch.GetTimestamp();
            performance.PipelineSuperseded(supersededGeneration);
        }

        RetireCancellationTokenSource(
            supersededCts,
            supersededTask,
            supersededGeneration,
            supersessionTimestamp);
        QueuePendingRebuild();
    }

    private void RecordRejectedCrossThreadRebuildRequest(RebuildReason reason)
    {
        int requested = (int)reason;
        while (true)
        {
            int current = Volatile.Read(ref _rejectedCrossThreadRebuildReason);
            int merged = Math.Max(current, requested);
            if (Interlocked.CompareExchange(
                    ref _rejectedCrossThreadRebuildReason,
                    merged,
                    current) == current)
            {
                return;
            }
        }
    }

    private void DrainRejectedCrossThreadRebuildRequest()
    {
        var dispatcher = DispatcherQueue;
        if (_isDisposed || _isUnloaded || dispatcher is not null && !dispatcher.HasThreadAccess)
            return;

        int reason = Interlocked.Exchange(ref _rejectedCrossThreadRebuildReason, -1);
        if (reason >= 0)
        {
            RequestRebuild((MarkdownRebuildCoordinatorReason)reason);
        }
    }

    private void QueuePendingRebuild()
    {
        DrainRejectedCrossThreadRebuildRequest();
        if (_isDisposed || _isUnloaded ||
            !_rebuildCoordinator.TryGetDispatchReason(out MarkdownRebuildCoordinatorReason pendingReason) ||
            !IsLoaded || _canvas is null || _overlay is null || _root is null)
        {
            return;
        }

        MarkdownRebuildDispatchPriority dispatchPriority =
            MarkdownRebuildDispatchPolicy.SelectPriority(
                pendingReason == MarkdownRebuildCoordinatorReason.Full);
        if (!_rebuildDispatchState.ShouldSchedule(dispatchPriority))
            return;

        var dispatcher = DispatcherQueue;
        if (dispatcher is null)
        {
            _rebuildDispatchState = default;
            ProcessQueuedRebuildCore();
            return;
        }

        long dispatchTicket = NextRebuildDispatchTicket();
        bool enqueued = false;
        try
        {
            enqueued = dispatcher.TryEnqueue(
                ToDispatcherQueuePriority(dispatchPriority),
                () => ProcessQueuedRebuild(dispatchTicket));
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Dispatcher rejected rebuild callback: {exception.Message}");
        }

        _rebuildDispatchState = _rebuildDispatchState.AfterEnqueueAttempt(
            dispatchTicket,
            dispatchPriority,
            enqueued);
    }

    private long NextRebuildDispatchTicket()
    {
        unchecked
        {
            ++_lastRebuildDispatchTicket;
            if (_lastRebuildDispatchTicket == 0)
                ++_lastRebuildDispatchTicket;
        }

        return _lastRebuildDispatchTicket;
    }

    private static Microsoft.UI.Dispatching.DispatcherQueuePriority ToDispatcherQueuePriority(
        MarkdownRebuildDispatchPriority priority) =>
        priority switch
        {
            MarkdownRebuildDispatchPriority.Low =>
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            MarkdownRebuildDispatchPriority.Normal =>
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(priority)),
        };

    private void ProcessQueuedRebuild(long dispatchTicket)
    {
        if (!_rebuildDispatchState.IsCurrent(dispatchTicket))
            return;

        _rebuildDispatchState = default;
        ProcessQueuedRebuildCore();
    }

    private void ProcessQueuedRebuildCore()
    {
        if (_isDisposed || _isUnloaded || !IsLoaded)
            return;

        // Image completion, theme changes, and width changes may request a
        // relayout while the user is actively selecting text. Releasing pointer
        // capture here terminates the gesture and visibly drops the selection.
        // Keep the latest merged rebuild request pending until pointer release.
        if (HasSnapshotPointerInteraction)
            return;

        if (!_rebuildCoordinator.TryGetDispatchReason(
                out MarkdownRebuildCoordinatorReason pendingReason))
        {
            return;
        }

        if (pendingReason == MarkdownRebuildCoordinatorReason.SceneMaterialized)
        {
            // Scene-only requests do not supersede an in-flight/published
            // generation until dispatch reaches this safe start boundary.
            ++_pipelineGeneration;
        }

        long generation = _pipelineGeneration;
        if (!_rebuildCoordinator.TryBeginPipeline(
                generation,
                out MarkdownRebuildCoordinatorReason coordinatorReason))
        {
            return;
        }

        RebuildReason reason = coordinatorReason;
        CancelLazyLayoutRealization();
        if (_imageRelayoutCts is { } imageRelayoutCancellation)
            _ = CancellationTokenSourceRetirement.RequestCancellation(imageRelayoutCancellation);

        string effectiveSource = _hasExplicitDocument
            ? Document?.Source ?? string.Empty
            : Markdown ?? string.Empty;
        // Keep the committed selection attached to the still-published snapshot
        // while its replacement is prepared. The commit compares immutable
        // document identities and clears selection only if replacement succeeds.
        CaptureTouchSelectionPresentationRestoreIntent();
        ResetTransientInteractionState(clearSelection: false);

        // A deferred scene may be the first request after the preceding
        // pipeline completed. Keep that published token alive until this new
        // snapshot actually begins replacing it, then retire it with its task.
        var previousCts = _pipelineCts;
        var previousTask = _pipelineTask;
        var cts = new CancellationTokenSource();
        _pipelineCts = cts;
        _activePipelineGeneration = generation;
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Publish a lifetime task before entering RebuildAsync: a synchronous
        // RenderCompleted callback can reenter RequestRebuild before the async
        // method returns its Task. Supersession must still wait for this run.
        _pipelineTask = lifetime.Task;
        if (previousCts is not null && !ReferenceEquals(previousCts, cts))
            RetireCancellationTokenSource(previousCts, previousTask);

        var performance = MarkdownPerformanceEventSource.Log;
        long pipelineStartTimestamp = performance.IsMeasurementEnabled()
            ? Interlocked.Exchange(ref _pendingRebuildRequestTimestamp, 0)
            : 0;
        if (pipelineStartTimestamp == 0 && performance.IsMeasurementEnabled())
            pipelineStartTimestamp = Stopwatch.GetTimestamp();
        long sourceUtf16Bytes = (long)effectiveSource.Length * sizeof(char);
        if (pipelineStartTimestamp != 0)
            performance.PipelineStarted(generation, sourceUtf16Bytes);

        // Capture the borrowed queue while this start boundary is on the UI
        // thread. Completion can run on the pool after unload/disposal and must
        // not query the control's native dependency object again there.
        Microsoft.UI.Dispatching.DispatcherQueue? completionDispatcher = DispatcherQueue;
        Task task;
        try
        {
            task = RebuildAsync(
                cts.Token,
                reason,
                generation,
                pipelineStartTimestamp,
                sourceUtf16Bytes);
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild startup failed: {exception}");
            task = Task.FromException(exception);
        }

        _ = task.ContinueWith(
            completed => CompleteRebuildTask(completed, lifetime, generation, completionDispatcher),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteRebuildTask(
        Task completed,
        TaskCompletionSource lifetime,
        long generation,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
    {
        if (completed.IsFaulted)
        {
            AggregateException? pipelineFailure = completed.Exception;
            Exception? diagnosticFailure = pipelineFailure is { InnerExceptions.Count: > 0 }
                ? pipelineFailure.InnerExceptions[0]
                : pipelineFailure;
            if (diagnosticFailure is not null)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] Rebuild faulted: {diagnosticFailure.GetType().Name} " +
                    $"(0x{diagnosticFailure.HResult:X8}).");
            }

        }

        // `_pipelineTask` is the barrier published before RebuildAsync starts;
        // preserve the worker's exact terminal state for teardown observers.
        MarkdownRebuildCoordinator.CompletePipelineLifetime(completed, lifetime);

        // The task is the retirement barrier for the CTS. Do not clear
        // _pipelineCts/_activePipelineGeneration here: they identify the
        // published snapshot's image and lazy-layout lifetime after rendering.

        if (dispatcher is null)
        {
            // A loaded WinUI control normally has a dispatcher. If it has been
            // torn down, lifecycle cleanup has already reset the coordinator.
            _rebuildCoordinator.CompletePipeline(generation);
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            OnRebuildPipelineCompleted(generation);
            return;
        }

        bool enqueued = false;
        try
        {
            enqueued = dispatcher.TryEnqueue(() => OnRebuildPipelineCompleted(generation));
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Dispatcher rejected rebuild completion callback: {exception.Message}");
        }

        if (!enqueued)
        {
            // Keep pending state intact. A later pointer release or rebuild
            // request can retry scheduling; unloading resets it outright.
            _rebuildCoordinator.CompletePipeline(generation);
            MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Dispatcher did not accept rebuild completion callback.");
        }
    }

    private void OnRebuildPipelineCompleted(long generation)
    {
        if (_isDisposed || _isUnloaded || !_rebuildCoordinator.CompletePipeline(generation))
            return;

        DrainRejectedCrossThreadRebuildRequest();
        if (_rebuildCoordinator.HasPending)
        {
            // ProcessQueuedRebuildCore allocates the scene-only generation at
            // the moment the replacement pipeline can actually start.
            QueuePendingRebuild();
        }
        else
        {
            // A failed/canceled current-generation rebuild did not publish the
            // snapshot needed to restore handles. Do not carry this intent into
            // an unrelated future rebuild.
            _pendingTouchSelectionPresentationRestore = null;
        }
    }

    private void CaptureTouchSelectionPresentationRestoreIntent()
    {
        if (_snapshot is null || !_selection.IsActive)
        {
            _pendingTouchSelectionPresentationRestore = null;
            return;
        }

        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        DocumentRange selectionRange = _selection.Range.Normalized();
        if (_touchSelection.ShouldShowHandles)
        {
            _pendingTouchSelectionPresentationRestore =
                new TouchSelectionPresentationRestoreIntent(snapshotGeneration, selectionRange);
            return;
        }

        if (_pendingTouchSelectionPresentationRestore is not { } pending ||
            !pending.Matches(snapshotGeneration, selectionRange))
        {
            _pendingTouchSelectionPresentationRestore = null;
        }
    }

    private Task RetireCancellationTokenSource(
        CancellationTokenSource? cts,
        Task? lifetime,
        long supersededGeneration = 0,
        long cancellationStartTimestamp = 0)
    {
        if (lifetime is not null)
            _snapshotRetirements.Track(lifetime);

        Task retirement;
        if (cts is null)
        {
            retirement = lifetime ?? Task.CompletedTask;
        }
        else
        {
            // Retain the work and cancellation-callback tasks independently:
            // CancelAndDisposeAfter intentionally suppresses their failures so
            // it can always dispose the source.
            Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(cts);
            _snapshotRetirements.Track(callbacks);
            retirement = CancellationTokenSourceRetirement.DisposeAfter(
                cts,
                callbacks,
                lifetime);
            _snapshotRetirements.Track(retirement);
        }

        var performance = MarkdownPerformanceEventSource.Log;
        if (cancellationStartTimestamp != 0 &&
            (lifetime is null || lifetime.IsCompleted))
        {
            performance.SupersededPipelineStopped(
                supersededGeneration,
                Stopwatch.GetTimestamp() - cancellationStartTimestamp);
        }
        else if (cancellationStartTimestamp != 0 && lifetime is not null)
        {
            _ = lifetime.ContinueWith(
                static (_, state) =>
                {
                    var observation = (PipelineCancellationObservation)state!;
                    MarkdownPerformanceEventSource.Log.SupersededPipelineStopped(
                        observation.Generation,
                        Stopwatch.GetTimestamp() - observation.CancellationStartTimestamp);
                },
                new PipelineCancellationObservation(
                    supersededGeneration,
                    cancellationStartTimestamp),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return retirement;
    }

    private Task RetireOwnedEngine(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // Snapshot the barrier before tracking this engine's own retirement;
        // otherwise a later shutdown drain could wait on a task that waits on
        // that same drain.
        if (_pipelineTask is { } activePipeline)
            _snapshotRetirements.Track(activePipeline);
        Task lifetime = _snapshotRetirements.WaitForPendingAsync();
        Task retirement = OwnedResourceRetirement.DisposeAfterPropagating(engine, lifetime);
        _snapshotRetirements.Track(retirement);
        return retirement;
    }

    private sealed class PipelineCancellationObservation(
        long Generation,
        long CancellationStartTimestamp)
    {
        public readonly long Generation = Generation;
        public readonly long CancellationStartTimestamp = CancellationStartTimestamp;
    }

    private void ResetTransientInteractionState(bool clearSelection)
    {
        // Clear logical capture ownership before touching XAML. A disconnected
        // input site must not keep a replacement blocked by a stale gesture.
        _touchTapSuppression.Reset();
        ResetSelectionHandleDrag();
        _touchSelection.Reset();
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        _clickMode = ClickMode.Single;
        _dragAnchorStart = default;
        _dragAnchorEnd = default;
        _consecutiveClickCount = 0;
        _lastPressTickMs = 0;
        _lastPressPoint = default;
        _lastHoveredRun = null;
        _lastHoveredBox = null;
        _lastHoveredAbbreviation = null;
        _pendingAbbreviationTooltipRun = null;
        CloseSelectionContextMenu();
        SetSelectionDragShieldActive(false);
        HideAbbreviationTooltip();

        SetCursorShape(null);

        try { _canvas?.ReleasePointerCaptures(); } catch { }

        if (!clearSelection)
            return;

        _selection.Clear();
        _selectionAdornerGeometryDirty = true;
        _selectionAdornerVisualDirty = true;
        InvalidateSelectionAdorner();
    }

    private async Task RebuildAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes)
    {
        try
        {
            await RebuildInternalAsync(
                ct,
                reason,
                generation,
                pipelineStartTimestamp,
                sourceUtf16Bytes).ConfigureAwait(true);
            if (!ct.IsCancellationRequested && generation == _pipelineGeneration)
            {
                RaiseRenderCompleted(generation);
            }
        }
        catch (OperationCanceledException) { /* expected – a new build was requested */ }
        catch (MarkdownLayoutRetirementException retirementFailure)
        {
            if (retirementFailure.TakePrimaryFailure() is { } primaryFailure &&
                primaryFailure is not OperationCanceledException)
            {
                RaiseRenderFailed(generation, primaryFailure);
            }

            // Resource retirement is part of pipeline completion, not an
            // ordinary render diagnostic. Keep this task faulted so a control
            // shutdown barrier cannot report success after failed cleanup.
            throw;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild failed: {ex}");
            RaiseRenderFailed(generation, ex);
        }
    }

    private void RaiseRenderCompleted(long generation)
    {
        if (_isDisposed || _isUnloaded || generation != _pipelineGeneration)
            return;

        try
        {
            RenderCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] RenderCompleted subscriber failed: {ex.Message}");
        }

        PendingImagePrefetch? pending = Interlocked.Exchange(ref _pendingImagePrefetch, null);
        if (pending is not null)
        {
            // The first resolver call can perform synchronous host-policy work.
            // Queue collection and resolution together so RenderCompleted never
            // inherits that work on the UI thread.
            _ = Task.Run(() => PrefetchImageSourcesObservedAsync(
                pending.Document,
                pending.SafeHtmlPolicy,
                pending.Prefetcher,
                pending.Context,
                pending.CancellationToken));
        }
    }

    private void RaiseRenderFailed(long generation, Exception exception)
    {
        if (_isDisposed || _isUnloaded || generation != _pipelineGeneration)
            return;

        try
        {
            RenderFailed?.Invoke(this, new MarkdownRenderFailedEventArgs(exception));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] RenderFailed subscriber failed: {ex.Message}");
        }
    }

    private async Task<ParsedMarkdown?> GetParsedMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken ct)
    {
        var normalizedSource = ForgivingDataUriFixer.Fix(source ?? string.Empty);
        int registryRevision = registry.Revision;
        lock (_parseCacheGate)
        {
            if (_parseCache is not null &&
                ReferenceEquals(_parseCacheRegistry, registry) &&
                _parseCacheRegistryRevision == registryRevision &&
                string.Equals(_parseCacheSource, normalizedSource, StringComparison.Ordinal))
            {
                return _parseCache;
            }
        }

        if (ct.IsCancellationRequested)
            return null;

        var pipeline = registry.BuildPipeline();
        var parser = new MarkdigParser(pipeline);
        var parsed = await parser.ParseAsync(normalizedSource, ct).ConfigureAwait(true);
        if (parsed is null || ct.IsCancellationRequested)
            return null;

        lock (_parseCacheGate)
        {
            _parseCache = parsed;
            _parseCacheSource = parsed.SourceText;
            _parseCacheRegistry = registry;
            _parseCacheRegistryRevision = registryRevision;
        }

        return parsed;
    }

    Task<ParsedMarkdown?> IMarkdownPerformanceLegacyParser.ParseLegacyMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken) =>
        GetParsedMarkdownAsync(source, registry, cancellationToken);

    private async Task RebuildInternalAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes)
    {
        if (_canvas is null || _overlay is null || _root is null) return;
        var width = (float)Math.Max(50, ActualWidth);
        _lastWidth = width;

        MarkdownExtensionRegistry? extensionRegistrySnapshot = ExtensionRegistry;
        MarkdownExtensionRegistry legacyRegistry = extensionRegistrySnapshot ?? _defaultRegistry;
        int parserRegistryRevisionSnapshot = legacyRegistry.Revision;
        MarkdownEngine? engineSnapshot = Engine;
        IMarkdownPerformanceSessionInternal? performanceSessionSnapshot =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
        if (performanceSessionSnapshot?.IsDisposed == true)
            performanceSessionSnapshot = null;
        var reusableDocument = _hasExplicitDocument ? Document : null;
        var source = reusableDocument?.Source ?? Markdown ?? string.Empty;
        long parseStarted = Stopwatch.GetTimestamp();

        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        MarkdownRenderer.Document.MarkdownDocument? semanticDocument = reusableDocument;
        MarkdownRenderer.Document.MarkdownDocument? reusableParsedDocument =
            reusableDocument?.ParsedDocument is not null ? reusableDocument : null;
        if (performanceSessionSnapshot is not null)
        {
            semanticDocument = await performanceSessionSnapshot.ParseAndPrepareDocumentAsync(
                engineSnapshot,
                reusableParsedDocument,
                source,
                legacyRegistry,
                this,
                ct).ConfigureAwait(true);
        }

        ParsedMarkdown? parsed;
        if (semanticDocument?.ParsedDocument is { } semanticAst)
        {
            parsed = new ParsedMarkdown(semanticDocument.Source, semanticAst);
        }
        else if (engineSnapshot is { } engine)
        {
            semanticDocument = await engine.ParseAsync(source, ct).ConfigureAwait(true);
            parsed = semanticDocument.ParsedDocument is { } engineAst
                ? new ParsedMarkdown(semanticDocument.Source, engineAst)
                : null;
        }
        else
        {
            parsed = await GetParsedMarkdownAsync(source, legacyRegistry, ct).ConfigureAwait(true);
            if (parsed is not null)
            {
                semanticDocument = await Task.Run(
                    () => MarkdownRenderer.Document.MarkdownDocument.FromParsed(
                        parsed.SourceText,
                        parsed.Document,
                        diagnostics: [],
                        ct),
                    CancellationToken.None).ConfigureAwait(true);
            }
        }

        if (parsed is null || semanticDocument is null || ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;
        long parseEnded = Stopwatch.GetTimestamp();
        double parseMilliseconds = Stopwatch.GetElapsedTime(parseStarted, parseEnded).TotalMilliseconds;

        // Presentation registrations travel with reusable documents/engines.
        // Keep the legacy control registry only as the fallback parser/layout
        // source so an engine-produced document cannot silently lose its
        // custom renderers when it reaches the WinUI adapter.
        MarkdownExtensionRegistry baseRegistry =
            semanticDocument.PresentationConfiguration as MarkdownExtensionRegistry ??
            engineSnapshot?.PresentationConfiguration as MarkdownExtensionRegistry ??
            _defaultRegistry;
        int presentationRegistryRevisionSnapshot = baseRegistry.Revision;
        int extensionRegistryRevisionSnapshot = extensionRegistrySnapshot?.Revision ?? 0;
        MarkdownExtensionRegistry layoutRegistry =
            MarkdownExtensionRegistry.ComposePresentation(baseRegistry, extensionRegistrySnapshot);

        var sourceMap = new MarkdownSourceMap(parsed.SourceText);
        var theme = Theme ?? _defaultTheme;
        MarkdownStyleSheet? styleSheetSnapshot = StyleSheet;
        FlowDirection flowDirectionSnapshot = FlowDirection;
        var embedFactorySnapshot = EmbedFactory;
        var hostedElementFactorySnapshot = HostedElementFactory;
        var imageResolverSnapshot = ImageResolver;
        // The host resolver may be wrapped in a per-document scope below; the
        // carry identity uses the host's own resolver.
        var imageResolverIdentity = imageResolverSnapshot;
        var svgRendererSnapshot = SvgRenderer;
        var rasterDecoderSnapshot = RasterDecoder;
        Uri? imageBaseUriSnapshot = ImageBaseUri;
        string? imageDocumentPathSnapshot = ImageDocumentPath;
        MarkdownDocumentSource? imageDocumentSourceSnapshot = ImageDocumentSource;
        bool allowThirdPartyRemoteImagesSnapshot = AllowThirdPartyRemoteImages;
        var imageResolveContext = new MarkdownImageResolveContext(
            imageBaseUriSnapshot,
            imageDocumentPathSnapshot,
            allowThirdPartyRemoteImagesSnapshot,
            imageDocumentSourceSnapshot);
        if (performanceSessionSnapshot is not null && imageResolverSnapshot is not null)
        {
            IMarkdownPerformanceDocumentScope? scope = performanceSessionSnapshot.PrepareDocumentScope(
                this,
                parsed.SourceText,
                layoutRegistry.Revision,
                parsed.Document,
                layoutRegistry.SafeHtmlPolicy,
                imageResolverSnapshot,
                imageResolveContext);
            if (scope is null)
            {
                performanceSessionSnapshot = null;
                QueueImageSourcePrefetch(
                    parsed.Document,
                    parsed.SourceText,
                    layoutRegistry,
                    imageResolverSnapshot,
                    imageResolveContext);
            }
            else
                imageResolverSnapshot = scope;
        }
        else
        {
            performanceSessionSnapshot?.ReleaseDocumentScope(this);
            QueueImageSourcePrefetch(
                parsed.Document,
                parsed.SourceText,
                layoutRegistry,
                imageResolverSnapshot,
                imageResolveContext);
        }
        bool codeBlockCopyEnabledSnapshot = IsCodeBlockCopyEnabled;
        bool taskListEditingEnabledSnapshot = IsTaskListEditingEnabled;
        var commandProviderSnapshot = CommandProvider;
        MarkdownLocalizationSnapshot localizationSnapshot = CaptureRequestedLocalization();
        var stringProviderSnapshot = localizationSnapshot.StringProvider;
        string languageSnapshot = localizationSnapshot.Culture.Name;
        CodeBlockLineNumberMode lineNumberModeSnapshot = CodeBlockLineNumberMode;
        CodeBlockWrappingMode wrappingModeSnapshot = CodeBlockWrappingMode;
        CodeBlockChromeMode chromeModeSnapshot = CodeBlockChromeMode;
        var disclosureStatesSnapshot = new Dictionary<string, bool>(
            _disclosureStates,
            StringComparer.Ordinal);
        double textScaleFactor = _environmentSnapshot.TextScaleFactor > 0
            ? _environmentSnapshot.TextScaleFactor
            : 1.0;
        ulong? styleRoleDemandMask = null;
        if (performanceSessionSnapshot is not null &&
            !layoutRegistry.HasUnclassifiedStyleRoleRenderers &&
            performanceSessionSnapshot.TryGetStyleRoleDemandMask(
                semanticDocument,
                ct,
                out ulong completeStyleRoleDemandMask))
        {
            styleRoleDemandMask = completeStyleRoleDemandMask;
        }

        long themeSnapshotStarted = Stopwatch.GetTimestamp();
        var themeResolver = new ThemeResolver(this, theme);
        var themeSnapshot = themeResolver.CreateSnapshot(
            styleSheetSnapshot,
            textScaleFactor,
            semanticDocument.GetExtensionStyleRoleNames(),
            styleRoleDemandMask);
        double themeSnapshotMilliseconds = Stopwatch.GetElapsedTime(themeSnapshotStarted).TotalMilliseconds;
        // Use the shared CanvasDevice (always available, no visual-tree required).
        // CanvasVirtualControl only has a device after CreateResources fires, so
        // passing _canvas directly would crash if layout runs before first draw.
        var device = CanvasDevice.GetSharedDevice();
        // Capture the host's rasterization scale so raster-fallback image
        // paths (e.g. SvgSkiaRasterizer) render at device-pixel resolution.
        // XamlRoot is null until the control is loaded; default to 1.0.
        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        MarkdownLayoutDiagnostics? layoutDiagnostics =
            performanceSessionSnapshot?.IsLayoutDiagnosticCaptureEnabled == true ||
            EnableStandaloneLayoutDiagnostics
                ? new MarkdownLayoutDiagnostics()
                : null;
#endif
        var imageCarryIdentity = new Layout.Boxes.MarkdownImageCarryIdentity(
            parsed.SourceText,
            imageBaseUriSnapshot,
            imageDocumentPathSnapshot,
            imageDocumentSourceSnapshot,
            imageResolverIdentity,
            svgRendererSnapshot,
            rasterDecoderSnapshot,
            allowThirdPartyRemoteImagesSnapshot);
        IReadOnlyDictionary<string, Layout.Boxes.CompletedImageCarry>? completedImageCarrySnapshot =
            CaptureCompletedImageCarry(
                imageCarryIdentity,
                // A permanent failure is carried only across an automatic
                // scene rebuild; any other rebuild (for example the host's Retry)
                // still retries it.
                carryFailures: reason == RebuildReason.SceneMaterialized);
        var ctx = new MarkdownLayoutContext(
            device,
            themeSnapshot,
            sourceMap,
            layoutRegistry,
            flowDirectionSnapshot,
            DispatcherQueue,
            languageSnapshot)
        {
            RasterizationScale = rasterScale,
            // Propagate the rebuild lifetime through layout and host image
            // construction. Image resolution has a separate loaded-lifetime
            // token so benign width/theme relayouts do not cancel committed
            // image boxes before they can publish their result.
            CancellationToken = ct,
            ImageCancellationToken = _imageLifetimeCts?.Token ?? CancellationToken.None,
            // Failed layout construction can retire images after the control's
            // current drain epoch has started. Keep a context-local barrier and
            // also publish those retirements to the control's observer.
            RetirementSink = _snapshotRetirements.Track,
            IsCodeBlockCopyEnabled = codeBlockCopyEnabledSnapshot,
            IsTaskListEditingEnabled = taskListEditingEnabledSnapshot,
            CommandProvider = commandProviderSnapshot,
            TaskCommandAvailabilityChanged = OnTaskCommandAvailabilityChanged,
            StringProvider = stringProviderSnapshot,
            CodeBlockLineNumberMode = lineNumberModeSnapshot,
            CodeBlockWrappingMode = wrappingModeSnapshot,
            CodeBlockChromeMode = chromeModeSnapshot,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            LayoutDiagnostics = layoutDiagnostics,
#endif
            ImageResolver = imageResolverSnapshot,
            PerformanceSession = performanceSessionSnapshot,
            PerformanceDocumentOwner = this,
            ImagePreparationOwner = _imagePreparationOwnerToken,
            SvgRenderer = svgRendererSnapshot,
            RasterDecoder = rasterDecoderSnapshot,
            ImageBaseUri = imageBaseUriSnapshot,
            ImageDocumentPath = imageDocumentPathSnapshot,
            ImageDocumentSource = imageDocumentSourceSnapshot,
            AllowThirdPartyRemoteImages = allowThirdPartyRemoteImagesSnapshot,
            ImageUnavailable = RaiseImageUnavailable,
            DisclosureStates = disclosureStatesSnapshot,
            ImageCarryIdentity = imageCarryIdentity,
            CompletedImageCarry = completedImageCarrySnapshot,
        };
        Exception? primaryFailure = null;
        try
        {
        SharedLayoutMetricsKey? sharedLayoutMetricsKey = engineSnapshot is null
            ? null
            : new SharedLayoutMetricsKey(
                parsed.SourceText,
                semanticDocument,
                widthInSixtyFourthDips: (int)Math.Clamp(
                    Math.Round(width * 64d),
                    1d,
                    int.MaxValue),
                typographyFingerprint: themeSnapshot.LayoutFingerprint,
                flowDirection: (int)flowDirectionSnapshot,
                codeWrappingMode: (int)wrappingModeSnapshot,
                lineNumberMode: (int)lineNumberModeSnapshot,
                codeCopyEnabled: codeBlockCopyEnabledSnapshot,
                taskEditingEnabled: taskListEditingEnabledSnapshot,
                rasterScaleInThousandths: (int)Math.Clamp(
                    Math.Round(rasterScale * 1_000d),
                    1d,
                    int.MaxValue),
                language: languageSnapshot,
                registryRevision: layoutRegistry.Revision,
                disclosureFingerprint: ComputeDisclosureFingerprint(disclosureStatesSnapshot),
                imageContext: new SharedLayoutImageContext(
                    imageBaseUriSnapshot?.OriginalString,
                    imageDocumentPathSnapshot,
                    allowThirdPartyRemoteImagesSnapshot,
                    imageDocumentSourceSnapshot),
                styleSheetSnapshot,
                layoutRegistry,
                embedFactorySnapshot,
                hostedElementFactorySnapshot,
                imageResolverSnapshot,
                svgRendererSnapshot,
                rasterDecoderSnapshot,
                commandProviderSnapshot,
                stringProviderSnapshot);
        var builder = new LayoutBuilder(
            ctx,
            embedFactorySnapshot,
            enableDeclarativeHostedElements: hostedElementFactorySnapshot is not null,
            semanticDocument,
            new HashSet<HostedElementFallbackKey>(_hostedElementFallbacks),
            engineSnapshot,
            sharedLayoutMetricsKey);

        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        // Custom block embed measurement remains on the background build path.
        // Subsequent lazy-band extension is also dispatched to a background
        // worker, so IMarkdownEmbedFactory.MeasureHeight and text reflow never
        // run from viewport or paint callbacks.
        bool visibleFirstVectorAdmissionRequested = performanceSessionSnapshot is
            { IsDisposed: false, Options.DeferOffscreenScenes: true };
        bool useLazyLayout = visibleFirstVectorAdmissionRequested || MarkdownLazyLayoutPolicy.ShouldUse(
            parsed.Document.Count,
            parsed.SourceText.Length,
            hasCustomBlockEmbedMeasurement: embedFactorySnapshot is not null);
        double initialLazyOverscan = performanceSessionSnapshot is null
            ? LazyLayoutOverscanPx
            : Math.Clamp(
                viewportHeight * performanceSessionSnapshot.Options.LookAheadViewports,
                0,
                6000);
        long layoutStarted = Stopwatch.GetTimestamp();
        double setupMilliseconds = Stopwatch.GetElapsedTime(parseEnded, layoutStarted).TotalMilliseconds;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        using var initialLayoutDispatcherProbe =
            layoutDiagnostics is not null && !_hasInitialLayoutDispatcherDiagnostics
                ? new MarkdownInitialLayoutDispatcherProbe(this, DispatcherQueue, layoutDiagnostics)
                : null;
#endif
        (LayoutSnapshot? Snapshot, double CpuMilliseconds, long Started, long Ended) layoutResult;
        Func<CancellationToken, (LayoutSnapshot? Snapshot, double CpuMilliseconds, long Started, long Ended)> layoutWork =
            layoutToken =>
            {
                ctx.SetCancellationToken(layoutToken);
                long started = Stopwatch.GetTimestamp();
                // Keep this synchronous build on one worker. Its CPU time
                // separates layout work from queueing/descheduling when a
                // profile reports an anomalous wall-clock stall.
                bool captureLayoutCpu = performanceSessionSnapshot is not null;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                captureLayoutCpu |= layoutDiagnostics is not null;
#endif
                long cpuStarted = !captureLayoutCpu
                    ? -1
                    : ThreadCpuClock.ReadCurrentThreadTicks();
                LayoutSnapshot? built = BuildSnapshotOrNullOnCancellation(
                    builder,
                    parsed.Document,
                    width,
                    viewportTop,
                    viewportHeight,
                    useLazyLayout,
                    initialLazyOverscan,
                    layoutToken);
                long cpuEnded = cpuStarted < 0
                    ? -1
                    : ThreadCpuClock.ReadCurrentThreadTicks();
                long ended = Stopwatch.GetTimestamp();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                initialLayoutDispatcherProbe?.EnqueueWorkerCompletionSentinel(ended);
#endif
                return (built, ThreadCpuClock.ElapsedMilliseconds(cpuStarted, cpuEnded), started, ended);
            };
        try
        {
            Task<(LayoutSnapshot? Snapshot, double CpuMilliseconds, long Started, long Ended)> layoutTask =
                performanceSessionSnapshot is null
                    ? Task.Run(() => layoutWork(ct), CancellationToken.None)
                    : performanceSessionSnapshot.ScheduleLayoutWorkAsync(
                        MarkdownLayoutWorkPriority.Primary,
                        layoutWork,
                        ct);
            layoutResult = await layoutTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (
            ct.IsCancellationRequested || performanceSessionSnapshot?.IsDisposed == true)
        {
            return;
        }
        var (snapshot, layoutCpuMilliseconds, workerStarted, workerEnded) = layoutResult;
        long layoutEnded = Stopwatch.GetTimestamp();
        double layoutMilliseconds = Stopwatch.GetElapsedTime(layoutStarted, layoutEnded).TotalMilliseconds;
        double layoutQueueMilliseconds = Stopwatch.GetElapsedTime(layoutStarted, workerStarted).TotalMilliseconds;
        double layoutWorkerWallMilliseconds = Stopwatch.GetElapsedTime(workerStarted, workerEnded).TotalMilliseconds;
        double layoutContinuationMilliseconds = Stopwatch.GetElapsedTime(workerEnded, layoutEnded).TotalMilliseconds;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        MarkdownLayoutDiagnosticsSnapshot layoutDiagnosticSnapshot =
            layoutDiagnostics?.Capture() ?? default;
#endif
        if (snapshot is null || ct.IsCancellationRequested || generation != _pipelineGeneration)
        {
            if (snapshot is not null)
                ctx.RegisterRetirement(snapshot.Retire());
            return;
        }

        // From this point the snapshot holds GPU-side CanvasTextLayout objects.
        // If we are cancelled before committing, dispose it to avoid a native-memory leak.
        // `committed` tracks whether the snapshot was written to _snapshot; the catch
        // block must only dispose it if not yet committed (post-commit _snapshot owns it).
        bool committed = false;
        long publicationStarted = Stopwatch.GetTimestamp();
        try
        {
        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        // Scroll anchoring: capture the current read position before the canvas
        // height changes.  If the user has scrolled down, we identify the first
        // visible block (its top edge closest to viewport top) and how far it is
        // from the viewport top.  After committing the new layout we restore the
        // same offset so content above the fold shifting (e.g. an image loading)
        // doesn't jump the reader's position.
        (int? BlockIndex, double OffsetFromTop, double OldOffset, double OldHeight)? scrollAnchor = null;
        string? imageDocumentId = imageDocumentSourceSnapshot?.DocumentId;
        bool sameDocumentIdentity = _committedImageDocumentId == imageDocumentId;
        if (sameDocumentIdentity &&
            _scroll is { VerticalOffset: > 0 } scrollSnap &&
            _snapshot is { } prevSnap)
        {
            double vTop = scrollSnap.VerticalOffset;
            if (prevSnap.TryCaptureScrollAnchor(vTop, out var capturedAnchor))
            {
                scrollAnchor = (
                    capturedAnchor.BlockIndex,
                    capturedAnchor.OffsetFromTop,
                    vTop,
                    capturedAnchor.Height);
            }
            else
            {
                // Lazy measurement still owns the old snapshot. Preserve a
                // proportional fallback from the already-committed XAML extent
                // rather than blocking this UI-thread replacement for its lock.
                scrollAnchor = (null, 0, vTop, _root?.Height ?? 0);
            }
        }

        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        // Atomically swap snapshots, then retire the old one so its
        // CanvasTextLayout / placeholder handles are released without making
        // this UI-thread commit wait for an in-flight lazy measure.
        var old = _snapshot;
        long touchSelectionRestoreSourceGeneration = _snapshotGeneration;
        TouchSelectionPresentationRestoreIntent? touchSelectionRestoreIntent =
            _pendingTouchSelectionPresentationRestore;
        var automationDocumentIdentity = new MarkdownAutomationDocumentIdentity(
            snapshot.SourceFingerprint,
            snapshot.SemanticDocument.TextProjectionFingerprint,
            snapshot.SemanticDocument.SemanticRangeFingerprint,
            reusableDocument,
            engineSnapshot,
            legacyRegistry,
            parserRegistryRevisionSnapshot,
            baseRegistry,
            presentationRegistryRevisionSnapshot,
            extensionRegistrySnapshot,
            extensionRegistryRevisionSnapshot,
            stringProviderSnapshot,
            languageSnapshot);
        bool sameAutomationDocument = _automationDocumentGenerations.IsCurrent(automationDocumentIdentity);
        bool sameDocument = old is not null && sameDocumentIdentity &&
            old.SourceFingerprint.Equals(snapshot.SourceFingerprint) &&
            sameAutomationDocument;
        bool semanticInputChanged = old is null ||
            !sameDocument ||
            !ReferenceEquals(Document, semanticDocument) ||
            !ReferenceEquals(_committedLocalization?.StringProvider, stringProviderSnapshot) ||
            !string.Equals(
                _committedLocalization?.Culture.Name,
                languageSnapshot,
                StringComparison.OrdinalIgnoreCase);
        bool preserveSelection = !_selection.Range.Normalized().IsEmpty && sameDocument;
        // Scrolling or paint can queue another band realization while this
        // replacement pipeline is building. Fence that old-snapshot work at
        // the commit boundary before publishing and retiring its owner.
        CancelLazyLayoutRealization();
        _pendingLayoutCommit = null;
        if (!sameDocument)
            _scrollViewIntermediate = false;
        snapshot.AssignAutomationDocumentGeneration(
            _automationDocumentGenerations.Publish(automationDocumentIdentity));
        // Old subscriptions/dirty indices cannot cross this snapshot boundary,
        // including reentrant property notifications during the commit below.
        ClearPendingFocusGeometryWork();
        UnsubscribeAllImages();
        _pendingImageRelayoutBlockIndices.Clear();
        _imageRelayoutQueued = false;
        ++_lastImageRelayoutDispatchTicket;
        if (!_imageRelayoutActive)
            StopImageRelayoutRecovery();
        _pendingScrollToBlock = null;
        Volatile.Write(ref _imagePlanPublication, null);
        if (!sameDocument)
        {
            // A new logical document must not expose the former document's
            // value-only focus identities to automation or reentrant property
            // callbacks below.
            _focusableItems = Array.Empty<Layout.FocusableItem>();
            _focusedItemIndex = -1;
            _focusResumeItemIndex = -1;
            _pendingLazyFocus = null;
        }
        _snapshot = snapshot;
        _snapshotLayoutDevice = device;
        // Publish presentation state with the new snapshot before any
        // dependency-property notification can re-enter UIA or menu queries.
        // These immutable objects therefore always describe the same commit.
        _themeSnapshot = themeSnapshot;
        // The tile clear color is presentation state too. Publish it only with
        // the snapshot whose boxes use the matching immutable ThemeSnapshot;
        // otherwise an invalidation during background layout can clear using
        // the next theme and repaint using the previous one.
        _canvasBackground = themeSnapshot.SurfaceColor;
        _committedLocalization = localizationSnapshot;
        _committedImageDocumentId = imageDocumentId;
        _snapshotGeneration = generation;
        MarkFocusableItemsRefreshPending(snapshot, generation);
        SynchronizeVectorCapacityObserver(snapshot);
        _snapshotPipelineStartTimestamp = pipelineStartTimestamp;
        _snapshotSourceUtf16Bytes = sourceUtf16Bytes;
        committed = true;
        NotifyAutomationSnapshotChanged();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        if (!_hasInitialLayoutDispatcherDiagnostics && initialLayoutDispatcherProbe is not null)
        {
            _initialLayoutDispatcherDiagnostics = initialLayoutDispatcherProbe.StopAndCapture();
            _initialLayoutDispatcherDiagnosticsSource = layoutDiagnostics;
            _hasInitialLayoutDispatcherDiagnostics = true;
        }
#endif
        _appliedLazyLayoutRevision = snapshot.LayoutRevision;
        if (!ReferenceEquals(Document, semanticDocument))
        {
            _isUpdatingDocumentProperty = true;
            try { SetValue(DocumentProperty, semanticDocument); }
            finally { _isUpdatingDocumentProperty = false; }
        }
        if (old is not null)
            CloseSelectionContextMenu();
        // Dependency-property publication above can reenter Dispose before
        // this old snapshot is retired. Tie its retirement to this context's
        // pipeline lifetime as well as the control's global drain.
        ctx.RegisterRetirement(RetireSnapshot(old));
        // Clear stale hover references so OnPointerExited/OnPointerMoved after the
        // rebuild don't use Bounds from the now-disposed old snapshot for invalidation.
        _lastHoveredRun = null;
        _lastHoveredBox = null;
        ApplySnapshotSize(snapshot.Size);

        // Restore scroll anchor: find the anchor block's new Y in the new layout
        // and adjust the scroll offset so the user's read position is unchanged.
        if (scrollAnchor is { } anchor &&
            _scroll is { } scrollRestore &&
            LazyLayoutScrollAnchorPolicy.ShouldRestore(
                anchor.OldOffset,
                scrollRestore.VerticalOffset,
                _scrollViewIntermediate))
        {
            double? newY = null;
            if (anchor.BlockIndex is { } anchorBlock)
            {
                if (snapshot.TryCaptureScrollAnchorTarget(
                        anchorBlock,
                        anchor.OffsetFromTop,
                        out double capturedTargetOffset))
                {
                    newY = capturedTargetOffset;
                }
            }

            if (newY is null)
            {
                double ratio = anchor.OldHeight > 0
                    ? anchor.OldOffset / anchor.OldHeight
                    : 0;
                newY = ratio > 0
                    ? ratio * snapshot.Size.Height
                    : anchor.OldOffset;
            }

            if (newY is { } targetOffset)
            {
                double maxOffset = Math.Max(0, snapshot.Size.Height - scrollRestore.ViewportHeight);
                scrollRestore.ChangeView(null, Math.Clamp(targetOffset, 0, maxOffset), null, disableAnimation: true);
            }
        }
        long commitEnded = Stopwatch.GetTimestamp();

        // UI thread: collect embed plans (don't realise yet), hook image
        // LoadCompleted, then realise only embeds that fall in the current
        // viewport. Hooking _scroll.ViewChanged drives subsequent realisation
        // as the user scrolls.
        DerealizeAllEmbeds();
        SetSelectionDragShieldActive(false);
        // Keep the attached adorner surface and its placeholder across
        // commits. Repaint against the new snapshot instead of replacing the
        // surface on every relayout.
        InvalidateSelectionAdornerPresentation();
        if (_selectionAdorner is not null)
            _selectionAdorner.Visibility = Visibility.Collapsed;
        RemoveOverlayChildrenExceptSelectionAdorner();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();
        _embedPlans.Clear();
        _embedPlanIndex = null;
        _activeEmbedOrdinals.Clear();
        _codeBlockActionPlans.Clear();
        _hasMeasuredCodeBlocks = false;
        _codeBlockActionPlanIndex = null;
        _activeCodeBlockActionOrdinals.Clear();
        UnsubscribeAllImages();
        _imagePlans.Clear();
        _imagePlanIndex = null;
        ResetImageResidency();
        // Identities change across rebuild even when the count happens to
        // match — reset so the first post-rebuild realisation always fires.
        _lastFiredRealizedCount = -1;
        _selectionAdornerRects.Clear();
        _selectionAdornerRectScratch.Clear();
        _selectionAdornerBackgrounds.Clear();
        _selectionAdornerBackgroundScratch.Clear();
        _selectionStartHandleEndpointAvailable = false;
        _selectionEndHandleEndpointAvailable = false;
        // Rebuild detaches selection chrome with the other overlay children.
        // Surviving selection reattaches what it needs below; inactive chrome
        // stays detached until the next interaction.
        bool restoreTouchSelectionPresentation = false;
        if (preserveSelection)
        {
            // Snapshot publication and dependency-property callbacks above can
            // reenter selection or request a newer rebuild. Revalidate the
            // original token against live state at the point of use rather
            // than relying on eligibility captured before those callbacks.
            bool touchSelectionRestoreStillMatches =
                _selection.IsActive &&
                touchSelectionRestoreIntent is { } capturedRestoreIntent &&
                _pendingTouchSelectionPresentationRestore is { } currentRestoreIntent &&
                currentRestoreIntent == capturedRestoreIntent &&
                capturedRestoreIntent.Matches(
                    touchSelectionRestoreSourceGeneration,
                    _selection.Range);
            bool hasPendingRebuild = _rebuildCoordinator.HasPending;

            if (touchSelectionRestoreStillMatches && !hasPendingRebuild)
            {
                restoreTouchSelectionPresentation =
                    _touchSelection.TryRestoreSelectedPresentation();
                _pendingTouchSelectionPresentationRestore = null;
            }
            else if (touchSelectionRestoreStillMatches && hasPendingRebuild)
            {
                // Rebase only across a successful same-document commit while a
                // newer rebuild is already queued. The request stays bound to
                // this exact range and the just-published snapshot.
                _pendingTouchSelectionPresentationRestore =
                    new TouchSelectionPresentationRestoreIntent(generation, _selection.Range);
            }
            else
            {
                _pendingTouchSelectionPresentationRestore = null;
            }

            UpdateSelectionOverlay();
            if (restoreTouchSelectionPresentation)
                UpdateSelectionHandles();
        }
        else
        {
            _pendingTouchSelectionPresentationRestore = null;
            _selection.Clear();
        }
        long overlayResetEnded = Stopwatch.GetTimestamp();
        _focusResumeItemIndex = -1;
        _pendingLazyFocus = null;
        _focusRing = null;              // evicted from overlay; will be lazily re-created on demand
        bool realizationPlansReady = RebuildRealizationPlans(
            snapshot,
            preserveRealized: false,
            out _,
            out _);
        if (!realizationPlansReady)
            DeferLayoutCommit(scrollAnchor: null);
        long planConstructionEnded = Stopwatch.GetTimestamp();
        if (_scroll is not null)
        {
            _scroll.ViewChanged -= OnScrollViewChanged;
            _scroll.ViewChanged += OnScrollViewChanged;
        }
        RealizeVisibleEmbeds();
        long embedRealizationEnded = Stopwatch.GetTimestamp();
        bool resumedPendingHostedFocus = ResumePendingLazyFocus(snapshot);
        _ = RestartCodeBlockHighlighting();
        long highlightRetirementEnded = Stopwatch.GetTimestamp();
        ScheduleVisibleCodeBlockHighlighting();
        long highlightSchedulingEnded = Stopwatch.GetTimestamp();
        UpdateSelectionAdornerViewport();
        if (!resumedPendingHostedFocus)
            UpdateFocusRing();
        long visibleRealizationEnded = Stopwatch.GetTimestamp();

        InvalidateCanvas();
        if (semanticInputChanged)
        {
            (FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer)?
                .NotifyDocumentChanged();
        }
        else
        {
            // A progressive scene may have been replaced while the semantic
            // source stayed identical. Refresh root UIA ItemStatus after the
            // committed snapshot without publishing text/structure events.
            NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
        }
        long publicationEnded = Stopwatch.GetTimestamp();
        _lastPipelineTiming = new MarkdownPipelineTimingSnapshot(
            generation,
            sourceUtf16Bytes,
            parseMilliseconds,
            setupMilliseconds,
            themeSnapshotMilliseconds,
            layoutMilliseconds,
            layoutCpuMilliseconds,
            layoutQueueMilliseconds,
            layoutWorkerWallMilliseconds,
            layoutContinuationMilliseconds,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            layoutDiagnosticSnapshot.TopLevelBlockCount,
            layoutDiagnosticSnapshot.CanvasTextLayoutConstructorCalls,
            layoutDiagnosticSnapshot.CanvasTextLayoutConstructorMilliseconds,
            layoutDiagnosticSnapshot.BlockConstructionMilliseconds,
            layoutDiagnosticSnapshot.MeasureArrangeMilliseconds,
            layoutDiagnosticSnapshot.SnapshotConstructionMilliseconds,
            _initialLayoutDispatcherDiagnostics.SentinelEnqueued,
            _initialLayoutDispatcherDiagnostics.SentinelDispatched,
            _initialLayoutDispatcherDiagnostics.SentinelDelayMilliseconds,
            _initialLayoutDispatcherDiagnostics.HeartbeatSampleCount,
            _initialLayoutDispatcherDiagnostics.HeartbeatMaxGapMilliseconds,
#endif
            publicationStarted,
            commitEnded,
            overlayResetEnded,
            planConstructionEnded,
            embedRealizationEnded,
            highlightRetirementEnded,
            highlightSchedulingEnded,
            visibleRealizationEnded,
            publicationEnded);
        } // end of snapshot try-block
        catch
        {
            throw;
        }
        finally
        {
            // Every cancellation/generation retry after construction must release
            // the unpublished Win2D layout. Once committed, _snapshot owns it.
            if (!committed)
                ctx.RegisterRetirement(snapshot.Retire());
        }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            // The control-level drain is epoch-based: work registered after its
            // disposal snapshot belongs to a later epoch. Drain this build's
            // own registrations before its pipeline task completes so provider
            // retirement cannot race failed-build image teardown.
            await ctx.DrainOwnedRetirementsAsync(primaryFailure).ConfigureAwait(true);
        }
    }

    private static LayoutSnapshot? BuildSnapshotOrNullOnCancellation(
        LayoutBuilder builder,
        Markdig.Syntax.MarkdownDocument document,
        float width,
        double viewportTop,
        double viewportHeight,
        bool useLazyLayout,
        double overscan,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        try
        {
            return useLazyLayout
                ? builder.BuildLazyForViewport(document, width, viewportTop, viewportHeight, overscan, ct)
                : builder.Build(document, width, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // Diagnostic callers run on the UI thread. A canceled or superseded build never
    // replaces these timings, so they always describe the snapshot
    // currently committed to the control.
    internal MarkdownPipelineTimingSnapshot GetLastPipelineTimingSnapshot() =>
        _lastPipelineTiming;

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private void OnInitialLayoutDispatcherSentinelDispatched(MarkdownLayoutDiagnostics diagnostics)
    {
        if (!_hasInitialLayoutDispatcherDiagnostics ||
            !ReferenceEquals(_initialLayoutDispatcherDiagnosticsSource, diagnostics))
        {
            return;
        }

        _initialLayoutDispatcherDiagnostics = diagnostics.CaptureDispatcher();
        _lastPipelineTiming = _lastPipelineTiming.WithInitialLayoutDispatcherDiagnostics(
            _initialLayoutDispatcherDiagnostics);
    }

    /// <summary>
    /// Diagnostic-only probe for the first layout build captured by a performance
    /// session. Its timer performs no XAML/UIA writes; it only updates the
    /// diagnostics accumulator from the dispatcher thread.
    /// </summary>
    private sealed class MarkdownInitialLayoutDispatcherProbe : IDisposable
    {
        private readonly WeakReference<MarkdownRendererControl> _owner;
        private readonly MarkdownLayoutDiagnostics _diagnostics;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
        private readonly Microsoft.UI.Dispatching.DispatcherQueueHandler _sentinelHandler;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _heartbeatTimer;
        private int _stopped;

        internal MarkdownInitialLayoutDispatcherProbe(
            MarkdownRendererControl owner,
            Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue,
            MarkdownLayoutDiagnostics diagnostics)
        {
            _owner = new WeakReference<MarkdownRendererControl>(owner);
            _diagnostics = diagnostics;
            _dispatcherQueue = dispatcherQueue;
            _sentinelHandler = OnSentinelDispatched;
            if (dispatcherQueue is null)
                return;

            try
            {
                Microsoft.UI.Dispatching.DispatcherQueueTimer timer = dispatcherQueue.CreateTimer();
                _heartbeatTimer = timer;
                timer.Interval = TimeSpan.FromMilliseconds(16);
                timer.IsRepeating = true;
                timer.Tick += OnHeartbeatTick;
                timer.Start();
                diagnostics.StartDispatcherHeartbeat(Stopwatch.GetTimestamp());
            }
            catch
            {
                // Diagnostics must not change layout behavior if the optional
                // dispatcher timer cannot be created or started.
                StopHeartbeatTimer();
            }
        }

        internal void EnqueueWorkerCompletionSentinel(long workerCompletionTimestamp)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;

            bool enqueued = false;
            try
            {
                enqueued = _dispatcherQueue?.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
                    _sentinelHandler) == true;
            }
            catch
            {
                // A failed diagnostic enqueue is recorded, not a layout failure.
            }

            _diagnostics.RecordDispatcherSentinelEnqueue(workerCompletionTimestamp, enqueued);
        }

        internal MarkdownInitialLayoutDispatcherDiagnosticsSnapshot StopAndCapture()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                StopHeartbeatTimer();
                _diagnostics.RecordDispatcherHeartbeat(Stopwatch.GetTimestamp());
            }

            return _diagnostics.CaptureDispatcher(freezeHeartbeat: true);
        }

        public void Dispose() => StopAndCapture();

        private void OnHeartbeatTick(
            Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
            object args) =>
            _diagnostics.RecordDispatcherHeartbeat(Stopwatch.GetTimestamp());

        private void OnSentinelDispatched()
        {
            _diagnostics.RecordDispatcherSentinelDispatched(Stopwatch.GetTimestamp());
            if (_owner.TryGetTarget(out MarkdownRendererControl? owner))
                owner.OnInitialLayoutDispatcherSentinelDispatched(_diagnostics);
        }

        private void StopHeartbeatTimer()
        {
            if (_heartbeatTimer is not { } timer)
                return;

            _heartbeatTimer = null;
            try
            {
                timer.Stop();
                timer.Tick -= OnHeartbeatTick;
            }
            catch
            {
                // Stopping an optional diagnostic timer must remain best-effort.
            }
        }
    }
#endif

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _scrollViewIntermediate = e.IsIntermediate;
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long afterLazyLayout = allocatedBefore;
        long afterAdorner = allocatedBefore;
        long afterRealization = allocatedBefore;
        try
        {
            QueueLazyLayoutForViewport();
            if (measure)
                afterLazyLayout = GC.GetAllocatedBytesForCurrentThread();
            // CanvasVirtualControl already requests newly exposed document
            // tiles. Only viewport-refresh the separate text adorner when it
            // has visible, pending, or interactive state that can change.
            // UpdateSelectionAdornerViewport includes the invalidation, so do
            // not issue a second request for the same scroll callback.
            if (ShouldRefreshSelectionAdornerForScroll())
                UpdateSelectionAdornerViewport();
            if (measure)
                afterAdorner = GC.GetAllocatedBytesForCurrentThread();
            // Run a final realisation pass after intermediate-view bursts settle
            // so we don't thrash during fling/inertia. ViewChanged with
            // IsIntermediate=false fires at the end of inertia; we also realise on
            // intermediate ticks to keep the visual current.
            RealizeVisibleEmbeds();
            if (measure)
                afterRealization = GC.GetAllocatedBytesForCurrentThread();
            ScheduleVisibleCodeBlockHighlighting();
        }
        finally
        {
            if (measure)
            {
                long allocated = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                performance.ScrollWork(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Stopwatch.GetTimestamp() - started,
                    allocated);
                performance.ScrollAllocationBreakdown(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Math.Max(0, afterLazyLayout - allocatedBefore),
                    Math.Max(0, afterAdorner - afterLazyLayout),
                    Math.Max(0, afterRealization - afterAdorner),
                    Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - afterRealization));
                GetCodeBlockActionRealizationCounts(out int realizedActions, out int offscreenActions);
                performance.CodeActionRealization(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    realizedActions,
                    offscreenActions);
            }
        }
    }

    private Task RestartCodeBlockHighlighting()
        => RetireCodeBlockHighlighting(restart: true);

    private Task RetireCodeBlockHighlighting(bool restart)
    {
        var old = _codeBlockHighlightCts;
        Task workLifetime = OwnedResourceRetirement.CombineLifetimes(
            _codeBlockHighlightRetirement,
            DrainCodeBlockHighlightTasks());
        _codeBlockHighlightCts = restart ? new CancellationTokenSource() : null;
        _codeBlockHighlightGeneration++;
        _codeBlockHighlightInFlight.Clear();
        Task retirement = RetireCancellationTokenSource(old, workLifetime);
        _codeBlockHighlightRetirement = retirement;
        return retirement;
    }

    private void RetireOwnedCodeHighlighter(IDisposable owner, Task lifetime)
    {
        Task retirement = OwnedResourceRetirement.DisposeAfter(
            owner,
            lifetime,
            static exception => MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Code highlighter retirement failed: " +
                $"{exception.GetType().Name} (0x{exception.HResult:X8})."));
        _snapshotRetirements.Track(retirement);
    }

    private void ScheduleVisibleCodeBlockHighlighting()
    {
        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return;
        ScheduleVisibleDeferredScenes();

        if (!_hasMeasuredCodeBlocks ||
            !IsCodeBlockSyntaxHighlightingEnabled ||
            CodeHighlighter is not { } highlighter ||
            _snapshot is not { } snapshot ||
            _themeSnapshot is not { } theme ||
            theme.IsHighContrast)
        {
            return;
        }

        var cts = _codeBlockHighlightCts;
        if (!CodeBlockHighlightOperationLifetime.TryCaptureToken(cts, out CancellationToken operationToken))
            return;

        IMarkdownPerformanceSession? performanceSession = PerformanceSession;
        bool runOnWorker = performanceSession is not null;
        var variant = ResolveCodeBlockThemeVariant(theme);
        int generation = _codeBlockHighlightGeneration;
        int providerIdentity = RuntimeHelpers.GetHashCode(highlighter);
        IMarkdownPerformanceSessionInternal? sceneSession =
            performanceSession as IMarkdownPerformanceSessionInternal;
        if (sceneSession?.IsDisposed == true)
            return;
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
        if (dispatcher is null)
            return;

        // Revision is provider code. It may synchronously replace the provider,
        // unload the control, or retire this generation. Capture all lifetime
        // state first, then fence immediately after this one callback without
        // calling Revision a second time.
        int providerRevision;
        try
        {
            providerRevision = highlighter.Revision;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Code highlighter revision failed: {ex.Message}");
            return;
        }

        if (!CodeBlockHighlightOperationLifetime.IsCurrentAfterProviderCallback(
                CodeHighlighter,
                highlighter,
                _codeBlockHighlightGeneration,
                generation,
                _isDisposed,
                _isUnloaded,
                operationToken))
        {
            return;
        }

        bool appliedCached = false;
        List<(CodeBlockHighlightCacheKey Key, string CodeText)>? pendingHighlights = null;
        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand highlightBand = performanceSession is { } activePerformanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                activePerformanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond)
            : LazyLayoutBand.FromViewport(
                viewportTop,
                viewportHeight,
                CodeBlockHighlightOverscanPx);
        // BlockBox instances are mutable and are disposed with their snapshot.
        // Inspect them only while holding the snapshot's nonblocking read
        // lease, and copy the immutable provider input before releasing it.
        if (!snapshot.TryBeginInteraction())
            return;
        try
        {
            IReadOnlyList<BlockBox> inspectedBlocks;
            if (!runOnWorker)
            {
                // The outer lease remains held while this legacy full measured
                // list is consumed; its returned BlockBox references do not
                // escape the protected inspection.
                inspectedBlocks = snapshot.GetMeasuredTopLevelBlocks();
            }
            else if (!snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                         highlightBand.Top,
                         highlightBand.Bottom,
                         out inspectedBlocks))
            {
                // Measurement or deferred scene readiness is transient. The
                // layout/scroll completion path schedules another inspection.
                return;
            }

            foreach (var block in EnumerateCodeBlocks(inspectedBlocks))
            {
                if (!IsCodeBlockInHighlightBand(snapshot, block, highlightBand.Top, highlightBand.Bottom))
                    continue;

                if (block.CodeText.Length > 200_000 || block.LineCount > 5_000)
                    continue;

                var key = CreateHighlightCacheKey(block, variant, providerIdentity, providerRevision);
                if (block.HasAppliedSyntaxHighlighting(key))
                    continue;
                if (_codeBlockHighlightCache.TryGetValue(key, out var cached))
                {
                    appliedCached |= block.ApplySyntaxHighlighting(key, cached.Spans);
                    continue;
                }

                if (!_codeBlockHighlightInFlight.Add(key))
                    continue;

                (pendingHighlights ??= [])
                    .Add((key, block.CodeText));
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (pendingHighlights is not null)
        {
            // One weak identity is shared across this admitted batch. The
            // provider work may outlive snapshot retirement, so task state and
            // dispatcher callbacks must not keep the retired document graph
            // rooted while they wait for completion.
            var snapshotIdentity = new WeakReference<LayoutSnapshot>(snapshot);
            for (int pendingIndex = 0; pendingIndex < pendingHighlights.Count; pendingIndex++)
            {
                if (!CodeBlockHighlightOperationLifetime.IsCurrentAfterProviderCallback(
                        CodeHighlighter,
                        highlighter,
                        _codeBlockHighlightGeneration,
                        generation,
                        _isDisposed,
                        _isUnloaded,
                        operationToken))
                {
                    for (int remaining = pendingIndex; remaining < pendingHighlights.Count; remaining++)
                        RemoveCodeBlockHighlightInFlight(pendingHighlights[remaining].Key, generation);
                    break;
                }

                var pending = pendingHighlights[pendingIndex];
                // Start user highlighter code only after releasing the layout
                // lease. Reserve its retirement lifetime before invoking
                // provider code: sessionless providers may reenter unload or
                // replacement synchronously before returning their Task.
                try
                {
                    CodeBlockHighlightOperationLifetime.StartAndTrack(
                        TrackCodeBlockHighlightTask,
                        () => runOnWorker
                            ? Task.Run(
                                () => HighlightCodeBlockAsync(
                                    snapshotIdentity, pending.CodeText, pending.Key,
                                    highlighter, generation,
                                    sceneSession, dispatcher, operationToken),
                                CancellationToken.None)
                            : HighlightCodeBlockAsync(
                                snapshotIdentity, pending.CodeText, pending.Key,
                                highlighter, generation,
                                sceneSession, dispatcher, operationToken));
                }
                catch (Exception ex)
                {
                    RemoveCodeBlockHighlightInFlight(pending.Key, generation);
                    MarkdownDiagnostics.WriteLine(
                        $"[MarkdownRendererControl] Code block highlighting could not start: {ex.Message}");
                }
            }
        }

        if (appliedCached)
            InvalidateCanvas();
    }

    internal void ScheduleVisibleDeferredScenes()
    {
        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return;
        IMarkdownPerformanceSessionInternal? session =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
        if (session is not null && !session.IsDisposed && session.Options.DeferOffscreenScenes)
            session.ScheduleDeferredScenes(this);
    }

    internal void RequestRebuildAfterDeferredScene()
        => RequestRebuild(RebuildReason.SceneMaterialized);

    /// <summary>Re-runs the visible deferred-scene probe after the next successful paint.</summary>
    internal void RequestDeferredSceneProbeAfterPaint()
        => Interlocked.Exchange(ref _deferredSceneProbeAfterPaint, 1);

    private void TrackCodeBlockHighlightTask(Task task)
    {
        lock (_codeBlockHighlightTasksGate)
        {
            _codeBlockHighlightTasks.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var owner = (MarkdownRendererControl)state!;
                lock (owner._codeBlockHighlightTasksGate)
                {
                    owner._codeBlockHighlightTasks.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task DrainCodeBlockHighlightTasks()
    {
        Task[] tasks;
        lock (_codeBlockHighlightTasksGate)
        {
            if (_codeBlockHighlightTasks.Count == 0)
                return Task.CompletedTask;

            tasks = new Task[_codeBlockHighlightTasks.Count];
            _codeBlockHighlightTasks.CopyTo(tasks);
            _codeBlockHighlightTasks.Clear();
        }

        return Task.WhenAll(tasks);
    }

    private async Task HighlightCodeBlockAsync(
        WeakReference<LayoutSnapshot> snapshotIdentity,
        string codeText,
        CodeBlockHighlightCacheKey key,
        ICodeHighlighter highlighter,
        int generation,
        IMarkdownPerformanceSessionInternal? sceneSession,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        CancellationToken token)
    {
        bool publicationQueued = false;
        try
        {
            token.ThrowIfCancellationRequested();
            await _codeBlockHighlightSemaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using IMarkdownScenePreparationLease? sceneLease = sceneSession is null
                    ? null
                    : await sceneSession.EnterScenePreparationAsync(this, token)
                        .ConfigureAwait(false);
                CancellationToken workToken = sceneLease?.CancellationToken ?? token;
                if (workToken.IsCancellationRequested)
                    return;

                var request = new CodeBlockHighlightRequest(key.Language, codeText, key.ThemeVariant, workToken);
                CodeBlockHighlightResult? providerResult = await highlighter.HighlightAsync(
                    request,
                    workToken).ConfigureAwait(false);
                if (workToken.IsCancellationRequested)
                    return;

                CodeBlockHighlightResult result = dispatcher.HasThreadAccess
                    ? await Task.Run(
                        () => CodeBlockHighlightResultSnapshot.CopyProviderResult(
                            providerResult,
                            workToken),
                        CancellationToken.None).ConfigureAwait(false)
                    : CodeBlockHighlightResultSnapshot.CopyProviderResult(
                        providerResult,
                        workToken);
                if (workToken.IsCancellationRequested)
                    return;

                publicationQueued = dispatcher.TryEnqueue(() =>
                {
                    bool snapshotChanged =
                        !snapshotIdentity.TryGetTarget(out LayoutSnapshot? callbackSnapshot) ||
                        callbackSnapshot is null ||
                        callbackSnapshot.IsRetiringOrDisposed ||
                        !ReferenceEquals(_snapshot, callbackSnapshot);
                    bool providerCurrent = false;
                    if (CodeBlockHighlightOperationLifetime.IsCurrentAfterProviderCallback(
                            CodeHighlighter,
                            highlighter,
                            _codeBlockHighlightGeneration,
                            generation,
                            _isDisposed,
                            _isUnloaded,
                            token))
                    {
                        try
                        {
                            int observedRevision = highlighter.Revision;
                            providerCurrent = CodeBlockHighlightOperationLifetime.IsCurrentAfterRevisionCallback(
                                CodeHighlighter,
                                highlighter,
                                _codeBlockHighlightGeneration,
                                generation,
                                _isDisposed,
                                _isUnloaded,
                                token,
                                observedRevision,
                                key.ProviderRevision);
                        }
                        catch (Exception ex)
                        {
                            MarkdownDiagnostics.WriteLine(
                                $"[MarkdownRendererControl] Code highlighter revision failed: {ex.Message}");
                        }
                    }

                    CodeBlockHighlightCompletionAction completion =
                        CodeBlockHighlightPublicationFence.DecideCompletion(
                            snapshotChanged,
                            _isUnloaded,
                            workToken.IsCancellationRequested,
                            sceneSession?.IsDisposed == true,
                            providerCurrent);
                    bool retryAfterSnapshotBusy = false;
                    try
                    {
                        if (completion != CodeBlockHighlightCompletionAction.Publish)
                            return;
                        _codeBlockHighlightCache.Set(key, result);
                        if (_codeBlockHighlightCache.TryGetValue(key, out _))
                        {
                            // The visible-band scheduler applies the retained
                            // result to matching blocks without a document scan.
                            ScheduleVisibleCodeBlockHighlighting();
                        }
                        else if (callbackSnapshot is not null &&
                            ApplyUncachedCodeBlockHighlightResult(
                                callbackSnapshot, key, result,
                                out retryAfterSnapshotBusy))
                        {
                            // Oversized results cannot enter the bounded cache.
                            // Publish to the current band directly, without
                            // immediately requeuing the same expensive request.
                            InvalidateCanvas();
                        }
                    }
                    finally
                    {
                        RemoveCodeBlockHighlightInFlight(key, generation);
                        if (completion == CodeBlockHighlightCompletionAction.RetryCurrentSnapshot ||
                            retryAfterSnapshotBusy)
                            ScheduleVisibleCodeBlockHighlighting();
                    }
                });
            }
            finally
            {
                _codeBlockHighlightSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The completion key is released by the shared finally path.
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block highlighting failed: {ex.Message}");
        }
        finally
        {
            // Cancellation may occur after admission but before publication.
            // A stale key must not suppress a future visible-band request.
            if (!publicationQueued)
                dispatcher.TryEnqueue(() => RemoveCodeBlockHighlightInFlight(key, generation));
        }
    }

    private void RemoveCodeBlockHighlightInFlight(CodeBlockHighlightCacheKey key, int generation)
    {
        if (generation == _codeBlockHighlightGeneration)
            _codeBlockHighlightInFlight.Remove(key);
    }

    private bool ApplyUncachedCodeBlockHighlightResult(
        LayoutSnapshot snapshot,
        CodeBlockHighlightCacheKey key,
        CodeBlockHighlightResult result,
        out bool snapshotBusy)
    {
        snapshotBusy = false;
        if (!snapshot.TryBeginInteraction())
        {
            snapshotBusy = true;
            return false;
        }

        try
        {
            IReadOnlyList<BlockBox> inspectedBlocks;
            IEnumerable<Layout.Boxes.CodeBlockBox> candidates;
            LazyLayoutBand band = default;
            if (PerformanceSession is null)
            {
                inspectedBlocks = snapshot.GetMeasuredTopLevelBlocks();
                candidates = EnumerateCodeBlocks(inspectedBlocks);
            }
            else
            {
                TryGetViewport(out double viewportTop, out double viewportHeight, out _);
                band = LazyLayoutBand.FromDirectionalViewport(
                    viewportTop,
                    viewportHeight,
                    PerformanceSession.Options.LookAheadViewports,
                    _lazyScrollVelocityPixelsPerSecond);
                if (!snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                        band.Top, band.Bottom, out inspectedBlocks))
                {
                    // The snapshot may still be measuring this band or waiting
                    // for deferred native scenes. Keep the oversized result
                    // retryable after the in-flight key is released.
                    snapshotBusy = true;
                    return false;
                }

                candidates = EnumerateCodeBlocks(inspectedBlocks);
            }

            bool changed = false;
            foreach (var candidate in candidates)
            {
                if (PerformanceSession is not null &&
                    !IsCodeBlockInHighlightBand(snapshot, candidate, band.Top, band.Bottom))
                    continue;
                if (CreateHighlightCacheKey(candidate, key.ThemeVariant, key.ProviderIdentity, key.ProviderRevision).Equals(key))
                    changed |= candidate.ApplySyntaxHighlighting(key, result.Spans);
            }

            return changed;
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static bool IsCodeBlockInHighlightBand(
        LayoutSnapshot snapshot,
        Layout.Boxes.CodeBlockBox block,
        double top,
        double bottom)
    {
        Rect bounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(block.BlockIndex, block.Bounds);
        return bounds.Bottom >= top && bounds.Top <= bottom;
    }

    internal static IEnumerable<Layout.Boxes.CodeBlockBox> EnumerateCodeBlocks(
        IReadOnlyList<BlockBox> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var codeBlock in EnumerateCodeBlocks(block))
                yield return codeBlock;
        }
    }

    private static IEnumerable<Layout.Boxes.CodeBlockBox> EnumerateCodeBlocks(BlockBox block)
    {
        switch (block)
        {
            case Layout.Boxes.CodeBlockBox codeBlock:
                yield return codeBlock;
                break;
            case Layout.Boxes.ListItemBox listItem:
                foreach (var item in EnumerateCodeBlocks(listItem.Marker))
                    yield return item;
                foreach (var item in EnumerateCodeBlocks(listItem.Content))
                    yield return item;
                break;
            case Layout.Boxes.StackBox stack:
                foreach (var child in stack.Children)
                {
                    foreach (var item in EnumerateCodeBlocks(child))
                        yield return item;
                }
                break;
        }
    }

    private CodeBlockHighlightCacheKey CreateHighlightCacheKey(
        Layout.Boxes.CodeBlockBox block,
        CodeBlockThemeVariant variant,
        int providerIdentity,
        int providerRevision)
        => new(block.CodeLanguage, block.Metadata.CodeTextHash, block.CodeText.Length, variant, providerIdentity, providerRevision);

    private void ClearCodeBlockHighlightCache()
        => _codeBlockHighlightCache.Clear();

    private static CodeBlockThemeVariant ResolveCodeBlockThemeVariant(Theming.ThemeSnapshot theme)
    {
        if (theme.IsHighContrast)
            return CodeBlockThemeVariant.HighContrast;

        var bg = theme.SurfaceColor;
        double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance < 0.5 ? CodeBlockThemeVariant.Dark : CodeBlockThemeVariant.Light;
    }

    private static ulong ComputeDisclosureFingerprint(
        IReadOnlyDictionary<string, bool> disclosureStates)
    {
        if (disclosureStates.Count == 0)
            return 0;

        const ulong prime = 1099511628211UL;
        var keys = new List<string>(disclosureStates.Keys);
        keys.Sort(StringComparer.Ordinal);
        ulong hash = 14695981039346656037UL;
        foreach (string key in keys)
        {
            hash ^= Fnv1A64(key);
            hash *= prime;
            hash ^= disclosureStates[key] ? 1UL : 0UL;
            hash *= prime;
        }

        return hash;
    }

    private static ulong Fnv1A64(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }

    private void ApplySnapshotSize(Size committedSize)
    {
        if (_canvas is null || _root is null || _overlay is null) return;
        _canvas.Width = Math.Max(1, committedSize.Width);
        // Round the height up to whole device pixels so layout rounding never
        // clips the document's last partial pixel row. Width stays
        // as committed: rounding it up could add a horizontal scroll range.
        _canvas.Height = Math.Max(1, SelectionController.SnapExtentUpToPixelGrid(
            committedSize.Height,
            XamlRoot?.RasterizationScale ?? 1.0));
        _root.Width = _canvas.Width;
        _root.Height = _canvas.Height;
        _overlay.Width = _canvas.Width;
        _overlay.Height = _canvas.Height;
        SetSelectionDragShieldActive(_selectionAnchor is not null);
    }

    private static (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(LayoutSnapshot snapshot, double verticalOffset)
    {
        if (verticalOffset <= 0 ||
            !snapshot.TryCaptureScrollAnchor(verticalOffset, out SnapshotScrollAnchor anchor) ||
            anchor.BlockIndex is not { } blockIndex)
            return null;
        return (blockIndex, anchor.OffsetFromTop);
    }

    private static double? GetScrollAnchorOffset(LayoutSnapshot snapshot, (int BlockIndex, double OffsetFromTop)? anchor)
    {
        if (anchor is not { } value)
            return null;

        foreach (var b in snapshot.Blocks)
        {
            if (b.BlockIndex != value.BlockIndex)
                continue;

            return snapshot.TryGetLogicalTopForBlockIndex(value.BlockIndex, out double logicalTop)
                ? Math.Max(0, logicalTop - value.OffsetFromTop)
                : null;
        }
        return null;
    }

    private void QueueLazyLayoutForViewport(bool forceCapacityRetry = false)
    {
        var snapshot = _snapshot;
        if (RequestGeometryRecoveryIfRequired(snapshot) || snapshot is null)
            return;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand visibleVectorBand = LazyLayoutBand.FromVisibleViewport(
            viewportTop, viewportHeight);
        LazyLayoutBand band;
        if (PerformanceSession is { } performanceSession)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastLazyViewportTimestamp > 0)
            {
                double elapsedSeconds = (now - _lastLazyViewportTimestamp) /
                    (double)Stopwatch.Frequency;
                if (elapsedSeconds is > 0.002 and < 1)
                {
                    _lazyScrollVelocityPixelsPerSecond =
                        (viewportTop - _lastLazyViewportTop) / elapsedSeconds;
                }
            }
            _lastLazyViewportTop = viewportTop;
            _lastLazyViewportTimestamp = now;
            band = LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond);
        }
        else
        {
            band = LazyLayoutBand.FromViewport(
                viewportTop, viewportHeight, LazyLayoutOverscanPx);
        }

        // Directional look-ahead and image realization can be broader than the
        // viewport. Keep vector admission tied to the actual viewport rather
        // than letting an unchanged prefetch union absorb a changed viewport.
        band = IncludeBand(band, visibleVectorBand);

        bool capacityAdmissionEnabled = IsVisibleFirstVectorAdmissionEnabled;
        bool capacityDeferred = capacityAdmissionEnabled && snapshot.HasCapacityDeferredScenes;
        if (!snapshot.IsLazyLayoutEnabled && !capacityDeferred)
            return;

        if (capacityAdmissionEnabled)
        {
            SynchronizeVectorCapacityObserver(snapshot);
            forceCapacityRetry |= ConsumePendingVectorCapacityWake(snapshot);
        }

        bool capacityRetryDue = capacityDeferred && IsVectorCapacityRetryDue(
            snapshot, visibleVectorBand, forceCapacityRetry);
        if (capacityDeferred && !capacityRetryDue &&
            snapshot.IsRealizationBandMeasured(band))
        {
            return;
        }

        if (snapshot.IsBandWorkSettled(band, visibleVectorBand) && !capacityRetryDue)
            return;

        QueueLazyLayoutForBand(
            snapshot,
            band,
            visibleVectorBand,
            preserveScrollAnchor: _scroll is not null);
    }

    private void QueueLazyLayoutForPaintRegion(Rect region)
    {
        var snapshot = _snapshot;
        if (RequestGeometryRecoveryIfRequired(snapshot) || snapshot is null)
            return;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand visibleVectorBand = LazyLayoutBand.FromVisibleViewport(
            viewportTop, viewportHeight);

        var band = PerformanceSession is { } performanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                region.Top,
                region.Height,
                performanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond)
            : LazyLayoutBand.FromViewport(region.Top, region.Height, LazyLayoutOverscanPx);
        band = IncludeBand(band, visibleVectorBand);
        bool capacityAdmissionEnabled = IsVisibleFirstVectorAdmissionEnabled;
        bool capacityDeferred = capacityAdmissionEnabled && snapshot.HasCapacityDeferredScenes;
        if (!snapshot.IsLazyLayoutEnabled && !capacityDeferred)
            return;
        bool forceCapacityRetry = false;
        if (capacityAdmissionEnabled)
        {
            SynchronizeVectorCapacityObserver(snapshot);
            bool wake = ConsumePendingVectorCapacityWake(snapshot);
            if (capacityDeferred && wake)
                forceCapacityRetry = true;
            if (capacityDeferred && !wake &&
                !IsVectorCapacityRetryDue(snapshot, visibleVectorBand, forceCapacityRetry) &&
                snapshot.IsRealizationBandMeasured(band))
            {
                return;
            }
        }
        if (snapshot.IsBandWorkSettled(band, visibleVectorBand) &&
            !(capacityDeferred && IsVectorCapacityRetryDue(snapshot, visibleVectorBand, forceCapacityRetry)))
            return;

        QueueLazyLayoutForBand(
            snapshot,
            band,
            visibleVectorBand,
            preserveScrollAnchor: _scroll is not null);
    }

    private void QueueLazyLayoutForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor)
    {
        if (_isDisposed || _isUnloaded || !ReferenceEquals(snapshot, _snapshot))
            return;

        if (RequestGeometryRecoveryIfRequired(snapshot))
            return;

        if (_lazyLayoutCts is { IsCancellationRequested: false } &&
            ReferenceEquals(snapshot, _activeLazyLayoutSnapshot) &&
            // Same-band work from an older image-reflow revision must not
            // absorb the retry needed to refresh scene indexes/graph bindings.
            _activeLazyLayoutRevision == snapshot.LayoutRevision &&
            (!IsVisibleFirstVectorAdmissionEnabled ||
             VectorNativeResourceAdmission.Shared.CapacityGeneration <=
                _activeLazyLayoutCapacityGeneration) &&
            band.Top >= _activeLazyLayoutBand.Top &&
            band.Bottom <= _activeLazyLayoutBand.Bottom &&
            (!IsVisibleFirstVectorAdmissionEnabled ||
             visibleVectorBand.Top == _activeLazyLayoutVisibleVectorBand.Top &&
             visibleVectorBand.Bottom == _activeLazyLayoutVisibleVectorBand.Bottom) &&
            (!preserveScrollAnchor ||
             (_scroll is not null &&
              _activeLazyLayoutAnchorOffset is { } activeOffset &&
              Math.Abs(_scroll.VerticalOffset - activeOffset) < 0.5)))
        {
            return;
        }

        if (_lazyLayoutCts is { } activeCancellation)
            _ = CancellationTokenSourceRetirement.RequestCancellation(activeCancellation);

        if (_hasPendingLazyLayoutBand && ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot))
        {
            _pendingLazyLayoutBand = new LazyLayoutBand(
                Math.Min(_pendingLazyLayoutBand.Top, band.Top),
                Math.Max(_pendingLazyLayoutBand.Bottom, band.Bottom));
            // Layout/image overscan is unioned; visible vector demand is the
            // latest viewport, never a stale union of previously visible bands.
            _pendingLazyLayoutVisibleVectorBand = visibleVectorBand;
            _pendingLazyLayoutPreserveScrollAnchor |= preserveScrollAnchor;
        }
        else
        {
            _pendingLazyLayoutSnapshot = snapshot;
            _pendingLazyLayoutBand = band;
            _pendingLazyLayoutVisibleVectorBand = visibleVectorBand;
            _pendingLazyLayoutPreserveScrollAnchor = preserveScrollAnchor;
            _hasPendingLazyLayoutBand = true;
        }

        if (_lazyLayoutDispatchQueued)
            return;

        // Lazy work has two UI handoffs: request admission and result
        // publication. Start the timer before either so rejected or accepted-
        // but-withheld dispatcher callbacks still have a bounded recovery path.
        StartImageRelayoutRecovery();
        if (!HasImageRelayoutRecoveryClock)
        {
            // There is no dispatcher timer left to own a rejected or withheld
            // lazy handoff. Drop this request epoch and rebuild instead of
            // advertising permanently pending work that cannot be recovered.
            CancelLazyLayoutRealization();
            RequestRebuild(RebuildReason.Full);
            return;
        }

        _lazyLayoutDispatchQueued = true;
        long dispatchTicket = ++_lastLazyLayoutDispatchTicket;
        if (dispatchTicket == 0)
            dispatchTicket = ++_lastLazyLayoutDispatchTicket;
        var dispatcher = DispatcherQueue;
        var receiver = new WeakReference<MarkdownRendererControl>(this);
        try
        {
            if (dispatcher is null || !dispatcher.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (receiver.TryGetTarget(out var control))
                            control.ProcessQueuedLazyLayoutRealization(dispatchTicket);
                    }))
            {
                _lazyLayoutDispatchQueued = false;
            }
        }
        catch (Exception exception)
        {
            _lazyLayoutDispatchQueued = false;
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] lazy layout request dispatch failed: {exception.GetType().Name} " +
                $"(0x{exception.HResult:X8}).");
        }
    }

    private void ProcessQueuedLazyLayoutRealization(long dispatchTicket)
    {
        if (dispatchTicket != _lastLazyLayoutDispatchTicket || !_hasPendingLazyLayoutBand)
            return;

        _lazyLayoutDispatchQueued = false;
        if (_isDisposed || _isUnloaded || !_hasPendingLazyLayoutBand)
        {
            _pendingLazyLayoutSnapshot = null;
            _hasPendingLazyLayoutBand = false;
            _pendingLazyLayoutPreserveScrollAnchor = false;
            StopImageRelayoutRecoveryIfIdle();
            return;
        }

        LayoutSnapshot? snapshot = _pendingLazyLayoutSnapshot;
        LazyLayoutBand band = _pendingLazyLayoutBand;
        LazyLayoutBand visibleVectorBand = _pendingLazyLayoutVisibleVectorBand;
        bool preserveScrollAnchor = _pendingLazyLayoutPreserveScrollAnchor;
        _pendingLazyLayoutSnapshot = null;
        _hasPendingLazyLayoutBand = false;
        _pendingLazyLayoutPreserveScrollAnchor = false;

        if (snapshot is null || !ReferenceEquals(snapshot, _snapshot))
            return;

        var oldCts = _lazyLayoutCts;
        var oldTask = _lazyLayoutTask;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _pipelineCts?.Token ?? CancellationToken.None);
        long realizationGeneration = ++_lazyLayoutGeneration;
        _lazyLayoutPublicationHandoff.Reset(realizationGeneration);
        long pipelineGeneration = _pipelineGeneration;
        CancellationToken operationToken = cts.Token;
        double? anchorOffset = preserveScrollAnchor && _scroll is not null
            ? _scroll.VerticalOffset
            : null;
        var dispatcher = DispatcherQueue;

        _lazyLayoutCts = cts;
        _activeLazyLayoutSnapshot = snapshot;
        _activeLazyLayoutRevision = snapshot.LayoutRevision;
        _activeLazyLayoutBand = band;
        _activeLazyLayoutVisibleVectorBand = visibleVectorBand;
        // The active ticket is current through every refund already visible
        // when dispatch starts. A later refund invalidates this work and makes
        // the capacity wake eligible to schedule a fresh attempt.
        _activeLazyLayoutCapacityGeneration = IsVisibleFirstVectorAdmissionEnabled
            ? VectorNativeResourceAdmission.Shared.CapacityGeneration
            : snapshot.ActiveCapacityGeneration;
        _activeLazyLayoutAnchorOffset = anchorOffset;
        Task operationTask = RunLazyLayoutRealizationAsync(
            snapshot,
            band,
            visibleVectorBand,
            anchorOffset,
            pipelineGeneration,
            realizationGeneration,
            operationToken,
            dispatcher);
        _lazyLayoutTask = operationTask;
        _ = operationTask.ContinueWith(
            static (_, state) =>
            {
                var (receiver, generation) =
                    ((WeakReference<MarkdownRendererControl> Receiver, long Generation))state!;
                if (receiver.TryGetTarget(out var control))
                    control.MarkLazyLayoutWorkDrained(generation);
            },
            (new WeakReference<MarkdownRendererControl>(this), realizationGeneration),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        RetireCancellationTokenSource(oldCts, oldTask);
    }

    private async Task RunLazyLayoutRealizationAsync(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        CancellationToken operationToken,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
    {
        LazyLayoutWorkResult result = default;
        Exception? failure = null;
        bool canceled = false;
        IMarkdownPerformanceSessionInternal? session =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
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
                        this, snapshot, visibleVectorBand, operationToken).ConfigureAwait(false))
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
            var publication = CreateLazyLayoutCompletionPublication(
                snapshot,
                new WeakReference<MarkdownRendererControl>(this),
                result,
                failure,
                anchorOffset,
                pipelineGeneration,
                realizationGeneration,
                canceled,
                operationToken);

            if (_lazyLayoutPublicationHandoff.TryStage(realizationGeneration, publication) &&
                dispatcher is not null)
            {
                try
                {
                    dispatcher.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        CreateLazyLayoutCompletionDispatchCallback(
                            new WeakReference<MarkdownRendererControl>(this),
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
        finally
        {
            // The task continuation marks the operation drained only after this
            // method completes. Clear graph-bearing locals before that signal.
            layoutWork = null;
            snapshot = null!;
        }
    }

    private static WeakSnapshotPublication<LayoutSnapshot> CreateLazyLayoutCompletionPublication(
        LayoutSnapshot snapshot,
        WeakReference<MarkdownRendererControl> receiver,
        LazyLayoutWorkResult result,
        Exception? failure,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        bool canceled,
        CancellationToken operationToken)
        => new(snapshot, publishedSnapshot =>
        {
            if (receiver.TryGetTarget(out var control))
            {
                control.CompleteLazyLayoutRealization(
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

    private static Microsoft.UI.Dispatching.DispatcherQueueHandler
        CreateLazyLayoutCompletionDispatchCallback(
            WeakReference<MarkdownRendererControl> receiver,
            long realizationGeneration)
        => () =>
        {
            if (receiver.TryGetTarget(out var control))
                control.TryPublishLazyLayoutCompletion(realizationGeneration);
        };

    private void MarkLazyLayoutWorkDrained(long realizationGeneration)
        => _lazyLayoutPublicationHandoff.MarkWorkDrained(realizationGeneration);

    private void TryPublishLazyLayoutCompletion(long realizationGeneration)
        => _lazyLayoutPublicationHandoff.TryPublishAfterWorkDrains(realizationGeneration);

    private void CompleteLazyLayoutRealization(
        LayoutSnapshot? snapshot,
        LazyLayoutWorkResult result,
        Exception? failure,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        bool canceled,
        CancellationToken operationToken)
    {
        // A canceled or superseded worker can still have mutated the currently
        // published tree. Recover that identity before dropping its result.
        if (RequestGeometryRecoveryIfRequired(snapshot))
        {
            if (snapshot is not null)
                OnDeferredGestureLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        if (realizationGeneration != _lazyLayoutGeneration)
        {
            if (snapshot is not null)
                OnDeferredGestureLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        var completedCts = _lazyLayoutCts;
        var completedTask = _lazyLayoutTask;
        LazyLayoutBand attemptedVisibleBand = _activeLazyLayoutVisibleVectorBand;
        long attemptedCapacityGeneration = snapshot?.ActiveCapacityGeneration ??
            _activeLazyLayoutCapacityGeneration;
        // Retirement requests cancellation as part of disposal. Capture the
        // operation's outcome first so a successful completion isn't mistaken
        // for superseded work merely because this method is now retiring its CTS.
        bool operationWasCanceled = canceled || operationToken.IsCancellationRequested;
        _lazyLayoutCts = null;
        _lazyLayoutTask = null;
        _activeLazyLayoutSnapshot = null;
        _activeLazyLayoutRevision = -1;
        _activeLazyLayoutAnchorOffset = null;
        RetireCancellationTokenSource(completedCts, completedTask);

        // A current canceled or obsolete operation still owns its CTS/task
        // fields. Retire those before dropping its result, or the timer will
        // keep observing a completed but permanently active operation.
        if (_isDisposed ||
            _isUnloaded ||
            operationWasCanceled ||
            pipelineGeneration != _pipelineGeneration ||
            snapshot is null ||
            !ReferenceEquals(snapshot, _snapshot))
        {
            StopImageRelayoutRecoveryIfIdle();
            if (snapshot is not null)
                OnDeferredGestureLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        // Record only a completed attempt. Canceled/superseded workers must not
        // consume the one retry associated with a capacity generation.
        _lastVectorCapacityAttemptVisibleBand = attemptedVisibleBand;
        _lastVectorCapacityAttemptGeneration = attemptedCapacityGeneration;
        _hasVectorCapacityAttempt = true;

        if (failure is not null)
        {
            NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
            if (GraphicsDeviceErrors.IsDeviceLost(failure))
            {
                HandleCanvasDeviceLost(failure);
            }
            else
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] lazy layout realization failed: {failure}");
                RequestRebuild(RebuildReason.Full);
            }
            StopImageRelayoutRecoveryIfIdle();
            OnDeferredGestureLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        SynchronizeVectorCapacityObserver(snapshot);

        if (result.Commit.LayoutRevision <= _appliedLazyLayoutRevision)
        {
            // Moving the active band can finish visible readiness without a
            // geometry revision or resource change (for example, two already
            // measured text-only viewports). Publish that status transition
            // even when there is no new drawing lease to repaint.
            NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
            // This band is now measured and its native resources are
            // settled, but no layout commit follows to re-run the deferred
            // scene probe. A Mermaid or Math fallback that scrolled into this
            // band while it was being prepared would otherwise never be
            // scheduled and keep showing its source as "Loading".
            ScheduleVisibleDeferredScenes();
            if (result.Commit.NativeSceneResourcesChanged)
            {
                // A prepared/retired drawing lease changes only the raster
                // surface. Repaint without rebuilding layout or disturbing
                // selection, focus, source mapping, or scroll position.
                InvalidateAutomationLayout();
                InvalidateCanvas();
            }

            // A focus request can arrive while an already-measured band is
            // publishing its completion. Re-evaluate it even when that worker
            // produced no newer layout revision.
            _ = ResumePendingLazyFocus(snapshot);
            TryResumePendingScrollToBlock(snapshot, pipelineGeneration);
            OnDeferredGestureLayoutCompletion(snapshot, succeeded: true);
            StopImageRelayoutRecoveryIfIdle();
            return;
        }

        // A user can jump to an already-measured band while this worker is
        // finishing. That jump need not queue replacement work, so the old
        // completion still arrives. Never restore its captured scroll anchor
        // over a newer viewport position.
        bool restoreAnchor = LazyLayoutScrollAnchorPolicy.ShouldRestore(
            anchorOffset, _scroll?.VerticalOffset, _scrollViewIntermediate);
        ApplyLazyLayoutCommit(snapshot, restoreAnchor ? result.ScrollAnchor : null);
        SynchronizeVectorCapacityObserver(snapshot);
        if (result.Commit.NativeSceneResourcesChanged)
            NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
        OnDeferredGestureLayoutCompletion(snapshot, succeeded: true);
        StopImageRelayoutRecoveryIfIdle();
    }

    private void CancelLazyLayoutRealization()
    {
        // Superseding work is not snapshot replacement. Preserve an already
        // committed geometry publication if the replacement subsequently fails.
        long generation = ++_lazyLayoutGeneration;
        _lazyLayoutPublicationHandoff.Reset(generation);
        ++_lastLazyLayoutDispatchTicket;
        _lazyLayoutDispatchQueued = false;
        _pendingLazyLayoutSnapshot = null;
        _activeLazyLayoutSnapshot = null;
        _activeLazyLayoutRevision = -1;
        _pendingLazyLayoutVisibleVectorBand = default;
        _activeLazyLayoutAnchorOffset = null;
        _hasPendingLazyLayoutBand = false;
        _pendingLazyLayoutPreserveScrollAnchor = false;
        _pendingLazyFocus = null;

        var oldCts = _lazyLayoutCts;
        var oldTask = _lazyLayoutTask;
        _lazyLayoutCts = null;
        _lazyLayoutTask = null;
        RetireCancellationTokenSource(oldCts, oldTask);
        StopImageRelayoutRecoveryIfIdle();
    }

    private void ApplyLazyLayoutCommit(
        LayoutSnapshot snapshot,
        (int BlockIndex, double OffsetFromTop)? scrollAnchor)
    {
        if (_isDisposed || _isUnloaded || !ReferenceEquals(snapshot, _snapshot))
            return;
        if (RequestGeometryRecoveryIfRequired(snapshot))
            return;
        if (_publishingLayoutCommit)
        {
            DeferLayoutCommit(scrollAnchor);
            return;
        }
        _publishingLayoutCommit = true;
        try
        {
            _pendingLayoutCommit = null;
            if (!RebuildRealizationPlans(snapshot, preserveRealized: true, out Size size,
                    out double? anchorOffset, scrollAnchor))
            {
                DeferLayoutCommit(scrollAnchor);
                return;
            }
            if (_isDisposed || _isUnloaded || !ReferenceEquals(snapshot, _snapshot))
                return;
            ApplySnapshotSize(size);
            if (anchorOffset is { } target && _scroll is { } scroll &&
                Math.Abs(target - scroll.VerticalOffset) >= 0.5)
                scroll.ChangeView(null, target, null, disableAnimation: true);
            RealizeVisibleEmbeds();
            ScheduleVisibleCodeBlockHighlighting();
            if (!ResumePendingLazyFocus(snapshot))
                UpdateFocusRing();
            RefreshInteractiveTextAdornerAfterLayoutChange();
            InvalidateAutomationLayout();
            InvalidateCanvas();
        }
        finally
        {
            _publishingLayoutCommit = false;
        }

        TryResumePendingScrollToBlock(snapshot, Volatile.Read(ref _snapshotGeneration));
        if (!_isDisposed && !_isUnloaded &&
            ReferenceEquals(snapshot, _snapshot) &&
            _pendingLayoutCommit is null &&
            _appliedLazyLayoutRevision >= snapshot.LayoutRevision)
        {
            // Release logical input waiters only after plans/anchor are applied,
            // not when a worker first updates the snapshot's measured mask.
            OnDeferredGestureLayoutCompletion(snapshot, succeeded: true);
        }
    }

    private void DeferLayoutCommit((int BlockIndex, double OffsetFromTop)? scrollAnchor)
    {
        if (_isDisposed || _isUnloaded || _snapshot is null)
            return;
        // Retain only value metadata, never a retired graph. Keep old plans and
        // native children until one coherent geometry read is admitted. The
        // existing weak recovery timer also covers a withheld UI dispatch.
        _pendingLayoutCommit = (_snapshotGeneration, scrollAnchor, _scroll?.VerticalOffset);
        ++_lastLayoutCommitTicket;
        StartImageRelayoutRecovery();
        if (!HasImageRelayoutRecoveryClock)
            RequestRebuild(RebuildReason.Full);
    }

    private void PublishPendingLayoutCommit()
    {
        if (_pendingLayoutCommit is not { } pending)
            return;
        if (pending.Generation != _snapshotGeneration || _snapshot is not { } snapshot)
        {
            _pendingLayoutCommit = null;
            return;
        }
        bool restoreAnchor = LazyLayoutScrollAnchorPolicy.ShouldRestore(
            pending.ScrollOffset, _scroll?.VerticalOffset, _scrollViewIntermediate);
        ApplyLazyLayoutCommit(snapshot, restoreAnchor ? pending.Anchor : null);
    }

    private long InvalidateHostedFocusPlanPublication()
    {
        long epoch = unchecked(Volatile.Read(ref _hostedFocusPlanEpoch) + 1);
        Volatile.Write(ref _hostedFocusPlanEpoch, epoch);
        Volatile.Write(ref _publishedHostedFocusPlanEpoch, -1);
        Volatile.Write(ref _publishedHostedFocusPlanGeneration, -1);
        Volatile.Write(ref _publishedHostedFocusPlanRevision, -1);
        return epoch;
    }

    private void DerealizeAllEmbeds()
    {
        if (_derealizingAllEmbeds)
            return;

        InvalidateHostedFocusPlanPublication();
        _derealizingAllEmbeds = true;
        _realizationPassEpoch++;
        _realizeVisibleEmbedsRequested = false;
        _realizeVisibleEmbedsContinuationQueued = false;
        _realizeVisibleEmbedsContinuationTicket++;
        // Native callbacks can synchronously unload or replace the document.
        // Keep this teardown's ownership detached from the mutable plan lists;
        // nested cleanup is rejected above and each plan retires its element
        // before invoking callback-capable recycling code.
        EmbedPlan[] embedPlans = Array.Empty<EmbedPlan>();
        CodeBlockActionPlan[] actionPlans = Array.Empty<CodeBlockActionPlan>();
        try
        {
            embedPlans = _embedPlans.ToArray();
            actionPlans = _codeBlockActionPlans.ToArray();
            // Previously pooled buttons may still be collapsed children of the
            // current overlay. The caller clears that overlay after this pass,
            // so discard those attached-state entries before pooling plans.
            _codeBlockCopyButtonPool.Clear();
            foreach (EmbedPlan plan in embedPlans)
                plan.Derealize(this);
            foreach (CodeBlockActionPlan plan in actionPlans)
            {
                if (plan.Realized is not null) plan.Derealize(this);
            }
        }
        finally
        {
            _derealizingAllEmbeds = false;
            _activeEmbedOrdinals.Clear();
            _activeCodeBlockActionOrdinals.Clear();
        }
    }

    private bool RebuildRealizationPlans(LayoutSnapshot snapshot, bool preserveRealized,
        out Size size, out double? anchorOffset,
        (int BlockIndex, double OffsetFromTop)? scrollAnchor = null)
    {
        size = default;
        anchorOffset = null;
        long planGeneration = _snapshotGeneration;
        if (!snapshot.TryBeginInteraction())
            return false;
        long hostedFocusPlanEpoch = -1;
        long hostedFocusPlanRevision = -1;
        long focusRequestVersion = _pendingFocusableItemsRefresh.RequestVersion;
        IReadOnlyList<Layout.FocusableItem> collectedFocusableItems =
            Array.Empty<Layout.FocusableItem>();
        ImagePlanPublication? imagePlanPublication = null;
        EmbedPlan[] oldPlans = Array.Empty<EmbedPlan>();
        CodeBlockActionPlan[] oldActionPlans = Array.Empty<CodeBlockActionPlan>();
        bool plansBuilt = false;
        Exception? focusPlanCaptureFailure = null;
        try
        {
            try
            {
                // Collect before mutating realization plans. Dynamic overflow
                // eligibility is read only while this exact snapshot lease is
                // held, and a failed capture leaves the previous focus list and
                // plan state intact.
                bool focusPlanCaptured;
                try
                {
                    focusPlanCaptured = snapshot.TryCollectFocusableItemsUnderReadLease(
                        out collectedFocusableItems);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A custom overflow getter failed before any realization
                    // plan is touched. Let the lease unwind before reporting
                    // it or clearing the matching pending ticket.
                    focusPlanCaptureFailure = exception;
                    focusPlanCaptured = false;
                }
                if (focusPlanCaptureFailure is null)
                {
                    if (!focusPlanCaptured)
                        return false;

                    hostedFocusPlanRevision = snapshot.LayoutRevision;
                    if (!IsCurrentFocusablePlanAttempt(
                            snapshot,
                            planGeneration,
                            hostedFocusPlanRevision,
                            focusRequestVersion))
                    {
                        return false;
                    }

                    hostedFocusPlanEpoch = InvalidateHostedFocusPlanPublication();
                    oldPlans = preserveRealized ? _embedPlans.ToArray() : Array.Empty<EmbedPlan>();
                    oldActionPlans = preserveRealized ? _codeBlockActionPlans.ToArray() : Array.Empty<CodeBlockActionPlan>();
                    UnsubscribeAllImages();
                    _imagePlans.Clear();
                    _imagePlanIndex = null;
                    _embedRects.Clear();
                    _blockEmbedRects.Clear();
                    _codeBlockActionRects.Clear();
                    _embedPlans.Clear();
                    _embedPlanIndex = null;
                    _activeEmbedOrdinals.Clear();
                    _codeBlockActionPlans.Clear();
                    _hasMeasuredCodeBlocks = false;
                    _codeBlockActionPlanIndex = null;
                    _activeCodeBlockActionOrdinals.Clear();

                    LayoutSnapshot.LayoutPlanCandidateEnumerator planCandidates =
                        snapshot.GetMeasuredLayoutPlanCandidatesUnderPaint();
                    int candidateRootOrdinal = -1;
                    BlockBox? candidateRoot = null;
                    bool candidateRootIsMeasured = false;
                    while (planCandidates.MoveNext())
                    {
                        LayoutPlanCandidate candidate = planCandidates.Current;
                        if (candidate.TopLevelOrdinal != candidateRootOrdinal)
                        {
                            candidateRootOrdinal = candidate.TopLevelOrdinal;
                            candidateRootIsMeasured = snapshot.TryGetMeasuredBlockByOrdinalUnderPaint(
                                candidateRootOrdinal,
                                out BlockBox measuredRoot);
                            candidateRoot = candidateRootIsMeasured ? measuredRoot : null;
                        }

                        if (candidateRootIsMeasured)
                        {
                            using LayoutPlacementTransformContext.Scope placementScope =
                                snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(candidateRoot!);
                            CollectEmbedPlans(snapshot, candidateRoot!, candidate.Box);
                        }
                    }

                    _embedPlanIndex = BuildPlanIndex(_embedPlans);
                    _codeBlockActionPlanIndex = BuildPlanIndex(_codeBlockActionPlans);

                    // Membership and indexed bounds remain immutable until the next
                    // plan publication. The ImageBox itself is mutable and borrowed.
                    if (_imagePlans.Count > 0)
                    {
                        _imagePlanIndex = new ViewportBandIndex(_imagePlans.Count);
                        var imageBounds = new Rect[_imagePlans.Count];
                        for (int index = 0; index < _imagePlans.Count; index++)
                        {
                            Rect bounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                                _imagePlans[index].BlockIndex, _imagePlans[index].Bounds);
                            imageBounds[index] = bounds;
                            _imagePlanIndex.SetEntry(index, index, bounds.Top, bounds.Bottom);
                        }
                        _imagePlanIndex.Commit();
                        imagePlanPublication = new ImagePlanPublication(
                            snapshot,
                            planGeneration,
                            hostedFocusPlanRevision,
                            _imagePlans.ToArray(),
                            imageBounds,
                            _imagePlanIndex);
                    }
                    size = snapshot.Size;
                    anchorOffset = GetScrollAnchorOffset(snapshot, scrollAnchor);
                    _appliedLazyLayoutRevision = hostedFocusPlanRevision;
                    imagePlanPublication ??= new ImagePlanPublication(
                        snapshot,
                        planGeneration,
                        _appliedLazyLayoutRevision,
                        Array.Empty<Layout.Boxes.ImageBox>(),
                        Array.Empty<Rect>(),
                        null);
                }
            }
            finally
            {
                snapshot.EndInteraction();
            }

            if (focusPlanCaptureFailure is not null)
            {
                ClearFocusableItemsRefresh(focusRequestVersion);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(focusPlanCaptureFailure).Throw();
            }
            if (!IsCurrentRealizationSnapshot(snapshot, planGeneration) ||
                hostedFocusPlanEpoch != Volatile.Read(ref _hostedFocusPlanEpoch) ||
                snapshot.LayoutRevision != hostedFocusPlanRevision)
                return false;
            if (imagePlanPublication is null)
                return false;

            Volatile.Write(ref _imagePlanPublication, imagePlanPublication);

            // Native child transfer, factories, residency policies and XAML
            // notifications can reenter the control. Do not invoke them under the
            // geometry lease; every plan rectangle above is already copied.
            PrepareImageResidency(snapshot);

            if (oldPlans.Length > 0)
            {
                // Only plans owned by the same logical box can be the same
                // logical embed. Index realized plans once: a long document can
                // otherwise spend O(n²) UI-thread work after every lazy-layout or
                // image-size publication.
                var oldByOwner = new Dictionary<object, List<EmbedPlan>>(
                    ReferenceEqualityComparer.Instance);
                foreach (EmbedPlan oldPlan in oldPlans)
                {
                    if (oldPlan.Realized is null)
                        continue;
                    if (!oldByOwner.TryGetValue(oldPlan.LogicalOwner, out var candidates))
                        oldByOwner.Add(oldPlan.LogicalOwner, candidates = []);
                    candidates.Add(oldPlan);
                }

                for (int ordinal = 0; ordinal < _embedPlans.Count; ordinal++)
                {
                    if (!IsCurrentRealizationSnapshot(snapshot, planGeneration) ||
                        hostedFocusPlanEpoch != Volatile.Read(ref _hostedFocusPlanEpoch) ||
                        snapshot.LayoutRevision != hostedFocusPlanRevision)
                        return false;
                    EmbedPlan newPlan = _embedPlans[ordinal];
                    if (!oldByOwner.TryGetValue(newPlan.LogicalOwner, out var candidates))
                        continue;
                    for (int index = 0; index < candidates.Count; index++)
                    {
                        EmbedPlan oldPlan = candidates[index];
                        if (!newPlan.IsSameLogicalEmbed(oldPlan))
                            continue;

                        if (newPlan is DeclarativeHostedElementPlan newDeclarative &&
                            oldPlan is DeclarativeHostedElementPlan oldDeclarative)
                        {
                            newDeclarative.AdoptRealizedFrom(oldDeclarative);
                        }
                        else
                        {
                            newPlan.Realized = oldPlan.Realized;
                            oldPlan.Realized = null;
                            AttachRealizedElement(newPlan);
                        }
                        newPlan.UpdatePlacement();
                        if (!IsCurrentRealizationSnapshot(snapshot, planGeneration) ||
                            hostedFocusPlanEpoch != Volatile.Read(ref _hostedFocusPlanEpoch) ||
                            snapshot.LayoutRevision != hostedFocusPlanRevision)
                            return false;
                        candidates.RemoveAt(index);
                        break;
                    }
                }
            }

            if (oldActionPlans.Length > 0)
            {
                var oldByBox = new Dictionary<Layout.Boxes.CodeBlockBox, List<CodeBlockActionPlan>>(
                    ReferenceEqualityComparer.Instance);
                foreach (CodeBlockActionPlan oldPlan in oldActionPlans)
                {
                    if (oldPlan.Realized is null)
                        continue;
                    if (!oldByBox.TryGetValue(oldPlan.Box, out var candidates))
                        oldByBox.Add(oldPlan.Box, candidates = []);
                    candidates.Add(oldPlan);
                }

                for (int ordinal = 0; ordinal < _codeBlockActionPlans.Count; ordinal++)
                {
                    if (!IsCurrentRealizationSnapshot(snapshot, planGeneration) ||
                        hostedFocusPlanEpoch != Volatile.Read(ref _hostedFocusPlanEpoch) ||
                        snapshot.LayoutRevision != hostedFocusPlanRevision)
                        return false;
                    CodeBlockActionPlan newPlan = _codeBlockActionPlans[ordinal];
                    if (!oldByBox.TryGetValue(newPlan.Box, out var candidates))
                        continue;
                    for (int index = 0; index < candidates.Count; index++)
                    {
                        CodeBlockActionPlan oldPlan = candidates[index];
                        if (!newPlan.IsSameLogicalAction(oldPlan))
                            continue;

                        newPlan.AdoptRealizedFrom(oldPlan, this);
                        if (!IsCurrentRealizationSnapshot(snapshot, planGeneration) ||
                            hostedFocusPlanEpoch != Volatile.Read(ref _hostedFocusPlanEpoch) ||
                            snapshot.LayoutRevision != hostedFocusPlanRevision)
                            return false;
                        candidates.RemoveAt(index);
                        break;
                    }
                }
            }

            for (int index = 0; index < _embedPlans.Count; index++)
            {
                if (_embedPlans[index].HasActiveWork)
                    _activeEmbedOrdinals.Add(index);
            }
            for (int index = 0; index < _codeBlockActionPlans.Count; index++)
            {
                if (_codeBlockActionPlans[index].Realized is not null)
                    _activeCodeBlockActionOrdinals.Add(index);
            }

            _lastFiredRealizedCount = -1;
            plansBuilt = true;
        }
        finally
        {
            // Even reentrant unload/replacement or failed geometry collection
            // must retire the old plans not transferred to the current set.
            // These detached arrays are not invalidated by native callbacks.
            foreach (EmbedPlan oldPlan in oldPlans)
                oldPlan.Derealize(this);
            foreach (CodeBlockActionPlan oldPlan in oldActionPlans)
            {
                if (oldPlan.Realized is not null)
                    oldPlan.Derealize(this);
            }
        }

        // Retiring detached plans is callback-capable. Publish the focus index
        // only after that cleanup completes and the complete plan attempt still
        // owns the same snapshot, generation, layout revision, and epoch.
        if (!plansBuilt ||
            !IsCurrentRealizationSnapshot(snapshot, planGeneration) ||
            hostedFocusPlanEpoch != Volatile.Read(ref _hostedFocusPlanEpoch) ||
            snapshot.LayoutRevision != hostedFocusPlanRevision ||
            !IsCurrentFocusablePlanAttempt(
                snapshot,
                planGeneration,
                hostedFocusPlanRevision,
                focusRequestVersion))
        {
            return false;
        }

        // Host, native-resource, and retirement callbacks above may navigate
        // focus while realization plans are being published. Resolve the
        // currently focused value identity only now, then atomically replace
        // the value-only list/index after every snapshot and ticket fence holds.
        Layout.FocusableItem? focusedIdentity = GetCurrentFocusableIdentity();
        _focusableItems = collectedFocusableItems;
        _focusedItemIndex = focusedIdentity is { } identity
            ? FindFocusableItemIndex(collectedFocusableItems, identity)
            : -1;
        Volatile.Write(ref _publishedHostedFocusPlanGeneration, planGeneration);
        Volatile.Write(ref _publishedHostedFocusPlanRevision, hostedFocusPlanRevision);
        Volatile.Write(ref _publishedHostedFocusPlanEpoch, hostedFocusPlanEpoch);
        CompleteFocusableItemsRefresh(
            snapshot,
            planGeneration,
            hostedFocusPlanRevision,
            focusRequestVersion);
        return true;
    }

    private bool IsCurrentRealizationSnapshot(LayoutSnapshot snapshot, long generation) =>
        !_isDisposed && !_isUnloaded && generation == _snapshotGeneration &&
        ReferenceEquals(snapshot, _snapshot);

    private bool IsCurrentFocusablePlanAttempt(
        LayoutSnapshot snapshot,
        long generation,
        long revision,
        long requestVersion)
    {
        if (!IsCurrentRealizationSnapshot(snapshot, generation) ||
            snapshot.LayoutRevision != revision)
        {
            return false;
        }

        var pending = _pendingFocusableItemsRefresh;
        return pending.RequestVersion == requestVersion &&
            (!pending.IsPending ||
             (pending.SnapshotGeneration == generation &&
              pending.AutomationDocumentGeneration == snapshot.AutomationDocumentGeneration &&
              pending.LayoutRevision == revision));
    }

    private static ViewportBandIndex? BuildPlanIndex(List<EmbedPlan> plans)
    {
        if (plans.Count == 0)
            return null;
        var index = new ViewportBandIndex(plans.Count);
        for (int ordinal = 0; ordinal < plans.Count; ordinal++)
        {
            Rect rect = plans[ordinal].Rect;
            index.SetEntry(ordinal, ordinal, rect.Top, rect.Bottom);
        }
        index.Commit();
        return index;
    }

    private static ViewportBandIndex? BuildPlanIndex(List<CodeBlockActionPlan> plans)
    {
        if (plans.Count == 0)
            return null;
        var index = new ViewportBandIndex(plans.Count);
        for (int ordinal = 0; ordinal < plans.Count; ordinal++)
        {
            Rect rect = plans[ordinal].Rect;
            index.SetEntry(ordinal, ordinal, rect.Top, rect.Bottom);
        }
        index.Commit();
        return index;
    }

    private static void AddActiveOrdinal(List<int> ordinals, int ordinal)
    {
        int position = ordinals.BinarySearch(ordinal);
        if (position < 0)
            ordinals.Insert(~position, ordinal);
    }

    private static void AttachRealizedElement(EmbedPlan plan)
    {
        switch (plan)
        {
            case BlockEmbedPlan block:
                block.Box.RealizedElement = block.Realized;
                break;
            case InlineEmbedPlan inline:
                inline.Run.RealizedElement = inline.Realized;
                break;
            case DeclarativeHostedElementPlan declarative:
                declarative.Box.RealizedElement = declarative.Realized;
                break;
        }
    }

    /// <summary>
    /// Realise embeds whose rect intersects the realisation band (viewport +
    /// overscan) and derealise embeds that have left the wider derealisation
    /// band. Called from snapshot commit, scroll, and resize.
    /// </summary>
    internal void RealizeVisibleEmbeds()
    {
        if (_isDisposed || _isUnloaded || _derealizingAllEmbeds)
            return;

        // Plan factories and native child teardown can synchronously raise
        // layout/scroll events. Coalesce those nested requests instead of
        // allowing them to mutate the active ordinal lists being traversed.
        if (_realizingVisibleEmbeds)
        {
            _realizeVisibleEmbedsRequested = true;
            return;
        }

        _realizingVisibleEmbeds = true;
        bool queueContinuation = false;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                _realizeVisibleEmbedsRequested = false;
                if (_isDisposed || _isUnloaded || _derealizingAllEmbeds)
                    break;

                RealizeVisibleEmbedsCore();
                if (!_realizeVisibleEmbedsRequested)
                    break;
            }
            queueContinuation = _realizeVisibleEmbedsRequested &&
                !_isDisposed && !_isUnloaded && !_derealizingAllEmbeds;
        }
        finally
        {
            _realizingVisibleEmbeds = false;
        }

        if (queueContinuation)
            QueueVisibleRealizationContinuation();
    }

    private void QueueVisibleRealizationContinuation()
    {
        if (_realizeVisibleEmbedsContinuationQueued ||
            _isDisposed || _isUnloaded || _derealizingAllEmbeds)
            return;

        var dispatcher = DispatcherQueue;
        if (dispatcher is null)
            return;

        long ticket = ++_realizeVisibleEmbedsContinuationTicket;
        if (ticket == 0)
            ticket = ++_realizeVisibleEmbedsContinuationTicket;
        var receiver = new WeakReference<MarkdownRendererControl>(this);
        try
        {
            if (dispatcher.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (receiver.TryGetTarget(out var control))
                            control.ProcessQueuedVisibleRealizationContinuation(ticket);
                    }))
            {
                _realizeVisibleEmbedsContinuationQueued = true;
            }
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] visible realization continuation dispatch failed: {exception.GetType().Name} " +
                $"(0x{exception.HResult:X8}).");
        }
    }

    private void ProcessQueuedVisibleRealizationContinuation(long ticket)
    {
        if (ticket != _realizeVisibleEmbedsContinuationTicket)
            return;

        _realizeVisibleEmbedsContinuationQueued = false;
        if (_isDisposed || _isUnloaded || _derealizingAllEmbeds)
        {
            _realizeVisibleEmbedsRequested = false;
            return;
        }

        RealizeVisibleEmbeds();
    }

    private void RealizeVisibleEmbedsCore()
    {
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null)
            return;

        long snapshotGeneration = _snapshotGeneration;
        long realizationPassEpoch = _realizationPassEpoch;
        ViewportBandIndex? imageIndex = _imagePlanIndex;
        ViewportBandIndex? embedIndex = _embedPlanIndex;
        ViewportBandIndex? actionIndex = _codeBlockActionPlanIndex;
        if (!IsCurrentVisibleRealizationPass(
                snapshot, snapshotGeneration, realizationPassEpoch,
                imageIndex, embedIndex, actionIndex))
            return;

        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;

        // Lazy image loading: trigger EnsureLoading for images within the
        // viewport + LazyImageOverscanPx band.  Images already started (cached
        // or previously triggered) are silently skipped by EnsureLoading().
        LazyLayoutBand imageBand = PerformanceSession is { } performanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                top,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond)
            : LazyLayoutBand.FromViewport(top, viewportHeight, LazyImageOverscanPx);
        double imgLoadTop = imageBand.Top;
        double imgLoadBottom = imageBand.Bottom;
        ViewportRange imageRange = imageIndex?.Find(imgLoadTop, imgLoadBottom) ?? default;
        ReconcileImageResidency(imageBand, imageRange);
        if (!IsCurrentVisibleRealizationPass(
                snapshot, snapshotGeneration, realizationPassEpoch,
                imageIndex, embedIndex, actionIndex))
            return;

        for (int index = imageRange.Start; index < imageRange.End; index++)
        {
            int imageOrdinal = imageIndex!.GetBlockOrdinal(index);
            (double imageTop, double imageBottom) = imageIndex.GetBounds(index);
            var img = _imagePlans[imageOrdinal];
            if (imageBottom >= imgLoadTop && imageTop <= imgLoadBottom)
            {
                bool intersectsViewport = imageBottom >= top && imageTop <= bottom;
                if (!IsCurrentVisibleRealizationPass(
                        snapshot, snapshotGeneration, realizationPassEpoch,
                        imageIndex, embedIndex, actionIndex))
                    return;

                // Paint culls offscreen images before ImageBox can observe the
                // viewport. Keep current residency visibility separate from the
                // monotonic load-demand ticket, including images in overscan.
                object? svgObservation = img.CaptureSvgViewportObservationToken();
                img.ObserveSvgViewportVisibility(svgObservation, intersectsViewport);
                img.EnsureLoading(isVisible: intersectsViewport);
                if (!IsCurrentVisibleRealizationPass(
                        snapshot, snapshotGeneration, realizationPassEpoch,
                        imageIndex, embedIndex, actionIndex))
                    return;
            }
        }

        // Drop old realisation-side caches; they'll be repopulated from realised plans.
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();

        ViewportRange actionRange = actionIndex?.Find(top, bottom) ?? default;
        for (int index = actionRange.Start; index < actionRange.End; index++)
        {
            int ordinal = actionIndex!.GetBlockOrdinal(index);
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            double actionTop = plan.Rect.Top;
            double actionBottom = plan.Rect.Bottom;
            // Copy buttons are XAML controls, so retaining them in an overscan
            // band creates off-screen automation peers and warm-scroll churn.
            // Realize only actions that intersect the effective viewport; the
            // detached bounded pool makes boundary crossings inexpensive.
            bool inRealize = actionBottom >= top && actionTop <= bottom;
            if (inRealize)
            {
                plan.Realize(this);
                if (!IsCurrentVisibleRealizationPass(
                        snapshot, snapshotGeneration, realizationPassEpoch,
                        imageIndex, embedIndex, actionIndex))
                    return;
                if (plan.Realized is not null)
                    AddActiveOrdinal(_activeCodeBlockActionOrdinals, ordinal);
            }
        }

        for (int index = _activeCodeBlockActionOrdinals.Count - 1; index >= 0; index--)
        {
            int ordinal = _activeCodeBlockActionOrdinals[index];
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            if (plan.Rect.Bottom >= top && plan.Rect.Top <= bottom)
                continue;
            plan.Derealize(this);
            if (!IsCurrentVisibleRealizationPass(
                    snapshot, snapshotGeneration, realizationPassEpoch,
                    imageIndex, embedIndex, actionIndex))
                return;
            _activeCodeBlockActionOrdinals.RemoveAt(index);
        }

        foreach (int ordinal in _activeCodeBlockActionOrdinals)
        {
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            if (plan.Realized is not null)
            {
                double left = Math.Round(plan.Rect.X);
                double t = Math.Round(plan.Rect.Y);
                double w = Math.Round(plan.Rect.X + plan.Rect.Width) - left;
                double h = Math.Round(plan.Rect.Y + plan.Rect.Height) - t;
                _codeBlockActionRects.Add((plan.Box, new Rect(left, t, w, h), plan.Kind));
            }
        }

        if (_embedPlans.Count == 0)
        {
            // No embeds: still emit a transition-to-zero event so subscribers
            // that mirror the count (e.g. UI-automation) don't go stale after
            // a rebuild that dropped all embeds.
            if (_lastFiredRealizedCount != 0)
            {
                _lastFiredRealizedCount = 0;
                try { EmbedsRealizationChanged?.Invoke(this, EventArgs.Empty); }
                catch { /* swallow */ }
            }
            return;
        }

        ViewportRange embedRange = embedIndex?.Find(
            top - EmbedVirtualizationOverscanPx,
            bottom + EmbedVirtualizationOverscanPx) ?? default;
        for (int index = embedRange.Start; index < embedRange.End; index++)
        {
            int ordinal = embedIndex!.GetBlockOrdinal(index);
            EmbedPlan plan = _embedPlans[ordinal];
            double pTop = plan.Rect.Top;
            double pBottom = plan.Rect.Bottom;
            bool inRealize = EmbedVisibility.IsInRealizeBand(pTop, pBottom, top, bottom, EmbedVirtualizationOverscanPx);
            if (inRealize)
            {
                plan.Realize(this);
                if (!IsCurrentVisibleRealizationPass(
                        snapshot, snapshotGeneration, realizationPassEpoch,
                        imageIndex, embedIndex, actionIndex))
                    return;
                if (plan.HasActiveWork)
                    AddActiveOrdinal(_activeEmbedOrdinals, ordinal);
            }
        }

        for (int index = _activeEmbedOrdinals.Count - 1; index >= 0; index--)
        {
            int ordinal = _activeEmbedOrdinals[index];
            EmbedPlan plan = _embedPlans[ordinal];
            if (EmbedVisibility.IsInDerealizeBand(
                plan.Rect.Top, plan.Rect.Bottom, top, bottom,
                EmbedVirtualizationDerealizeOverscanPx))
                continue;
            plan.Derealize(this);
            if (!IsCurrentVisibleRealizationPass(
                    snapshot, snapshotGeneration, realizationPassEpoch,
                    imageIndex, embedIndex, actionIndex))
                return;
            _activeEmbedOrdinals.RemoveAt(index);
        }

        foreach (int ordinal in _activeEmbedOrdinals)
        {
            EmbedPlan plan = _embedPlans[ordinal];
            // Pending declarative factories occupy the active band but do not
            // contribute a hit-test rectangle or a realized-element count.
            if (plan.Realized is null)
                continue;

            // Rebuild hit-rect caches from realised plans in document order.
            if (plan is BlockEmbedPlan bp)
            {
                Rect rect = bp.Rect;
                double left = Math.Round(rect.X);
                double t = Math.Round(rect.Y);
                double w = Math.Round(rect.Width);
                double h = Math.Round(rect.Height);
                _blockEmbedRects.Add((bp.Box, new Rect(left, t, w, h)));
            }
            else if (plan is InlineEmbedPlan ip)
            {
                double iLeft = Math.Round(ip.Rect.X);
                double iTop  = Math.Round(ip.Rect.Y);
                double iW    = Math.Round(ip.Rect.X + ip.Rect.Width)  - iLeft;
                double iH    = Math.Round(ip.Rect.Y + ip.Rect.Height) - iTop;
                _embedRects.Add((ip.Icb, ip.Run, new Rect(iLeft, iTop, iW, iH)));
            }
            else if (plan is DeclarativeHostedElementPlan declarative)
            {
                _blockEmbedRects.Add((declarative.Box, declarative.Rect));
            }
        }

        // Surface the realised count to subscribers (e.g. sample app exposing
        // it via a hidden TextBlock for UI-automation tests) — but only when
        // the count actually changed. Firing on every scroll tick causes a
        // property-change event storm for any UIA listener bound to a Name
        // property mirror in the subscriber's UI.
        int realised = 0;
        foreach (int ordinal in _activeEmbedOrdinals)
        {
            if (_embedPlans[ordinal].Realized is not null)
                realised++;
        }
        if (realised != _lastFiredRealizedCount)
        {
            _lastFiredRealizedCount = realised;
            InvalidateAutomationLayout();
            if (!IsCurrentVisibleRealizationPass(
                    snapshot, snapshotGeneration, realizationPassEpoch,
                    imageIndex, embedIndex, actionIndex))
                return;
            try { EmbedsRealizationChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* subscriber threw — swallow to keep scroll pipeline alive. */ }
        }
    }

    private bool IsCurrentVisibleRealizationPass(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long realizationPassEpoch,
        ViewportBandIndex? imageIndex,
        ViewportBandIndex? embedIndex,
        ViewportBandIndex? actionIndex) =>
        !_isDisposed && !_isUnloaded && !_derealizingAllEmbeds &&
        snapshotGeneration == _snapshotGeneration &&
        realizationPassEpoch == _realizationPassEpoch &&
        ReferenceEquals(snapshot, _snapshot) &&
        ReferenceEquals(imageIndex, _imagePlanIndex) &&
        ReferenceEquals(embedIndex, _embedPlanIndex) &&
        ReferenceEquals(actionIndex, _codeBlockActionPlanIndex);

    private void GetCodeBlockActionRealizationCounts(out int realizedCount, out int offscreenCount)
    {
        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        realizedCount = 0;
        offscreenCount = 0;
        foreach (int ordinal in _activeCodeBlockActionOrdinals)
        {
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            if (plan.Realized is null)
                continue;
            realizedCount++;
            if (plan.Rect.Bottom < top || plan.Rect.Top > bottom)
                offscreenCount++;
        }
    }

    private MarkdownHostedElementRequest CreateHostedElementRequest(
        Layout.Boxes.DeclarativeHostedElementBox box,
        Rect layoutBounds)
    {
        TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        return new MarkdownHostedElementRequest(
            box.FactoryKey,
            box.SourceRange,
            layoutBounds,
            new Rect(0, viewportTop, Math.Max(0, viewportWidth), Math.Max(0, viewportHeight)),
            box.Attributes,
            box.AccessibilityRole,
            box.AccessibilityName,
            box.AccessibilityDescription,
            box.SemanticText,
            box.AutomationId);
    }

    private bool CanAttachHostedElement(
        DeclarativeHostedElementPlan plan,
        long pipelineGeneration)
    {
        if (_isUnloaded ||
            pipelineGeneration != _pipelineGeneration ||
            _overlay is null ||
            !_embedPlans.Contains(plan))
            return false;

        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        return EmbedVisibility.IsInRealizeBand(
            plan.Rect.Top,
            plan.Rect.Bottom,
            top,
            bottom,
            EmbedVirtualizationOverscanPx);
    }

    private bool CanActivateHostedElementFallback(
        DeclarativeHostedElementPlan plan,
        long pipelineGeneration)
        => !_isUnloaded &&
           pipelineGeneration == _pipelineGeneration &&
           _embedPlans.Contains(plan);

    private void ActivateHostedElementFallback(DeclarativeHostedElementPlan plan)
    {
        if (!_hostedElementFallbacks.Add(plan.Box.FallbackKey))
            return;

        MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] using native markdown fallback for hosted element '{plan.Box.FactoryKey}'.");
        RequestRebuild(RebuildReason.Restyle);
    }

    private static void ApplyHostedElementAutomationProperties(
        Layout.Boxes.DeclarativeHostedElementBox box,
        FrameworkElement element)
    {
        // The semantic wrapper owns the logical identity across virtualization.
        // Give its realized native child a stable, distinct id when the host
        // did not provide one (or copied the logical request id verbatim).
        string nativeAutomationId = AutomationProperties.GetAutomationId(element);
        if (string.IsNullOrWhiteSpace(nativeAutomationId) ||
            string.Equals(nativeAutomationId, box.AutomationId, StringComparison.Ordinal))
        {
            AutomationProperties.SetAutomationId(element, $"{box.AutomationId}-Native");
        }

        // Host-owned names and descriptions remain authoritative when supplied.
        if (!string.IsNullOrWhiteSpace(box.AccessibilityName) &&
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
        {
            AutomationProperties.SetName(element, box.AccessibilityName);
        }

        if (!string.IsNullOrWhiteSpace(box.AccessibilityDescription) &&
            string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)))
        {
            AutomationProperties.SetHelpText(element, box.AccessibilityDescription);
        }
    }

    private void RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan)
    {
        if (FocusState == FocusState.Unfocused ||
            plan.Realized is null ||
            plan.FocusIdentity is not { } planIdentity ||
            !_embedPlans.Contains(plan) ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return;
        }

        var focusedItem = items[_focusedItemIndex];
        if (SameFocusableIdentity(focusedItem, planIdentity))
        {
            _ = TryFocusHostedElement(focusedItem, reverse: false);
        }
    }

    private void CollectEmbedPlans(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        Layout.BlockBox box)
    {
        switch (box)
        {
            case Layout.Boxes.EmbedBox eb:
            {
                double left = eb.Bounds.X + eb.Margin.Left;
                double top  = eb.Bounds.Y + eb.Margin.Top;
                double w = eb.Bounds.Width  - eb.Margin.Left - eb.Margin.Right;
                double h = eb.Bounds.Height - eb.Margin.Top  - eb.Margin.Bottom;
                Rect physicalBounds = new(left, top, w, h);
                _embedPlans.Add(new BlockEmbedPlan
                {
                    Box = eb,
                    FocusIdentity = new Layout.FocusableItem(
                        eb.BlockIndex, 0, Layout.FocusableItemKind.BlockEmbed),
                    Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelRoot, physicalBounds),
                });
                break;
            }
            case Layout.Boxes.DeclarativeHostedElementBox hosted when HostedElementFactory is { } factory:
            {
                double left = hosted.Bounds.X + hosted.Margin.Left;
                double top = hosted.Bounds.Y + hosted.Margin.Top;
                double width = hosted.Bounds.Width - hosted.Margin.Left - hosted.Margin.Right;
                double height = hosted.Bounds.Height - hosted.Margin.Top - hosted.Margin.Bottom;
                _embedPlans.Add(new DeclarativeHostedElementPlan
                {
                    Box = hosted,
                    Factory = factory,
                    FocusIdentity = new Layout.FocusableItem(
                        hosted.BlockIndex, 0, Layout.FocusableItemKind.DeclarativeHostedElement),
                    Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                        topLevelRoot, new Rect(left, top, Math.Max(0, width), Math.Max(0, height))),
                });
                break;
            }
            case Layout.Boxes.ImageBox ib:
            {
                AddImagePlan(ib);
                break;
            }
            case Layout.Boxes.InlineContainerBox icb:
            {
                // Most paragraphs contain only text. Avoid walking their runs
                // and querying DirectWrite geometry when no overlay/image
                // plan can possibly be produced.
                if (!icb.HasInlineImages && !icb.HasInlineEmbeds)
                    break;

                // Register completion before asking DirectWrite for rectangles. During the
                // first commit an inline image can be measurable but not yet have a stable
                // character region; paint will start it and completion must still reflow.
                if (icb.HasInlineImages)
                {
                    foreach (var (imageRun, _) in icb.InlineImageRuns)
                    {
                        // Image registration must not depend on DirectWrite
                        // returning a character rectangle for the object
                        // replacement slot. In particular, a linked image on
                        // an otherwise empty line can temporarily have no
                        // region during the first layout pass. Keeping it out
                        // of _imagePlans in that state leaves it permanently
                        // "Loading" because no later viewport pass can start
                        // it. A missing first-pass rectangle has the default
                        // origin, which deliberately starts the bounded load;
                        // EnumerateInlineImageRects synchronizes the real
                        // bounds as soon as DirectWrite publishes them.
                        AddImagePlan(imageRun.Image);
                    }
                }
                if (icb.HasInlineEmbeds)
                {
                    foreach (var (run, rect) in icb.EnumerateEmbedRects())
                        _embedPlans.Add(new InlineEmbedPlan
                        {
                            Icb = icb,
                            Run = run,
                            FocusIdentity = new Layout.FocusableItem(
                                icb.BlockIndex, run.InlineIndex, Layout.FocusableItemKind.InlineEmbed),
                            Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelRoot, rect),
                        });
                }
                // Force enumeration so each ImageBox receives its document
                // bounds. Images were already added above, independently of
                // whether this geometry query succeeds.
                if (icb.HasInlineImages)
                {
                    foreach (var _ in icb.EnumerateInlineImageRects()) { }
                }
                break;
            }
            case Layout.Boxes.CodeBlockBox codeBlock:
            {
                _hasMeasuredCodeBlocks = true;
                if (codeBlock.IsCopyButtonEnabled && codeBlock.CopyButtonBounds.Width > 0 && codeBlock.CopyButtonBounds.Height > 0)
                    _codeBlockActionPlans.Add(new CodeBlockActionPlan
                    {
                        Box = codeBlock,
                        FocusIdentity = new Layout.FocusableItem(
                            codeBlock.BlockIndex, 0, Layout.FocusableItemKind.CodeBlockCopy),
                        Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                            topLevelRoot, codeBlock.CopyButtonBounds),
                        Kind = CodeBlockHostedElementKind.Copy,
                    });
                break;
            }
            case Layout.Boxes.ListItemBox lib:
                CollectEmbedPlans(snapshot, topLevelRoot, lib.Marker);
                CollectEmbedPlans(snapshot, topLevelRoot, lib.Content);
                break;
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) CollectEmbedPlans(snapshot, topLevelRoot, cell);
                break;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children) CollectEmbedPlans(snapshot, topLevelRoot, c);
                break;
        }
    }

    /// <summary>
    /// Records the completed images of the current layout so the next
    /// rebuild of the same document keeps their result. The ledger accumulates
    /// across consecutive rebuilds (a lazily rebuilt document lists only its
    /// measured images) and is discarded whenever the source text or image
    /// configuration changes. Runs on the UI thread, which owns image
    /// publication; the context receives an immutable copy.
    /// </summary>
    private IReadOnlyDictionary<string, Layout.Boxes.CompletedImageCarry>? CaptureCompletedImageCarry(
        Layout.Boxes.MarkdownImageCarryIdentity identity,
        bool carryFailures)
    {
        bool identityReset = _completedImageCarryIdentity is not { } previous || !previous.Matches(identity);
        if (identityReset)
            _completedImageCarry.Clear();
        _completedImageCarryIdentity = identity;

        foreach (Layout.Boxes.ImageBox image in _imagePlans)
        {
            if (image.Context.ImageCarryIdentity is not { } builtWith || !builtWith.Matches(identity))
                continue;
            if (!image.TryCaptureCompletedCarry(out Layout.Boxes.CompletedImageCarry? carry) || carry is null)
                continue;
            if (_completedImageCarry.Count >= MaximumCompletedImageCarryEntries &&
                !_completedImageCarry.ContainsKey(carry.Url))
            {
                break;
            }
            _completedImageCarry[carry.Url] = carry;
        }

        var snapshot = new Dictionary<string, Layout.Boxes.CompletedImageCarry>(StringComparer.Ordinal);
        foreach ((string url, Layout.Boxes.CompletedImageCarry carry) in _completedImageCarry)
        {
            if (carryFailures || !carry.Failed)
                snapshot[url] = carry;
        }

        return snapshot.Count == 0 ? null : snapshot;
    }

    private void AddImagePlan(Layout.Boxes.ImageBox image)
    {
        if (!RegisterImage(image))
            return;

        _imagePlans.Add(image);
    }

    private void QueueImageSourcePrefetch(
        Markdig.Syntax.MarkdownDocument document,
        string markdownSource,
        MarkdownExtensionRegistry registry,
        IMarkdownImageResolver? resolver,
        MarkdownImageResolveContext context)
    {
        if (resolver is not IMarkdownImagePrefetcher prefetcher ||
            _imageLifetimeCts is not { IsCancellationRequested: false } imageLifetime)
        {
            return;
        }

        if (ReferenceEquals(_activeImagePrefetcher, prefetcher) &&
            string.Equals(_prefetchedMarkdownSource, markdownSource, StringComparison.Ordinal) &&
            Equals(_activeImagePrefetchContext, context) &&
            _prefetchedImageRegistryRevision == registry.Revision)
        {
            return;
        }

        _activeImagePrefetcher = prefetcher;
        _prefetchedMarkdownSource = markdownSource;
        _activeImagePrefetchContext = context;
        _prefetchedImageRegistryRevision = registry.Revision;
        _pendingImagePrefetch = new PendingImagePrefetch(
            document,
            registry.SafeHtmlPolicy,
            prefetcher,
            context,
            imageLifetime.Token);
    }

    private static async Task PrefetchImageSourcesObservedAsync(
        Markdig.Syntax.MarkdownDocument document,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImagePrefetcher prefetcher,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> sources = MarkdownImagePrefetchSourceCollector.Collect(
                document,
                safeHtmlPolicy,
                cancellationToken);
            if (sources.Count > 0)
            {
                await prefetcher.PrefetchAsync(sources, context, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRenderer] image prefetch failed: {exception.Message}");
        }
    }

    private bool RegisterImage(Layout.Boxes.ImageBox image)
    {
        if (!_subscribedImages.Add(image))
            return false;

        image.LoadCompleted += OnImageLoadCompleted;
        // A fast background denial may precede first UI subscription.
        RequestBitmapOutputRecovery(image);
        return true;
    }

    private void UnsubscribeAllImages()
    {
        ResetBitmapOutputRecovery();
        foreach (var image in _subscribedImages)
        {
            image.LoadCompleted -= OnImageLoadCompleted;
        }

        _subscribedImages.Clear();
    }

    private void OnImageLoadCompleted(object? sender, Layout.Boxes.LoadCompletedEventArgs e)
    {
        // ImageBox publishes on UI already. Keep that path inline; only custom
        // off-thread senders need a second weak dispatcher handoff.
        var dq = DispatcherQueue;
        if (dq is null) return;
        bool layoutInvalidated = e?.LayoutInvalidated ?? true;
        bool bitmapOutputRecoverySignal = e?.IsBitmapOutputRecoverySignal ?? false;
        if (dq.HasThreadAccess)
        {
            ApplyImageLoadCompleted(sender as Layout.Boxes.ImageBox, layoutInvalidated, bitmapOutputRecoverySignal);
            return;
        }
        var receiver = new WeakReference<MarkdownRendererControl>(this);
        var image = sender is Layout.Boxes.ImageBox imageSender
            ? new WeakReference<Layout.Boxes.ImageBox>(imageSender)
            : null;
        bool enqueued = false;
        try
        {
            enqueued = dq.TryEnqueue(() =>
            {
                if (!receiver.TryGetTarget(out var control))
                    return;
                Layout.Boxes.ImageBox? completedImage = null;
                if (image is not null && !image.TryGetTarget(out completedImage))
                    return;
                control.ApplyImageLoadCompleted(completedImage, layoutInvalidated, bitmapOutputRecoverySignal);
            });
        }
        catch (Exception exception)
        {
            ReportImageRelayoutFailure("completion dispatch", exception.Message);
        }
        if (!enqueued && !_isDisposed && !_isUnloaded)
            RecordRejectedCrossThreadRebuildRequest(RebuildReason.Full);
    }

    private void ApplyImageLoadCompleted(Layout.Boxes.ImageBox? completedImage, bool layoutInvalidated,
        bool bitmapOutputRecoverySignal = false)
    {
        if (_isDisposed || _isUnloaded)
            return;
        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return;
        if (bitmapOutputRecoverySignal)
        {
            if (completedImage is not null)
                RequestBitmapOutputRecovery(completedImage);
            return;
        }
        if (completedImage is not null)
        {
            if (!_subscribedImages.Contains(completedImage))
                return;
            (FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer)?
                .NotifyImageStatusChanged(completedImage);
            // UIA notification can run host code. Recheck lifetime and membership
            // before using a snapshot-owned placement proof or queuing work.
            if (_isDisposed || _isUnloaded || !_subscribedImages.Contains(completedImage))
                return;
            if (layoutInvalidated && !_publishingLayoutCommit &&
                PerformanceSession is IMarkdownPerformanceSessionInternal session &&
                !session.IsDisposed && ReferenceEquals(session, completedImage.Context.PerformanceSession) &&
                _snapshot is { } snapshot &&
                snapshot.TryCanUsePaintOnlyImageCompletion(
                    completedImage, (float)Math.Max(50, ActualWidth)))
            {
                // Authored dimensions can make intrinsic publication irrelevant
                // to native layout. Keep the same UIA status and paint/selection
                // invalidation, without scheduling a no-op text reflow.
                layoutInvalidated = false;
            }
        }
        if (layoutInvalidated)
        {
            if (completedImage is not null)
                QueueImageRelayout(completedImage.BlockIndex);
            else
                RequestRebuild(RebuildReason.Full);
        }
        else
        {
            // Device/theme reparses are paint-only: rebuilding here would
            // dispose the newly parsed SVG and start the reparse loop again.
            InvalidateCanvas();
            InvalidateSelectionAdorner();
        }
    }

    private void QueueImageRelayout(int blockIndex)
    {
        if (_isDisposed || _isUnloaded || _snapshot is null)
            return;

        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return;

        // These indices belong to the still-published snapshot, not the
        // requested replacement. Only a successful snapshot commit clears them.
        _pendingImageRelayoutBlockIndices.Add(blockIndex);
        if (_imageRelayoutQueued || _imageRelayoutActive)
        {
            StartImageRelayoutRecovery();
            return;
        }

        _imageRelayoutQueued = true;
        long ticket = ++_lastImageRelayoutDispatchTicket;
        StartImageRelayoutRecovery();
        if (!HasImageRelayoutRecoveryClock)
        {
            // Do not start an asynchronous handoff without its recovery route.
            _imageRelayoutQueued = false;
            RequestRebuild(RebuildReason.Full);
            return;
        }
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
        var receiver = new WeakReference<MarkdownRendererControl>(this);
        try
        {
            // The timer retains the pending set on rejection and can also
            // claim accepted-but-withheld dispatch. Neither callback owns us.
            dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (receiver.TryGetTarget(out var control))
                        control.ProcessQueuedImageRelayout(ticket);
                });
        }
        catch (Exception exception)
        {
            ReportImageRelayoutFailure("relayout dispatch", exception.Message);
        }
    }

    private void StartImageRelayoutRecovery()
    {
        if (HasImageRelayoutRecoveryClock || DispatcherQueue is not { } dispatcher)
            return;
        try
        {
            var receiver = new WeakReference<MarkdownRendererControl>(this);
            var timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.IsRepeating = true;
            timer.Tick += (sender, _) =>
            {
                if (!receiver.TryGetTarget(out var control) ||
                    !ReferenceEquals(sender, control._imageRelayoutRecoveryTimer))
                {
                    try { sender.Stop(); }
                    catch (Exception) { /* A stopped dispatcher has no delivery to recover. */ }
                    return;
                }
                ProcessImageRelayoutRecovery(control);
            };
            _imageRelayoutRecoveryTimer = timer;
            timer.Start();
        }
        catch (Exception exception)
        {
            var failedTimer = _imageRelayoutRecoveryTimer;
            _imageRelayoutRecoveryTimer = null;
            try { failedTimer?.Stop(); }
            catch (Exception) { /* Its replacement clock owns recovery now. */ }
            ReportImageRelayoutFailure("relayout recovery", exception.Message);
            // A transient native timer failure must not clear one-shot overflow
            // signals or pending publication. This fallback remains weak and
            // has at most one queued UI tick, even if delivery is withheld.
            if (!_isDisposed && !_isUnloaded)
            {
                try
                {
                    _managedImageRelayoutRecoveryTimer = new(
                        this, callback => dispatcher.TryEnqueue(callback.Invoke),
                        ProcessImageRelayoutRecovery, TimeSpan.FromMilliseconds(100));
                }
                catch (Exception fallbackException)
                {
                    ReportImageRelayoutFailure("managed relayout recovery", fallbackException.Message);
                    // Preserve pending state; subsequent normal control work
                    // can retry clock creation. Resource exhaustion is not a
                    // reason to silently discard a deferred image demand.
                }
            }
        }
    }

    private static void ProcessImageRelayoutRecovery(MarkdownRendererControl control)
    {
        long layoutTicket = control._lastLayoutCommitTicket;
        try
        {
            if (!control._isDisposed && !control._isUnloaded)
            {
                if (control.RequestGeometryRecoveryIfRequired(control._snapshot))
                {
                    // Drain canceled image work so its retirement/active flag
                    // can complete, but do not probe or retry failed geometry.
                    control.ClearFocusableItemsRefreshForCurrentSnapshot();
                    control._imageRelayoutPayload?.PublishAfterWorkDrains();
                    return;
                }
                control.ProcessBitmapOutputRecovery();
                control.ProcessQueuedImageRelayout(control._lastImageRelayoutDispatchTicket);
                control._imageRelayoutPayload?.PublishAfterWorkDrains();
                control.PublishPendingLayoutCommit();
                control.ProcessQueuedLazyLayoutRealization(control._lastLazyLayoutDispatchTicket);
                control.TryPublishLazyLayoutCompletion(control._lazyLayoutGeneration);
                control.ProcessPendingHorizontalOverflowVisualRetry();
                control.ProcessPendingFocusGeometry();
                control.TryPublishPendingFocusableItems();
                if (control._imageRelayoutTask is { IsFaulted: true } failed &&
                    control._imageRelayoutCts is { } cancellation)
                {
                    ReportImageRelayoutFailure("relayout handoff", failed.Exception!);
                    control.CompleteImageRelayout(null, null, 0, cancellation, null);
                    if (!control.RequestGeometryRecoveryIfRequired(control._snapshot))
                        control.RequestRebuild(RebuildReason.Full);
                }
            }
        }
        catch (Exception exception)
        {
            // Reentrant newer publications retain their own recovery ticket.
            if (layoutTicket == control._lastLayoutCommitTicket)
                control._pendingLayoutCommit = null;
            ReportImageRelayoutFailure("layout publication", exception);
            if (!control.RequestGeometryRecoveryIfRequired(control._snapshot))
                control.RequestRebuild(RebuildReason.Full);
            else
                control.ClearFocusableItemsRefreshForCurrentSnapshot();
            if (control._snapshot is { } failedLayoutSnapshot)
                control.OnDeferredGestureLayoutCompletion(failedLayoutSnapshot, succeeded: false);
        }
        finally
        {
            if (control._isDisposed || control._isUnloaded)
                control.StopImageRelayoutRecovery(force: true);
            else
                control.StopImageRelayoutRecoveryIfIdle();
        }
    }

    private void StopImageRelayoutRecovery(bool force = false)
    {
        if (!force && (_bitmapOutputRecovery.IsPending || HasPendingLazyLayoutWork ||
            _hasPendingHorizontalOverflowRefresh ||
            _pendingFocusableItemsRefresh.IsPending ||
            _pendingSpatialFocusKeys.Count > 0 ||
            _pendingFocusGeometryRefresh ||
            _hasPendingFocusResumePoint))
            return;

        if (force)
        {
            ResetBitmapOutputRecovery();
            ResetPendingFocusableItemsRefresh();
        }
        var timer = _imageRelayoutRecoveryTimer;
        _imageRelayoutRecoveryTimer = null;
        Interlocked.Exchange(ref _managedImageRelayoutRecoveryTimer, null)?.Dispose();
        try { timer?.Stop(); }
        catch (Exception) { /* Dispatcher shutdown already ended UI delivery. */ }
    }

    private void StopImageRelayoutRecoveryIfIdle()
    {
        if (_imageRelayoutQueued ||
            _bitmapOutputRecovery.IsPending ||
            _imageRelayoutActive ||
            _pendingImageRelayoutBlockIndices.Count > 0 ||
            _hasPendingHorizontalOverflowRefresh ||
            _pendingFocusableItemsRefresh.IsPending ||
            _pendingSpatialFocusKeys.Count > 0 ||
            _pendingFocusGeometryRefresh ||
            _hasPendingFocusResumePoint ||
            HasPendingLazyLayoutWork)
        {
            return;
        }

        StopImageRelayoutRecovery();
    }

    private void ProcessQueuedImageRelayout(long ticket)
    {
        if (!_imageRelayoutQueued || ticket != _lastImageRelayoutDispatchTicket)
            return;

        LayoutSnapshot? snapshot = _snapshot;
        if (RequestGeometryRecoveryIfRequired(snapshot))
            return;
        if (_isDisposed || _isUnloaded || snapshot is null ||
            _pendingImageRelayoutBlockIndices.Count == 0)
        {
            _imageRelayoutQueued = false;
            if (_pendingLayoutCommit is null)
                StopImageRelayoutRecovery();
            return;
        }

        // Do not compete with a replacement build. If it fails or is canceled
        // without committing, the timer resumes work for the old visible graph.
        // Its prepublished task is the actual drain barrier; a withheld UI
        // completion must not leave the coordinator's building marker wedged.
        if (_pipelineTask is { IsCompleted: true })
        {
            OnRebuildPipelineCompleted(_activePipelineGeneration);
            // The worker may already have cleared that marker after enqueue
            // rejection. Retry its pending drain without creating a request.
            QueuePendingRebuild();
        }
        if (_rebuildCoordinator.HasPendingOrBuilding)
            return;

        // Keep the pending set and recovery route intact. Neither in-place
        // reflow nor private snapshot replacement may change geometry owned
        // by a live or not-yet-captured table/selection gesture.
        if (HasSnapshotPointerInteraction)
            return;

        _imageRelayoutQueued = false;
        CancellationTokenSource? cancellation = null;
        try
        {
            IMarkdownPerformanceSessionInternal? session =
                PerformanceSession as IMarkdownPerformanceSessionInternal;
            int[] changed = ImageRelayoutBatch.CapturePendingIndices(
                _pendingImageRelayoutBlockIndices,
                optedIn: session is not null);
            (int BlockIndex, double OffsetFromTop)? anchor = null;
            if (_scroll is { VerticalOffset: > 0 } scroll &&
                snapshot.TryCaptureScrollAnchor(scroll.VerticalOffset, out var captured) &&
                captured.BlockIndex is { } blockIndex)
            {
                anchor = (blockIndex, captured.OffsetFromTop);
            }

            // Rebuild start and unload explicitly cancel this operation. A
            // failed replacement's canceled CTS must not poison recovery of
            // the snapshot that remains published afterward.
            cancellation = new CancellationTokenSource();
            var payload = new ImageRelayoutWorkPayload<LayoutSnapshot>(
                snapshot, changed, (float)Math.Max(50, ActualWidth));
            var receiver = new WeakReference<MarkdownRendererControl>(this);

            _imageRelayoutActive = true;
            _imageRelayoutCts = cancellation;
            _imageRelayoutPayload = payload;
            RasterPlacementInputs? rasterPlacement = session is not null &&
                snapshot.TopLevelBlockCount is > 0 and <= LayoutSnapshot.MaximumPrivateRasterPlacementRoots &&
                _embedPlans.Count == 0 &&
                _imagePlans.Count is > 0 and <= InlineContainerBox.MaximumPrivateRasterPlacementImages
                ? new RasterPlacementInputs(receiver, _snapshotGeneration,
                    _imagePreparationOwnerToken, _imageLifetimeCts?.Token ?? CancellationToken.None,
                    _snapshotRetirements, _scroll?.VerticalOffset)
                : null;
            _imageRelayoutTask = RunImageRelayoutAsync(
                payload, anchor, _snapshotGeneration, cancellation, DispatcherQueue,
                receiver, session, rasterPlacement, snapshot.PointerInputEpoch,
                _scroll?.VerticalOffset);
            // The worker now owns only this immutable bounded index array.
            // Leave any storm remainder in the UI-owned pending set for the
            // normal completion/recovery route to schedule next.
            foreach (int completedBlockIndex in changed)
                _pendingImageRelayoutBlockIndices.Remove(completedBlockIndex);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _imageRelayoutPayload, null)?.DiscardPublication();
            RetireCancellationTokenSource(cancellation, _imageRelayoutTask);
            _imageRelayoutCts = null;
            _imageRelayoutTask = null;
            _imageRelayoutActive = false;
            StopImageRelayoutRecovery();
            ReportImageRelayoutFailure("relayout setup", exception);
            RequestRebuild(RebuildReason.Full);
        }
        return;
    }

    private void InvalidateAutomationLayout()
    {
        // Custom-drawn descendants do not receive XAML layout notifications of
        // their own. Refresh an existing document peer after image reflow so UIA
        // clients see the same bounds as the newly painted snapshot immediately.
        FrameworkElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    private void OnTaskCommandAvailabilityChanged()
    {
        if (_isDisposed || _isUnloaded || _taskCommandAvailabilityRefreshQueued)
            return;

        _taskCommandAvailabilityRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(RefreshTaskCommandAvailability))
            _taskCommandAvailabilityRefreshQueued = false;
    }

    private void RefreshTaskCommandAvailability()
    {
        _taskCommandAvailabilityRefreshQueued = false;
        if (_isDisposed || _isUnloaded || _snapshot is not { } snapshot)
            return;

        MarkFocusableItemsRefreshPending(snapshot, Volatile.Read(ref _snapshotGeneration));
        if (!TryPublishPendingFocusableItems())
            StartImageRelayoutRecovery();
    }

    private void MarkFocusableItemsRefreshPending(LayoutSnapshot snapshot, long generation)
    {
        long requestVersion = unchecked(_focusableItemsRefreshRequestVersion + 1);
        if (requestVersion <= 0)
            requestVersion = 1;
        _focusableItemsRefreshRequestVersion = requestVersion;
        _pendingFocusableItemsRefresh = (
            generation,
            snapshot.AutomationDocumentGeneration,
            snapshot.LayoutRevision,
            requestVersion,
            true);
    }

    private bool TryPublishPendingFocusableItems()
    {
        var request = _pendingFocusableItemsRefresh;
        if (!request.IsPending)
            return true;
        if (_isDisposed || _isUnloaded)
            return false;

        LayoutSnapshot? snapshot = _snapshot;
        long generation = Volatile.Read(ref _snapshotGeneration);
        if (snapshot is null)
        {
            ResetPendingFocusableItemsRefresh();
            return false;
        }

        long automationGeneration = snapshot.AutomationDocumentGeneration;
        if (request.SnapshotGeneration != generation ||
            request.AutomationDocumentGeneration != automationGeneration)
        {
            // A newer snapshot supersedes the value-only request. Never follow
            // an old ticket into its retired graph; the current generation gets
            // a fresh request instead.
            MarkFocusableItemsRefreshPending(snapshot, generation);
            request = _pendingFocusableItemsRefresh;
        }

        if (snapshot.IsRetiringOrDisposed)
        {
            ClearFocusableItemsRefresh(request.RequestVersion);
            return false;
        }
        if (snapshot.GeometryRecoveryRequired)
        {
            RequestGeometryRecoveryIfRequired(snapshot);
            ClearFocusableItemsRefresh(request.RequestVersion);
            return false;
        }

        long revisionBefore = snapshot.LayoutRevision;
        if (request.LayoutRevision != revisionBefore)
        {
            _pendingFocusableItemsRefresh = (
                generation,
                automationGeneration,
                revisionBefore,
                request.RequestVersion,
                true);
            request = _pendingFocusableItemsRefresh;
        }

        if (!snapshot.TryBeginInteraction())
            return false;

        IReadOnlyList<Layout.FocusableItem> collected = Array.Empty<Layout.FocusableItem>();
        long revisionUnderLease = -1;
        bool collectedSuccessfully = false;
        Exception? captureFailure = null;
        try
        {
            if (snapshot.LayoutRevision == revisionBefore &&
                snapshot.TryCollectFocusableItemsUnderReadLease(out collected))
            {
                revisionUnderLease = snapshot.LayoutRevision;
                collectedSuccessfully = true;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A custom overflow owner can fail while its availability is read.
            // The local partial list is discarded, and the normal replacement
            // route is responsible for recovery. Defer diagnostics until after
            // EndInteraction because log sinks are callback-capable.
            captureFailure = exception;
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!IsCurrentFocusableItemsRefresh(
                request,
                snapshot,
                generation,
                automationGeneration))
        {
            return false;
        }

        if (captureFailure is not null)
            ReportImageRelayoutFailure("focus plan refresh", captureFailure);

        // Diagnostics can reenter the dispatcher/control. Do not let a failed
        // older attempt clear or rebuild over a newer availability request.
        if (!IsCurrentFocusableItemsRefresh(
                request,
                snapshot,
                generation,
                automationGeneration))
        {
            return false;
        }

        if (!collectedSuccessfully)
        {
            if (snapshot.IsRetiringOrDisposed || snapshot.GeometryRecoveryRequired)
                ClearFocusableItemsRefresh(request.RequestVersion);
            else if (!ReferenceEquals(snapshot, _snapshot) || generation != Volatile.Read(ref _snapshotGeneration))
                return false;
            else if (revisionBefore != snapshot.LayoutRevision)
            {
                _pendingFocusableItemsRefresh = (
                    generation,
                    automationGeneration,
                    snapshot.LayoutRevision,
                    request.RequestVersion,
                    true);
            }
            else
            {
                ClearFocusableItemsRefresh(request.RequestVersion);
                RequestRebuild(RebuildReason.Full);
            }
            return false;
        }

        long revisionAfter = snapshot.LayoutRevision;
        if (!ReferenceEquals(snapshot, _snapshot) ||
            generation != Volatile.Read(ref _snapshotGeneration) ||
            automationGeneration != snapshot.AutomationDocumentGeneration ||
            revisionUnderLease != revisionBefore ||
            revisionAfter != revisionBefore ||
            !IsCurrentFocusableItemsRefresh(
                request,
                snapshot,
                generation,
                automationGeneration))
        {
            if (ReferenceEquals(snapshot, _snapshot) &&
                generation == Volatile.Read(ref _snapshotGeneration) &&
                automationGeneration == snapshot.AutomationDocumentGeneration &&
                IsCurrentFocusableItemsRefresh(
                    request,
                    snapshot,
                    generation,
                    automationGeneration))
            {
                _pendingFocusableItemsRefresh = (
                    generation,
                    automationGeneration,
                    revisionAfter,
                    request.RequestVersion,
                    true);
            }
            return false;
        }

        Layout.FocusableItem? focusedIdentity = GetCurrentFocusableIdentity();
        _focusableItems = collected;
        _focusedItemIndex = focusedIdentity is { } identity
            ? FindFocusableItemIndex(collected, identity)
            : -1;
        ClearFocusableItemsRefresh(request.RequestVersion);
        InvalidateAutomationLayout();
        return true;
    }

    private bool IsCurrentFocusableItemsRefresh(
        (long SnapshotGeneration, long AutomationDocumentGeneration,
            long LayoutRevision, long RequestVersion, bool IsPending) request,
        LayoutSnapshot snapshot,
        long generation,
        long automationGeneration)
    {
        var pending = _pendingFocusableItemsRefresh;
        return request.IsPending &&
            pending.IsPending &&
            pending.RequestVersion == request.RequestVersion &&
            pending.SnapshotGeneration == generation &&
            pending.AutomationDocumentGeneration == automationGeneration &&
            pending.LayoutRevision == request.LayoutRevision &&
            ReferenceEquals(snapshot, _snapshot) &&
            generation == Volatile.Read(ref _snapshotGeneration) &&
            automationGeneration == snapshot.AutomationDocumentGeneration;
    }

    private void CompleteFocusableItemsRefresh(
        LayoutSnapshot snapshot,
        long generation,
        long revision,
        long requestVersion)
    {
        var pending = _pendingFocusableItemsRefresh;
        if (pending.IsPending &&
            pending.RequestVersion == requestVersion &&
            pending.SnapshotGeneration == generation &&
            pending.AutomationDocumentGeneration == snapshot.AutomationDocumentGeneration &&
            pending.LayoutRevision == revision &&
            ReferenceEquals(snapshot, _snapshot) &&
            generation == Volatile.Read(ref _snapshotGeneration) &&
            revision == snapshot.LayoutRevision)
        {
            _pendingFocusableItemsRefresh = (
                pending.SnapshotGeneration,
                pending.AutomationDocumentGeneration,
                pending.LayoutRevision,
                pending.RequestVersion,
                false);
        }
    }

    private void ClearFocusableItemsRefresh(long requestVersion)
    {
        var pending = _pendingFocusableItemsRefresh;
        if (pending.IsPending && pending.RequestVersion == requestVersion)
        {
            _pendingFocusableItemsRefresh = (
                pending.SnapshotGeneration,
                pending.AutomationDocumentGeneration,
                pending.LayoutRevision,
                pending.RequestVersion,
                false);
        }
    }

    private void ClearFocusableItemsRefreshForCurrentSnapshot()
    {
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null)
        {
            ResetPendingFocusableItemsRefresh();
            return;
        }

        var pending = _pendingFocusableItemsRefresh;
        if (pending.IsPending &&
            pending.SnapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
            pending.AutomationDocumentGeneration == snapshot.AutomationDocumentGeneration)
        {
            ClearFocusableItemsRefresh(pending.RequestVersion);
        }
    }

    private void ResetPendingFocusableItemsRefresh() =>
        _pendingFocusableItemsRefresh = default;

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (_isDisposed || _isUnloaded || !IsLoaded || !ReferenceEquals(sender, _canvas))
            return;

        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            PaintInvalidatedRegions(sender, args);
        }
        catch (Exception ex) when (ShouldIgnoreShutdownException(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored canvas paint during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
        }
        finally
        {
            if (measure)
            {
                long allocated = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                performance.PaintWork(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Stopwatch.GetTimestamp() - started,
                    allocated);
            }
        }
    }

    private void PaintInvalidatedRegions(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long allocationCheckpoint = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        QueueLazyLayoutForViewport();
        long schedulingBytes = measure
            ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint)
            : 0;
        long platformSessionBytes = 0;
        long snapshotPaintBytes = 0;
        long interactivePaintBytes = 0;

        if (_snapshot is not { } snapshot) return;
        if (RequestGeometryRecoveryIfRequired(snapshot))
            return; // No drawing session/clear: retain the last good tiles.
        bool refreshDeferredSceneStatusAfterPaint =
            Volatile.Read(ref _deferredSceneStatusRefreshAfterPaint) != 0;
        bool paintedRegion = false;
        foreach (var region in args.InvalidatedRegions)
        {
            try
            {
                long regionStart = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                // The tile itself is the authoritative paint band; the ScrollViewer
                // viewport can lag behind CanvasVirtualControl's region requests.
                QueueLazyLayoutForPaintRegion(region);
                if (!snapshot.TryBeginPaint())
                {
                    // Do not create or clear the tile while background realization
                    // owns the layout. Completion invalidates the canvas, so the
                    // existing pixels remain visible without blocking this frame.
                    continue;
                }

                try
                {
                    long afterSetup = regionStart;
                    long afterSnapshot = regionStart;
                    long afterInteractive = regionStart;
                    using (var ds = sender.CreateDrawingSession(region))
                    {
                        // Clear to the theme-appropriate background color so that switching between
                        // light and dark mode (or any theme change) fully overwrites old tile content.
                        // We use an opaque theme color rather than Colors.Transparent because
                        // CanvasVirtualControl may not alpha-composite with the XAML compositor
                        // depending on the DirectX swap-chain configuration of the platform; on
                        // such configurations transparent pixels show as black rather than letting
                        // the XAML background show through.
                        ds.Clear(_canvasBackground);
                        // Force grayscale text anti-aliasing. ClearType is colour-aware:
                        // the same glyph rendered onto a white background versus an
                        // alpha-blended selection-tinted background produces subtly
                        // different sub-pixel RGB values. Switching to grayscale makes
                        // glyph edges background-independent.
                        ds.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
                        if (measure)
                            afterSetup = GC.GetAllocatedBytesForCurrentThread();
                        snapshot.Paint(ds, region);
                        if (measure)
                            afterSnapshot = GC.GetAllocatedBytesForCurrentThread();
                        // Selection/link state is rendered by the dedicated
                        // viewport-bounded image-source adorner. Never bake it
                        // into document tiles: changing a selection must not
                        // invalidate and repaint immutable document content.
                        afterInteractive = afterSnapshot;
                        paintedRegion = true;
                    }
                    if (measure)
                    {
                        long afterDispose = GC.GetAllocatedBytesForCurrentThread();
                        platformSessionBytes += Math.Max(0, afterSetup - regionStart) +
                                                Math.Max(0, afterDispose - afterInteractive);
                        snapshotPaintBytes += Math.Max(0, afterSnapshot - afterSetup);
                        interactivePaintBytes += Math.Max(0, afterInteractive - afterSnapshot);
                    }
                }
                finally
                {
                    snapshot.EndPaint();
                }
            }
            catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
            {
                HandleCanvasDeviceLost(ex);
                return;
            }
        }
        if (refreshDeferredSceneStatusAfterPaint)
        {
            if (paintedRegion)
                RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded();
        }
        if (paintedRegion &&
            Interlocked.Exchange(ref _deferredSceneProbeAfterPaint, 0) != 0 &&
            !_isDisposed && !_isUnloaded)
        {
            // A Mermaid or Math scene that scrolled into a band that was
            // still being measured was never scheduled; its source fallback
            // stayed "Loading" forever. Re-probe once the band has painted.
            DispatcherQueue?.TryEnqueue(ScheduleVisibleDeferredScenes);
        }
        if (paintedRegion)
        {
            _canvasDeviceRecoveryAttempt = 0;
            StopCanvasDeviceRecovery();
        }
        if (paintedRegion &&
            _snapshotGeneration == _pipelineGeneration &&
            _snapshotPipelineStartTimestamp != 0)
        {
            long pipelineStartTimestamp = _snapshotPipelineStartTimestamp;
            long sourceUtf16Bytes = _snapshotSourceUtf16Bytes;
            long generation = _snapshotGeneration;
            _snapshotPipelineStartTimestamp = 0;
            MarkdownPerformanceEventSource.Log.FirstViewportPresented(
                generation,
                sourceUtf16Bytes,
                Stopwatch.GetTimestamp() - pipelineStartTimestamp);
        }
        if (measure)
        {
            performance.PaintAllocationBreakdown(
                MarkdownPerformanceEventSource.GetLogicalFrameId(),
                schedulingBytes,
                platformSessionBytes,
                snapshotPaintBytes,
                interactivePaintBytes);
        }
    }

    private static async Task RunImageRelayoutAsync(
        ImageRelayoutWorkPayload<LayoutSnapshot> payload,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        CancellationTokenSource cancellation,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher,
        WeakReference<MarkdownRendererControl> receiver,
        IMarkdownPerformanceSessionInternal? session,
        RasterPlacementInputs? rasterPlacement,
        long pointerInputEpoch,
        double? capturedScrollOffset)
    {
        Exception? failure = null;
        long started = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        Func<CancellationToken, bool>? layoutWork = null;
        Task<bool>? layoutTask = null;
        WeakSnapshotPublication<LayoutSnapshot>? publication = null;
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        MarkdownLayoutContext? rootMeasureContext = null;
        RasterPlacementPlan? candidatePlan = null;
        int? retryImageBlockIndex = null;
        bool retryCurrentImages = false;
        bool rootMeasureCommitted = false;
        try
        {
            try
            {
                layoutWork = layoutToken =>
                {
                    // Completed standalone, paragraph, table, list and quote images need
                    // only their owning placements, not an entire document
                    // clone. Prefer the bounded root
                    // batch even when the whole-tree COW path would be eligible.
                    PreparedImageRelayoutResult result = session is null
                        ? PreparedImageRelayoutResult.Unavailable
                        : payload.Snapshot.TryRelayoutCompletedImageRoots(
                            payload.Changed, payload.Width, layoutToken, out rootMeasureContext,
                            expectedPointerInputEpoch: pointerInputEpoch);
                    rootMeasureCommitted = result == PreparedImageRelayoutResult.Committed;
                    if (result == PreparedImageRelayoutResult.Retry)
                    {
                        if (payload.Changed.Length == 1)
                            retryImageBlockIndex = payload.Changed[0];
                        else
                            retryCurrentImages = true;
                    }
                    else if (result == PreparedImageRelayoutResult.Unavailable)
                    {
                        if (rasterPlacement is not null)
                            candidate = payload.Snapshot.CreateSingleRasterCandidate(
                                payload.Changed, payload.Width, layoutToken,
                                rasterPlacement.ImageLifetime, rasterPlacement.ImagePreparationOwner,
                                rasterPlacement.RetirementSink,
                                rasterPlacement.ImageUnavailable, out candidateContext);
                        if (candidate is null)
                            if (!payload.Snapshot.TryRelayoutChangedBlocks(
                                    payload.Changed, payload.Width, layoutToken, pointerInputEpoch))
                            {
                                if (payload.Changed.Length == 1)
                                    retryImageBlockIndex = payload.Changed[0];
                                else
                                    retryCurrentImages = true;
                            }
                    }
                    return true;
                };
                layoutTask = session is null
                    ? Task.Run(() => layoutWork(cancellation.Token), CancellationToken.None)
                    : session.ScheduleLayoutWorkAsync(
                        MarkdownLayoutWorkPriority.Background,
                        layoutWork,
                        cancellation.Token);
                await layoutTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellation.IsCancellationRequested || session?.IsDisposed == true)
            {
                if (session?.IsDisposed == true && !cancellation.IsCancellationRequested)
                    _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            // Join construction cleanup only after scheduled native work exits.
            // The new context has its own ledger, including pre-snapshot faults.
            if (candidateContext is not null)
            {
                try { await candidateContext.DrainOwnedRetirementsAsync(failure).ConfigureAwait(false); }
                catch (Exception exception) { failure = exception; }
            }
            if (rootMeasureContext is not null)
            {
                try { await rootMeasureContext.DrainOwnedRetirementsAsync(failure).ConfigureAwait(false); }
                catch (Exception exception) { failure = exception; }
            }
            if (candidate is not null && failure is null && !cancellation.IsCancellationRequested && session?.IsDisposed != true)
            {
                try
                {
                    rasterPlacement!.Bind(candidate.Snapshot);
                    candidatePlan = new RasterPlacementPlan(candidate, generation, anchor, rasterPlacement.ScrollOffset);
                    candidate = null; // plan now owns all private native resources
                }
                catch (Exception exception) { failure = exception; }
            }

            if (started != 0)
                MarkdownPerformanceEventSource.Log.ResourceWork(
                    3, Stopwatch.GetTimestamp() - started, payload.Changed.Length);

            // The control is the strong owner of the currently published
            // snapshot. A stale delayed completion needs only weak identity;
            // it still carries the CTS ticket so ordinary UI cleanup can run.
            publication = CreateImageRelayoutPublication(
                payload, anchor, generation, cancellation, failure, receiver, candidatePlan,
                retryImageBlockIndex, retryCurrentImages, rootMeasureCommitted, capturedScrollOffset);
            candidatePlan = null; // one-shot envelope owns publish or discard
            if (!payload.TryOwnPublication(publication))
            {
                // Unload/completion closes this specific operation owner. Its
                // existing CTS retirement barrier owns callback and work drain;
                // never dispose that ticket early from the losing worker path.
                return;
            }
            // Every delivery route uses the same ready, one-shot owner. Actual
            // layout has returned; release strong borrows before dispatch so a
            // retained callback/task cannot hold a retired graph indirectly.
            payload.ReleaseAfterWorkDrains();
            try
            {
                dispatcher?.TryEnqueue(payload.PublishAfterWorkDrains);
            }
            catch (Exception exception)
            {
                ReportImageRelayoutFailure("relayout completion dispatch", exception.Message);
            }
            // Rejection/lost dispatch leaves the ready envelope and CTS owned
            // by the loaded control's recovery timer, or unload's drain fence.
        }
        finally
        {
            // These async/closure fields can survive in a retained completed
            // task. Clear them only after the scheduled work really returned,
            // not when cancellation was merely requested. The UI callback has
            // a separate weak handoff and never reads these worker aliases.
            // Clear the actual owner as well as local aliases: compiler-created
            // parameter/closure copies then retain only an empty owner.
            payload.ReleaseAfterWorkDrains();
            payload = null!;
            layoutWork = null;
            layoutTask = null;
            publication = null;
            try
            {
                candidate?.Dispose();
                candidatePlan?.Dispose();
                // Failed/canceled candidates can register retirement after an
                // unload drain epoch has begun. The worker's existing barrier
                // must include this private retirement, not just observe it in
                // the host's next epoch.
                Task candidateDrain = candidateContext?.DrainOwnedRetirementsAsync(failure) ?? Task.CompletedTask;
                Task rootMeasureDrain = rootMeasureContext?.DrainOwnedRetirementsAsync(failure) ?? Task.CompletedTask;
                await Task.WhenAll(candidateDrain, rootMeasureDrain).ConfigureAwait(false);
            }
            finally
            {
                candidate = null;
                candidatePlan = null;
                candidateContext = null;
                rootMeasureContext = null;
                retryImageBlockIndex = null;
                rasterPlacement = null;
                session = null;
                dispatcher = null;
                failure = null;
                cancellation = null!;
                receiver = null!;
            }
        }
    }

    // Keep the graph-bearing temporary inside this factory frame, not in the
    // async caller's frame that may still be unwinding after task completion.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakSnapshotPublication<LayoutSnapshot> CreateImageRelayoutPublication(
        ImageRelayoutWorkPayload<LayoutSnapshot> payload,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        CancellationTokenSource cancellation,
        Exception? failure,
        WeakReference<MarkdownRendererControl> receiver,
        RasterPlacementPlan? candidate,
        int? retryImageBlockIndex,
        bool retryCurrentImages,
        bool rootMeasureCommitted,
        double? capturedScrollOffset) =>
        new(payload.Snapshot, publishedSnapshot =>
        {
            try
            {
                if (receiver.TryGetTarget(out var control))
                    control.CompleteImageRelayout(publishedSnapshot, anchor, generation, cancellation, failure, candidate,
                        retryImageBlockIndex, retryCurrentImages, rootMeasureCommitted, capturedScrollOffset);
                else
                    _ = CancellationTokenSourceRetirement.CancelAndDisposeAfter(cancellation, lifetime: null);
            }
            finally { candidate?.Dispose(); }
        }, candidate is null ? null : candidate.Dispose);

    private void CompleteImageRelayout(
        LayoutSnapshot? snapshot,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        CancellationTokenSource cancellation,
        Exception? failure,
        RasterPlacementPlan? candidate = null,
        int? retryImageBlockIndex = null,
        bool retryCurrentImages = false,
        bool rootMeasureCommitted = false,
        double? capturedScrollOffset = null)
    {
        bool current = ReferenceEquals(_imageRelayoutCts, cancellation);
        Task? workLifetime = current ? _imageRelayoutTask : null;
        long started = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        if (current)
        {
            Interlocked.Exchange(ref _imageRelayoutPayload, null)?.DiscardPublication();
            _imageRelayoutCts = null;
            _imageRelayoutTask = null;
            _imageRelayoutActive = false;
            if (_pendingLayoutCommit is null)
                StopImageRelayoutRecovery();
        }

        bool canceled = cancellation.IsCancellationRequested;
        RetireCancellationTokenSource(cancellation, workLifetime);
        try
        {
            // Cancellation does not make a failed live mutation safe. This
            // check precedes the ordinary canceled-result publication fence.
            if (RequestGeometryRecoveryIfRequired(snapshot))
                return;

            // Private preparation remains cancelable. Once root placement has
            // committed, its live size/paint state must still reach the UI even
            // if cancellation arrived after that non-cancelable commit point.
            // A private whole-tree candidate has not committed and is excluded.
            if (current && snapshot is not null && !_isDisposed && !_isUnloaded &&
                (!canceled || rootMeasureCommitted) &&
                generation == _snapshotGeneration && ReferenceEquals(snapshot, _snapshot))
            {
                if (failure is not null)
                {
                    ReportImageRelayoutFailure("relayout", failure);
                    RequestRebuild(RebuildReason.Full);
                }
                else if (retryImageBlockIndex.HasValue || retryCurrentImages)
                {
                    // Private measurement lost an input/layout/resource fence.
                    // Keep only a scalar for a singleton. Multi-image fallback
                    // recovers from the current control-owned subscriptions,
                    // never retaining the worker's dirty array in a dispatch.
                    if (retryImageBlockIndex is { } retryIndex)
                        _pendingImageRelayoutBlockIndices.Add(retryIndex);
                    if (retryCurrentImages)
                        foreach (var image in _subscribedImages)
                            _pendingImageRelayoutBlockIndices.Add(image.BlockIndex);
                    if (snapshot.IsRetiringOrDisposed ||
                        (float)Math.Max(50, ActualWidth) != snapshot.Size.Width)
                        RequestRebuild(RebuildReason.Full);
                }
                else if (candidate is not null)
                {
                    if (HasSnapshotPointerInteraction)
                    {
                        // A gesture may have started after the worker captured
                        // its source. Discard this private candidate and retry
                        // after release; do not reset capture or request a full
                        // rebuild solely because the user is interacting.
                        foreach (var image in _subscribedImages)
                            _pendingImageRelayoutBlockIndices.Add(image.BlockIndex);
                    }
                    else if (!TryPublishRasterPlacement(snapshot, candidate, generation))
                    {
                        // A changed width/policy/raster invalidates the private
                        // proof. Rebuild rather than mutate the old tree here.
                        foreach (var image in _subscribedImages)
                            _pendingImageRelayoutBlockIndices.Add(image.BlockIndex);
                        RequestRebuild(RebuildReason.Full);
                    }
                }
                else
                    ApplyLazyLayoutCommit(snapshot, rootMeasureCommitted &&
                        (canceled || capturedScrollOffset != _scroll?.VerticalOffset) ? null : anchor);
            }
        }
        catch (Exception exception)
        {
            ReportImageRelayoutFailure("relayout", exception);
            RequestRebuild(RebuildReason.Full);
        }
        finally
        {
            // A replacement can cancel an active reflow and then fail itself.
            // Recover current image geometry without retaining the worker's
            // dirty array in the delayed UI envelope or touching a retired graph.
            if (current && canceled && !_isDisposed && !_isUnloaded &&
                ReferenceEquals(snapshot, _snapshot) && snapshot?.GeometryRecoveryRequired != true)
            {
                foreach (var image in _subscribedImages)
                    _pendingImageRelayoutBlockIndices.Add(image.BlockIndex);
            }
            // The pending set is cleared before every snapshot replacement.
            // New-snapshot completions may accumulate while an old task drains.
            if (current && _pendingImageRelayoutBlockIndices.Count > 0 && !_isDisposed && !_isUnloaded &&
                _snapshot?.GeometryRecoveryRequired != true)
            {
                foreach (int pending in _pendingImageRelayoutBlockIndices)
                {
                    QueueImageRelayout(pending);
                    break;
                }
            }
            if (started != 0)
                MarkdownPerformanceEventSource.Log.ResourceWork(
                    4, Stopwatch.GetTimestamp() - started, 1);
        }
    }

    [Conditional("DEBUG")]
    private static void ReportImageRelayoutFailure(string operation, object details) =>
        MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] image {operation} failed: {details}");

    private void HandleCanvasDeviceLost(Exception exception)
        => HandleCanvasDeviceLost(exception.HResult);

    private void HandleCanvasDeviceLost(int failureHResult)
    {
        // Capture every device in use before release clears the surface owner.
        CanvasDevice? layoutDevice = _snapshotLayoutDevice;
        CanvasDevice? surfaceDevice = _selectionAdornerCanvasDevice;
        CanvasDevice? canvasDevice = null;
        try { canvasDevice = _canvas?.Device; }
        catch (Exception deviceException)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] canvas device query failed: {deviceException.Message}");
        }

        TryReleaseSelectionAdornerSurface();
        // Snapshot bitmaps are cached under the layout (shared) device, which
        // need not be the CanvasVirtualControl device.
        ReleaseImageDeviceResources(layoutDevice);
        if (!ReferenceEquals(canvasDevice, layoutDevice))
            ReleaseImageDeviceResources(canvasDevice);
        // Exceptions were caught instead of reaching Win2D, so notify each
        // genuinely lost device as the manual device-loss contract requires.
        NotifyLostCanvasDevices(failureHResult, layoutDevice, surfaceDevice, canvasDevice);

        if (_canvasDeviceRecoveryQueued || _isDisposed || _isUnloaded)
            return;

        _canvasDeviceRecoveryAttempt = Math.Min(_canvasDeviceRecoveryAttempt + 1, 6);
        int delayMs = Math.Min(5000, 250 << (_canvasDeviceRecoveryAttempt - 1));

        MarkdownDiagnostics.WriteLine(
            "[MarkdownRendererControl] Win2D device lost while painting; " +
            $"HRESULT={GraphicsDeviceErrors.FormatHResult(failureHResult)}. " +
            $"Retrying canvas rebuild in {delayMs}ms.");

        try
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = TimeSpan.FromMilliseconds(delayMs);
            timer.Tick += OnCanvasDeviceRecoveryTimer;
            _canvasDeviceRecoveryTimer = timer;
            _canvasDeviceRecoveryQueued = true;
            timer.Start();
        }
        catch (Exception schedulingFailure)
        {
            StopCanvasDeviceRecovery();
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] device recovery timer failed: {schedulingFailure.Message}");
            RequestRebuild(RebuildReason.Full);
        }
    }

    private static void ReleaseImageDeviceResources(CanvasDevice? device)
    {
        if (device is null)
            return;

        try
        {
            Layout.Boxes.ImageBox.ReleaseDeviceResources(device);
        }
        catch (Exception releaseException)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] image device-resource release failed: {releaseException.Message}");
        }
    }

    private static void NotifyLostCanvasDevices(int failureHResult, params CanvasDevice?[] devices)
    {
        for (int index = 0; index < devices.Length; index++)
        {
            CanvasDevice? device = devices[index];
            if (device is null || Array.IndexOf(devices, device) != index)
                continue;

            try
            {
                if (device.IsDeviceLost(failureHResult))
                    device.RaiseDeviceLost();
            }
            catch (Exception notifyException)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] device-lost notification failed: {notifyException.Message}");
            }
        }
    }

    private void OnCanvasCreateResources(
        CanvasVirtualControl sender,
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        if (_isDisposed || _isUnloaded || !ReferenceEquals(sender, _canvas))
            return;

        // Initial resource creation can be deferred until the first input frame in
        // packaged WinUI. Rebuilding there resets pointer capture and selection
        // state, which makes the first click/drag flash and get swallowed. Layout
        // uses CanvasDevice.GetSharedDevice(), so initial creation needs no rebuild.
        // Later resource creation means device/DPI changed, so rebuild layouts.
        if (args.Reason != Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.FirstTime)
            RequestRebuild(RebuildReason.Full);
    }

    // ---- Input ----

    private static bool IsPrecisePointer(PointerInput e)
        => MarkdownPointerGesturePolicy.BeginsImmediateSelection(
            GetPointerModality(e.Pointer.PointerDeviceType));

    private static MarkdownPointerModality GetPointerModality(
        Microsoft.UI.Input.PointerDeviceType deviceType)
        => deviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Mouse => MarkdownPointerModality.Mouse,
            Microsoft.UI.Input.PointerDeviceType.Touch => MarkdownPointerModality.Touch,
            Microsoft.UI.Input.PointerDeviceType.Pen => MarkdownPointerModality.Pen,
            _ => MarkdownPointerModality.Unknown,
        };

    private bool IsActivePointerSessionEvent(PointerInput e)
        => _pointerSession.IsActive && _pointerSession.PointerId == e.Pointer.PointerId;

    private void ProcessPointerWheelChanged(ref PointerInput e)
    {
        if (_snapshot is not { } snapshot || _canvas is null)
            return;

        var point = e.Point;
        bool horizontalWheel = point.Properties.IsHorizontalMouseWheel;
        bool shift = (e.Modifiers & VirtualKeyModifiers.Shift) != 0;
        if (!horizontalWheel && !shift)
            return;

        if (!snapshot.TryGetHorizontalOverflow(point.Position, out var target) || target is null)
            return;

        int wheelDelta = point.Properties.MouseWheelDelta;
        if (wheelDelta == 0)
            return;

        double oldOffset = target.HorizontalOffset;
        double delta = -(wheelDelta / 120.0) * 64.0;
        if (target.IsRightToLeft)
            delta = -delta;

        if (!snapshot.TryScrollHorizontal(target, delta))
            return;

        RefreshHorizontalOverflowVisualState(target, oldOffset);
        e.Handled = true;
    }

    private bool TryBeginHorizontalOverflowPointer(ref PointerInput e)
    {
        if (_snapshot is not { } snapshot || _canvas is null)
            return false;

        var point = e.Point;
        if (!point.IsInContact ||
            !snapshot.TryGetHorizontalOverflow(point.Position, out var target) ||
            target is null)
        {
            return false;
        }

        Rect track = target.HorizontalScrollTrackBounds;
        bool precise = IsPrecisePointer(e);
        if (precise && !track.Contains(point.Position))
            return false;

        _horizontalOverflowPointerId = e.Pointer.PointerId;
        _horizontalOverflowSnapshot = snapshot;
        _horizontalOverflowTarget = target;
        _horizontalOverflowPressPoint = point.Position;
        _horizontalOverflowInitialOffset = target.HorizontalOffset;
        _horizontalOverflowCaptured = false;
        _horizontalOverflowThumbDrag = precise;

        if (!precise)
        {
            // A touch contact starts in a pending state. Do not capture or mark
            // handled until horizontal movement wins the gesture threshold, so
            // the ancestor ScrollViewer remains free to own vertical panning.
            return true;
        }

        Rect thumb = target.HorizontalScrollThumbBounds;
        double oldOffset = target.HorizontalOffset;
        if (!thumb.Contains(point.Position))
        {
            SetHorizontalOffsetFromTrackPoint(snapshot, target, point.Position.X, centerThumb: true);
            thumb = target.HorizontalScrollThumbBounds;
        }
        _horizontalOverflowThumbGrabOffset = Math.Clamp(
            point.Position.X - thumb.Left,
            0,
            Math.Max(0, thumb.Width));

        if (!CapturePointerForInput(_canvas, e))
        {
            ClearHorizontalOverflowPointerState();
            e.ResumeRebuildAfterInteraction = true;
            return false;
        }

        _horizontalOverflowCaptured = true;
        if (Math.Abs(target.HorizontalOffset - oldOffset) > 0.1)
            RefreshHorizontalOverflowVisualState(target, oldOffset);
        e.Handled = true;
        return true;
    }

    private bool HandleHorizontalOverflowPointerMoved(ref PointerInput e, Point point)
    {
        if (_horizontalOverflowPointerId == 0 ||
            _horizontalOverflowPointerId != e.Pointer.PointerId ||
            _horizontalOverflowSnapshot is not { } snapshot ||
            _horizontalOverflowTarget is not { } target ||
            !ReferenceEquals(snapshot, _snapshot))
        {
            return false;
        }

        if (_horizontalOverflowThumbDrag)
        {
            double oldOffset = target.HorizontalOffset;
            SetHorizontalOffsetFromTrackPoint(
                snapshot,
                target,
                point.X - _horizontalOverflowThumbGrabOffset,
                centerThumb: false);
            if (Math.Abs(target.HorizontalOffset - oldOffset) > 0.1)
                RefreshHorizontalOverflowVisualState(target, oldOffset);
            e.Handled = true;
            return true;
        }

        double dx = point.X - _horizontalOverflowPressPoint.X;
        double dy = point.Y - _horizontalOverflowPressPoint.Y;
        if (!_horizontalOverflowCaptured)
        {
            MarkdownPanGestureDecision decision =
                MarkdownPointerGesturePolicy.ClassifyHorizontalPan(dx, dy);
            if (decision == MarkdownPanGestureDecision.Pending)
                return false;

            if (decision == MarkdownPanGestureDecision.YieldToAncestor)
            {
                ClearHorizontalOverflowPointerState();
                e.ResumeRebuildAfterInteraction = true;
                return false;
            }

            if (_canvas is null || !CapturePointerForInput(_canvas, e))
            {
                ClearHorizontalOverflowPointerState();
                e.ResumeRebuildAfterInteraction = true;
                return false;
            }
            _horizontalOverflowCaptured = true;
            SuppressTouchTap(point, e.TouchTapGestureEpoch);
        }

        double oldTouchOffset = target.HorizontalOffset;
        double requested = target.IsRightToLeft
            ? _horizontalOverflowInitialOffset + dx
            : _horizontalOverflowInitialOffset - dx;
        if (snapshot.TrySetHorizontalOffset(target, requested))
            RefreshHorizontalOverflowVisualState(target, oldTouchOffset);
        e.Handled = true;
        return true;
    }

    private bool HandleHorizontalOverflowPointerReleased(ref PointerInput e)
    {
        if (_horizontalOverflowPointerId == 0 ||
            _horizontalOverflowPointerId != e.Pointer.PointerId)
        {
            return false;
        }

        bool captured = _horizontalOverflowCaptured;
        if (captured && _canvas is not null)
            SuppressTouchTap(e.Point.Position, e.TouchTapGestureEpoch);
        ClearHorizontalOverflowPointerState();
        e.ResumeRebuildAfterInteraction = true;
        if (captured && _canvas is not null)
        {
            e.Handled = true;
            e.ReleaseCanvasCaptureAfterInteraction = true;
        }
        return captured;
    }

    private bool HandleHorizontalOverflowPointerCanceled(ref PointerInput e)
    {
        if (_horizontalOverflowPointerId == 0 ||
            _horizontalOverflowPointerId != e.Pointer.PointerId)
        {
            return false;
        }

        bool captured = _horizontalOverflowCaptured;
        ClearHorizontalOverflowPointerState();
        e.ResumeRebuildAfterInteraction = true;
        if (captured)
        {
            e.Handled = true;
        }
        return captured;
    }

    private static void SetHorizontalOffsetFromTrackPoint(
        LayoutSnapshot snapshot,
        IHorizontalOverflowBox target,
        double x,
        bool centerThumb)
    {
        Rect track = target.HorizontalScrollTrackBounds;
        Rect thumb = target.HorizontalScrollThumbBounds;
        if (track.IsEmpty || thumb.IsEmpty)
            return;

        double thumbLeft = centerThumb ? x - thumb.Width / 2.0 : x;
        double travel = Math.Max(0, track.Width - thumb.Width);
        if (travel <= 0)
            return;

        double physicalFraction = Math.Clamp((thumbLeft - track.Left) / travel, 0, 1);
        double logicalFraction = target.IsRightToLeft
            ? 1.0 - physicalFraction
            : physicalFraction;
        double maximum = Math.Max(0, target.HorizontalExtent - target.HorizontalViewport);
        snapshot.TrySetHorizontalOffset(target, logicalFraction * maximum);
    }

    private void RefreshHorizontalOverflowVisualState(
        IHorizontalOverflowBox target,
        double oldOffset)
    {
        if (_isDisposed || _isUnloaded || _snapshot is not { } snapshot ||
            target is not BlockBox targetBlock)
        {
            return;
        }

        long generation = Volatile.Read(ref _snapshotGeneration);
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
        {
            QueueHorizontalOverflowVisualRetry(
                snapshot, generation, targetBlock.BlockIndex, oldOffset);
            return;
        }

        HorizontalOverflowVisualState state = default;
        bool captured = false;
        try
        {
            if (IsCurrentScrollTarget(snapshot, generation, revision))
            {
                captured = snapshot.TryGetHorizontalOverflowForBlockIndex(
                    targetBlock.BlockIndex,
                    out IHorizontalOverflowBox? currentTarget) &&
                    ReferenceEquals(currentTarget, target) &&
                    TryCaptureHorizontalOverflowVisualStateNoLock(
                        snapshot, generation, revision, target, oldOffset, out state);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (captured && IsCurrentScrollTarget(snapshot, generation, revision))
            PublishHorizontalOverflowVisualState(state);
        else if (ReferenceEquals(snapshot, _snapshot) &&
                 generation == Volatile.Read(ref _snapshotGeneration))
            QueueHorizontalOverflowVisualRetry(
                snapshot, generation, targetBlock.BlockIndex, oldOffset);
    }

    private bool TryCaptureHorizontalOverflowVisualStateNoLock(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        IHorizontalOverflowBox target,
        double oldOffset,
        out HorizontalOverflowVisualState state)
    {
        state = default;
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) ||
            target is not BlockBox targetBlock ||
            !snapshot.TryGetHorizontalOverflowForBlockIndex(
                targetBlock.BlockIndex,
                out IHorizontalOverflowBox? currentTarget) ||
            !ReferenceEquals(currentTarget, target))
        {
            return false;
        }

        double newOffset = target.HorizontalOffset;
        double logicalDelta = newOffset - oldOffset;
        if (Math.Abs(logicalDelta) <= 0.1)
            return false;

        double physicalDelta = target.IsRightToLeft ? logicalDelta : -logicalDelta;
        List<InlineEmbedPlacementUpdate>? placements = null;
        foreach (EmbedPlan plan in _embedPlans)
        {
            if (plan is not InlineEmbedPlan inline ||
                !HorizontalOverflowContains(target, inline.Icb))
            {
                continue;
            }

            Rect rect = inline.Rect;
            placements ??= new List<InlineEmbedPlacementUpdate>();
            placements.Add(new(
                new WeakReference<InlineEmbedPlan>(inline),
                new Rect(rect.X + physicalDelta, rect.Y, rect.Width, rect.Height)));
        }

        state = new(
            new WeakReference<LayoutSnapshot>(snapshot),
            snapshotGeneration,
            layoutRevision,
            targetBlock.BlockIndex,
            new WeakReference<IHorizontalOverflowBox>(target),
            oldOffset,
            newOffset,
            target.HorizontalExtent,
            target.HorizontalViewport,
            target.IsRightToLeft,
            snapshot.ToLogicalRectForBlockIndexUnderPaint(
                targetBlock.BlockIndex, target.HorizontalViewportBounds),
            snapshot.ToLogicalRectForBlockIndexUnderPaint(
                targetBlock.BlockIndex, target.HorizontalScrollTrackBounds),
            placements?.ToArray() ?? Array.Empty<InlineEmbedPlacementUpdate>());
        return true;
    }

    private void PublishHorizontalOverflowVisualState(HorizontalOverflowVisualState state)
    {
        bool queued = false;
        try
        {
            queued = DispatcherQueue is { } dispatcher &&
                dispatcher.TryEnqueue(() => ApplyHorizontalOverflowVisualState(state));
        }
        catch (Exception)
        {
        }
        if (!queued)
        {
            QueueHorizontalOverflowVisualRetry(
                state.Snapshot.TryGetTarget(out LayoutSnapshot? snapshot)
                    ? snapshot
                    : null,
                state.SnapshotGeneration,
                state.BlockIndex,
                state.OldOffset);
        }
    }

    private void ApplyHorizontalOverflowVisualState(HorizontalOverflowVisualState state)
    {
        LayoutSnapshot? snapshot = null;
        IHorizontalOverflowBox? target = null;
        if (_isDisposed || _isUnloaded ||
            state.Snapshot is null || state.Target is null ||
            !state.Snapshot.TryGetTarget(out snapshot) ||
            !state.Target.TryGetTarget(out target) ||
            !IsCurrentScrollTarget(snapshot, state.SnapshotGeneration, state.LayoutRevision))
        {
            if (snapshot is not null &&
                ReferenceEquals(snapshot, _snapshot) &&
                state.SnapshotGeneration == Volatile.Read(ref _snapshotGeneration))
            {
                QueueHorizontalOverflowVisualRetry(
                    snapshot, state.SnapshotGeneration, state.BlockIndex, state.OldOffset);
            }
            return;
        }

        bool movedEmbed = false;
        foreach (InlineEmbedPlacementUpdate placement in state.EmbedPlacements)
        {
            if (!IsCurrentScrollTarget(snapshot, state.SnapshotGeneration, state.LayoutRevision))
                return;
            if (!placement.Plan.TryGetTarget(out InlineEmbedPlan? plan) ||
                !_embedPlans.Contains(plan))
            {
                continue;
            }

            plan.Rect = placement.Rect;
            plan.UpdatePlacement();
            movedEmbed = true;
        }

        if (!IsCurrentScrollTarget(snapshot, state.SnapshotGeneration, state.LayoutRevision))
            return;
        if (movedEmbed)
            RealizeVisibleEmbeds();
        if (!IsCurrentScrollTarget(snapshot, state.SnapshotGeneration, state.LayoutRevision))
            return;
        if (_selection.IsActive)
            UpdateSelectionOverlay();
        if (!IsCurrentScrollTarget(snapshot, state.SnapshotGeneration, state.LayoutRevision))
            return;
        if (_focusedItemIndex >= 0)
            UpdateFocusRing();
        QueueHorizontalOverflowAutomationNotification(
            snapshot,
            state.SnapshotGeneration,
            target,
            state.OldOffset,
            state.NewOffset,
            state.Extent,
            state.Viewport,
            state.IsRightToLeft);

        if (_canvas is { } canvas)
        {
            if (MarkdownCanvasInvalidationPolicy.TryClipToCanvas(
                    new Rect(
                        state.ViewportBounds.X - 1,
                        state.ViewportBounds.Y - 1,
                        state.ViewportBounds.Width + 2,
                        Math.Max(state.ViewportBounds.Bottom, state.TrackBounds.Bottom) - state.ViewportBounds.Y + 2),
                    canvas.Width,
                    canvas.Height,
                    out Rect invalidation))
            {
                canvas.Invalidate(invalidation);
            }
        }
    }

    private void QueueHorizontalOverflowVisualRetry(
        LayoutSnapshot? snapshot,
        long snapshotGeneration,
        int blockIndex,
        double oldOffset)
    {
        if (snapshot is null || !ReferenceEquals(snapshot, _snapshot) ||
            snapshotGeneration != Volatile.Read(ref _snapshotGeneration))
        {
            return;
        }

        _hasPendingHorizontalOverflowRefresh = true;
        _pendingHorizontalOverflowRefreshGeneration = snapshotGeneration;
        _pendingHorizontalOverflowRefreshBlockIndex = blockIndex;
        _pendingHorizontalOverflowRefreshOldOffset = oldOffset;
        StartImageRelayoutRecovery();
        if (!HasImageRelayoutRecoveryClock)
        {
            _hasPendingHorizontalOverflowRefresh = false;
            RequestRebuild(RebuildReason.Full);
        }
    }

    private void ProcessPendingHorizontalOverflowVisualRetry()
    {
        if (!_hasPendingHorizontalOverflowRefresh)
            return;
        if (RequestGeometryRecoveryIfRequired(_snapshot))
        {
            _hasPendingHorizontalOverflowRefresh = false;
            return;
        }
        if (_isDisposed || _isUnloaded || _snapshot is not { } snapshot ||
            _pendingHorizontalOverflowRefreshGeneration != Volatile.Read(ref _snapshotGeneration))
        {
            _hasPendingHorizontalOverflowRefresh = false;
            return;
        }

        long generation = _pendingHorizontalOverflowRefreshGeneration;
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
            return;

        HorizontalOverflowVisualState state = default;
        bool found = false;
        bool missing = false;
        try
        {
            if (IsCurrentScrollTarget(snapshot, generation, revision) &&
                snapshot.TryGetHorizontalOverflowForBlockIndex(
                    _pendingHorizontalOverflowRefreshBlockIndex,
                    out IHorizontalOverflowBox? target) &&
                target is not null)
            {
                found = TryCaptureHorizontalOverflowVisualStateNoLock(
                    snapshot,
                    generation,
                    revision,
                    target,
                    _pendingHorizontalOverflowRefreshOldOffset,
                    out state);
                missing = !found &&
                    Math.Abs(target.HorizontalOffset - _pendingHorizontalOverflowRefreshOldOffset) <= 0.1;
            }
            else
            {
                missing = true;
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!IsCurrentScrollTarget(snapshot, generation, revision))
            return;
        if (found)
        {
            _hasPendingHorizontalOverflowRefresh = false;
            PublishHorizontalOverflowVisualState(state);
        }
        else if (missing)
        {
            _hasPendingHorizontalOverflowRefresh = false;
        }
    }

    private void QueueHorizontalOverflowAutomationNotification(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        IHorizontalOverflowBox target,
        double oldOffset,
        double newOffset,
        double extent,
        double viewport,
        bool isRightToLeft)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) ||
            _isDisposed ||
            _isUnloaded ||
            !ReferenceEquals(snapshot, _snapshot) ||
            snapshotGeneration != Volatile.Read(ref _snapshotGeneration) ||
            !snapshot.SemanticDocument.TryGetHorizontalOverflowNode(target, out _))
        {
            return;
        }

        double oldPhysicalPercent = MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
            oldOffset,
            extent,
            viewport,
            isRightToLeft);
        double newPhysicalPercent = MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
            newOffset,
            extent,
            viewport,
            isRightToLeft);
        if (Math.Abs(newPhysicalPercent - oldPhysicalPercent) <= 0.0001)
            return;

        if (_pendingHorizontalOverflowAutomationChanges.TryGetValue(
                target,
                out PendingHorizontalOverflowAutomationChange pending) &&
            ReferenceEquals(pending.Snapshot, snapshot))
        {
            _pendingHorizontalOverflowAutomationChanges[target] = new(
                pending.Snapshot,
                pending.OldPhysicalPercent,
                newPhysicalPercent);
        }
        else
        {
            _pendingHorizontalOverflowAutomationChanges[target] = new(
                snapshot,
                oldPhysicalPercent,
                newPhysicalPercent);
        }

        if (_horizontalOverflowAutomationEventQueued)
            return;

        _horizontalOverflowAutomationEventQueued = true;
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ProcessQueuedHorizontalOverflowAutomationNotifications))
        {
            _horizontalOverflowAutomationEventQueued = false;
            _pendingHorizontalOverflowAutomationChanges.Clear();
        }
    }

    private void ProcessQueuedHorizontalOverflowAutomationNotifications()
    {
        _horizontalOverflowAutomationEventQueued = false;
        var changes = new KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>[
            _pendingHorizontalOverflowAutomationChanges.Count];
        ((ICollection<KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>>)
            _pendingHorizontalOverflowAutomationChanges).CopyTo(changes, 0);
        _pendingHorizontalOverflowAutomationChanges.Clear();

        if (_isDisposed || _isUnloaded || _snapshot is not { } currentSnapshot)
            return;

        MarkdownAutomationPeer? peer =
            FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer;
        if (peer is null)
            return;

        foreach (KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange> entry in changes)
        {
            PendingHorizontalOverflowAutomationChange change = entry.Value;
            if (!ReferenceEquals(change.Snapshot, currentSnapshot) ||
                !currentSnapshot.SemanticDocument.TryGetHorizontalOverflowNode(entry.Key, out _))
            {
                continue;
            }

            peer.NotifyHorizontalOverflowScrolled(
                entry.Key,
                change.OldPhysicalPercent,
                change.NewPhysicalPercent);
        }
    }

    internal void ScrollHorizontalOverflowForAutomation(
        IHorizontalOverflowBox target,
        ScrollAmount amount)
    {
        if (_snapshot is not { } snapshot)
            return;

        double physicalDelta = amount switch
        {
            ScrollAmount.LargeDecrement => -Math.Max(48, target.HorizontalViewport * 0.9),
            ScrollAmount.SmallDecrement => -48,
            ScrollAmount.SmallIncrement => 48,
            ScrollAmount.LargeIncrement => Math.Max(48, target.HorizontalViewport * 0.9),
            _ => 0,
        };
        double logicalDelta = MarkdownHorizontalScrollPolicy.GetLogicalDelta(
            physicalDelta,
            target.IsRightToLeft);
        double oldOffset = target.HorizontalOffset;
        if (snapshot.TryScrollHorizontal(target, logicalDelta))
            RefreshHorizontalOverflowVisualState(target, oldOffset);
    }

    internal void SetHorizontalOverflowPercentForAutomation(
        IHorizontalOverflowBox target,
        double percent)
    {
        if (_snapshot is not { } snapshot || !double.IsFinite(percent))
            return;

        double oldOffset = target.HorizontalOffset;
        double logicalOffset = MarkdownHorizontalScrollPolicy.GetLogicalOffset(
            percent,
            target.HorizontalExtent,
            target.HorizontalViewport,
            target.IsRightToLeft);
        if (snapshot.TrySetHorizontalOffset(target, logicalOffset))
            RefreshHorizontalOverflowVisualState(target, oldOffset);
    }

    private static bool HorizontalOverflowContains(
        IHorizontalOverflowBox target,
        Layout.Boxes.InlineContainerBox box)
    {
        if (target is Layout.Boxes.CodeBlockBox code)
        {
            foreach (var chunk in code.Chunks)
            {
                if (ReferenceEquals(chunk, box))
                    return true;
            }
            return false;
        }

        if (target is Layout.Boxes.TableBox table)
        {
            foreach (var cell in table.GetCellBoxes())
            {
                if (ReferenceEquals(cell, box))
                    return true;
            }
        }
        return false;
    }

    private void ClearHorizontalOverflowPointerState()
    {
        _horizontalOverflowPointerId = 0;
        _horizontalOverflowSnapshot = null;
        _horizontalOverflowTarget = null;
        _horizontalOverflowPressPoint = default;
        _horizontalOverflowInitialOffset = 0;
        _horizontalOverflowThumbGrabOffset = 0;
        _horizontalOverflowCaptured = false;
        _horizontalOverflowThumbDrag = false;
    }

    private void ProcessPointerPressed(ref PointerInput e)
    {
        if (_snapshot is not { } snapshot || _canvas is null)
            return;
        _pendingTouchSelectionPresentationRestore = null;
        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        long layoutRevision = snapshot.LayoutRevision;

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            Rect contact = e.Point.Properties.ContactRect;
            bool contactAccepted = _touchSelection.BeginContact(
                e.Pointer.PointerId,
                new TouchContactGeometry(contact.X, contact.Y, contact.Width, contact.Height),
                selectionIsActive: _selection.IsActive);
            if (contactAccepted &&
                _touchTapSuppression.TryBeginContact(e.Pointer.PointerId, out long gestureEpoch))
            {
                e.TouchTapGestureEpoch = gestureEpoch;
            }
            else
            {
                // Holding/Tapped routed events do not expose a pointer ID. Keep
                // a secondary contact's origin so a recognized event at that
                // location cannot be mistaken for the primary contact's event.
                _touchTapSuppression.TrackUnownedContact(
                    e.Pointer.PointerId,
                    e.Point.Position);
                if (contactAccepted)
                    _touchSelection.Cancel(e.Pointer.PointerId);
            }
        }

        if (TryBeginHorizontalOverflowPointer(ref e))
        {
            if (e.Handled)
                return;
            if (!IsPrecisePointer(e))
                return;
        }
        MarkdownPointerModality modality = GetPointerModality(e.Pointer.PointerDeviceType);
        if (!MarkdownPointerGesturePolicy.BeginsImmediateSelection(modality))
            return;

        // Only process left (primary) button presses; right-clicks are handled by
        // OnRightTapped and must not affect the multi-click counter or selection anchor.
        var currentPoint = e.Point;
        if (!MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
                modality,
                currentPoint.IsInContact,
                currentPoint.Properties.IsLeftButtonPressed,
                currentPoint.Properties.IsBarrelButtonPressed))
            return;
        var pt = currentPoint.Position;
        if (_selectionHandleCaptureTarget is not null)
            ResetSelectionHandleDrag();
        _touchTapSuppression.Reset();
        _touchSelection.Reset();
        UpdateSelectionHandles();
        TryGetViewport(out double pointerViewportTop, out _, out _);
        _lastSelectionPointerViewportY = pt.Y - pointerViewportTop;
        RememberFocusResumePoint(pt);

        // Pressing renderer chrome (code-copy action) must not create or clear
        // selection; let the native button handle the click.
        if (IsPointOverCodeBlockAction(pt))
        {
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint = default;
            return;
        }

        HideAbbreviationTooltip();

        // Pressing *on* a hosted inline embed must NOT start a selection.
        // The embed is a real WinUI element layered above the canvas — its
        // own pointer-pressed handler must run (Button click, TextBox focus,
        // …).  Returning here without setting _selectionAnchor or capturing
        // the pointer lets XAML's normal pointer routing deliver the event
        // to the embedded element.
        if (IsPointOverEmbed(pt))
        {
            // A press on an embedded control breaks any ongoing double/triple-click
            // sequence; reset timing state so the next text press starts fresh.
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint  = default;
            // Clear any prior selection so the user gets visual feedback that
            // the click isn't a new selection start.
            if (!_selection.Range.IsEmpty)
            {
                _selection.Clear();
                // No _canvas.Invalidate() needed — overlay cleared by Changed handler.
            }
            // Also dismiss keyboard focus ring on click over embed.
            if (_focusedItemIndex >= 0)
            {
                _pendingLazyFocus = null;
                _focusedItemIndex = -1;
                UpdateFocusRing();
            }
            return;
        }

        // Dismiss any keyboard focus ring — mouse clicks and keyboard nav are
        // separate modalities; clicking anywhere in the document should clear
        // the ring so the focus indicator doesn't linger after the user
        // switches from keyboard to mouse.
        if (_focusedItemIndex >= 0)
        {
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            UpdateFocusRing();
        }

        // Track consecutive clicks for word (double) and line (triple) selection.
        long nowMs = e.TickCount;
        double dx = pt.X - _lastPressPoint.X;
        double dy = pt.Y - _lastPressPoint.Y;
        bool sameSpot = dx * dx + dy * dy < 16; // 4px radius
        bool withinTime = (nowMs - _lastPressTickMs) <= _doubleClickTimeMs;
        if (sameSpot && withinTime)
        {
            _consecutiveClickCount++;
            if (_consecutiveClickCount > 3) _consecutiveClickCount = 3; // cap: 4+ repeats = line-select
        }
        else
            _consecutiveClickCount = 1;

        FocusRendererForPointerInteraction();
        // Focus can synchronously re-enter application code, including document
        // replacement or unload. Keep all geometry reads on the snapshot whose
        // interaction lease DispatchPointerInput acquired.
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;

        if (snapshot.HitTest(pt, out var pos))
        {
            // Advance clock/position only on successful text hits so a miss in the
            // same spot doesn't corrupt the double/triple-click timing window.
            _lastPressTickMs = nowMs;
            _lastPressPoint  = pt;
            _pointerSession = new PointerSession(e.Pointer.PointerId, IsPrimary: true);

            if (!IsSelectionEnabled)
            {
                // Selection is disabled but links must still work: capture the pointer
                // so OnPointerReleased fires and can raise LinkClick.
                // Also ensure _clickMode is reset so the link-click guard in
                // OnPointerReleased is not stale from a previous double/triple-click
                // sequence made while IsSelectionEnabled was true.
                _clickMode = ClickMode.Single;
                if (!CapturePointerForInput(_canvas, e)) _pointerSession = default;
                else e.Handled = true;
                return;
            }

            // Always arm the anchor first: this suppresses hover processing
            // in OnPointerMoved during any captured drag (single, double, or triple-click)
            // and prevents a stale anchor from an earlier interaction being reused.
            _selectionAnchor = pos;

            if (_consecutiveClickCount == 3)
            {
                // Triple-click: select the entire block (line).
                _clickMode = ClickMode.Block;
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToBlock(
                    snapshot, pos, snapshotGeneration, layoutRevision);
                if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                    return;
                _selection.SetAnchor(_dragAnchorStart);
                if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                    return;
                _selection.ExtendTo(_dragAnchorEnd);
                if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                    return;
                // Selection is rendered by the XAML overlay; do not dirty canvas
                // text during mouse-down. Repainting DirectWrite text here causes
                // visible shake on selection starts, especially on the embeds page.
                if (!CapturePointerForInput(_canvas, e)) { _pointerSession = default; _selectionAnchor = null; }
                else e.Handled = true;
                return;
            }
            if (_consecutiveClickCount == 2)
            {
                // Double-click: select the word under the cursor.
                _clickMode = ClickMode.Word;
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToWord(
                    snapshot, pos, snapshotGeneration, layoutRevision);
                if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                    return;
                _selection.SetAnchor(_dragAnchorStart);
                if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                    return;
                _selection.ExtendTo(_dragAnchorEnd);
                if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                    return;
                // Selection is rendered by the XAML overlay; do not dirty canvas
                // text during mouse-down.
                if (!CapturePointerForInput(_canvas, e)) { _pointerSession = default; _selectionAnchor = null; }
                else e.Handled = true;
                return;
            }
            _clickMode = ClickMode.Single;
            _selection.SetAnchor(pos);
            if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                return;
            // Selection is rendered by the XAML overlay; do not dirty canvas text
            // for the empty anchor state.
            bool captured = CapturePointerForInput(_canvas, e);
            if (!captured) { _pointerSession = default; _selectionAnchor = null; }
                else e.Handled = true;
            }
        else if (TryGetVectorPointerState(pt, out bool vectorLinked, out _) && vectorLinked)
        {
            // Linked and Selectable are independent scene semantics. A linked-only
            // item must still receive a complete press/release gesture, but it must
            // not become a text-selection anchor.
            _lastPressTickMs = nowMs;
            _lastPressPoint = pt;
            _clickMode = ClickMode.Single;
            _pointerSession = new PointerSession(e.Pointer.PointerId, IsPrimary: true);
            _selectionAnchor = null;
            _selection.Clear();
            if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                return;
            if (!CapturePointerForInput(_canvas, e))
                _pointerSession = default;
            else
                e.Handled = true;
        }
        else
        {
            // HitTest missed (pointer landed on a gap or embed area).
            // Reset click state so this miss isn't counted toward a future
            // double/triple-click; also reset timing so a miss→text sequence
            // never misclassifies as a double-click.
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint  = default;
            _selectionAnchor = null; // defensive: clear any stale anchor from a prior capture loss
            _lastSelectionPointerViewportY = double.NaN;
            _selection.Clear();
            // No canvas invalidate: selection clear is overlay-only.
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_canvas is not null && e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.Tap, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_canvas is not null && e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.DoubleTap, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void ProcessTapped(ref PointerInput e)
    {
        if (e.InteractionSnapshot is not { } snapshot || _canvas is null ||
            e.GestureDevice != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        Point point = e.GesturePosition;
        bool suppressTap = IsSuppressedTouchGesture(e.TouchTapGestureEpoch, point);
        _touchTapSuppression.CompleteTap(e.TouchTapGestureEpoch);
        if (suppressTap)
        {
            e.Handled = true;
            return;
        }

        if (IsPointOverEmbed(point) || IsPointOverCodeBlockAction(point))
            return;

        if (_selection.IsActive && IsPointInsideSelection(snapshot, point))
        {
            e.ShowContextMenuAfterInteraction = true;
            e.TouchSelectionContextMenu = true;
            e.Handled = true;
            return;
        }

        if (_selection.IsActive)
            e.ClearSelectionAfterInteraction = true;

        // Tapped is raised only after WinUI's gesture recognizer has ruled out
        // manipulation. Unlike PointerPressed, this lets an ancestor
        // ScrollViewer retain vertical-pan ownership without a speculative
        // renderer capture.
        RememberFocusResumePoint(point);
        if (TryCreateLinkActivationIntent(
            snapshot,
            point,
            MarkdownLinkInputKind.Touch,
            e.LinkModifiers,
            out LinkActivationIntent intent))
        {
            e.SetLinkActivation(intent);
            e.Handled = true;
        }
    }

    private void ProcessDoubleTapped(ref PointerInput e)
    {
        if (_snapshot is not { } snapshot || _canvas is null ||
            e.GestureDevice != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }
        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        long layoutRevision = snapshot.LayoutRevision;

        Point point = e.GesturePosition;
        if (IsPointOverEmbed(point) || IsPointOverCodeBlockAction(point))
            return;

        ClearHorizontalOverflowPointerState();
        e.ResumeRebuildAfterInteraction = true;
        EndCapturedSelectionForContextGesture(ref e);
        if (IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) &&
            TrySelectWordForTouchContext(
                point,
                snapshot,
                snapshotGeneration,
                layoutRevision,
                e.HasGestureContact ? e.GestureContact : null))
        {
            SuppressTouchTap(point, e.TouchTapGestureEpoch);
            e.Handled = true;
        }
    }

    private void OnHolding(object sender, HoldingRoutedEventArgs e)
    {
        if (_canvas is not null && e.HoldingState == Microsoft.UI.Input.HoldingState.Started &&
            e.PointerDeviceType is Microsoft.UI.Input.PointerDeviceType.Touch or Microsoft.UI.Input.PointerDeviceType.Pen)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.Hold, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void ProcessHolding(ref PointerInput e)
    {
        if (_snapshot is not { } snapshot || _canvas is null ||
            e.GestureDevice is not (
                Microsoft.UI.Input.PointerDeviceType.Touch or
                Microsoft.UI.Input.PointerDeviceType.Pen))
        {
            return;
        }
        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        long layoutRevision = snapshot.LayoutRevision;

        Point point = e.GesturePosition;
        if (IsPointOverEmbed(point) || IsPointOverCodeBlockAction(point))
            return;

        // A stationary hold has established selection/context intent. Clear a
        // still-pending block-local pan and end any pen-selection capture: the
        // flyout and explicit selection handles own subsequent interaction.
        ClearHorizontalOverflowPointerState();
        e.ResumeRebuildAfterInteraction = true;
        EndCapturedSelectionForContextGesture(ref e);

        // A hold inside a committed selection is a context gesture for
        // that selection, as a tap or context request inside it already is.
        // Keep the selection and its handles; the release opens its commands.
        if (_selection.IsActive && IsPointInsideSelection(snapshot, point))
        {
            if (IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            {
                _touchSelection.Select();
                UpdateSelectionOverlay();
                SuppressTouchTap(point, e.TouchTapGestureEpoch);
                e.Handled = true;
            }
            return;
        }

        if (IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) &&
            TrySelectWordForTouchContext(
                point,
                snapshot,
                snapshotGeneration,
                layoutRevision,
                e.HasGestureContact ? e.GestureContact : null))
        {
            SuppressTouchTap(point, e.TouchTapGestureEpoch);
            e.Handled = true;
        }
    }

    private void EndCapturedSelectionForContextGesture(ref PointerInput input)
    {
        if (input.ContextCaptureEnded)
            return;
        input.ContextCaptureEnded = true;

        if (!_pointerSession.IsActive || _canvas is null)
            return;

        // Retire the live logical session under the snapshot lease, but defer
        // the native/XAML release: WinUI can synchronously route CaptureLost.
        uint pointerId = _pointerSession.PointerId;
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        SetSelectionDragShieldActive(false);
        input.InteractionCanvas = _canvas;
        input.ReleasePointerIdAfterInteraction = pointerId;
        input.ReleaseCanvasCaptureAfterInteraction = pointerId != 0;
    }

    private bool TrySelectWordForTouchContext(
        Point point,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        TouchContactGeometry? capturedContact = null)
    {
        if (!IsSelectionEnabled ||
            !IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) ||
            !TryHitTestTouchSelection(point, snapshot, capturedContact, out DocumentPosition position))
        {
            return false;
        }

        FocusRendererForPointerInteraction();
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return false;

        _consecutiveClickCount = 0;
        _lastPressTickMs = 0;
        _lastPressPoint = default;
        _selectionAnchor = null;
        _clickMode = ClickMode.Word;
        (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToWord(
            snapshot, position, snapshotGeneration, layoutRevision);
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return false;

        if (_selection.IsActive)
            _touchSelection.Select();
        UpdateSelectionOverlay();
        return IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) && _selection.IsActive;
    }

    private bool TryHitTestTouchSelection(
        Point gesturePoint,
        LayoutSnapshot snapshot,
        TouchContactGeometry? capturedContact,
        out DocumentPosition position)
    {
        position = default;
        if (IsSelectableTouchPoint(gesturePoint, snapshot, out position))
            return true;

        if (capturedContact is not { } contact ||
            !IsBoundedGestureContact(gesturePoint, contact))
            return false;

        double left = contact.X;
        double top = contact.Y;
        double right = contact.X + contact.Width;
        double bottom = contact.Y + contact.Height;
        Point center = new(contact.CenterX, contact.CenterY);
        double insetX = Math.Min(4, contact.Width / 4);
        double insetY = Math.Min(4, contact.Height / 4);
        return IsSelectableTouchPoint(center, snapshot, out position) ||
               IsSelectableTouchPoint(new Point(left + insetX, center.Y), snapshot, out position) ||
               IsSelectableTouchPoint(new Point(right - insetX, center.Y), snapshot, out position) ||
               IsSelectableTouchPoint(new Point(center.X, top + insetY), snapshot, out position) ||
               IsSelectableTouchPoint(new Point(center.X, bottom - insetY), snapshot, out position);
    }

    private bool IsSelectableTouchPoint(Point point, LayoutSnapshot snapshot, out DocumentPosition position)
    {
        position = default;
        return !IsPointOverEmbed(point) &&
               !IsPointOverCodeBlockAction(point) &&
               snapshot.HitTest(point, out position);
    }

    private bool IsPointInsideSelection(LayoutSnapshot snapshot, Point point)
    {
        TryGetViewport(out double top, out double height, out double width);
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;

        return _selection.ContainsPointUnderReadLease(
            snapshot,
            point,
            new Rect(0, top, width, height),
            scale);
    }

    private void SuppressTouchTap(Point point, long gestureEpoch)
    {
        _touchTapSuppression.SuppressTouchTap(
            gestureEpoch,
            point,
            Environment.TickCount64);
    }

    private bool IsSuppressedTouchGesture(long gestureEpoch, Point point)
        => _touchTapSuppression.ShouldSuppressTap(
            gestureEpoch,
            Environment.TickCount64,
            point);

    private void FocusRendererForPointerInteraction()
    {
        _suppressNextFocusPromotion = true;
        bool focused = Focus(FocusState.Programmatic);
        if (!focused)
        {
            _suppressNextFocusPromotion = false;
            return;
        }

        DispatcherQueue.TryEnqueue(() => _suppressNextFocusPromotion = false);
    }

    private InlineRun? _lastHoveredRun;
    private Layout.Boxes.InlineContainerBox? _lastHoveredBox; // box that contains _lastHoveredRun; used for targeted canvas invalidation
    private AbbreviationRun? _lastHoveredAbbreviation;
    private ToolTip? _abbreviationToolTip;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _abbreviationTooltipShowTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _abbreviationTooltipHideTimer;
    private AbbreviationRun? _pendingAbbreviationTooltipRun;
    private Rect _pendingAbbreviationTooltipPlacementRect;
    private static readonly TimeSpan AbbreviationTooltipShowDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AbbreviationTooltipHideDelay = TimeSpan.FromMilliseconds(200);
    // Tracks the ProtectedCursor shape we last set, or null when we have
    // reset to the system default.  Three states:
    //   null            → ProtectedCursor was reset; system default (Arrow) shows.
    //                     Occurs when pointer exits the canvas or is over an embed.
    //   IBeam           → pointer is over selectable text (not a link).
    //   Hand            → pointer is over a link run.
    // We only call ProtectedCursor setter when the desired state changes, but
    // the "null" state is critical: setting ProtectedCursor = null on *this*
    // UserControl means child elements (embeds) can use their own cursors
    // without a parent IBeam overriding them.
    private Microsoft.UI.Input.InputSystemCursorShape? _currentCursorShape;

    private void ProcessPointerMoved(ref PointerInput e)
    {
        if (_snapshot is null || _canvas is null) return;
        var pt = e.Point.Position;

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            Rect contact = e.Point.Properties.ContactRect;
            _touchSelection.UpdateContact(
                e.Pointer.PointerId,
                new TouchContactGeometry(contact.X, contact.Y, contact.Width, contact.Height));
        }

        if (HandleHorizontalOverflowPointerMoved(ref e, pt))
            return;

        // Drag-select.
        if (_selectionAnchor is not null)
        {
            if (!IsActivePointerSessionEvent(e))
                return;

            e.Handled = true;

            HideAbbreviationTooltip();
            var dragPoint = PrepareSelectionDragPoint(pt);
            // Atomic embed inclusion: when the pointer is inside an inline
            // embed rect during a drag, snap the position to either the
            // start or end of the InlineEmbedRun (whichever side the pointer
            // is closer to).  This treats the embed as a single, indivisible
            // unit of selectable content — the user can never have a
            // selection that ends *halfway through* an embedded button or
            // textbox, which matches how browsers handle <input> /
            // <textarea> inside contenteditable text.
            if (TryHitTestEmbed(dragPoint, out var embedPos))
            {
                _selection.ExtendTo(embedPos);
                // No _canvas.Invalidate(): selection is on the XAML overlay.
            }
            else if (_snapshot.HitTestSelectionEndpoint(dragPoint, out var pos))
            {
                // For word/block click modes extend selection snapped to the
                // appropriate boundary so dragging after double/triple-click
                // produces word-by-word or block-by-block selection, matching
                // browser / native text editor behaviour.
                if (_clickMode == ClickMode.Word)
                {
                    var icb = FindInlineContainerAt(_snapshot, pos.BlockIndex);
                    if (icb is not null)
                    {
                        var (ws, we) = icb.GetWordBoundaries(pos);
                        // Backward drag: anchor at initial-word end, extend to current-word start.
                        // Forward drag: anchor at initial-word start, extend to current-word end.
                        if (pos.CompareTo(_dragAnchorStart) < 0)
                        { _selection.SetAnchor(_dragAnchorEnd); _selection.ExtendTo(ws); }
                        else
                        { _selection.SetAnchor(_dragAnchorStart); _selection.ExtendTo(we); }
                    }
                    else
                        _selection.ExtendTo(pos); // no ICB (code block/embed) — fall back to char selection
                }
                else if (_clickMode == ClickMode.Block)
                {
                    var icb = FindInlineContainerAt(_snapshot, pos.BlockIndex);
                    if (icb is not null)
                    {
                        var (bs, be) = icb.GetBlockBoundaries();
                        if (pos.CompareTo(_dragAnchorStart) < 0)
                        { _selection.SetAnchor(_dragAnchorEnd); _selection.ExtendTo(bs); }
                        else
                        { _selection.SetAnchor(_dragAnchorStart); _selection.ExtendTo(be); }
                    }
                    else
                        _selection.ExtendTo(pos); // no ICB — fall back to char selection
                }
                else
                    _selection.ExtendTo(pos);
                // No _canvas.Invalidate(): selection is on the XAML overlay.
            }
            // Do NOT update hover state during an active selection drag.
            // Calling SetColor on the CanvasTextLayout (which ApplyHoverColor
            // does whenever HoveredRun changes) causes DirectWrite to
            // invalidate cached glyph metrics, which in turn produces
            // sub-pixel vertical jitter ("selection shake") whenever the
            // pointer crosses run boundaries — most visibly on body text
            // that contains links, because every time the pointer moves
            // between the link run and the surrounding body run the
            // hovered-run identity flips.  Suppressing the toggle here
            // costs nothing because the IBeam cursor and link-hover color
            // aren't relevant while the user is mid-drag selecting.
            return;
        }

        // Touch contacts should pan the containing ScrollViewer. Treating touch
        // like a mouse press can capture the pointer, start text selection, and
        // drive the Win2D selection overlay at touch-scroll frequency.
        if (!IsPrecisePointer(e))
        {
            HideAbbreviationTooltip();
            SetCursorShape(null);
            return;
        }

        // Pointer hovering an inline embed: do nothing.  The embed is its
        // own pointer-event target and its ProtectedCursor (Hand / IBeam /
        // arrow / …) takes effect via XAML's normal pointer routing.  We
        // reset our own ProtectedCursor to null so child elements are not
        // overridden by an IBeam that this UserControl last set.  null
        // means "no cursor override" — XAML walks up the tree and finds
        // nothing, so the system default (Arrow) applies, allowing each
        // embed to set its own cursor if desired.
        if (IsPointOverEmbed(pt))
        {
            HideAbbreviationTooltip();
            // Clear our own link-hover so when the pointer leaves the embed
            // the previous hovered link doesn't appear stuck-on.
            if (_lastHoveredRun is not null)
            {
                // Only the LinkRun case actually mutates visible state (link
                // foreground color); plain TextRun hover changes nothing.
                // Issuing a _canvas.Invalidate for a TextRun→null transition
                // causes a partial tile repaint, and CanvasVirtualControl
                // partial-tile repaints reveal DirectWrite glyph-position
                // variance at tile boundaries → visible text shake.
                bool wasLink = _lastHoveredRun is LinkRun;
                var boxToInvalidate = _lastHoveredBox;
                foreach (var b in _snapshot.Blocks) ClearHover(b);
                _lastHoveredRun = null;
                _lastHoveredBox = null;
                // No canvas invalidate: hover transitions no longer mutate
                // the text layout, so the link→null transition has no painted
                // representation either.
                _ = wasLink; _ = boxToInvalidate;
                if (wasLink)
                    InvalidateInteractiveTextAdorner();
            }
            SetCursorShape(null);
            return;
        }

        // Hover effect for links + cursor change.
        // Only respond to transitions that *actually affect rendering or the
        // cursor shape*.  Earlier this method invalidated the canvas and
        // re-set ProtectedCursor on every transition between any two runs
        // (including TextRun → TextRun within the same paragraph as the
        // pointer moved character-by-character).  That produced visible
        // text "shake" on plain hover because ProtectedCursor reassignment
        // and a full Canvas.Invalidate per pointer-move event nudges the
        // visual tree by a sub-pixel.  Now we only react when the link
        // we're hovering changes (which mutates run color → must repaint)
        // or when the cursor shape needs to flip Hand↔IBeam.
        InlineRun? hovered = null;
        Layout.Boxes.InlineContainerBox? hoveredBox = null;
        Point hoveredPhysicalPoint = pt;
        foreach (var b in _snapshot.Blocks)
        {
            Point physicalPoint = e.InteractionSnapshot is { } interactionSnapshot
                ? interactionSnapshot.ToPhysicalPointForTopLevelBlockUnderPaint(b, pt)
                : pt;
            using LayoutPlacementTransformContext.Scope placementScope =
                e.InteractionSnapshot?.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(b) ?? default;
            if (FindInlineHover(b, physicalPoint) is var h && h.Run is not null)
            {
                hovered = h.Run;
                hoveredBox = h.Box;
                hoveredPhysicalPoint = physicalPoint;
                break;
            }
        }

        if (IsLinkedRun(hovered) && hoveredBox is { } linkedBox)
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                e.InteractionSnapshot?.EnterPlacementTransformScopeForBlockIndexUnderPaint(linkedBox.BlockIndex) ?? default;
            if (linkedBox.IsPointInsideRunBounds(hovered!, hoveredPhysicalPoint) != true)
            {
                hovered = null;
                hoveredBox = null;
            }
        }

        var hoveredLinkedRun = IsLinkedRun(hovered) ? hovered : null;
        var lastLinkedRun = IsLinkedRun(_lastHoveredRun) ? _lastHoveredRun : null;
        bool linkChanged = !ReferenceEquals(hoveredLinkedRun, lastLinkedRun);
        bool overVectorScene = TryGetVectorPointerState(
            pt,
            out bool hoveredVectorLink,
            out bool hoveredVectorSelectable);
        var wantedShape = hoveredLinkedRun is not null || hoveredVectorLink
            ? Microsoft.UI.Input.InputSystemCursorShape.Hand
            : IsSelectionEnabled && (!overVectorScene || hoveredVectorSelectable)
                ? Microsoft.UI.Input.InputSystemCursorShape.IBeam
                : (Microsoft.UI.Input.InputSystemCursorShape?)null; // Arrow when selection is off

        if (linkChanged)
        {
            // Update hover bookkeeping for click routing, but do NOT invalidate
            // the canvas — hover causes no visual change to the text any more
            // (see InlineContainerBox.HoveredRun docs).  Eliminating these
            // partial-region invalidates is what finally killed the long-
            // standing text-shake bug: even with grayscale AA + device-pixel
            // origin snapping, repainting a partial canvas region forced
            // DirectWrite to re-tile glyphs at sub-pixel-different positions.
            foreach (var b in _snapshot.Blocks) ClearHover(b);
            if (hoveredBox is not null && hoveredLinkedRun is LinkRun hoveredLink)
                hoveredBox.HoveredRun = hoveredLink;
            InvalidateInteractiveTextAdorner();
        }
        _lastHoveredRun = hovered;
        _lastHoveredBox = hoveredBox;

        UpdateAbbreviationTooltip(hovered, hoveredBox, pt);
        SetCursorShape(wantedShape);
    }

    private void UpdateAbbreviationTooltip(
        InlineRun? hoveredRun,
        Layout.Boxes.InlineContainerBox? hoveredBox,
        Point pointerPoint)
    {
        var abbreviation = hoveredRun as AbbreviationRun;
        if (abbreviation is null ||
            string.IsNullOrWhiteSpace(abbreviation.Expansion) ||
            _canvas is null ||
            hoveredBox is null ||
            !hoveredBox.TryGetRunBounds(abbreviation, pointerPoint, out var placementRect))
        {
            ScheduleAbbreviationTooltipHide();
            return;
        }

        CancelAbbreviationTooltipHide();
        if (ReferenceEquals(abbreviation, _lastHoveredAbbreviation))
        {
            CancelAbbreviationTooltipShow();
            return;
        }

        if (ReferenceEquals(abbreviation, _pendingAbbreviationTooltipRun))
        {
            _pendingAbbreviationTooltipPlacementRect = placementRect;
            return;
        }

        if (_lastHoveredAbbreviation is not null)
            HideAbbreviationTooltip();

        ScheduleAbbreviationTooltipShow(abbreviation, placementRect);
    }

    private void ScheduleAbbreviationTooltipShow(AbbreviationRun abbreviation, Rect placementRect)
    {
        CancelAbbreviationTooltipHide();
        _pendingAbbreviationTooltipRun = abbreviation;
        _pendingAbbreviationTooltipPlacementRect = placementRect;

        var timer = EnsureAbbreviationTooltipShowTimer();
        if (timer is null)
        {
            ShowAbbreviationTooltip(abbreviation, placementRect);
            return;
        }

        timer.Stop();
        timer.Interval = AbbreviationTooltipShowDelay;
        timer.Start();
    }

    private void ShowPendingAbbreviationTooltip()
    {
        _abbreviationTooltipShowTimer?.Stop();
        var abbreviation = _pendingAbbreviationTooltipRun;
        var placementRect = _pendingAbbreviationTooltipPlacementRect;
        _pendingAbbreviationTooltipRun = null;

        if (_isUnloaded ||
            abbreviation is null ||
            !ReferenceEquals(abbreviation, _lastHoveredRun as AbbreviationRun))
        {
            return;
        }

        ShowAbbreviationTooltip(abbreviation, placementRect);
    }

    private void ShowAbbreviationTooltip(AbbreviationRun abbreviation, Rect placementRect)
    {
        if (_isUnloaded || _canvas is null || string.IsNullOrWhiteSpace(abbreviation.Expansion))
            return;

        CloseAbbreviationTooltip();
        _lastHoveredAbbreviation = abbreviation;
        _abbreviationToolTip = new ToolTip
        {
            Content = abbreviation.Expansion,
            IsHitTestVisible = false,
            Placement = PlacementMode.Top,
            PlacementTarget = _canvas,
            PlacementRect = placementRect,
            VerticalOffset = -4,
        };
        ToolTipService.SetToolTip(_canvas, _abbreviationToolTip);
        _abbreviationToolTip.IsOpen = true;
    }

    private void ScheduleAbbreviationTooltipHide()
    {
        CancelAbbreviationTooltipShow();
        _pendingAbbreviationTooltipRun = null;

        if (_abbreviationToolTip is null)
        {
            _lastHoveredAbbreviation = null;
            return;
        }

        var timer = EnsureAbbreviationTooltipHideTimer();
        if (timer is null)
        {
            HideAbbreviationTooltip();
            return;
        }

        timer.Stop();
        timer.Interval = AbbreviationTooltipHideDelay;
        timer.Start();
    }

    private void HideAbbreviationTooltip()
    {
        CancelAbbreviationTooltipShow();
        CancelAbbreviationTooltipHide();
        _pendingAbbreviationTooltipRun = null;
        CloseAbbreviationTooltip();
        _lastHoveredAbbreviation = null;
    }

    private void CloseAbbreviationTooltip()
    {
        if (_abbreviationToolTip is not null)
        {
            try { _abbreviationToolTip.IsOpen = false; } catch { }
            if (_canvas is not null)
                ToolTipService.SetToolTip(_canvas, null);
            _abbreviationToolTip = null;
        }
    }

    private void CancelAbbreviationTooltipShow()
    {
        _abbreviationTooltipShowTimer?.Stop();
    }

    private void CancelAbbreviationTooltipHide()
    {
        _abbreviationTooltipHideTimer?.Stop();
    }

    private void ReleaseAbbreviationTooltipTimers()
    {
        _abbreviationTooltipShowTimer?.Stop();
        _abbreviationTooltipHideTimer?.Stop();
        _abbreviationTooltipShowTimer = null;
        _abbreviationTooltipHideTimer = null;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? EnsureAbbreviationTooltipShowTimer()
    {
        if (_abbreviationTooltipShowTimer is not null)
            return _abbreviationTooltipShowTimer;

        var dispatcher = DispatcherQueue ?? _canvas?.DispatcherQueue;
        if (dispatcher is null)
            return null;

        _abbreviationTooltipShowTimer = dispatcher.CreateTimer();
        _abbreviationTooltipShowTimer.Tick += (_, _) => ShowPendingAbbreviationTooltip();
        return _abbreviationTooltipShowTimer;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? EnsureAbbreviationTooltipHideTimer()
    {
        if (_abbreviationTooltipHideTimer is not null)
            return _abbreviationTooltipHideTimer;

        var dispatcher = DispatcherQueue ?? _canvas?.DispatcherQueue;
        if (dispatcher is null)
            return null;

        _abbreviationTooltipHideTimer = dispatcher.CreateTimer();
        _abbreviationTooltipHideTimer.Tick += (_, _) => HideAbbreviationTooltip();
        return _abbreviationTooltipHideTimer;
    }

    private void InvalidateInteractiveTextAdorner()
    {
        if (_overlay is null)
            return;

        if (_selectionAdorner is null && !_selection.IsActive &&
            _lastHoveredRun is not LinkRun && !TryGetFocusedLink(out _, out _))
            return;

        InvalidateSelectionAdorner();
    }

    private void RefreshInteractiveTextAdornerAfterLayoutChange()
    {
        // Lazy realization and image measurement can move text without replacing
        // the snapshot. Recompute selection geometry before redrawing the
        // viewport-bounded overlay; merely moving the surface would retain stale
        // highlight/text pixels at the old block positions.
        if (_selection.IsActive)
        {
            UpdateSelectionOverlay();
            return;
        }

        UpdateSelectionAdornerViewport();
        InvalidateSelectionAdorner();
    }

    private void InvalidateSelectionAdorner()
    {
        _selectionAdornerVisualDirty = true;
        RequestSelectionAdornerRefresh();
    }

    private void RequestSelectionAdornerRefresh()
    {
        if (_isDisposed || _isUnloaded || !IsLoaded)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        // Device recovery owns its bounded backoff. Keep the geometry/visual
        // dirty flags intact, but do not race the replacement surface every
        // Rendering frame while the recovery timer is waiting.
        if (_canvasDeviceRecoveryQueued)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        _selectionAdornerRetryState.Request();
        _selectionAdornerRequestVersion = unchecked(_selectionAdornerRequestVersion + 1);
        ScheduleSelectionAdornerRetry();
    }

    private void TrySelectionAdornerRefreshAttempt()
    {
        if (_selectionAdornerRefreshInProgress)
        {
            // A synchronous XAML/native callback can re-enter the frame path.
            // Preserve that new request for the next rendering frame instead
            // of nesting a second snapshot lease or letting the outer attempt
            // clear its dirtiness.
            _selectionAdornerRetryState.Request();
            _selectionAdornerRequestVersion = unchecked(_selectionAdornerRequestVersion + 1);
            ScheduleSelectionAdornerRetry();
            return;
        }

        if (_canvasDeviceRecoveryQueued)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        if (!_selectionAdornerRetryState.TryBeginFrame())
            return;

        if (_isDisposed || _isUnloaded || !IsLoaded)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        long requestVersion = _selectionAdornerRequestVersion;
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null)
        {
            CompleteSelectionAdornerRetry();
            return;
        }

        if (snapshot.GeometryRecoveryRequired)
        {
            RequestGeometryRecoveryIfRequired(snapshot);
            CancelSelectionAdornerRetry();
            return;
        }
        if (snapshot.IsRetiringOrDisposed)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        long generation = _snapshotGeneration;
        var operation = new SelectionAdornerRefreshOperation(this, generation);
        SelectionAdornerRefreshResult result;
        _selectionAdornerRefreshInProgress = true;
        try
        {
            result = SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref operation);
        }
        finally
        {
            _selectionAdornerRefreshInProgress = false;
        }

        if (_isDisposed || _isUnloaded || !IsLoaded)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        if (_canvasDeviceRecoveryQueued)
        {
            // The native operation may have discovered device loss and queued
            // the normal backoff while it held the paint lease. Do not let the
            // stale revision created by surface release bypass that backoff.
            CancelSelectionAdornerRetry();
            return;
        }

        if (result != SelectionAdornerRefreshResult.Completed)
        {
            if (result == SelectionAdornerRefreshResult.AwaitLifecycle)
            {
                // Surface attachment/shutdown failures have no useful
                // per-frame retry. Keep dirty state and wait for the next
                // lifecycle/resource request. Restore the dirty state even if
                // a shutdown exception occurred after the visual commit.
                _selectionAdornerGeometryDirty = true;
                _selectionAdornerVisualDirty = true;
                if (requestVersion != _selectionAdornerRequestVersion)
                    ScheduleSelectionAdornerRetry();
                else
                    CancelSelectionAdornerRetry();
            }
            else
            {
                ScheduleSelectionAdornerRetry();
            }
            return;
        }

        if (requestVersion != _selectionAdornerRequestVersion ||
            !ReferenceEquals(snapshot, _snapshot) || generation != _snapshotGeneration ||
            snapshot.LayoutRevision != _selectionAdornerLayoutRevision)
        {
            _selectionAdornerGeometryDirty = true;
            _selectionAdornerVisualDirty = true;
            _selectionAdornerRetryState.Request();
            ScheduleSelectionAdornerRetry();
            return;
        }

        CompleteSelectionAdornerRetry();
    }

    private void OnSelectionAdornerRetryFrame(object? sender, object args)
    {
        if (_isDisposed || _isUnloaded || !IsLoaded)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        var performance = MarkdownPerformanceEventSource.Log;
        bool hasPendingWork =
            _selectionAdornerRetryState.IsPending || _selectionAdornerRefreshInProgress;
        bool measure = hasPendingWork &&
            !_canvasDeviceRecoveryQueued &&
            performance.IsMeasurementEnabled();
        // The harness changes this thread-static ID at its Rendering sample;
        // it labels the interval currently open. Work before that sample uses
        // the prior ID, while work after it uses the new ID, in either handler
        // subscription order.
        long logicalFrameId = measure
            ? MarkdownPerformanceEventSource.GetLogicalFrameId()
            : 0;
        measure = logicalFrameId > 0;
        if (!measure)
        {
            // Keep idle, uncorrelated, and device-recovery callbacks on the
            // normal path without adding timing/allocation probes to them.
            TrySelectionAdornerRefreshAttempt();
            return;
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        try
        {
            TrySelectionAdornerRefreshAttempt();
        }
        finally
        {
            performance.PaintWork(
                logicalFrameId,
                Stopwatch.GetTimestamp() - started,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
        }
    }

    private void ScheduleSelectionAdornerRetry()
    {
        if (_isDisposed || _isUnloaded || !IsLoaded)
        {
            CancelSelectionAdornerRetry();
            return;
        }

        _selectionAdornerRetryState.Retry();
        if (_selectionAdornerRetryFrameSubscribed)
            return;

        _selectionAdornerRetryFrameHandler ??= OnSelectionAdornerRetryFrame;
        CompositionTarget.Rendering += _selectionAdornerRetryFrameHandler;
        _selectionAdornerRetryFrameSubscribed = true;
    }

    private void CompleteSelectionAdornerRetry()
    {
        _selectionAdornerRetryState.Complete();
        UnsubscribeSelectionAdornerRetryFrame();
    }

    private void CancelSelectionAdornerRetry()
    {
        _selectionAdornerRetryState.Cancel();
        UnsubscribeSelectionAdornerRetryFrame();
    }

    private void UnsubscribeSelectionAdornerRetryFrame()
    {
        if (!_selectionAdornerRetryFrameSubscribed)
            return;

        if (_selectionAdornerRetryFrameHandler is { } handler)
            CompositionTarget.Rendering -= handler;
        _selectionAdornerRetryFrameSubscribed = false;
    }

    private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(
        LayoutSnapshot snapshot,
        long generation)
    {
        if (!ReferenceEquals(snapshot, _snapshot) || generation != _snapshotGeneration ||
            snapshot.GeometryRecoveryRequired || snapshot.IsRetiringOrDisposed)
        {
            return SelectionAdornerRefreshOperationResult.Retry;
        }

        bool hasViewport = TryGetViewport(out double top, out double height, out double width);
        if (!hasViewport && (!double.IsFinite(height) || !double.IsFinite(width)))
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;

        if (!double.IsFinite(top)) top = 0;
        if (!double.IsFinite(height) || height <= 0) height = Math.Max(ActualHeight, 1);
        if (!double.IsFinite(width) || width <= 0) width = Math.Max(ActualWidth, 1);

        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;

        long layoutRevision = snapshot.LayoutRevision;
        long requestVersion = _selectionAdornerRequestVersion;
        bool viewportChanged = generation != _selectionAdornerSnapshotGeneration ||
            layoutRevision != _selectionAdornerLayoutRevision ||
            Math.Abs(top - _selectionAdornerViewportTop) > 0.01 ||
            Math.Abs(width - _selectionAdornerViewportWidth) > 0.5 ||
            Math.Abs(height - _selectionAdornerViewportHeight) > 0.5 ||
            Math.Abs(scale - _selectionAdornerRasterizationScale) > 0.001;
        bool rebuildGeometry = _selectionAdornerGeometryDirty || viewportChanged;
        if (!_selectionAdornerVisualDirty && !rebuildGeometry)
            return SelectionAdornerRefreshOperationResult.Completed;

        Rect viewport = new(0, top, width, height);
        List<Rect> paintRects = _selectionAdornerRects;
        List<SelectionBackgroundGeometry> paintBackgrounds = _selectionAdornerBackgrounds;
        if (rebuildGeometry)
        {
            paintRects = _selectionAdornerRectScratch;
            paintRects.Clear();
            paintBackgrounds = _selectionAdornerBackgroundScratch;
            paintBackgrounds.Clear();
            if (_selection.IsActive)
            {
                _selection.AppendVisibleHighlightRects(
                    snapshot,
                    viewport,
                    paintRects,
                    paintBackgrounds);
            }
            SnapSelectionAdornerRectangles(paintRects, scale);
            SnapSelectionAdornerBackgroundRectangles(paintBackgrounds, scale);
        }

        bool hasContent = paintRects.Count > 0 ||
            (_lastHoveredRun is LinkRun && _lastHoveredBox is not null) ||
            TryGetFocusedLink(out _, out _);
        if (!hasContent)
        {
            if (rebuildGeometry)
                CommitSelectionAdornerGeometry(paintRects, paintBackgrounds);
            if (_selectionAdorner is not null)
                _selectionAdorner.Visibility = Visibility.Collapsed;
            CommitSelectionAdornerViewport(generation, layoutRevision, top, width, height, scale);
            UpdateSelectionHandlesUnderPaint(snapshot, new Rect(0, top, width, height));
            return SelectionAdornerRefreshOperationResult.Completed;
        }

        if (!EnsureSelectionAdorner())
        {
            UpdateSelectionHandlesUnderPaint(snapshot, viewport);
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
        }

        _selectionAdornerSurfaces.AdvanceFrame();
        SelectionAdornerCompositionSurface? source = null;
        try
        {
            // Snapshot bitmaps belong to the device that built the snapshot.
            // After device loss the shared device is replaced; never draw the
            // old snapshot into a surface created on the new device.
            if (!ReferenceEquals(_snapshotLayoutDevice, CanvasDevice.GetSharedDevice()))
            {
                RequestSelectionAdornerDeviceRebuild();
                UpdateSelectionHandlesUnderPaint(snapshot, viewport);
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            }

            // Surface creation and attachment can report device loss too, so
            // they share the guarded region with drawing.
            if (!ApplySelectionAdornerViewportUnderPaint(width, height, top, out source))
            {
                UpdateSelectionHandlesUnderPaint(snapshot, viewport);
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            }

            long surfaceVersion = _selectionAdornerSurfaces.Generation;
            // EndDraw can fail. Do not publish clean geometry or mutate XAML
            // handles until the explicitly owned drawing session has closed.
            using (var drawingSession = CanvasComposition.CreateDrawingSession(source.Surface, source.PixelBounds, source.Dpi))
            {
                drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
                drawingSession.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
                drawingSession.Transform = Matrix3x2.CreateTranslation(0, -(float)_selectionAdornerOffsetY);
                PaintInteractiveDocumentState(
                    snapshot,
                    drawingSession,
                    viewport,
                    paintBackgrounds);
            }

            if (_isDisposed || _isUnloaded || !IsLoaded)
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            if (surfaceVersion != _selectionAdornerSurfaces.Generation ||
                !ReferenceEquals(source, _selectionAdornerSurfaces.Surface) ||
                requestVersion != _selectionAdornerRequestVersion ||
                !ReferenceEquals(snapshot, _snapshot) || generation != _snapshotGeneration ||
                snapshot.LayoutRevision != layoutRevision)
            {
                return SelectionAdornerRefreshOperationResult.Retry;
            }

            if (rebuildGeometry)
                CommitSelectionAdornerGeometry(paintRects, paintBackgrounds);
            CommitSelectionAdornerViewport(generation, layoutRevision, top, width, height, scale);
            UpdateSelectionHandlesUnderPaint(snapshot, new Rect(0, top, width, height));
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            // HandleCanvasDeviceLost releases the surface under its own guard
            // and always schedules recovery.
            HandleCanvasDeviceLost(ex);
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
        }
        catch (Exception ex) when (ShouldIgnoreShutdownException(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored selection invalidate during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
        }
        finally
        {
            // A reentrant lifecycle callback may detach the field while the
            // drawing session is unwinding. Its managed owner outlives EndDraw.
            GC.KeepAlive(source);
        }

        return SelectionAdornerRefreshOperationResult.Completed;
    }

    private void SnapSelectionAdornerRectangles(List<Rect> rectangles, double scale)
    {
        for (int index = 0; index < rectangles.Count; index++)
        {
            Rect rect = rectangles[index];
            Rect snapped = SelectionController.SnapToRasterizationScale(rect, scale);
            if (snapped.Width <= 0 || snapped.Height <= 0)
            {
                rectangles.RemoveAt(index--);
                continue;
            }

            rectangles[index] = snapped;
        }
    }

    private void CommitSelectionAdornerGeometry(
        List<Rect> stagedRectangles,
        List<SelectionBackgroundGeometry> stagedBackgrounds)
    {
        if (ReferenceEquals(stagedRectangles, _selectionAdornerRects))
            return;

        List<Rect> previousRectangles = _selectionAdornerRects;
        _selectionAdornerRects = stagedRectangles;
        _selectionAdornerRectScratch = previousRectangles;
        previousRectangles.Clear();

        List<SelectionBackgroundGeometry> previousBackgrounds = _selectionAdornerBackgrounds;
        _selectionAdornerBackgrounds = stagedBackgrounds;
        _selectionAdornerBackgroundScratch = previousBackgrounds;
        previousBackgrounds.Clear();

        _selectionAdornerRectScratch.Clear();
        _selectionAdornerBackgroundScratch.Clear();
    }

    private static void SnapSelectionAdornerBackgroundRectangles(
        List<SelectionBackgroundGeometry> rectangles,
        double scale)
    {
        for (int index = 0; index < rectangles.Count; index++)
        {
            SelectionBackgroundGeometry background = rectangles[index];
            Rect snapped = SelectionController.SnapToRasterizationScale(background.Bounds, scale);
            if (snapped.Width <= 0 || snapped.Height <= 0)
            {
                rectangles.RemoveAt(index--);
                continue;
            }

            rectangles[index] = background with { Bounds = snapped };
        }
    }

    private void CommitSelectionAdornerViewport(
        long generation,
        long layoutRevision,
        double top,
        double width,
        double height,
        double scale)
    {
        _selectionAdornerSnapshotGeneration = generation;
        _selectionAdornerLayoutRevision = layoutRevision;
        _selectionAdornerViewportTop = top;
        _selectionAdornerViewportWidth = width;
        _selectionAdornerViewportHeight = height;
        _selectionAdornerRasterizationScale = scale;
        _selectionAdornerGeometryDirty = false;
        _selectionAdornerVisualDirty = false;
    }

    private bool ApplySelectionAdornerViewportUnderPaint(
        double width,
        double height,
        double top,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SelectionAdornerCompositionSurface? source)
    {
        source = null;
        if (_selectionAdorner is null)
            return false;

        source = EnsureSelectionAdornerSurface(width, height);
        if (source is null || _selectionAdorner is null)
        {
            source = null;
            if (_selectionAdorner is not null)
                _selectionAdorner.Visibility = Visibility.Collapsed;
            return false;
        }

        // The placeholder and its visual span the whole bucketed surface at 1:1
        // so a reused, larger surface is never stretched; the clips bound it to
        // the viewport.
        SelectionAdornerSurfaceAllocation allocation = _selectionAdornerSurfaces.Allocation;
        _selectionAdornerHost?.SetViewport(allocation, width, height);
        if (double.IsNaN(_selectionAdorner.Width) || Math.Abs(_selectionAdorner.Width - allocation.Width) > 0.01)
            _selectionAdorner.Width = allocation.Width;
        if (double.IsNaN(_selectionAdorner.Height) || Math.Abs(_selectionAdorner.Height - allocation.Height) > 0.01)
            _selectionAdorner.Height = allocation.Height;

        Rect visible = new(0, 0, width, height);
        if (_selectionAdorner.Clip is not RectangleGeometry clip)
        {
            clip = new RectangleGeometry();
            _selectionAdorner.Clip = clip;
        }
        if (clip.Rect != visible)
            clip.Rect = visible;

        double currentLeft = Canvas.GetLeft(_selectionAdorner);
        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft) > 0.1)
            Canvas.SetLeft(_selectionAdorner, 0);

        double currentTop = Canvas.GetTop(_selectionAdorner);
        if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
            Canvas.SetTop(_selectionAdorner, top);

        _selectionAdornerOffsetY = top;
        _selectionAdorner.Visibility = Visibility.Visible;
        return true;
    }

    private readonly struct SelectionAdornerRefreshOperation : ISelectionAdornerRefreshOperation
    {
        private readonly MarkdownRendererControl _owner;
        private readonly long _generation;

        internal SelectionAdornerRefreshOperation(MarkdownRendererControl owner, long generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
            => _owner.RefreshSelectionAdornerUnderPaintLease(snapshot, _generation);
    }

    private void InvalidateCanvas()
    {
        if (_isDisposed || _isUnloaded || !IsLoaded)
            return;

        try
        {
            _canvas?.Invalidate();
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            HandleCanvasDeviceLost(ex);
        }
        catch (Exception ex) when (ShouldIgnoreShutdownException(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored canvas invalidate during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
        }
    }

    private static bool ShouldIgnoreShutdownException(Exception ex)
        => GraphicsDeviceErrors.IsShutdownOrDisposed(ex);

    private Point PrepareSelectionDragPoint(
        Point point,
        bool sustained = false,
        double elapsedSeconds = 0)
    {
        if (_canvas is null)
            return point;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        double pointerViewportY = point.Y - viewportTop;
        double delta = sustained
            ? SelectionAutoScroll.ComputeFrameDelta(
                point.Y,
                viewportTop,
                viewportHeight,
                elapsedSeconds)
            : SelectionAutoScroll.ComputeDirectionalDelta(
                point.Y,
                viewportTop,
                viewportHeight,
                _lastSelectionPointerViewportY);
        _lastSelectionPointerViewportY = pointerViewportY;
        if (Math.Abs(delta) > 0.0001)
        {
            double maxOffset = Math.Max(0, _canvas.ActualHeight - viewportHeight);
            double target = Math.Clamp(viewportTop + delta, 0, maxOffset);
            if (Math.Abs(target - viewportTop) > 0.0001)
            {
                if (_scroll is not null)
                    _scroll.ChangeView(null, target, null, disableAnimation: true);
                else
                    BringDocumentRectIntoExternalViewport(new Rect(0, target, 1, 1), animate: false);
                viewportTop = target;
            }
        }

        double x = Math.Clamp(point.X, 0, Math.Max(0, _canvas.ActualWidth - 1));
        double y = SelectionAutoScroll.ClampPointToViewport(point.Y, viewportTop, viewportHeight);
        return new Point(x, y);
    }

    /// <summary>
    /// Sets the cursor shape to <paramref name="shape"/> if it
    /// differs from the last-set shape, or resets it to <c>null</c> (system
    /// default — Arrow) when <paramref name="shape"/> is <c>null</c>.
    /// Resetting to null is essential when the pointer leaves text areas and
    /// enters overlay embeds: with null, XAML walks up the tree and finds no
    /// cursor override, so child elements (Button, CheckBox, …) can show their
    /// own cursors instead of inheriting IBeam from this UserControl.
    /// Cursors are cached as fields to avoid creating a new IDisposable on every
    /// Hand↔IBeam transition.
    /// </summary>
    private void SetCursorShape(Microsoft.UI.Input.InputSystemCursorShape? shape)
    {
        if (shape == _currentCursorShape) return;
        // Update tracked state first so repeated failures don't cause a per-frame
        // exception storm: if ProtectedCursor throws, we still record the intent
        // and skip the setter on the next move event.
        _currentCursorShape = shape;
        try
        {
            ProtectedCursor = shape switch
            {
                Microsoft.UI.Input.InputSystemCursorShape.Hand =>
                    _cursorHand ??= Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Hand),
                Microsoft.UI.Input.InputSystemCursorShape.IBeam =>
                    _cursorIBeam ??= Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.IBeam),
                _ => null
            };
        }
        catch { /* ProtectedCursor isn't always settable */ }
    }

    private static (Layout.Boxes.InlineContainerBox? Box, InlineRun? Run) FindInlineHover(Layout.BlockBox box, Point pt)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb:
                var r = icb.RunAt(pt);
                return r is not null ? (icb, r) : (null, null);
            case Layout.Boxes.CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks)
                {
                    var c = FindInlineHover(chunk, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case Layout.Boxes.ListItemBox lib:
                var m = FindInlineHover(lib.Marker, pt);
                if (m.Run is not null) return m;
                return FindInlineHover(lib.Content, pt);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    var c = FindInlineHover(cell, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case Layout.Boxes.StackBox sb:
                foreach (var ch in sb.Children)
                {
                    var c = FindInlineHover(ch, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
        }
        return (null, null);
    }

    private static void ClearHover(Layout.BlockBox box)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb:
                icb.HoveredRun = null;
                break;
            case Layout.Boxes.CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks) ClearHover(chunk);
                break;
            case Layout.Boxes.ListItemBox lib:
                ClearHover(lib.Marker);
                ClearHover(lib.Content);
                break;
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) ClearHover(cell);
                break;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children) ClearHover(c);
                break;
        }
    }

    /// <summary>
    /// True when the given point in canvas coordinates is inside the
    /// rectangle of any realised inline or block embed.
    /// </summary>
    private bool IsPointOverEmbed(Point pt)
    {
        if (IsPointOverCodeBlockAction(pt))
            return true;

        for (int i = 0; i < _embedRects.Count; i++)
        {
            var r = _embedRects[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }
        for (int i = 0; i < _blockEmbedRects.Count; i++)
        {
            var r = _blockEmbedRects[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }
        return false;
    }

    private bool IsPointOverCodeBlockAction(Point pt)
    {
        for (int i = 0; i < _codeBlockActionRects.Count; i++)
        {
            var r = _codeBlockActionRects[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }

        return false;
    }

    /// <summary>
    /// If the point is inside an inline embed's rectangle, returns a
    /// DocumentPosition that snaps to the start (left half) or end (right
    /// half) of the embed run — making the embed an atomic, indivisible
    /// unit of selection.  Returns false when the point is not over any
    /// embed.
    /// </summary>
    private bool TryHitTestEmbed(Point pt, out DocumentPosition position)
    {
        for (int i = 0; i < _embedRects.Count; i++)
        {
            var (box, run, r) = _embedRects[i];
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
            {
                bool rightHalf = pt.X >= r.X + r.Width / 2.0;
                // In RTL flow the logical start of a run is visually on the
                // right, so the right half maps to charOffset 0 and the left
                // half maps to charOffset Text.Length. In LTR it's the
                // opposite.
                bool isRtl = FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft;
                int charOffset = isRtl
                    ? (rightHalf ? 0 : run.TextLength)
                    : (rightHalf ? run.TextLength : 0);
                position = new DocumentPosition(box.BlockIndex, run.InlineIndex, charOffset);
                return true;
            }
        }

        for (int i = 0; i < _blockEmbedRects.Count; i++)
        {
            var (box, r) = _blockEmbedRects[i];
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
            {
                bool afterMidpoint = pt.Y >= r.Y + r.Height / 2.0;
                position = new DocumentPosition(box.BlockIndex, 0, afterMidpoint ? 1 : 0);
                return true;
            }
        }

        position = default;
        return false;
    }

    /// <summary>
    /// Syncs the selection adorner geometry. The adorner is a single stable
    /// composition-surface child layered above the document text and below hosted controls.
    /// Selection changes only mutate this in-memory rect list and invalidate
    /// the adorner; they never invalidate the base document canvas or mutate
    /// the XAML child tree during a drag.
    /// </summary>
    private void UpdateSelectionOverlay()
    {
        _selectionAdornerGeometryDirty = true;
        _selectionAdornerVisualDirty = true;
        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return;
        InvalidateSelectionAdorner();
    }

    private bool HasInteractiveTextAdornerContent()
        => (_selection.IsActive && _selectionAdornerRects.Count > 0) ||
           (_lastHoveredRun is LinkRun && _lastHoveredBox is not null) ||
           TryGetFocusedLink(out _, out _);

    private bool ShouldRefreshSelectionAdornerForScroll()
    {
        bool hasHoveredLink = _lastHoveredRun is LinkRun && _lastHoveredBox is not null;
        IReadOnlyList<Layout.FocusableItem>? focusableItems = _focusableItems;
        bool hasFocusedLink = focusableItems is not null &&
            (uint)_focusedItemIndex < (uint)focusableItems.Count &&
            focusableItems[_focusedItemIndex].Kind == Layout.FocusableItemKind.Link;
        bool hasVisibleAdorner = _selectionAdorner?.Visibility == Visibility.Visible;
        bool hasRetainedRectangles = _selectionAdornerRects.Count > 0;
        bool hasPendingRefresh = _selectionAdornerRetryState.IsPending ||
            _selectionAdornerRefreshInProgress;

        return SelectionAdornerScrollRefreshPolicy.ShouldRefreshForScroll(
            hasActiveSelection: _selection.IsActive,
            hasHoveredLink: hasHoveredLink,
            hasFocusedLink: hasFocusedLink,
            hasVisibleAdorner: hasVisibleAdorner,
            hasRetainedRectangles: hasRetainedRectangles,
            hasTouchHandles: _touchSelection.ShouldShowHandles,
            hasPendingRefresh: hasPendingRefresh);
    }

    private Border CreateSelectionAdorner()
    {
        var adorner = new Border
        {
            IsHitTestVisible = false,
            UseLayoutRounding = true,
            // Keep the overlay inert until selection gives it viewport bounds.
            Width = 1,
            Height = 1,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetZIndex(adorner, 0);
        _selectionAdorner = adorner;
        _selectionAdornerHost = new SelectionAdornerVisualHost(adorner);
        return adorner;
    }

    /// <summary>
    /// Returns the long-lived adorner surface, attached to the current adorner
    /// visual. Device-loss and shutdown failures propagate to the guarded
    /// refresh, which owns recovery.
    /// </summary>
    private SelectionAdornerCompositionSurface? EnsureSelectionAdornerSurface(double width, double height)
    {
        SelectionAdornerVisualHost? host = _selectionAdornerHost;
        if (host is null || _selectionAdorner is null || _isDisposed || _isUnloaded ||
            !ReferenceEquals(host.Placeholder, _selectionAdorner))
        {
            return null;
        }

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            rasterizationScale = 1.0;
        float dpi = (float)(96.0 * rasterizationScale);

        SelectionAdornerCompositionSurface? surface = _selectionAdornerSurfaces.Ensure(
            host,
            width,
            height,
            dpi,
            EnsureSelectionAdornerGraphicsDevice(host.Compositor));
        // Attachment can reenter unload or replace the adorner placeholder.
        if (surface is null || _isDisposed || _isUnloaded || !ReferenceEquals(host, _selectionAdornerHost))
            return null;

        return surface;
    }

    /// <summary>
    /// Returns the composition graphics device for the adorner, rendering with
    /// the current shared Win2D device. A replaced Win2D device is switched in
    /// place: surfaces survive with lost content, and every refresh repaints.
    /// </summary>
    private CompositionGraphicsDevice EnsureSelectionAdornerGraphicsDevice(Compositor compositor)
    {
        CanvasDevice canvasDevice = CanvasDevice.GetSharedDevice();
        if (_selectionAdornerGraphicsDevice is { } graphicsDevice &&
            ReferenceEquals(graphicsDevice.Compositor, compositor))
        {
            if (!ReferenceEquals(_selectionAdornerCanvasDevice, canvasDevice))
            {
                CanvasComposition.SetCanvasDevice(graphicsDevice, canvasDevice);
                _selectionAdornerCanvasDevice = canvasDevice;
            }

            return graphicsDevice;
        }

        ReleaseSelectionAdornerGraphicsDevice();
        graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);
        graphicsDevice.RenderingDeviceReplaced += OnSelectionAdornerRenderingDeviceReplaced;
        _selectionAdornerGraphicsDevice = graphicsDevice;
        _selectionAdornerCanvasDevice = canvasDevice;
        return graphicsDevice;
    }

    /// <summary>
    /// A replaced rendering device discards surface content without any
    /// paint failure, so repaint the adorner on the UI thread.
    /// </summary>
    private void OnSelectionAdornerRenderingDeviceReplaced(
        CompositionGraphicsDevice sender,
        RenderingDeviceReplacedEventArgs args)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isDisposed || _isUnloaded || !ReferenceEquals(sender, _selectionAdornerGraphicsDevice))
                return;
            InvalidateSelectionAdornerPresentation();
            InvalidateSelectionAdorner();
        });

    /// <summary>
    /// Disposes the adorner's composition graphics device. The surface owner
    /// recreates its surface for a new device, so this is safe at any time.
    /// </summary>
    private void ReleaseSelectionAdornerGraphicsDevice()
    {
        CompositionGraphicsDevice? graphicsDevice = _selectionAdornerGraphicsDevice;
        _selectionAdornerGraphicsDevice = null;
        _selectionAdornerCanvasDevice = null;
        if (graphicsDevice is null)
            return;
        try
        {
            graphicsDevice.RenderingDeviceReplaced -= OnSelectionAdornerRenderingDeviceReplaced;
            graphicsDevice.Dispose();
        }
        catch (Exception failure)
        {
            ReportSelectionAdornerSurfaceReleaseFailure(failure);
        }
    }

    private static SelectionAdornerCompositionSurface CreateSelectionAdornerSurface(
        CompositionGraphicsDevice device,
        SelectionAdornerSurfaceAllocation allocation)
        => new(device, allocation);

    private static void DisposeSelectionAdornerSurface(SelectionAdornerCompositionSurface surface)
        => surface.Dispose();

    private static void ReportSelectionAdornerSurfaceReleaseFailure(Exception failure)
        => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] selection surface detach failed: {failure.GetType().Name} {GraphicsDeviceErrors.FormatHResult(failure.HResult)}");

    /// <summary>
    /// Marks the adorner for a full repaint without touching its surface.
    /// </summary>
    private void InvalidateSelectionAdornerPresentation()
    {
        CancelSelectionAdornerRetry();
        _selectionAdornerGeometryDirty = true;
        _selectionAdornerVisualDirty = true;
        _selectionAdornerSnapshotGeneration = -1;
        _selectionAdornerLayoutRevision = -1;
        _selectionAdornerViewportTop = double.NaN;
        _selectionAdornerViewportWidth = double.NaN;
        _selectionAdornerViewportHeight = double.NaN;
        _selectionAdornerRasterizationScale = double.NaN;
    }

    /// <summary>
    /// Detaches and retires the adorner surface. Reserved for unload, device
    /// loss and geometry recovery; ordinary snapshot commits only invalidate.
    /// Never throws: a failed XAML detach is reported and the next refresh
    /// rebinds explicitly.
    /// </summary>
    private void ReleaseSelectionAdornerSurface()
    {
        InvalidateSelectionAdornerPresentation();
        _selectionAdornerSurfaces.Release(_selectionAdornerHost);
    }

    private bool TryReleaseSelectionAdornerSurface()
    {
        try
        {
            ReleaseSelectionAdornerSurface();
            return true;
        }
        catch (Exception failure)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] selection surface release failed: {failure.GetType().Name} {GraphicsDeviceErrors.FormatHResult(failure.HResult)}");
            return false;
        }
    }

    private void RemoveOverlayChildrenExceptSelectionAdorner()
    {
        if (_overlay is null)
            return;

        var children = _overlay.Children;
        for (int index = children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(children[index], _selectionAdorner))
                children.RemoveAt(index);
        }
    }

    private void RequestSelectionAdornerDeviceRebuild()
    {
        // Device recovery already rebuilds against the replacement device.
        if (_canvasDeviceRecoveryQueued || _isDisposed || _isUnloaded)
            return;

        MarkdownDiagnostics.WriteLine(
            "[MarkdownRendererControl] snapshot layout device differs from the shared device; rebuilding.");
        RequestRebuild(RebuildReason.Full);
    }

    /// <summary>
    /// One Win2D-drawn composition surface and the brush that presents it.
    /// It has no XAML peer, so its native release never races XAML teardown.
    /// </summary>
    private sealed class SelectionAdornerCompositionSurface : IDisposable
    {
        internal SelectionAdornerCompositionSurface(
            CompositionGraphicsDevice device,
            SelectionAdornerSurfaceAllocation allocation)
        {
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(allocation.Width * allocation.Dpi / 96.0));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(allocation.Height * allocation.Dpi / 96.0));
            Surface = device.CreateDrawingSurface(
                new Size(pixelWidth, pixelHeight),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            try
            {
                Brush = device.Compositor.CreateSurfaceBrush(Surface);
                Brush.Stretch = CompositionStretch.Fill;
            }
            catch
            {
                Surface.Dispose();
                throw;
            }

            PixelBounds = new Rect(0, 0, pixelWidth, pixelHeight);
            Dpi = allocation.Dpi;
        }

        internal CompositionDrawingSurface Surface { get; }
        internal CompositionSurfaceBrush Brush { get; }
        internal Rect PixelBounds { get; }
        internal float Dpi { get; }

        public void Dispose()
        {
            Brush.Dispose();
            Surface.Dispose();
        }
    }

    /// <summary>
    /// Presents the adorner surface through a sprite visual hosted as the
    /// child visual of an inert placeholder element in the overlay.
    /// </summary>
    private sealed class SelectionAdornerVisualHost
        : ISelectionAdornerSurfaceHost<SelectionAdornerCompositionSurface>, IDisposable
    {
        private readonly SpriteVisual _visual;
        private SelectionAdornerCompositionSurface? _source;
        private bool _disposed;

        internal SelectionAdornerVisualHost(Border placeholder)
        {
            Placeholder = placeholder;
            Compositor = ElementCompositionPreview.GetElementVisual(placeholder).Compositor;
            _visual = Compositor.CreateSpriteVisual();
            ElementCompositionPreview.SetElementChildVisual(placeholder, _visual);
        }

        internal Border Placeholder { get; }
        internal Compositor Compositor { get; }

        public SelectionAdornerCompositionSurface? Source
        {
            get => _source;
            set
            {
                if (_disposed)
                    return;
                _visual.Brush = value?.Brush;
                _source = value;
            }
        }

        /// <summary>
        /// Sizes the visual to the whole allocation and clips it to the viewport.
        /// </summary>
        internal void SetViewport(SelectionAdornerSurfaceAllocation allocation, double width, double height)
        {
            if (_disposed)
                return;
            var size = new Vector2(allocation.Width, allocation.Height);
            if (_visual.Size != size)
                _visual.Size = size;
            float right = Math.Max(0f, allocation.Width - (float)width);
            float bottom = Math.Max(0f, allocation.Height - (float)height);
            if (_visual.Clip is InsetClip clip)
            {
                if (clip.RightInset != right)
                    clip.RightInset = right;
                if (clip.BottomInset != bottom)
                    clip.BottomInset = bottom;
            }
            else
            {
                _visual.Clip = Compositor.CreateInsetClip(0, 0, right, bottom);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _source = null;
            _visual.Brush = null;
            ElementCompositionPreview.SetElementChildVisual(Placeholder, null);
            _visual.Clip?.Dispose();
            _visual.Dispose();
        }
    }

    private bool EnsureSelectionAdorner()
    {
        if (_overlay is null)
            return false;

        if (_selectionAdorner is null)
            CreateSelectionAdorner();

        try
        {
            if (VisualTreeHelper.GetParent(_selectionAdorner) is null)
                _overlay.Children.Add(_selectionAdorner);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] selection adorner attach failed: {ex.Message}");
            return false;
        }

        return true;
    }

    private void CreateSelectionHandles()
    {
        if (_overlay is null)
            return;

        _selectionStartHandle = CreateSelectionHandle(SelectionHandleEndpoint.Start);
        _selectionEndHandle = CreateSelectionHandle(SelectionHandleEndpoint.End);
        _overlay.Children.Add(_selectionStartHandle);
        _overlay.Children.Add(_selectionEndHandle);
    }

    private Button CreateSelectionHandle(SelectionHandleEndpoint endpoint)
    {
        var dot = new Ellipse
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var handle = new Button
        {
            Tag = endpoint,
            Content = dot,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // Handles own their drag. Without opting out of the inherited
            // ScrollViewer manipulation contract, DirectManipulation can cancel
            // the Button's pointer stream as soon as the contact starts moving.
            ManipulationMode = ManipulationModes.None,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            Visibility = Visibility.Collapsed,
        };

        string automationId = endpoint == SelectionHandleEndpoint.Start
            ? "MarkdownSelectionStartHandle"
            : "MarkdownSelectionEndHandle";
        AutomationProperties.SetAutomationId(handle, automationId);
        UpdateSelectionHandleLocalization(handle, endpoint);
        handle.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSelectionHandlePointerPressed),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnSelectionHandlePointerMoved),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnSelectionHandlePointerReleased),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled),
            handledEventsToo: true);
        handle.RightTapped += OnSelectionHandleRightTapped;
        handle.KeyDown += OnSelectionHandleKeyDown;
        Canvas.SetZIndex(handle, 5);
        return handle;
    }

    private bool EnsureSelectionHandles()
    {
        if (_overlay is null)
            return false;
        if (_selectionStartHandle is null || _selectionEndHandle is null)
            CreateSelectionHandles();

        bool attached = false;
        try
        {
            if (VisualTreeHelper.GetParent(_selectionStartHandle) is null)
            {
                _overlay.Children.Add(_selectionStartHandle);
                attached = true;
            }
            if (VisualTreeHelper.GetParent(_selectionEndHandle) is null)
            {
                _overlay.Children.Add(_selectionEndHandle);
                attached = true;
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] selection handle attach failed: {ex.Message}");
            return false;
        }

        if (attached)
        {
            UpdateSelectionHandleLocalization(_selectionStartHandle!, SelectionHandleEndpoint.Start);
            UpdateSelectionHandleLocalization(_selectionEndHandle!, SelectionHandleEndpoint.End);
        }
        return true;
    }

    private void UpdateSelectionHandleLocalization(
        Button handle,
        SelectionHandleEndpoint endpoint)
    {
        string name = endpoint == SelectionHandleEndpoint.Start
            ? ResolveLocalizedString(
                MarkdownStringKeys.SelectionStartHandle,
                MarkdownLocalizedStrings.SelectionStartHandle)
            : ResolveLocalizedString(
                MarkdownStringKeys.SelectionEndHandle,
                MarkdownLocalizedStrings.SelectionEndHandle);
        string help = ResolveLocalizedString(
            MarkdownStringKeys.SelectionHandleHelp,
            MarkdownLocalizedStrings.SelectionHandleHelp);
        AutomationProperties.SetName(handle, name);
        AutomationProperties.SetHelpText(handle, help);
        ToolTipService.SetToolTip(handle, name);
    }

    private void UpdateSelectionHandles()
    {
        if (!_touchSelection.ShouldShowHandles || !_selection.IsActive)
        {
            HideUncapturedSelectionHandles();
            _selectionAdornerVisualDirty = true;
            RequestSelectionAdornerRefresh();
            return;
        }

        // Geometry is refreshed only by the frame coordinator while holding
        // the snapshot paint lease. This direct state change merely requests
        // that coalesced frame; it never walks boxes on the input callback.
        _selectionAdornerVisualDirty = true;
        RequestSelectionAdornerRefresh();
    }

    private void UpdateSelectionHandlesUnderPaint(LayoutSnapshot snapshot, Rect viewport)
    {
        if (snapshot.GeometryRecoveryRequired || snapshot.IsRetiringOrDisposed ||
            !_touchSelection.ShouldShowHandles || !_selection.IsActive || !EnsureSelectionHandles())
        {
            HideUncapturedSelectionHandles();
            return;
        }

        double targetSize = Math.Clamp(
            _themeSnapshot?.MinimumInteractiveSize ?? 40,
            44,
            128);
        double dotSize = Math.Clamp(targetSize * 0.36, 12, 20);
        // The hit target keeps one physical pixel of slack over the minimum so
        // its reported size meets the minimum at any sub-pixel alignment.
        double handleExtent = SelectionController.ComputeSelectionHandleExtent(
            targetSize,
            XamlRoot?.RasterizationScale ?? 1.0);
        Color handleColor = _themeSnapshot?.SelectionHighlightColor
            ?? Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        bool startCaptured = IsSelectionHandleCaptured(SelectionHandleEndpoint.Start);
        bool endCaptured = IsSelectionHandleCaptured(SelectionHandleEndpoint.End);
        bool pointerDragActive = IsSelectionHandlePointerDragActive();
        SelectionHandleEndpoint freeLogicalEndpoint = pointerDragActive
            ? _activeSelectionHandle == SelectionHandleEndpoint.Start
                ? SelectionHandleEndpoint.End
                : SelectionHandleEndpoint.Start
            : SelectionHandleEndpoint.None;
        if (!startCaptured)
            UpdateSelectionHandleAppearance(_selectionStartHandle!, handleExtent, dotSize, handleColor);
        if (!endCaptured)
            UpdateSelectionHandleAppearance(_selectionEndHandle!, handleExtent, dotSize, handleColor);

        Point startPoint = _selectionStartHandleEndpoint;
        Point endPoint = _selectionEndHandleEndpoint;
        bool startAvailable = startCaptured || TryGetSelectionHandleEndpointPoint(
            snapshot,
            pointerDragActive ? freeLogicalEndpoint : SelectionHandleEndpoint.Start,
            viewport,
            targetSize / 2,
            out startPoint);
        bool endAvailable = endCaptured || TryGetSelectionHandleEndpointPoint(
            snapshot,
            pointerDragActive ? freeLogicalEndpoint : SelectionHandleEndpoint.End,
            viewport,
            targetSize / 2,
            out endPoint);

        if (startCaptured)
        {
            // Keep the captured Button and its stored endpoint stationary until
            // release. Hiding or moving it during an edge-scroll frame can
            // synthesize PointerCaptureLost and terminate the drag.
            _selectionStartHandleEndpointAvailable = true;
        }
        else if (startAvailable)
        {
            _selectionStartHandleEndpoint = startPoint;
            _selectionStartHandleEndpointAvailable = PlaceSelectionHandle(
                _selectionStartHandle!, startPoint, handleExtent, viewport);
        }
        else
        {
            _selectionStartHandleEndpointAvailable = false;
            _selectionStartHandle!.Visibility = Visibility.Collapsed;
        }

        if (endCaptured)
        {
            _selectionEndHandleEndpointAvailable = true;
        }
        else if (endAvailable)
        {
            _selectionEndHandleEndpoint = endPoint;
            _selectionEndHandleEndpointAvailable = PlaceSelectionHandle(
                _selectionEndHandle!, endPoint, handleExtent, viewport);
        }
        else
        {
            _selectionEndHandleEndpointAvailable = false;
            _selectionEndHandle!.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsSelectionHandleCaptured(SelectionHandleEndpoint endpoint)
    {
        Button? endpointButton = endpoint switch
        {
            SelectionHandleEndpoint.Start => _selectionStartHandle,
            SelectionHandleEndpoint.End => _selectionEndHandle,
            _ => null,
        };

        return endpointButton is not null &&
            ReferenceEquals(endpointButton, _selectionHandleCaptureTarget) &&
            IsSelectionHandlePointerDragActive();
    }

    private bool IsSelectionHandlePointerDragActive()
        => _selectionHandleCaptureTarget is not null &&
           _activeSelectionHandle != SelectionHandleEndpoint.None &&
           _selectionHandlePointerId != 0 &&
           _touchSelection.ContactPointerId == _selectionHandlePointerId &&
           _touchSelection.State is TouchSelectionState.DraggingStartHandle or
               TouchSelectionState.DraggingEndHandle;

    private void HideUncapturedSelectionHandles()
    {
        if (!IsSelectionHandleCaptured(SelectionHandleEndpoint.Start))
        {
            _selectionStartHandleEndpointAvailable = false;
            if (_selectionStartHandle is not null)
                _selectionStartHandle.Visibility = Visibility.Collapsed;
        }

        if (!IsSelectionHandleCaptured(SelectionHandleEndpoint.End))
        {
            _selectionEndHandleEndpointAvailable = false;
            if (_selectionEndHandle is not null)
                _selectionEndHandle.Visibility = Visibility.Collapsed;
        }
    }

    private static void UpdateSelectionHandleAppearance(
        Button handle,
        double targetSize,
        double dotSize,
        Color color)
    {
        handle.Width = targetSize;
        handle.Height = targetSize;
        handle.MinWidth = targetSize;
        handle.MinHeight = targetSize;
        handle.CornerRadius = new CornerRadius(targetSize / 2.0);
        if (handle.Content is not Ellipse dot)
            return;

        dot.Width = dotSize;
        dot.Height = dotSize;
        if (dot.Fill is SolidColorBrush brush)
            brush.Color = color;
        else
            dot.Fill = new SolidColorBrush(color);
    }

    private bool PlaceSelectionHandle(Button handle, Point endpoint, double targetSize, Rect viewport)
    {
        double halfTarget = targetSize / 2.0;
        bool visible = endpoint.X + halfTarget >= viewport.Left &&
                       endpoint.X - halfTarget <= viewport.Right &&
                       endpoint.Y + halfTarget >= viewport.Top &&
                       endpoint.Y - halfTarget <= viewport.Bottom;
        if (!visible)
        {
            handle.Visibility = Visibility.Collapsed;
            return false;
        }

        // Snap to the physical pixel grid of the window root so a 44-DIP target
        // measures exactly 44 DIP when the renderer starts on a fractional
        // physical pixel.
        (double left, double top) = SelectionController.ComputeSelectionHandleOrigin(
            endpoint,
            targetSize,
            viewport,
            GetSelectionOverlayOriginInRoot(),
            XamlRoot?.RasterizationScale ?? 1.0);
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
        handle.Visibility = Visibility.Visible;
        return true;
    }

    private Point GetSelectionOverlayOriginInRoot()
    {
        if (_overlay is null || XamlRoot is null)
            return new Point(double.NaN, double.NaN);
        try
        {
            return _overlay.TransformToVisual(null).TransformPoint(new Point(0, 0));
        }
        catch (ArgumentException)
        {
            // Not yet in the live tree: keep the unsnapped placement.
            return new Point(double.NaN, double.NaN);
        }
    }

    private void SetSelectionHandleVisibility(Visibility visibility)
    {
        try
        {
            if (_selectionStartHandle is not null)
                _selectionStartHandle.Visibility = visibility;
        }
        finally
        {
            if (_selectionEndHandle is not null)
                _selectionEndHandle.Visibility = visibility;
        }
    }

    internal void AppendVisibleSelectionHandleAutomationPeers(IList<AutomationPeer> peers)
    {
        AppendVisibleSelectionHandleAutomationPeer(_selectionStartHandle, peers);
        AppendVisibleSelectionHandleAutomationPeer(_selectionEndHandle, peers);
    }

    private static void AppendVisibleSelectionHandleAutomationPeer(
        Button? handle,
        IList<AutomationPeer> peers)
    {
        if (handle is null || handle.Visibility != Visibility.Visible)
            return;

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(handle) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(handle);
        if (peer is not null)
            peers.Add(peer);
    }

    private void ProcessSelectionHandlePointerPressed(ref PointerInput e)
    {
        if (e.Sender is not Button { Tag: SelectionHandleEndpoint endpoint } handle ||
            endpoint == SelectionHandleEndpoint.None ||
            _snapshot is null || !_selection.IsActive || _canvas is null ||
            _selectionHandleCaptureTarget is not null ||
            _selectionHandlePointerId != 0 ||
            _activeSelectionHandle != SelectionHandleEndpoint.None)
        {
            return;
        }

        _pendingTouchSelectionPresentationRestore = null;

        var point = e.Point;
        MarkdownPointerModality modality = GetPointerModality(e.Pointer.PointerDeviceType);
        bool primary = modality == MarkdownPointerModality.Touch
            ? point.IsInContact
            : MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
                modality,
                point.IsInContact,
                point.Properties.IsLeftButtonPressed,
                point.Properties.IsBarrelButtonPressed);
        if (!primary)
            return;

        bool endpointAvailable = endpoint == SelectionHandleEndpoint.Start
            ? _selectionStartHandleEndpointAvailable
            : _selectionEndHandleEndpointAvailable;
        if (!endpointAvailable)
            return;

        DocumentRange range = _selection.Range.Normalized();
        _activeSelectionHandle = endpoint;
        _selectionHandlePointerId = e.Pointer.PointerId;
        _selectionHandleFixedPosition = endpoint == SelectionHandleEndpoint.Start
            ? range.End
            : range.Start;
        TryGetViewport(out double viewportTop, out _, out _);
        _lastSelectionPointerViewportY = point.Position.Y - viewportTop;
        Point endpointPoint = GetSelectionHandleEndpointPoint(endpoint);
        _selectionHandleCaretOffset = new Point(
            endpointPoint.X - point.Position.X,
            endpointPoint.Y - point.Position.Y);
        _selectionHandleLatestPoint = point.Position;
        _selectionHandleLatestViewportY = point.Position.Y - viewportTop;
        _selectionHandleMovePending = false;
        if (!_touchSelection.BeginHandleDrag(
                e.Pointer.PointerId,
                endpoint == SelectionHandleEndpoint.Start))
        {
            ResetSelectionHandleDrag();
            return;
        }
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            !_touchTapSuppression.TryBeginContact(e.Pointer.PointerId, out e.TouchTapGestureEpoch))
        {
            ResetSelectionHandleDrag();
            _touchSelection.Cancel(e.Pointer.PointerId);
            return;
        }
        if (!CapturePointerForInput(handle, e))
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
                _touchTapSuppression.CancelContact(e.Pointer.PointerId);
            ResetSelectionHandleDrag();
            _touchSelection.Cancel(e.Pointer.PointerId);
            return;
        }

        // Capture belongs to this physical Button even if logical start/end
        // roles later cross while extending the selection. Only publish the
        // target after WinUI (or the deferred-input path) confirms capture.
        if (_selectionHandlePointerId != e.Pointer.PointerId ||
            _touchSelection.ContactPointerId != e.Pointer.PointerId ||
            _activeSelectionHandle == SelectionHandleEndpoint.None ||
            _touchSelection.State is not TouchSelectionState.DraggingStartHandle and
                not TouchSelectionState.DraggingEndHandle)
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
                _touchTapSuppression.CancelContact(e.Pointer.PointerId);
            ResetSelectionHandleDrag();
            _touchSelection.Cancel(e.Pointer.PointerId);
            return;
        }
        _selectionHandleCaptureTarget = handle;

        SetSelectionHandleGlyphVisibility(visible: false);
        StartSelectionHandleFrameClock();
        e.Handled = true;
    }

    private void ProcessSelectionHandlePointerMoved(ref PointerInput e)
    {
        if (_activeSelectionHandle == SelectionHandleEndpoint.None ||
            _selectionHandlePointerId != e.Pointer.PointerId ||
            _snapshot is null || _canvas is null)
        {
            return;
        }

        var pointerPoint = e.Point;
        if (!pointerPoint.IsInContact)
            return;

        _selectionHandleLatestPoint = pointerPoint.Position;
        TryGetViewport(out double viewportTop, out _, out _);
        _selectionHandleLatestViewportY = pointerPoint.Position.Y - viewportTop;
        _selectionHandleMovePending = true;
        StartSelectionHandleFrameClock();
        e.Handled = true;
    }

    private void ProcessSelectionHandlePointerReleased(ref PointerInput e)
    {
        if (_selectionHandlePointerId != e.Pointer.PointerId)
        {
            // A handle can appear under a finger that is still down,
            // such as the second tap of a word-selecting double-tap. That
            // contact belongs to the canvas, which must see it end.
            ProcessPointerReleased(ref e);
            return;
        }

        // Commit the release coordinate even when the dispatcher coalesced the
        // final PointerMoved event. This mirrors the main text-drag path and
        // prevents quick flick-and-release gestures from stopping one frame early.
        _selectionHandleLatestPoint = e.Point.Position;
        TryGetViewport(out double viewportTop, out _, out _);
        _selectionHandleLatestViewportY = e.Point.Position.Y - viewportTop;
        _selectionHandleMovePending = true;
        ApplyPendingSelectionHandleMove(sustained: false);
        if (_canvas is not null && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            SuppressTouchTap(e.Point.Position, e.TouchTapGestureEpoch);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            _touchTapSuppression.EndContact(e.Pointer.PointerId);
        _selectionHandleCaptureTarget = null;
        _touchSelection.CompleteHandleDrag(e.Pointer.PointerId);
        _activeSelectionHandle = SelectionHandleEndpoint.None;
        _selectionHandlePointerId = 0;
        e.ResumeRebuildAfterInteraction = true;
        _lastSelectionPointerViewportY = double.NaN;
        StopSelectionHandleFrameClock();
        CollapseSelectionHandlesForPendingRefresh();
        SetSelectionHandleGlyphVisibility(visible: true);
        if (e.Sender is UIElement element)
            element.ReleasePointerCapture(e.Pointer);
        UpdateSelectionHandles();
        e.Handled = true;
    }

    private void ProcessSelectionHandlePointerCanceled(ref PointerInput e)
    {
        if (_selectionHandlePointerId != e.Pointer.PointerId)
        {
            // See ProcessSelectionHandlePointerReleased.
            ProcessPointerCanceledOrCaptureLost(ref e);
            return;
        }

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            _touchTapSuppression.CancelContact(e.Pointer.PointerId);
        ResetSelectionHandleDrag();
        _touchSelection.Cancel(e.Pointer.PointerId);
        e.ResumeRebuildAfterInteraction = true;
        UpdateSelectionHandles();
        e.Handled = true;
    }

    /// <summary>
    /// Decides whether an active handle drag lost its pointer capture
    /// without a routed terminal. WinUI raises no PointerCaptureLost when a
    /// touch capture is released by code or by tree changes (it keeps a replay
    /// pointer point only for mouse), and the contact's later release is then
    /// routed away from the handle. An unknown capture view, a deferred capture
    /// record or queued input for the pointer never counts as lost: queued
    /// terminals must still replay and commit their final position.
    /// </summary>
    internal static bool IsSelectionHandleCaptureSilentlyLost(
        uint dragPointerId,
        bool hasCaptureTarget,
        bool captureViewAvailable,
        bool captureTargetHoldsPointer,
        bool hasDeferredCaptureOrQueuedInput)
        => dragPointerId != 0 && hasCaptureTarget && captureViewAvailable &&
           !captureTargetHoldsPointer && !hasDeferredCaptureOrQueuedInput;

    /// <summary>
    /// Retires a handle drag whose capture was silently lost exactly
    /// as a routed handle cancel does: the committed selection is kept and the
    /// touch owner, gesture epoch, capture target and frame clock are cleared.
    /// </summary>
    private bool RetireSilentlyLostSelectionHandleCapture()
    {
        uint pointerId = _selectionHandlePointerId;
        UIElement? target = _selectionHandleCaptureTarget;
        IReadOnlyList<Pointer>? captures = target?.XamlRoot is null ? null : target.PointerCaptures;
        if (!IsSelectionHandleCaptureSilentlyLost(
                pointerId,
                target is not null,
                captures is not null,
                ContainsPointerCapture(captures, pointerId),
                _deferredPointerCaptures.ContainsKey(pointerId) || HasQueuedPointerInput(pointerId)))
        {
            return false;
        }

        _touchTapSuppression.CancelContact(pointerId);
        ResetSelectionHandleDrag();
        _touchSelection.Cancel(pointerId);
        ResumePendingRebuildAfterPointerInteraction();
        UpdateSelectionHandles();
        MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Retired a selection-handle drag whose pointer capture was lost without a routed terminal.");
        return true;
    }

    private bool HasQueuedPointerInput(uint pointerId)
    {
        if (_deferredPointerInput is not { Count: > 0 } queue)
            return false;
        for (int index = 0; index < queue.Count; index++)
        {
            if (queue.TryGetAt(index, out PointerInput queued) && queued.Pointer?.PointerId == pointerId)
                return true;
        }
        return false;
    }

    private void StartSelectionHandleFrameClock()
    {
        if (_selectionHandleFrameSubscribed)
            return;

        _selectionHandleLastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnSelectionHandleFrame;
        _selectionHandleFrameSubscribed = true;
    }

    private void OnSelectionHandleFrame(object? sender, object args)
    {
        if (_isDisposed || _isUnloaded ||
            _activeSelectionHandle == SelectionHandleEndpoint.None ||
            _snapshot is not { } snapshot)
        {
            StopSelectionHandleFrameClock();
            return;
        }

        if (!snapshot.TryBeginInteraction())
            return;

        try
        {
            // The clock runs for the whole drag, so a capture that
            // moved without a routed terminal is retired within a frame.
            if (RetireSilentlyLostSelectionHandleCapture())
                return;

            long timestamp = Stopwatch.GetTimestamp();
            double elapsedSeconds = Math.Max(
                0,
                (timestamp - _selectionHandleLastFrameTimestamp) / (double)Stopwatch.Frequency);
            _selectionHandleLastFrameTimestamp = timestamp;

            // Rendering is the coalescing boundary: at most one hit-test and
            // overlay update is performed per compositor frame. The latest
            // contact remains active without PointerMoved so edge scrolling
            // continues while the finger is stationary.
            // The return value only reports whether movement or edge scrolling
            // continues; the clock itself stays subscribed until the drag ends
            // (ResetSelectionHandleDrag or a routed release stops it).
            _ = ApplyPendingSelectionHandleMove(
                sustained: true,
                elapsedSeconds);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private bool ApplyPendingSelectionHandleMove(
        bool sustained,
        double elapsedSeconds = 0)
    {
        if (_activeSelectionHandle == SelectionHandleEndpoint.None ||
            _snapshot is null || _canvas is null ||
            (!sustained && !_selectionHandleMovePending))
        {
            return false;
        }

        TryGetViewport(out double viewportTop, out _, out _);
        Point contact = new(
            _selectionHandleLatestPoint.X,
            viewportTop + _selectionHandleLatestViewportY);
        Point caretPoint = new(
            contact.X + _selectionHandleCaretOffset.X,
            contact.Y + _selectionHandleCaretOffset.Y);
        if (sustained && !_selectionHandleMovePending)
        {
            TryGetViewport(out double edgeViewportTop, out double edgeViewportHeight, out _);
            if (Math.Abs(SelectionAutoScroll.ComputeVelocity(
                    caretPoint.Y,
                    edgeViewportTop,
                    edgeViewportHeight)) < 0.5)
            {
                return false;
            }
        }
        Point point = PrepareSelectionDragPoint(caretPoint, sustained, elapsedSeconds);
        _selectionHandleMovePending = false;

        DocumentPosition candidate;
        if (TryHitTestEmbed(point, out DocumentPosition embedPosition))
            candidate = embedPosition;
        else if (!_snapshot.HitTestSelectionEndpoint(point, out candidate))
            return IsSelectionHandleInAutoScrollBand(caretPoint);

        AdjustSelectionHandle(_activeSelectionHandle, candidate);
        return IsSelectionHandleInAutoScrollBand(caretPoint);
    }

    private bool IsSelectionHandleInAutoScrollBand(Point caretPoint)
    {
        if (_canvas is null)
            return false;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        double velocity = SelectionAutoScroll.ComputeVelocity(
            caretPoint.Y,
            viewportTop,
            viewportHeight);
        if (Math.Abs(velocity) < 0.5)
            return false;

        double maxOffset = Math.Max(0, _canvas.ActualHeight - viewportHeight);
        return velocity < 0
            ? viewportTop > 0.0001
            : viewportTop < maxOffset - 0.0001;
    }

    private bool TryGetSelectionHandleEndpointPoint(
        LayoutSnapshot snapshot,
        SelectionHandleEndpoint endpoint,
        Rect viewport,
        double verticalSearchMargin,
        out Point point)
    {
        point = default;
        if (!_selection.IsActive)
            return false;

        DocumentRange normalized = _selection.Range.Normalized();
        bool rangeStart = endpoint == SelectionHandleEndpoint.Start;
        DocumentPosition position = rangeStart ? normalized.Start : normalized.End;
        if (!_selection.TryGetSelectionEndpointGeometry(
                snapshot,
                position,
                normalized,
                viewport,
                verticalSearchMargin,
                rangeStart,
                out Point caretPoint,
                out Rect rectangle,
                out bool hasCaret))
        {
            return false;
        }

        if (hasCaret)
        {
            point = SnapSelectionHandlePoint(caretPoint);
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }

        if (rectangle.Width <= 0 || rectangle.Height <= 0)
            return false;

        bool rightToLeft = FlowDirection == FlowDirection.RightToLeft;
        point = rangeStart
            ? new Point(rightToLeft ? rectangle.Right : rectangle.Left, rectangle.Bottom)
            : new Point(rightToLeft ? rectangle.Left : rectangle.Right, rectangle.Bottom);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private Point GetSelectionHandleEndpointPoint(SelectionHandleEndpoint endpoint)
        => endpoint == SelectionHandleEndpoint.Start
            ? (_selectionStartHandleEndpointAvailable ? _selectionStartHandleEndpoint : default)
            : (_selectionEndHandleEndpointAvailable ? _selectionEndHandleEndpoint : default);

    private Point SnapSelectionHandlePoint(Point point)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;

        return new Point(
            Math.Round(point.X * scale) / scale,
            Math.Round(point.Y * scale) / scale);
    }

    private void SetSelectionHandleGlyphVisibility(bool visible)
    {
        double opacity = visible ? 1 : 0;
        if (_selectionStartHandle?.Content is Ellipse startGlyph)
            startGlyph.Opacity = opacity;
        if (_selectionEndHandle?.Content is Ellipse endGlyph)
            endGlyph.Opacity = opacity;
    }

    private void StopSelectionHandleFrameClock()
    {
        if (!_selectionHandleFrameSubscribed)
            return;

        CompositionTarget.Rendering -= OnSelectionHandleFrame;
        _selectionHandleFrameSubscribed = false;
        _selectionHandleLastFrameTimestamp = 0;
    }

    private void OnSelectionHandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return;
        if (sender is not Button { Tag: SelectionHandleEndpoint endpoint } ||
            !_selection.IsActive)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            FocusRendererForPointerInteraction();
            e.Handled = true;
            return;
        }

        int delta = e.Key switch
        {
            VirtualKey.Left => FlowDirection == FlowDirection.RightToLeft ? 1 : -1,
            VirtualKey.Right => FlowDirection == FlowDirection.RightToLeft ? -1 : 1,
            VirtualKey.Up => -1,
            VirtualKey.Down => 1,
            _ => 0,
        };
        if (delta == 0)
            return;

        MoveSelectionHandleByTextOffset(endpoint, delta);
        e.Handled = true;
    }

    private void MoveSelectionHandleByTextOffset(
        SelectionHandleEndpoint endpoint,
        int delta)
    {
        if (_snapshot is null || !_selection.IsActive)
            return;

        DocumentRange range = _selection.Range.Normalized();
        DocumentPosition current = endpoint == SelectionHandleEndpoint.Start
            ? range.Start
            : range.End;
        int currentOffset = _snapshot.SemanticDocument.TextOffsetFromDocumentPosition(current);
        int requestedOffset = _snapshot.SemanticDocument.TextElementBoundaries.Move(
            currentOffset,
            delta,
            out _);
        AdjustSelectionHandleAtTextOffset(
            endpoint,
            requestedOffset,
            candidateIsBoundary: true);
    }

    private void AdjustSelectionHandle(
        SelectionHandleEndpoint endpoint,
        DocumentPosition candidate)
    {
        if (_snapshot is null || !_selection.IsActive)
            return;

        var semantic = _snapshot.SemanticDocument;
        int rawCandidateOffset = semantic.TextOffsetFromDocumentPosition(candidate);
        AdjustSelectionHandleAtTextOffset(
            endpoint,
            rawCandidateOffset,
            candidateIsBoundary: false);
    }

    private void AdjustSelectionHandleAtTextOffset(
        SelectionHandleEndpoint endpoint,
        int rawCandidateOffset,
        bool candidateIsBoundary)
    {
        if (_snapshot is null || !_selection.IsActive)
            return;

        var semantic = _snapshot.SemanticDocument;
        int fixedOffset = semantic.TextOffsetFromDocumentPosition(_selectionHandlePointerId != 0
            ? _selectionHandleFixedPosition
            : endpoint == SelectionHandleEndpoint.Start
                ? _selection.Range.Normalized().End
                : _selection.Range.Normalized().Start);
        bool pointerDrag = _selectionHandlePointerId != 0;
        TouchSelectionEndpointAdjustment adjustment = TouchSelectionEndpointPolicy.Resolve(
            semantic.TextElementBoundaries,
            fixedOffset,
            rawCandidateOffset,
            endpoint == SelectionHandleEndpoint.Start,
            pointerDrag,
            candidateIsBoundary);
        int candidateOffset = adjustment.Offset;
        if (candidateOffset == fixedOffset)
            return;
        DocumentPosition? adjusted = semantic.PositionFromTextOffset(candidateOffset);
        if (adjusted is null)
            return;

        DocumentPosition fixedPosition = semantic.PositionFromTextOffset(fixedOffset)
            ?? _selectionHandleFixedPosition;
        _selection.SetAnchor(fixedPosition);
        _selection.ExtendTo(adjusted.Value);
        if (pointerDrag)
            _activeSelectionHandle = adjustment.IsStartEndpoint
                ? SelectionHandleEndpoint.Start
                : SelectionHandleEndpoint.End;
        _touchSelection.Select();
    }

    private void ResetSelectionHandleDrag()
    {
        bool hadCapturedButton = _selectionHandleCaptureTarget is not null;
        _selectionHandleCaptureTarget = null;
        if (hadCapturedButton)
        {
            _selectionAdornerVisualDirty = true;
        }
        _activeSelectionHandle = SelectionHandleEndpoint.None;
        _selectionHandlePointerId = 0;
        _selectionHandleFixedPosition = default;
        _selectionHandleLatestPoint = default;
        _selectionHandleLatestViewportY = double.NaN;
        _selectionHandleCaretOffset = default;
        _selectionHandleMovePending = false;
        _lastSelectionPointerViewportY = double.NaN;
        try
        {
            StopSelectionHandleFrameClock();
            if (hadCapturedButton)
                CollapseSelectionHandlesForPendingRefresh();
            SetSelectionHandleGlyphVisibility(visible: true);
        }
        finally
        {
            try { _selectionStartHandle?.ReleasePointerCaptures(); } catch { }
            try { _selectionEndHandle?.ReleasePointerCaptures(); } catch { }
        }

        if (hadCapturedButton && !_isDisposed && !_isUnloaded && IsLoaded)
            RequestSelectionAdornerRefresh();
    }

    private void CollapseSelectionHandlesForPendingRefresh()
    {
        _selectionStartHandleEndpointAvailable = false;
        _selectionEndHandleEndpointAvailable = false;
        if (_selectionStartHandle is not null)
            _selectionStartHandle.Visibility = Visibility.Collapsed;
        if (_selectionEndHandle is not null)
            _selectionEndHandle.Visibility = Visibility.Collapsed;
    }

    private void DetachSelectionHandleHandlers()
    {
        DetachSelectionHandleHandlers(_selectionStartHandle);
        DetachSelectionHandleHandlers(_selectionEndHandle);
    }

    private void DetachSelectionHandleHandlers(Button? handle)
    {
        if (handle is null)
            return;
        handle.RemoveHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSelectionHandlePointerPressed));
        handle.RemoveHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnSelectionHandlePointerMoved));
        handle.RemoveHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnSelectionHandlePointerReleased));
        handle.RemoveHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled));
        handle.RemoveHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled));
        handle.RightTapped -= OnSelectionHandleRightTapped;
        handle.KeyDown -= OnSelectionHandleKeyDown;
    }

    private void SetSelectionDragShieldActive(bool active)
    {
        if (_overlay is null)
            return;

        if (!active)
        {
            if (_selectionDragShield is not null)
                _selectionDragShield.Visibility = Visibility.Collapsed;
            return;
        }

        if (!EnsureSelectionDragShield())
            return;

        _selectionDragShield!.Width = GetSelectionOverlayWidth();
        _selectionDragShield.Height = GetSelectionOverlayHeight();
        _selectionDragShield.Visibility = Visibility.Visible;
    }

    private Border CreateSelectionDragShield()
    {
        var shield = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            IsHitTestVisible = true,
            Visibility = Visibility.Collapsed,
        };
        shield.PointerMoved += OnPointerMoved;
        shield.PointerReleased += OnPointerReleased;
        shield.PointerCanceled += OnPointerCanceledOrCaptureLost;
        shield.PointerCaptureLost += OnPointerCanceledOrCaptureLost;
        Canvas.SetLeft(shield, 0);
        Canvas.SetTop(shield, 0);
        Canvas.SetZIndex(shield, 4);
        _selectionDragShield = shield;
        return shield;
    }

    private bool EnsureSelectionDragShield()
    {
        if (_overlay is null)
            return false;

        if (_selectionDragShield is null)
            CreateSelectionDragShield();

        try
        {
            if (VisualTreeHelper.GetParent(_selectionDragShield) is null)
                _overlay.Children.Add(_selectionDragShield);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] selection drag shield attach failed: {ex.Message}");
            return false;
        }

        return true;
    }

    private double GetSelectionOverlayWidth()
    {
        TryGetViewport(out _, out _, out double width);
        return double.IsFinite(width) && width > 0 ? width : 1.0;
    }

    private double GetSelectionOverlayHeight()
    {
        TryGetViewport(out _, out double height, out _);
        return double.IsFinite(height) && height > 0 ? height : 1.0;
    }

    private void UpdateSelectionAdornerViewport()
    {
        _selectionAdornerGeometryDirty = true;
        _selectionAdornerVisualDirty = true;
        InvalidateSelectionAdorner();
    }

    private void PaintInteractiveDocumentState(
        LayoutSnapshot snapshot,
        CanvasDrawingSession drawingSession,
        Rect viewport,
        IReadOnlyList<SelectionBackgroundGeometry> selectionBackgrounds)
    {
        PaintLinkStateOverlay(drawingSession, viewport);
        if (!_selection.IsActive || selectionBackgrounds.Count == 0)
            return;

        Color selectedBackground = _themeSnapshot?.SelectionHighlightColor
            ?? Color.FromArgb(0xFF, 0x66, 0xAA, 0xE8);
        Color selectedForeground = _themeSnapshot?.SelectionForegroundColor
            ?? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        bool highContrast = _themeSnapshot?.IsHighContrast ?? false;
        foreach (SelectionBackgroundGeometry background in selectionBackgrounds)
        {
            Rect rectangle = background.Bounds;
            if (rectangle.Right < viewport.Left || rectangle.Left > viewport.Right ||
                rectangle.Bottom < viewport.Top || rectangle.Top > viewport.Bottom)
            {
                continue;
            }

            background.Paint(drawingSession, selectedBackground, highContrast);
        }
        snapshot.PaintSelectionForeground(
            drawingSession,
            _selection.Range.Normalized(),
            selectedForeground,
            viewport);
    }

    private void PaintLinkStateOverlay(CanvasDrawingSession drawingSession, Rect viewport)
    {
        if (_lastHoveredRun is LinkRun hover && _lastHoveredBox is { } hoverBox)
            hoverBox.PaintLinkStateForeground(drawingSession, hover, focused: false, viewport);

        if (TryGetFocusedLink(out var focusedBox, out var focusedLink))
            focusedBox.PaintLinkStateForeground(drawingSession, focusedLink, focused: true, viewport);
    }

    private void ProcessPointerCanceledOrCaptureLost(ref PointerInput e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            _touchTapSuppression.CancelContact(e.Pointer.PointerId);
            TouchSelectionCancelResult cancelResult = _touchSelection.CancelContact(e.Pointer.PointerId);
            if (cancelResult != TouchSelectionCancelResult.NotOwner)
            {
                // A canceled unrelated contact should not dismiss a committed
                // selection, but it must not leave the selection flyout alive.
                CloseSelectionContextMenu();
                UpdateSelectionHandles();
                if (cancelResult == TouchSelectionCancelResult.ClearSelection && _selection.IsActive)
                    ClearSelectionForExternalInteraction();
            }
        }

        if (HandleHorizontalOverflowPointerCanceled(ref e))
            return;

        if (!IsActivePointerSessionEvent(e))
            return;

        e.Handled = true;

        // True interruption (system-level cancel or capture taken by another element):
        // tear down drag state so a phantom anchor doesn't survive into the next gesture.
        // We deliberately do NOT do this on plain PointerExited — with capture, the
        // pointer can briefly leave canvas bounds during a normal drag, and clearing
        // the anchor there would kill drag-select on the very first vertical move
        // when the pointer crosses into a sibling overlay region (e.g. over a hosted
        // inline embed). PointerReleased handles the normal end-of-drag cleanup.
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        _clickMode = ClickMode.Single;
        SetSelectionDragShieldActive(false);
        if (!_selection.Range.Normalized().IsEmpty)
            InvalidateSelectionAdorner();
        ProcessPointerExited(ref e);
        ResumePendingRebuildAfterPointerInteraction();
    }

    private void ProcessPointerExited(ref PointerInput e)
    {
        ScheduleAbbreviationTooltipHide();

        // PointerExited fires when the pointer leaves canvas bounds. During an
        // active captured drag this is expected (drag through hosted embeds /
        // adjacent areas) so we MUST NOT clear _selectionAnchor here — that
        // killed drag-select on the embeds page. Capture-loss / cancel are
        // routed to OnPointerCanceledOrCaptureLost which does the real cleanup.

        // ROOT-CAUSE FIX for text-selection shake:
        // During an active drag (_selectionAnchor != null), PointerExited fires
        // whenever the captured pointer physically leaves the canvas bounds (e.g.
        // the user drags toward the window edge). Calling InvalidateLinkHoverRegion
        // here issues a partial _canvas.Invalidate(), which triggers a tile repaint
        // that calls InlineContainerBox.Paint() → ApplyHoverColor() → CanvasTextLayout
        // .SetColor() → DirectWrite invalidates cached glyph-run metrics → character
        // region coordinates shift by sub-pixel amounts → visible text shake on every
        // drag frame. The canvas was already fully invalidated at drag-start
        // (OnPointerPressed) so no intermediate repaint is needed during the drag.
        // OnPointerCanceledOrCaptureLost clears _selectionAnchor *before* calling us,
        // so real cleanup (cancel / capture-loss) still runs normally here.
        if (_selectionAnchor is not null)
        {
            // Cursor reset is still safe and needed: if the pointer crosses from
            // the canvas into a hosted embed during drag, the embed must see the
            // default cursor (not an inherited IBeam).
            SetCursorShape(null);
            return;
        }

        // Clear hover state when the pointer leaves the canvas (or capture is
        // lost).  Without this, a link's hover colour and the hand cursor
        // persist even when the pointer is no longer over the control.
        if (_snapshot is null || _canvas is null) return;
        bool hadHover = _lastHoveredRun is not null;
        if (hadHover)
        {
            // Only LinkRun hovers produce a visible color change. Plain TextRun
            // hover state is purely cursor/IBeam tracking and has no painted
            // representation. Calling _canvas.Invalidate for a TextRun→null
            // transition causes a partial-tile repaint, and CanvasVirtualControl
            // partial repaints expose DirectWrite sub-pixel glyph-position
            // variance at tile boundaries — i.e. the text-shake the user sees
            // on click-to-dismiss (capture-loss routes through here with
            // _selectionAnchor already nulled by OnPointerCanceledOrCaptureLost).
            bool wasLink = _lastHoveredRun is LinkRun;
            var boxToInvalidate = _lastHoveredBox;
            foreach (var b in _snapshot.Blocks) ClearHover(b);
            _lastHoveredRun = null;
            _lastHoveredBox = null;
            // No canvas invalidate: hover transitions no longer mutate the
            // text layout (see InlineContainerBox.HoveredRun docs).
            _ = wasLink; _ = boxToInvalidate;
            if (wasLink)
                InvalidateInteractiveTextAdorner();
        }
        // Always reset cursor to null (system default) on exit — not just
        // when a link was hovered.  PointerExited fires whenever the pointer
        // moves from the canvas to any sibling element — most importantly the
        // overlay that hosts embedded WinUI elements.  With ProtectedCursor =
        // null on this UserControl, XAML finds no cursor override anywhere in
        // the tree above the embed, so the embed (Button, CheckBox, …) can
        // show its own cursor instead of inheriting IBeam from us.
        SetCursorShape(null);
    }

    private void ProcessPointerReleased(ref PointerInput e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            _touchTapSuppression.EndContact(e.Pointer.PointerId);
            _touchSelection.EndContact(e.Pointer.PointerId);
        }

        if (_canvas is null) return;
        if (HandleHorizontalOverflowPointerReleased(ref e))
            return;
        if (!IsActivePointerSessionEvent(e))
            return;

        e.Handled = true;

        // PointerMoved is coalescible, including while the UI dispatcher is
        // busy. Commit the release location before ending capture so a fast
        // press/drag/release cannot leave selection at the last delivered move.
        if (_selectionAnchor is not null)
            ProcessPointerMoved(ref e);

        // Retire the gesture state while the leased snapshot is stable. Capture
        // release is staged below because WinUI can synchronously route
        // PointerCaptureLost from ReleasePointerCapture.
        bool wasLeft = _pointerSession.IsActive &&
            _pointerSession.PointerId == e.Pointer.PointerId &&
            _pointerSession.IsPrimary;
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        SetSelectionDragShieldActive(false);
        e.ReleaseCanvasCaptureAfterInteraction = true;
        e.ResumeRebuildAfterInteraction = true;
        if (!wasLeft) return;

        if (!_selection.Range.Normalized().IsEmpty)
        {
            e.RefreshSelectionAdornerAfterInteraction = true;
        }

        // Click handling for links: if no real selection occurred, raise LinkClick
        // when the click lands on a LinkRun.
        if (e.InteractionSnapshot is not { } snapshot) return;
        if (_clickMode != ClickMode.Single) return; // double/triple-click: selection intent, not link-click
        if (!_selection.Range.Normalized().IsEmpty) return; // text was dragged — not a click
        var pt = e.Point.Position;
        if (TryCreateLinkActivationIntent(
            snapshot,
            pt,
            GetLinkInputKind(e.Pointer.PointerDeviceType),
            e.LinkModifiers,
            out LinkActivationIntent intent))
        {
            e.SetLinkActivation(intent);
        }
    }

    private bool TryCreateLinkActivationIntent(
        LayoutSnapshot snapshot,
        Point point,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers,
        out LinkActivationIntent intent)
    {
        LinkTarget? link = FindVectorLinkTargetAtPoint(snapshot, point);
        if (link is null && snapshot.HitTest(point, out var position))
            link = FindLinkTargetAt(snapshot, position, point);
        if (link is null)
        {
            intent = default;
            return false;
        }

        intent = new LinkActivationIntent(link.Value, inputKind, modifiers);
        return true;
    }

    private bool ActivateLinkIntent(
        LinkActivationIntent intent,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        bool IsCurrent() => IsCurrentPointerInteraction(
            snapshot,
            snapshotGeneration,
            layoutRevision);

        if (!IsCurrent())
            return false;

        if (intent.DisclosureId is { Length: > 0 } disclosureId)
        {
            SetDisclosureExpanded(disclosureId, intent.DisclosureExpanded);
            return true;
        }

        return ActivateLinkTarget(
            new LinkTarget(
                intent.Url,
                intent.Title,
                intent.SourceSpan,
                run: null,
                action: intent.Action,
                external: intent.External),
            intent.InputKind,
            intent.Modifiers,
            IsCurrent);
    }

    private bool ActivateLinkTarget(
        LinkTarget link,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers,
        Func<bool>? isCurrent = null)
    {
        if (isCurrent is not null && !isCurrent())
            return false;

        if (TryActivateDisclosure(link.Run))
            return true;
        if (isCurrent is not null && !isCurrent())
            return false;

        // Intercept internal fragment anchors (e.g. footnote back/forward
        // links) and scroll without surfacing them to external subscribers.
        bool internalTargetHandled =
            link.Url.StartsWith("#", StringComparison.Ordinal) &&
            HandleInternalAnchor(link.Url);
        if (isCurrent is not null && !isCurrent())
            return false;

        MarkdownLinkActivationDecision decision;
        try
        {
            IMarkdownCommandProvider? commandProvider = CommandProvider;
            decision = isCurrent is null
                ? MarkdownLinkActivationPolicy.Evaluate(
                    inputKind,
                    internalTargetHandled,
                    commandProvider,
                    link.SourceSpan,
                    link.Url,
                    link.Action)
                : MarkdownLinkActivationPolicy.Evaluate(
                    inputKind,
                    internalTargetHandled,
                    commandProvider,
                    link.SourceSpan,
                    link.Url,
                    link.Action,
                    isCurrent);
        }
        catch (Exception ex)
        {
            // Provider failures must not make built-in link activation vanish.
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host activation command failed: {ex.Message}");
            if (isCurrent is not null && !isCurrent())
                return false;
            decision = MarkdownLinkActivationPolicy.CreateHostFallback(inputKind);
        }

        if (isCurrent is not null && !isCurrent())
            return false;
        if (decision.IsHandled)
            return true;

        if (isCurrent is not null && !isCurrent())
            return false;

        LinkClick?.Invoke(this, new MarkdownLinkClickEventArgs(
            link.Url,
            link.Title,
            link.SourceSpan,
            decision.InputKind,
            modifiers,
            link.External
                ? MarkdownLinkDisposition.External
                : GetRequestedDisposition(modifiers),
            link.Action));
        return true;
    }

    private void ResumePendingRebuildAfterPointerInteraction()
    {
        if (!_rebuildDispatchState.IsQueued && !_isUnloaded)
            QueuePendingRebuild();
        if (!_isDisposed && !_isUnloaded && _pendingImageRelayoutBlockIndices.Count > 0)
        {
            foreach (int pending in _pendingImageRelayoutBlockIndices)
            {
                QueueImageRelayout(pending);
                break;
            }
        }
    }

    /// <summary>
    /// Scrolls the document so the block with <paramref name="blockIndex"/>
    /// is near the top of the viewport.
    /// </summary>
    public void ScrollToBlock(int blockIndex)
    {
        NoteViewportRequest();
        if (_snapshot is not { } snapshot) return;
        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        SnapshotBlockScrollTargetState targetState = snapshot.TryCaptureBlockScrollTarget(
            blockIndex,
            out SnapshotBlockScrollTarget target);
        if (targetState == SnapshotBlockScrollTargetState.Busy)
        {
            _pendingScrollToBlock = (snapshot, snapshotGeneration, blockIndex);
            return;
        }

        if (targetState is SnapshotBlockScrollTargetState.NotFound or SnapshotBlockScrollTargetState.Unavailable)
        {
            ClearPendingScrollToBlock(snapshot, snapshotGeneration, blockIndex);
            return;
        }

        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, target.LayoutRevision))
            return;

        double targetTop = target.TargetTop;
        if (targetState == SnapshotBlockScrollTargetState.RequiresRealization)
        {
            // The requested descendant's old coordinates are not authoritative
            // until its top-level owner is measured. Scroll to the owner's
            // committed estimate now, then retry this exact block after the
            // asynchronous layout result publishes.
            _pendingScrollToBlock = (snapshot, snapshotGeneration, blockIndex);
            LazyLayoutBand visibleVectorBand = GetCurrentVisibleVectorBand();
            QueueLazyLayoutForBand(
                snapshot,
                IncludeBand(
                    new LazyLayoutBand(target.OwnerTop, Math.Max(target.OwnerTop + 1, target.OwnerBottom)),
                    visibleVectorBand),
                visibleVectorBand,
                preserveScrollAnchor: false);
        }
        else
        {
            ClearPendingScrollToBlock(snapshot, snapshotGeneration, blockIndex);
        }

        // Queueing can start worker measurement immediately. Do not apply a
        // captured offset after a newer geometry or document generation won.
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, target.LayoutRevision))
            return;

        if (_scroll is not null)
            _scroll.ChangeView(null, Math.Max(0, targetTop - 24), null, disableAnimation: true);
        else
            BringDocumentRectIntoExternalViewport(
                new Rect(0, Math.Max(0, targetTop - 24), 1, Math.Max(1, target.OwnerHeight)),
                animate: false);
    }

    private bool IsCurrentScrollTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        !_isDisposed && !_isUnloaded &&
        ReferenceEquals(snapshot, _snapshot) &&
        snapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
        snapshot.LayoutRevision == layoutRevision;

    private void ClearPendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration, int blockIndex)
    {
        if (_pendingScrollToBlock is { } pending &&
            ReferenceEquals(pending.Snapshot, snapshot) &&
            pending.SnapshotGeneration == snapshotGeneration &&
            pending.BlockIndex == blockIndex)
        {
            _pendingScrollToBlock = null;
        }
    }

    private void TryResumePendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration)
    {
        if (_pendingScrollToBlock is not { } pending)
            return;

        if (!ReferenceEquals(pending.Snapshot, snapshot) ||
            pending.SnapshotGeneration != snapshotGeneration ||
            !IsCurrentScrollTarget(snapshot, snapshotGeneration, snapshot.LayoutRevision))
        {
            ClearPendingScrollToBlock(pending.Snapshot, pending.SnapshotGeneration, pending.BlockIndex);
            return;
        }

        _pendingScrollToBlock = null;
        ScrollToBlock(pending.BlockIndex);
    }

    /// <summary>
    /// Handles internal fragment navigation URLs (e.g. <c>#footnote-def-1</c>,
    /// <c>#footnote-ref-1</c>) by scrolling to the target block directly.
    /// </summary>
    private bool HandleInternalAnchor(string url)
    {
        if (_snapshot is null) return false;
        // Resolve block index from footnote registry stored on the layout context.
        // The registry is embedded in the LayoutSnapshot's block tree; walk to find
        // the first InlineContainerBox whose block carries the matching footnote tag.
        // We look for a special metadata property set by FootnoteRenderer.
        //   #footnote-def-{order}  → scroll to the definition box
        //   #footnote-ref-{order}  → scroll to the inline citation box
        const string defPrefix = "#footnote-def-";
        const string refPrefix = "#footnote-ref-";
        bool isDef = url.StartsWith(defPrefix, StringComparison.OrdinalIgnoreCase);
        bool isRef = url.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase);
        if (isDef || isRef)
        {
            string orderStr = isDef ? url.Substring(defPrefix.Length) : url.Substring(refPrefix.Length);
            if (!int.TryParse(orderStr, out int order)) return false;

            // Walk blocks looking for the tagged block index stored in the
            // footnote index dictionary on the snapshot.
            int? targetIndex = isDef
                ? _snapshot.FootnoteDefBlock(order)
                : _snapshot.FootnoteRefBlock(order);
            if (targetIndex is { } idx)
            {
                ScrollToBlock(idx);
                return true;
            }
            return false;
        }

        if (url.StartsWith("#", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(url.Substring(1));
            if (_snapshot.FragmentTargetBlock(id) is { } idx)
            {
                ScrollToBlock(idx);
                return true;
            }
        }

        return false;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_snapshot is null || RequestGeometryRecoveryIfRequired(_snapshot)) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        switch (e.Key)
        {
            case VirtualKey.C when ctrl && _selection.IsActive:
                e.Handled = ExecuteSelectionCommand(MarkdownCommandKind.CopyRendered);
                return;
            case VirtualKey.A when MarkdownKeyboardInputPolicy.ShouldSelectAll(ctrl, IsSelectionEnabled):
                _selection.SetAnchor(DocumentPosition.Zero);
                _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
                e.Handled = true;
                return;
            case VirtualKey.F10 when shift && _selection.IsActive:
                e.Handled = ShowSelectionContextMenu(GetKeyboardContextMenuPoint());
                return;
            case VirtualKey.Application when _selection.IsActive:
                e.Handled = ShowSelectionContextMenu(GetKeyboardContextMenuPoint());
                return;
            case VirtualKey.Tab:
                e.Handled = MoveFocus(reverse: shift);
                return;
            case VirtualKey.Left:
            case VirtualKey.Right:
                e.Handled = HandleHorizontalArrowKey(e.Key);
                return;
            case VirtualKey.Home:
            case VirtualKey.End:
                e.Handled = ScrollFocusedHorizontalOverflowToEdge(e.Key == VirtualKey.End);
                return;
            case VirtualKey.Up:
            case VirtualKey.Down:
                e.Handled = MoveFocusSpatial(e.Key);
                return;
            case VirtualKey.Enter:
            case VirtualKey.Space when _focusedItemIndex >= 0:
                ClearPendingSpatialFocusKeys();
                e.Handled = ActivateFocusedItem();
                return;
            case VirtualKey.Escape:
                ClearPendingSpatialFocusKeys();
                if (_focusedItemIndex >= 0)
                {
                    _pendingLazyFocus = null;
                    _focusedItemIndex = -1;
                    UpdateFocusRing();
                    e.Handled = true;
                }
                else if (_selection.IsActive)
                {
                    _selection.Clear();
                    e.Handled = true;
                }
                return;
        }
    }

    private bool HandleHorizontalArrowKey(VirtualKey key)
    {
        if (_pendingSpatialFocusKeys.Count > 0)
            return QueuePendingSpatialFocusKey(key);

        HorizontalArrowHandlingOrder order =
            MarkdownKeyboardInputPolicy.GetHorizontalArrowHandlingOrder(
                GetCurrentFocusableIdentity()?.Kind);
        FocusGeometryReadStatus status = order == HorizontalArrowHandlingOrder.OverflowThenSpatial
            ? TryScrollFocusedHorizontalOverflow(key)
            : TryMoveFocusSpatial(key);
        if (status == FocusGeometryReadStatus.Ready)
        {
            bool handled = status == FocusGeometryReadStatus.Ready && _lastFocusNavigationHandled;
            if (handled)
                return true;

            status = order == HorizontalArrowHandlingOrder.OverflowThenSpatial
                ? TryMoveFocusSpatial(key)
                : TryScrollFocusedHorizontalOverflow(key);
            if (status == FocusGeometryReadStatus.Ready)
                return _lastFocusNavigationHandled;
        }

        return QueuePendingSpatialFocusKey(key);
    }

    private FocusGeometryReadStatus TryScrollFocusedHorizontalOverflow(VirtualKey key)
    {
        _lastFocusNavigationHandled = false;
        if (_snapshot is not { } snapshot ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return FocusGeometryReadStatus.Ready;
        }

        long generation = Volatile.Read(ref _snapshotGeneration);
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
            return FocusGeometryReadStatus.Busy;

        HorizontalOverflowVisualState visualState = default;
        bool changed = false;
        bool current;
        double oldOffset = 0;
        int blockIndex = items[_focusedItemIndex].BlockIndex;
        try
        {
            current = IsCurrentScrollTarget(snapshot, generation, revision);
            if (current && snapshot.TryGetHorizontalOverflowForBlockIndex(
                    blockIndex,
                    out IHorizontalOverflowBox? overflow) &&
                overflow is not null)
            {
                bool towardPhysicalRight = key == VirtualKey.Right;
                double logicalDelta = overflow.IsRightToLeft
                    ? (towardPhysicalRight ? -48 : 48)
                    : (towardPhysicalRight ? 48 : -48);
                oldOffset = overflow.HorizontalOffset;
                changed = snapshot.TryScrollHorizontal(overflow, logicalDelta);
                if (changed)
                    _ = TryCaptureHorizontalOverflowVisualStateNoLock(
                        snapshot, generation, revision, overflow, oldOffset, out visualState);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!current)
            return FocusGeometryReadStatus.Stale;
        if (!changed)
            return IsCurrentScrollTarget(snapshot, generation, revision)
                ? FocusGeometryReadStatus.Ready
                : FocusGeometryReadStatus.Stale;

        _lastFocusNavigationHandled = true;
        if (visualState.Snapshot is not null)
            PublishHorizontalOverflowVisualState(visualState);
        else
            QueueHorizontalOverflowVisualRetry(snapshot, generation, blockIndex, oldOffset);
        return FocusGeometryReadStatus.Ready;
    }

    private bool ScrollFocusedHorizontalOverflow(VirtualKey key)
    {
        if (_pendingSpatialFocusKeys.Count > 0)
            return QueuePendingSpatialFocusKey(key);
        FocusGeometryReadStatus status = TryScrollFocusedHorizontalOverflow(key);
        if (status == FocusGeometryReadStatus.Ready)
            return _lastFocusNavigationHandled;
        return QueuePendingSpatialFocusKey(key);
    }

    private bool ScrollFocusedHorizontalOverflowToEdge(bool end)
    {
        VirtualKey key = end ? VirtualKey.End : VirtualKey.Home;
        if (_pendingSpatialFocusKeys.Count > 0)
            return QueuePendingSpatialFocusKey(key);
        FocusGeometryReadStatus status = TryScrollFocusedHorizontalOverflowToEdge(end);
        if (status == FocusGeometryReadStatus.Ready)
            return _lastFocusNavigationHandled;
        return QueuePendingSpatialFocusKey(key);
    }

    private FocusGeometryReadStatus TryScrollFocusedHorizontalOverflowToEdge(bool end)
    {
        _lastFocusNavigationHandled = false;
        if (_snapshot is not { } snapshot ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return FocusGeometryReadStatus.Ready;
        }

        long generation = Volatile.Read(ref _snapshotGeneration);
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
            return FocusGeometryReadStatus.Busy;

        HorizontalOverflowVisualState visualState = default;
        bool changed = false;
        bool current;
        double oldOffset = 0;
        int blockIndex = items[_focusedItemIndex].BlockIndex;
        try
        {
            current = IsCurrentScrollTarget(snapshot, generation, revision);
            if (current && snapshot.TryGetHorizontalOverflowForBlockIndex(
                    blockIndex,
                    out IHorizontalOverflowBox? overflow) &&
                overflow is not null)
            {
                oldOffset = overflow.HorizontalOffset;
                double target = end
                    ? Math.Max(0, overflow.HorizontalExtent - overflow.HorizontalViewport)
                    : 0;
                changed = snapshot.TrySetHorizontalOffset(overflow, target);
                if (changed)
                    _ = TryCaptureHorizontalOverflowVisualStateNoLock(
                        snapshot, generation, revision, overflow, oldOffset, out visualState);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!current)
            return FocusGeometryReadStatus.Stale;
        if (!changed)
            return IsCurrentScrollTarget(snapshot, generation, revision)
                ? FocusGeometryReadStatus.Ready
                : FocusGeometryReadStatus.Stale;

        _lastFocusNavigationHandled = true;
        if (visualState.Snapshot is not null)
            PublishHorizontalOverflowVisualState(visualState);
        else
            QueueHorizontalOverflowVisualRetry(snapshot, generation, blockIndex, oldOffset);
        return FocusGeometryReadStatus.Ready;
    }

    /// <summary>
    /// Advances (or reverses) keyboard focus through the focusable items list.
    /// Returns true when focus is successfully moved (prevents Tab from leaving the control).
    /// </summary>
    private bool MoveFocus(bool reverse)
    {
        ClearPendingSpatialFocusKeys();
        var items = _focusableItems;
        if (items is null || items.Count == 0) return false;

        int nextIndex = FocusNavigationHelper.MoveTab(items.Count, _focusedItemIndex, _focusResumeItemIndex, reverse);
        if (nextIndex < 0)
        {
            // Let Tab/Shift+Tab leave the control to the next focusable element.
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            _focusResumeItemIndex = -1;
            UpdateFocusRing();
            SuppressHostedTabStopsForNativeTab();
            return false;
        }

        _focusedItemIndex = nextIndex;
        _focusResumeItemIndex = -1;
        return CommitFocusedItem(reverse);
    }

    private void SuppressHostedTabStopsForNativeTab()
    {
        var suppressed = new List<(Control Control, bool IsTabStop)>();
        var seen = new HashSet<Control>();
        foreach (var plan in _embedPlans)
        {
            if (plan.Realized is { } realized)
                SuppressHostedTabStops(realized, suppressed, seen);
        }
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is { } realized)
                SuppressHostedTabStops(realized, suppressed, seen);
        }

        if (suppressed.Count == 0)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var (control, isTabStop) in suppressed)
                control.IsTabStop = isTabStop;
        });
    }

    private static void SuppressHostedTabStops(
        DependencyObject root,
        List<(Control Control, bool IsTabStop)> suppressed,
        HashSet<Control> seen)
    {
        if (root is Control control && seen.Add(control))
        {
            suppressed.Add((control, control.IsTabStop));
            control.IsTabStop = false;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            SuppressHostedTabStops(VisualTreeHelper.GetChild(root, i), suppressed, seen);
        }
    }

    private bool MoveFocusSpatial(VirtualKey key)
    {
        if (_pendingSpatialFocusKeys.Count > 0)
            return QueuePendingSpatialFocusKey(key);

        FocusGeometryReadStatus status = TryMoveFocusSpatial(key);
        if (status == FocusGeometryReadStatus.Ready)
            return _lastFocusNavigationHandled;
        return QueuePendingSpatialFocusKey(key);
    }

    private FocusGeometryReadStatus TryMoveFocusSpatial(VirtualKey key)
    {
        _lastFocusNavigationHandled = false;
        var items = _focusableItems;
        if (items is null || items.Count == 0)
            return FocusGeometryReadStatus.Ready;

        if (_focusedItemIndex < 0)
        {
            if (_focusResumeItemIndex < 0)
                return FocusGeometryReadStatus.Ready;
            _focusedItemIndex = Math.Clamp(_focusResumeItemIndex, 0, items.Count - 1);
            _focusResumeItemIndex = -1;
            _lastFocusNavigationHandled = CommitFocusedItem(reverse: false);
            return FocusGeometryReadStatus.Ready;
        }

        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null)
            return FocusGeometryReadStatus.Stale;
        long generation = Volatile.Read(ref _snapshotGeneration);
        long revision = snapshot.LayoutRevision;
        FocusGeometryReadStatus captureStatus = TryCaptureFocusableRectMap(
            snapshot, generation, revision, items,
            out List<(int Index, Rect Rect)> map);
        if (captureStatus != FocusGeometryReadStatus.Ready)
            return captureStatus;

        int currentLocalIndex = -1;
        for (int i = 0; i < map.Count; i++)
        {
            if (map[i].Index == _focusedItemIndex)
            {
                currentLocalIndex = i;
                break;
            }
        }

        if (currentLocalIndex < 0)
            return FocusGeometryReadStatus.Ready;

        var rects = new List<Rect>(map.Count);
        foreach (var entry in map) rects.Add(entry.Rect);

        var direction = key switch
        {
            VirtualKey.Left => Layout.FocusNavigationDirection.Left,
            VirtualKey.Right => Layout.FocusNavigationDirection.Right,
            VirtualKey.Up => Layout.FocusNavigationDirection.Up,
            VirtualKey.Down => Layout.FocusNavigationDirection.Down,
            _ => Layout.FocusNavigationDirection.Down,
        };

        int nextLocalIndex = FocusNavigationHelper.MoveSpatial(rects, currentLocalIndex, direction);
        if (nextLocalIndex < 0)
            return FocusGeometryReadStatus.Ready;

        if (!IsCurrentScrollTarget(snapshot, generation, revision))
            return FocusGeometryReadStatus.Stale;

        _focusedItemIndex = map[nextLocalIndex].Index;
        _focusResumeItemIndex = -1;
        _lastFocusNavigationHandled = CommitFocusedItem(reverse: false);
        return FocusGeometryReadStatus.Ready;
    }

    private bool QueuePendingSpatialFocusKey(VirtualKey key)
    {
        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return false;
        if (_isDisposed || _isUnloaded || _snapshot is not { } snapshot ||
            _focusableItems is not { } items || items.Count == 0)
        {
            return false;
        }

        long generation = Volatile.Read(ref _snapshotGeneration);
        if (_pendingSpatialFocusKeys.Count > 0 &&
            (_pendingSpatialFocusGeneration != generation ||
             !_hasPendingSpatialFocusOrigin ||
             GetCurrentFocusableIdentity() is not { } current ||
             !SameFocusableIdentity(current, _pendingSpatialFocusOrigin)))
        {
            ClearPendingSpatialFocusKeys();
        }

        if (_pendingSpatialFocusKeys.Count >= MaxPendingSpatialFocusKeys)
            return false; // Leave this repeated key unhandled so its parent can act on it.

        if (!_hasPendingSpatialFocusOrigin)
        {
            if (GetCurrentFocusableIdentity() is not { } origin)
                return false;
            _pendingSpatialFocusOrigin = origin;
            _pendingSpatialFocusGeneration = generation;
            _hasPendingSpatialFocusOrigin = true;
        }

        _pendingSpatialFocusKeys.Enqueue(key);
        StartImageRelayoutRecovery();
        if (!HasImageRelayoutRecoveryClock)
        {
            ClearPendingSpatialFocusKeys();
            return false;
        }

        return true;
    }

    private void ProcessPendingFocusGeometry()
    {
        if (_processingPendingFocusGeometry || _isDisposed || _isUnloaded || _snapshot is not { } snapshot)
            return;

        if (RequestGeometryRecoveryIfRequired(snapshot))
        {
            ClearPendingFocusGeometryWork();
            return;
        }

        _processingPendingFocusGeometry = true;
        try
        {
            if (_pendingSpatialFocusKeys.Count > 0)
            {
                if (_pendingSpatialFocusGeneration != Volatile.Read(ref _snapshotGeneration) ||
                    !_hasPendingSpatialFocusOrigin ||
                    GetCurrentFocusableIdentity() is not { } current ||
                    !SameFocusableIdentity(current, _pendingSpatialFocusOrigin))
                {
                    ClearPendingSpatialFocusKeys();
                }
                else
                {
                    VirtualKey key = _pendingSpatialFocusKeys.Peek();
                    long queueEpoch = _pendingSpatialFocusQueueEpoch;
                    FocusGeometryReadStatus status = TryProcessFocusSpatialKey(key);
                    if (status == FocusGeometryReadStatus.Ready &&
                        queueEpoch == _pendingSpatialFocusQueueEpoch &&
                        _pendingSpatialFocusKeys.Count > 0 &&
                        _pendingSpatialFocusKeys.Peek() == key)
                    {
                        _pendingSpatialFocusKeys.Dequeue();
                        if (_pendingSpatialFocusKeys.Count == 0)
                        {
                            _hasPendingSpatialFocusOrigin = false;
                        }
                        else if (GetCurrentFocusableIdentity() is { } updatedOrigin)
                        {
                            _pendingSpatialFocusOrigin = updatedOrigin;
                        }
                        else
                        {
                            ClearPendingSpatialFocusKeys();
                        }
                    }
                }
            }

            if (_hasPendingFocusResumePoint)
            {
                long generation = Volatile.Read(ref _snapshotGeneration);
                if (_pendingFocusResumeGeneration != generation)
                {
                    _hasPendingFocusResumePoint = false;
                    _focusResumeItemIndex = -1;
                }
                else
                {
                    FocusGeometryReadStatus status = TryCaptureFocusableRectMap(
                        snapshot,
                        generation,
                        snapshot.LayoutRevision,
                        _focusableItems,
                        out List<(int Index, Rect Rect)> map);
                    if (status == FocusGeometryReadStatus.Ready)
                    {
                        Point point = _pendingFocusResumePoint;
                        _hasPendingFocusResumePoint = false;
                        _focusResumeItemIndex = FindNearestFocusableItem(map, point);
                    }
                    else if (status == FocusGeometryReadStatus.Stale &&
                             generation != Volatile.Read(ref _snapshotGeneration))
                    {
                        _hasPendingFocusResumePoint = false;
                        _focusResumeItemIndex = -1;
                    }
                }
            }

            if (_pendingFocusGeometryRefresh && _pendingSpatialFocusKeys.Count == 0)
            {
                _pendingFocusGeometryRefresh = false;
                // A reveal waiting for settled geometry must not
                // override a scroll the user requested since focus moved.
                if (IsFocusRevealCurrent())
                    _ = ScrollFocusedItemIntoView();
                UpdateFocusRing();
            }
        }
        finally
        {
            _processingPendingFocusGeometry = false;
        }
    }

    private FocusGeometryReadStatus TryProcessFocusSpatialKey(VirtualKey key)
    {
        if (key is VirtualKey.Home or VirtualKey.End)
            return TryScrollFocusedHorizontalOverflowToEdge(key == VirtualKey.End);

        if (key is VirtualKey.Left or VirtualKey.Right)
        {
            HorizontalArrowHandlingOrder order =
                MarkdownKeyboardInputPolicy.GetHorizontalArrowHandlingOrder(
                    GetCurrentFocusableIdentity()?.Kind);
            FocusGeometryReadStatus first = order == HorizontalArrowHandlingOrder.OverflowThenSpatial
                ? TryScrollFocusedHorizontalOverflow(key)
                : TryMoveFocusSpatial(key);
            if (first != FocusGeometryReadStatus.Ready || _lastFocusNavigationHandled)
                return first;
            return order == HorizontalArrowHandlingOrder.OverflowThenSpatial
                ? TryMoveFocusSpatial(key)
                : TryScrollFocusedHorizontalOverflow(key);
        }

        return TryMoveFocusSpatial(key);
    }

    private static bool SameFocusableIdentity(Layout.FocusableItem left, Layout.FocusableItem right) =>
        left.BlockIndex == right.BlockIndex &&
        left.InlineIndex == right.InlineIndex &&
        left.Kind == right.Kind;

    private void ClearPendingSpatialFocusKeys()
    {
        ++_pendingSpatialFocusQueueEpoch;
        _pendingSpatialFocusKeys.Clear();
        _hasPendingSpatialFocusOrigin = false;
        _pendingSpatialFocusGeneration = 0;
    }

    private void ClearPendingFocusGeometryWork()
    {
        ClearPendingSpatialFocusKeys();
        _pendingFocusGeometryRefresh = false;
        _hasPendingFocusResumePoint = false;
        _pendingFocusResumeGeneration = 0;
        _hasPendingHorizontalOverflowRefresh = false;
        _pendingHorizontalOverflowRefreshGeneration = 0;
        ++_focusRingPublicationTicket;
    }

    private void RequestFocusGeometryRetry()
    {
        if (_isDisposed || _isUnloaded)
            return;

        if (RequestGeometryRecoveryIfRequired(_snapshot))
            return;

        _pendingFocusGeometryRefresh = true;
        StartImageRelayoutRecovery();
        if (!HasImageRelayoutRecoveryClock)
        {
            _pendingFocusGeometryRefresh = false;
            _hasPendingFocusResumePoint = false;
            _focusResumeItemIndex = -1;
            RequestRebuild(RebuildReason.Full);
        }
    }

    private static int FindNearestFocusableItem(
        IReadOnlyList<(int Index, Rect Rect)> map,
        Point point)
    {
        if (map.Count == 0)
            return -1;
        var rects = new List<Rect>(map.Count);
        foreach (var entry in map)
            rects.Add(entry.Rect);
        int nearest = FocusNavigationHelper.FindNearestIndex(rects, point);
        return nearest >= 0 ? map[nearest].Index : -1;
    }

    private bool CommitFocusedItem(bool reverse)
    {
        var items = _focusableItems;
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null ||
            items is null ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            _pendingLazyFocus = null;
            return false;
        }

        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        if (!ReferenceEquals(snapshot, _snapshot))
            return true;

        Layout.FocusableItem item = items[_focusedItemIndex];
        FocusTargetLayoutState layoutState = snapshot.TryGetFocusTargetLayoutState(
            item.BlockIndex,
            out LazyLayoutBand estimatedBand);
        if (!ReferenceEquals(snapshot, _snapshot) ||
            snapshotGeneration != Volatile.Read(ref _snapshotGeneration))
        {
            return true;
        }

        if (layoutState is FocusTargetLayoutState.RequiresRealization or FocusTargetLayoutState.Busy)
        {
            // Keep native focus on the renderer while the target's top-level
            // block is measured. This consumes the Tab instead of allowing XAML
            // to advance beyond the renderer at a lazy-layout boundary.
            _pendingLazyFocus = new PendingLazyFocus(
                snapshot,
                snapshotGeneration,
                item,
                reverse);
            var pending = _pendingLazyFocus.Value;
            _ = Focus(FocusState.Keyboard);
            if (!IsCurrentPendingLazyFocus(pending))
                return true;
            HideFocusRing();
            if (!IsCurrentPendingLazyFocus(pending))
                return true;

            if (layoutState == FocusTargetLayoutState.RequiresRealization)
            {
                ScrollLazyFocusTargetIntoView(estimatedBand, reverse);
                if (!IsCurrentPendingLazyFocus(pending))
                    return true;
                var realizationBand = LazyLayoutBand.FromViewport(
                    estimatedBand.Top,
                    Math.Max(1, estimatedBand.Bottom - estimatedBand.Top),
                    LazyLayoutOverscanPx);
                LazyLayoutBand visibleVectorBand = GetCurrentVisibleVectorBand();
                QueueLazyLayoutForBand(
                    snapshot,
                    IncludeBand(realizationBand, visibleVectorBand),
                    visibleVectorBand,
                    preserveScrollAnchor: _scroll is not null);
            }

            return true;
        }

        if (layoutState == FocusTargetLayoutState.Unavailable)
        {
            // A retiring snapshot or malformed custom block index must never
            // turn an already-consumed Tab into focus escape.
            _pendingLazyFocus = null;
            _ = Focus(FocusState.Keyboard);
            if (!ReferenceEquals(snapshot, _snapshot) ||
                snapshotGeneration != Volatile.Read(ref _snapshotGeneration))
            {
                return true;
            }
            HideFocusRing();
            return true;
        }

        _pendingLazyFocus = null;
        bool scrolled = BeginFocusedItemReveal();
        FocusCurrentItem(reverse);
        if (scrolled)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isUnloaded) return;
                RealizeVisibleEmbeds();
                FocusCurrentItem(reverse);
            });
        }
        return true;
    }

    private bool ResumePendingLazyFocus(LayoutSnapshot snapshot)
    {
        if (_pendingLazyFocus is not { } pending)
            return false;

        if (!ReferenceEquals(snapshot, pending.Snapshot) ||
            pending.SnapshotGeneration != Volatile.Read(ref _snapshotGeneration))
        {
            _pendingLazyFocus = null;
            return false;
        }

        int index = FindFocusableItemIndex(_focusableItems, pending.Item);
        if (index < 0)
        {
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            return false;
        }

        _focusedItemIndex = index;
        return CommitFocusedItem(pending.Reverse);
    }

    private Layout.FocusableItem? GetCurrentFocusableIdentity()
    {
        var items = _focusableItems;
        return items is not null &&
               _focusedItemIndex >= 0 &&
               _focusedItemIndex < items.Count
            ? items[_focusedItemIndex]
            : null;
    }

    private static int FindFocusableItemIndex(
        System.Collections.Generic.IReadOnlyList<Layout.FocusableItem>? items,
        Layout.FocusableItem target)
    {
        if (items is null)
            return -1;

        for (int index = 0; index < items.Count; index++)
        {
            Layout.FocusableItem candidate = items[index];
            if (candidate.BlockIndex == target.BlockIndex &&
                candidate.InlineIndex == target.InlineIndex &&
                candidate.Kind == target.Kind)
            {
                return index;
            }
        }

        return -1;
    }

    private void ScrollLazyFocusTargetIntoView(LazyLayoutBand band, bool reverse)
    {
        TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        const double margin = 24;
        if (band.Top >= viewportTop + margin && band.Bottom <= viewportBottom - margin)
            return;

        if (_scroll is not null)
        {
            double target = reverse
                ? band.Bottom - viewportHeight + margin
                : band.Top - margin;
            double maximum = Math.Max(0, (_snapshot?.Size.Height ?? band.Bottom) - viewportHeight);
            _scroll.ChangeView(
                null,
                Math.Clamp(target, 0, maximum),
                null,
                disableAnimation: false);
            return;
        }

        var targetRect = new Rect(
            0,
            band.Top,
            Math.Max(1, viewportWidth),
            Math.Max(1, band.Bottom - band.Top));
        BringDocumentRectIntoExternalViewport(
            targetRect,
            animate: true,
            alignmentRatio: reverse ? 1 : 0);
    }

    /// <summary>Fires LinkClick for the currently focused painted link.</summary>
    private bool ActivateFocusedItem()
    {
        if (_pendingLazyFocus is not null)
            return true;

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        var item = items[_focusedItemIndex];
        MarkdownInputModifiers modifiers = GetCurrentInputModifiers();
        if (item.IsVectorSemantic &&
            TryGetVectorSemanticForFocusable(item, out var vectorBox, out int semanticIndex) &&
            vectorBox.TryGetLink(semanticIndex, out var vectorLink) &&
            vectorLink is not null)
        {
            MarkdownVectorSemanticItem? semantic = vectorBox.GetSemanticItem(semanticIndex);
            return ActivateLinkTarget(
                new LinkTarget(
                    vectorLink.Target ?? string.Empty,
                    semantic?.Description,
                    semantic?.SourceSpan ?? vectorBox.SourceRange,
                    null,
                    vectorLink.Action,
                    vectorLink.External),
                MarkdownLinkInputKind.Keyboard,
                modifiers);
        }

        if (!item.IsLink)
        {
            return TryFocusHostedElement(item, reverse: false) switch
            {
                HostedFocusResult.Focused => true,
                HostedFocusResult.Deferred => true,
                HostedFocusResult.Stale => true,
                _ => false,
            };
        }

        // Resolve a text link or linked atomic inline (image/math) and use the
        // same activation path as pointer input.
        if (_snapshot is null) return false;
        var pos = new DocumentPosition(item.BlockIndex, item.InlineIndex, 0);
        if (FindLinkTargetAt(pos) is { } link)
        {
            return ActivateLinkTarget(link, MarkdownLinkInputKind.Keyboard, modifiers);
        }
        return false;
    }

    private void FocusCurrentItem(bool reverse = false)
    {
        if (_pendingLazyFocus is not null)
        {
            HideFocusRing();
            return;
        }

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
        {
            UpdateFocusRing();
            return;
        }

        var item = items[_focusedItemIndex];
        if (!item.IsLink)
        {
            HostedFocusResult result = TryFocusHostedElement(item, reverse);
            if (result == HostedFocusResult.Focused)
            {
                if (_focusRing is not null) _focusRing.Visibility = Visibility.Collapsed;
                return;
            }

            // A pending realization is resumed by the normal layout path. A
            // stale tuple means a callback changed the active focus/snapshot;
            // neither case may write the old caller's focus ring or fallback.
            if (result is HostedFocusResult.Deferred or HostedFocusResult.Stale)
                return;
        }

        if (TryCaptureHostedFocusTuple(out HostedFocusTuple fallbackTuple) == HostedFocusPlanStatus.Stale ||
            !IsCurrentHostedFocusRequest(fallbackTuple, item))
        {
            return;
        }

        Focus(FocusState.Keyboard);
        if (!IsCurrentHostedFocusRequest(fallbackTuple, item))
            return;
        UpdateFocusRing();
        if (!IsCurrentHostedFocusRequest(fallbackTuple, item))
            return;
        NotifyFocusedItemAutomation();
    }

    private void NotifyFocusedItemAutomation()
    {
        // Virtual focus is observable through HasKeyboardFocus even when no UIA
        // client subscribes to focus events. Avoid entering UIA from the input
        // path unless an assistive-technology listener actually needs the event;
        // this also prevents a queued focus notification from racing a subsequent
        // key press and re-entering this provider while the client is polling it.
        if (!AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged))
            return;

        void Raise()
        {
            if (_pendingLazyFocus is not null ||
                !AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged))
                return;

            var peer = FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(this) as MarkdownAutomationPeer;
            if (TryGetFocusedLinkedAtomic(out var inline, out InlineRun linkedRun))
            {
                switch (linkedRun)
                {
                    case LinkRun link:
                        peer?.RaiseFocusForLink(inline, link);
                        break;
                    case InlineImageRun image:
                        peer?.RaiseFocusForLinkedImage(inline, image);
                        break;
                    case InlineVectorSceneRun inlineScene:
                        peer?.RaiseFocusForInlineVectorScene(inlineScene);
                        break;
                }
            }
            else if (TryGetFocusedVectorSemantic(out var vectorBox, out int semanticIndex))
            {
                peer?.RaiseFocusForVectorSemantic(vectorBox, semanticIndex);
            }
            else if (TryGetFocusedHorizontalOverflow(out var overflow))
            {
                peer?.RaiseFocusForHorizontalOverflow(overflow);
            }
        }

        // Let WinUI finish the real focus transition to the renderer first,
        // then publish the virtual hyperlink focus event. If we raise inside
        // GotFocus, Narrator can observe the root document focus event last and
        // announce the whole renderer instead of the focused painted link.
        if (DispatcherQueue is { } dispatcher)
            dispatcher.TryEnqueue(Raise);
        else
            Raise();
    }

    /// <summary>
    /// Places or moves the focus-ring <see cref="Border"/> on the overlay to
    /// surround the currently focused item. Hides the ring if nothing is focused.
    /// </summary>
    private void UpdateFocusRing()
    {
        if (_pendingLazyFocus is not null)
        {
            HideFocusRing();
            return;
        }

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
        {
            HideFocusRing();
            return;
        }

        var item = items[_focusedItemIndex];
        if (!item.IsLink && !item.IsVectorSemantic && !item.IsHorizontalOverflow)
        {
            HideFocusRing();
            return;
        }

        if (_snapshot is not { } snapshot)
        {
            HideFocusRing();
            return;
        }

        long generation = Volatile.Read(ref _snapshotGeneration);
        FocusGeometryReadStatus status = TryCaptureFocusableItemRect(
            snapshot, generation, snapshot.LayoutRevision, item, out Rect? rect);
        if (status != FocusGeometryReadStatus.Ready)
        {
            RequestFocusGeometryRetry();
            return;
        }

        if (rect is not { Width: > 0, Height: > 0 })
        {
            HideFocusRing();
            return;
        }

        QueueFocusRingPublication(generation, item, rect);
    }

    private void QueueFocusRingPublication(
        long snapshotGeneration,
        Layout.FocusableItem? item,
        Rect? rect)
    {
        long ticket = ++_focusRingPublicationTicket;
        bool queued = false;
        try
        {
            queued = DispatcherQueue is { } dispatcher &&
                dispatcher.TryEnqueue(() => ApplyFocusRingPublication(
                    ticket, snapshotGeneration, item, rect));
        }
        catch (Exception)
        {
        }
        if (!queued)
        {
            RequestFocusGeometryRetry();
        }
    }

    private void ApplyFocusRingPublication(
        long ticket,
        long snapshotGeneration,
        Layout.FocusableItem? item,
        Rect? rect)
    {
        if (ticket != _focusRingPublicationTicket ||
            _isDisposed || _isUnloaded ||
            snapshotGeneration != Volatile.Read(ref _snapshotGeneration))
        {
            return;
        }

        if (item is not { } expected ||
            rect is not { Width: > 0, Height: > 0 } r ||
            GetCurrentFocusableIdentity() is not { } current ||
            !SameFocusableIdentity(current, expected))
        {
            if (_focusRing is not null)
                _focusRing.Visibility = Visibility.Collapsed;
            InvalidateInteractiveTextAdorner();
            return;
        }

        if (!EnsureFocusRing())
            return;
        var focusRing = _focusRing!;

        var focusVisual = _themeSnapshot?.FocusVisualColor
                          ?? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        // Allocate a new SolidColorBrush only when the focus color actually changes;
        // avoid per-keystroke GC pressure during Tab traversal.
        if (focusRing.BorderBrush is null || focusVisual != _focusRingBrushColor)
        {
            _focusRingBrushColor = focusVisual;
            focusRing.BorderBrush = new SolidColorBrush(focusVisual);
        }

        const double pad = 2.0;
        Canvas.SetLeft(focusRing, r.X - pad);
        Canvas.SetTop(focusRing, r.Y - pad);
        focusRing.Width  = r.Width  + pad * 2;
        focusRing.Height = r.Height + pad * 2;
        focusRing.Visibility = Visibility.Visible;
        InvalidateInteractiveTextAdorner();
    }

    private void HideFocusRing()
    {
        QueueFocusRingPublication(
            Volatile.Read(ref _snapshotGeneration),
            item: null,
            rect: null);
    }

    private bool EnsureFocusRing()
    {
        if (_focusRing is not null)
            return true;

        if (_overlay is null || _isUnloaded || XamlRoot is null)
            return false;

        var focusRing = new Microsoft.UI.Xaml.Controls.Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        };
        Canvas.SetZIndex(focusRing, 1);

        try
        {
            _overlay.Children.Add(focusRing);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] focus ring attach failed: {ex.Message}");
            return false;
        }

        _focusRing = focusRing;
        return true;
    }

    private Rect? GetFocusableItemRect(Layout.FocusableItem item)
    {
        if (_snapshot is not { } snapshot)
            return null;
        return TryCaptureFocusableItemRect(
            snapshot,
            Volatile.Read(ref _snapshotGeneration),
            snapshot.LayoutRevision,
            item,
            out Rect? rect) == FocusGeometryReadStatus.Ready
            ? rect
            : null;
    }

    private void RememberFocusResumePoint(Point documentPoint)
    {
        if (_snapshot is not { } snapshot)
        {
            _focusResumeItemIndex = -1;
            _hasPendingFocusResumePoint = false;
            return;
        }

        long generation = Volatile.Read(ref _snapshotGeneration);
        FocusGeometryReadStatus status = TryCaptureFocusableRectMap(
            snapshot, generation, snapshot.LayoutRevision, _focusableItems,
            out List<(int Index, Rect Rect)> map);
        if (status != FocusGeometryReadStatus.Ready)
        {
            _pendingFocusResumePoint = documentPoint;
            _pendingFocusResumeGeneration = generation;
            _hasPendingFocusResumePoint = true;
            RequestFocusGeometryRetry();
            return;
        }

        _hasPendingFocusResumePoint = false;
        _focusResumeItemIndex = FindNearestFocusableItem(map, documentPoint);
    }

    private FocusGeometryReadStatus TryCaptureFocusableItemRect(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        Layout.FocusableItem item,
        out Rect? rect)
    {
        rect = null;
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return FocusGeometryReadStatus.Stale;
        if (!snapshot.TryBeginInteraction())
            return FocusGeometryReadStatus.Busy;

        bool captured = false;
        try
        {
            if (IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            {
                rect = GetFocusableItemRectFromSnapshotNoLock(snapshot, item);
                captured = true;
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        return captured && IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision)
            ? FocusGeometryReadStatus.Ready
            : FocusGeometryReadStatus.Stale;
    }

    private FocusGeometryReadStatus TryCaptureFocusableRectMap(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        IReadOnlyList<Layout.FocusableItem>? items,
        out List<(int Index, Rect Rect)> result)
    {
        result = new List<(int Index, Rect Rect)>();
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return FocusGeometryReadStatus.Stale;
        if (items is null || items.Count == 0)
            return FocusGeometryReadStatus.Ready;
        if (!snapshot.TryBeginInteraction())
            return FocusGeometryReadStatus.Busy;

        bool captured = false;
        try
        {
            if (IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (GetFocusableItemRectFromSnapshotNoLock(snapshot, items[i]) is
                        { Width: > 0, Height: > 0 } rect)
                    {
                        result.Add((i, rect));
                    }
                }
                captured = true;
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        return captured && IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision)
            ? FocusGeometryReadStatus.Ready
            : FocusGeometryReadStatus.Stale;
    }

    private Rect? GetFocusableItemRectFromSnapshotNoLock(
        LayoutSnapshot snapshot,
        Layout.FocusableItem item)
    {
        Rect? physicalRect = null;
        if (item.IsHorizontalOverflow &&
            snapshot.TryGetHorizontalOverflowForBlockIndex(item.BlockIndex, out var overflow) &&
            overflow is not null)
        {
            physicalRect = overflow.HorizontalViewportBounds;
        }
        else
        {
            foreach (var block in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(block);
                if (block is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == item.BlockIndex)
                {
                    physicalRect = icb.GetRunRect(item.InlineIndex);
                    break;
                }
                if (GetFocusableItemRectFromBlock(block, item) is { } nested)
                {
                    physicalRect = nested;
                    break;
                }
            }
        }

        return physicalRect is { } rect
            ? snapshot.ToLogicalRectForBlockIndexUnderPaint(item.BlockIndex, rect)
            : null;
    }

    private static Rect? GetFocusableItemRectFromBlock(BlockBox box, Layout.FocusableItem item)
    {
        if (item.IsHorizontalOverflow &&
            box.BlockIndex == item.BlockIndex &&
            box is IHorizontalOverflowBox overflow)
        {
            return overflow.HorizontalViewportBounds;
        }

        if (box is Layout.Boxes.DeclarativeHostedElementBox hosted &&
            item.IsDeclarativeHostedElement &&
            hosted.BlockIndex == item.BlockIndex)
        {
            return new Rect(
                hosted.Bounds.X + hosted.Margin.Left,
                hosted.Bounds.Y + hosted.Margin.Top,
                Math.Max(0, hosted.Bounds.Width - hosted.Margin.Left - hosted.Margin.Right),
                Math.Max(0, hosted.Bounds.Height - hosted.Margin.Top - hosted.Margin.Bottom));
        }
        if (box is Layout.Boxes.EmbedBox eb && eb.BlockIndex == item.BlockIndex)
        {
            return new Rect(
                eb.Bounds.X + eb.Margin.Left,
                eb.Bounds.Y + eb.Margin.Top,
                eb.Bounds.Width - eb.Margin.Left - eb.Margin.Right,
                eb.Bounds.Height - eb.Margin.Top - eb.Margin.Bottom);
        }
        if (box is Layout.Boxes.CodeBlockBox codeBlock && codeBlock.BlockIndex == item.BlockIndex)
        {
            if (item.IsCodeBlockCopy)
                return codeBlock.CopyButtonBounds;
        }
        if (box is Layout.Boxes.VectorSceneBox vector &&
            item.IsVectorSemantic &&
            vector.BlockIndex == item.BlockIndex)
        {
            return vector.GetSemanticBounds(item.InlineIndex);
        }
        if (box is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == item.BlockIndex)
            return icb.GetRunRect(item.InlineIndex);
        if (box is Layout.Boxes.ListItemBox lib)
        {
            return GetFocusableItemRectFromBlock(lib.Marker, item)
                ?? GetFocusableItemRectFromBlock(lib.Content, item);
        }
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                var r = GetFocusableItemRectFromBlock(c, item);
                if (r is not null) return r;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                var r = GetFocusableItemRectFromBlock(cell, item);
                if (r is not null) return r;
            }
        }
        return null;
    }

    /// <summary>Scrolls the focused item into view if it's outside the current viewport.</summary>
    /// <summary>Records an explicit scroll request; pending focus reveals yield to it.</summary>
    private void NoteViewportRequest() => _viewportRequestSerial++;

    /// <summary>
    /// Starts revealing the focused item from the current viewport. Geometry
    /// retries continue it only while <see cref="IsFocusRevealCurrent"/> holds.
    /// </summary>
    private bool BeginFocusedItemReveal()
    {
        TryGetViewport(out double top, out _, out _);
        _focusReveal = new FocusRevealOrigin(_viewportRequestSerial, top, null);
        return ScrollFocusedItemIntoView();
    }

    private bool IsFocusRevealCurrent()
    {
        TryGetViewport(out double top, out _, out _);
        if (FocusRevealPolicy.IsCurrent(_focusReveal, _viewportRequestSerial, top))
            return true;
        _focusReveal = null;
        return false;
    }

    private bool ScrollFocusedItemIntoView()
    {
        var items = _focusableItems;
        int focusedIndex = _focusedItemIndex;
        if (items is null || focusedIndex < 0 || focusedIndex >= items.Count) return false;
        var item = items[_focusedItemIndex];
        if (_snapshot is not { } snapshot)
            return false;

        long generation = Volatile.Read(ref _snapshotGeneration);
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
        {
            RequestFocusGeometryRetry();
            return false;
        }

        Rect? capturedRect = null;
        HorizontalOverflowVisualState visualState = default;
        bool horizontalScrolled = false;
        double oldHorizontalOffset = 0;
        bool captured = false;
        try
        {
            if (IsCurrentScrollTarget(snapshot, generation, revision) &&
                focusedIndex < items.Count &&
                SameFocusableIdentity(items[focusedIndex], item))
            {
                capturedRect = GetFocusableItemRectFromSnapshotNoLock(snapshot, item);
                captured = true;
                if (capturedRect is { } rect &&
                    snapshot.TryGetHorizontalOverflowForBlockIndex(item.BlockIndex, out var overflow) &&
                    overflow is not null)
                {
                    Rect localViewport = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                        item.BlockIndex, overflow.HorizontalViewportBounds);
                    const double localMargin = 8.0;
                    double physicalShift = 0;
                    if (rect.Left < localViewport.Left + localMargin)
                        physicalShift = localViewport.Left + localMargin - rect.Left;
                    else if (rect.Right > localViewport.Right - localMargin)
                        physicalShift = localViewport.Right - localMargin - rect.Right;

                    if (Math.Abs(physicalShift) > 0.1)
                    {
                        oldHorizontalOffset = overflow.HorizontalOffset;
                        double logicalShift = overflow.IsRightToLeft ? physicalShift : -physicalShift;
                        if (snapshot.TrySetHorizontalOffset(overflow, oldHorizontalOffset + logicalShift))
                        {
                            horizontalScrolled = true;
                            _ = TryCaptureHorizontalOverflowVisualStateNoLock(
                                snapshot, generation, revision, overflow, oldHorizontalOffset, out visualState);
                            capturedRect = GetFocusableItemRectFromSnapshotNoLock(snapshot, item) ?? rect;
                        }
                    }
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!captured || !IsCurrentScrollTarget(snapshot, generation, revision))
        {
            RequestFocusGeometryRetry();
            return false;
        }

        if (horizontalScrolled)
        {
            if (visualState.Snapshot is not null)
                PublishHorizontalOverflowVisualState(visualState);
            else
                QueueHorizontalOverflowVisualRetry(snapshot, generation, item.BlockIndex,
                    oldHorizontalOffset);
        }

        if (capturedRect is not { } detachedRect)
            return horizontalScrolled;

        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        const double margin = 24.0;
        bool verticalNeeded = detachedRect.Top < top + margin || detachedRect.Bottom > bottom - margin;
        if (verticalNeeded && !QueueFocusedItemScroll(snapshot, generation, revision, item, detachedRect))
            RequestFocusGeometryRetry();
        return horizontalScrolled || verticalNeeded;
    }

    private bool QueueFocusedItemScroll(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        Layout.FocusableItem item,
        Rect rect)
    {
        var weakSnapshot = new WeakReference<LayoutSnapshot>(snapshot);
        try
        {
            return DispatcherQueue is { } dispatcher && dispatcher.TryEnqueue(() =>
                ApplyFocusedItemScroll(weakSnapshot, snapshotGeneration, layoutRevision, item, rect));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ApplyFocusedItemScroll(
        WeakReference<LayoutSnapshot> weakSnapshot,
        long snapshotGeneration,
        long layoutRevision,
        Layout.FocusableItem item,
        Rect rect)
    {
        if (_isDisposed || _isUnloaded ||
            !weakSnapshot.TryGetTarget(out LayoutSnapshot? snapshot) ||
            !ReferenceEquals(snapshot, _snapshot) ||
            snapshotGeneration != Volatile.Read(ref _snapshotGeneration))
        {
            return;
        }
        if (snapshot.LayoutRevision != layoutRevision)
        {
            RequestFocusGeometryRetry();
            return;
        }
        if (GetCurrentFocusableIdentity() is not { } current ||
            !SameFocusableIdentity(current, item))
            return;
        if (!IsFocusRevealCurrent())
            return;

        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        const double margin = 24.0;
        double? revealTop = null;
        if (rect.Top < top + margin)
        {
            revealTop = _scroll is not null ? Math.Max(0, rect.Top - margin) : rect.Top;
            _ = _scroll is not null
                ? _scroll.ChangeView(null, Math.Max(0, rect.Top - margin), null, disableAnimation: false)
                : BringDocumentRectIntoExternalViewport(rect, animate: true);
        }
        else if (rect.Bottom > bottom - margin)
        {
            revealTop = _scroll is not null
                ? rect.Bottom - viewportHeight + margin
                : rect.Bottom - viewportHeight;
            _ = _scroll is not null
                ? _scroll.ChangeView(null, rect.Bottom - viewportHeight + margin, null, disableAnimation: false)
                : BringDocumentRectIntoExternalViewport(rect, animate: true, alignmentRatio: 1);
        }
        // The reveal's own animated scroll toward this top keeps it current.
        // Owned: ChangeView's target. Ancestor: StartBringIntoView aligns the
        // rect's top (ratio 0) or bottom (ratio 1) to the viewport edge.
        if (revealTop is { } requested && _focusReveal is { } reveal)
            _focusReveal = reveal with { RevealTop = requested };
    }

    private HostedFocusPlanStatus TryCaptureHostedFocusTuple(out HostedFocusTuple tuple)
    {
        tuple = default;
        if (_isDisposed || _isUnloaded || _snapshot is not { } snapshot ||
            _focusableItems is not { } items)
        {
            return HostedFocusPlanStatus.Stale;
        }

        long generation = Volatile.Read(ref _snapshotGeneration);
        long revision = snapshot.LayoutRevision;
        long epoch = Volatile.Read(ref _hostedFocusPlanEpoch);
        tuple = new HostedFocusTuple(snapshot, generation, revision, epoch, items);
        if (!ReferenceEquals(snapshot, _snapshot) ||
            generation != Volatile.Read(ref _snapshotGeneration))
        {
            return HostedFocusPlanStatus.Stale;
        }

        long publishedEpoch = Volatile.Read(ref _publishedHostedFocusPlanEpoch);
        if (publishedEpoch != epoch ||
            Volatile.Read(ref _publishedHostedFocusPlanGeneration) != generation ||
            Volatile.Read(ref _publishedHostedFocusPlanRevision) != revision ||
            _appliedLazyLayoutRevision != revision ||
            snapshot.LayoutRevision != revision)
        {
            return HostedFocusPlanStatus.LayoutPending;
        }

        return HostedFocusPlanStatus.Current;
    }

    private bool IsCurrentHostedFocusTuple(in HostedFocusTuple tuple) =>
        !_isDisposed && !_isUnloaded &&
        ReferenceEquals(tuple.Snapshot, _snapshot) &&
        tuple.SnapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
        tuple.LayoutRevision == tuple.Snapshot.LayoutRevision &&
        tuple.PlanEpoch == Volatile.Read(ref _hostedFocusPlanEpoch) &&
        tuple.PlanEpoch == Volatile.Read(ref _publishedHostedFocusPlanEpoch) &&
        tuple.SnapshotGeneration == Volatile.Read(ref _publishedHostedFocusPlanGeneration) &&
        tuple.LayoutRevision == Volatile.Read(ref _publishedHostedFocusPlanRevision) &&
        tuple.LayoutRevision == _appliedLazyLayoutRevision &&
        ReferenceEquals(tuple.Items, _focusableItems);

    private bool IsCurrentHostedFocusRequest(
        in HostedFocusTuple tuple,
        Layout.FocusableItem item) =>
        !_isDisposed && !_isUnloaded &&
        ReferenceEquals(tuple.Snapshot, _snapshot) &&
        tuple.SnapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
        tuple.LayoutRevision == tuple.Snapshot.LayoutRevision &&
        tuple.PlanEpoch == Volatile.Read(ref _hostedFocusPlanEpoch) &&
        ReferenceEquals(tuple.Items, _focusableItems) &&
        IsFocusedIdentity(tuple.Items, _focusedItemIndex, item);

    private static bool SamePendingLazyFocus(
        in PendingLazyFocus left,
        in PendingLazyFocus right) =>
        ReferenceEquals(left.Snapshot, right.Snapshot) &&
        left.SnapshotGeneration == right.SnapshotGeneration &&
        left.Reverse == right.Reverse &&
        SameFocusableIdentity(left.Item, right.Item);

    private bool IsCurrentPendingLazyFocus(in PendingLazyFocus pending) =>
        !_isDisposed && !_isUnloaded &&
        ReferenceEquals(pending.Snapshot, _snapshot) &&
        pending.SnapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
        _pendingLazyFocus is { } current &&
        SamePendingLazyFocus(current, pending);

    private bool IsCurrentHostedFocusOperation(
        in HostedFocusTuple tuple,
        Layout.FocusableItem item) =>
        IsCurrentHostedFocusTuple(tuple) &&
        IsFocusedIdentity(tuple.Items, _focusedItemIndex, item);

    private static bool IsFocusedIdentity(
        System.Collections.Generic.IReadOnlyList<Layout.FocusableItem> items,
        int focusedIndex,
        Layout.FocusableItem item) =>
        (uint)focusedIndex < (uint)items.Count &&
        SameFocusableIdentity(items[focusedIndex], item);

    private HostedFocusResult DeferHostedFocus(
        in HostedFocusTuple tuple,
        Layout.FocusableItem item,
        bool reverse)
    {
        if (!IsCurrentHostedFocusRequest(tuple, item))
            return HostedFocusResult.Stale;

        var pending = new PendingLazyFocus(
            tuple.Snapshot,
            tuple.SnapshotGeneration,
            item,
            reverse);
        _pendingLazyFocus = pending;
        _ = Focus(FocusState.Keyboard);

        // Focus is a native callback boundary. Do not hide the ring or report
        // deferred ownership if reentrancy changed the snapshot, focus item,
        // pending request, or plan attempt while it ran.
        if (!IsCurrentHostedFocusRequest(tuple, item) ||
            !IsCurrentPendingLazyFocus(pending))
        {
            return HostedFocusResult.Stale;
        }

        HideFocusRing();
        return HostedFocusResult.Deferred;
    }

    private HostedFocusResult TryFocusHostedElement(Layout.FocusableItem item, bool reverse)
    {
        HostedFocusPlanStatus status = TryCaptureHostedFocusTuple(out HostedFocusTuple tuple);
        if (status == HostedFocusPlanStatus.Stale ||
            !IsCurrentHostedFocusRequest(tuple, item))
        {
            return HostedFocusResult.Stale;
        }

        if (status == HostedFocusPlanStatus.LayoutPending)
            return DeferHostedFocus(tuple, item, reverse);

        FrameworkElement? element = FindRealizedHostedElement(item);
        if (!IsCurrentHostedFocusOperation(tuple, item))
            return HostedFocusResult.Stale;
        if (element is null)
            return HostedFocusResult.NotRealized;

        if (TryFocusHostedDescendant(element, reverse))
            return IsCurrentHostedFocusOperation(tuple, item)
                ? HostedFocusResult.Focused
                : HostedFocusResult.Stale;

        if (!IsCurrentHostedFocusOperation(tuple, item))
            return HostedFocusResult.Stale;

        bool focused = element.Focus(FocusState.Keyboard);
        if (!IsCurrentHostedFocusOperation(tuple, item))
            return HostedFocusResult.Stale;
        return focused ? HostedFocusResult.Focused : HostedFocusResult.NotRealized;
    }

    private FrameworkElement? FindRealizedHostedElement(Layout.FocusableItem item)
    {
        foreach (EmbedPlan plan in _embedPlans)
        {
            if (plan.FocusIdentity is { } identity &&
                SameFocusableIdentity(identity, item) &&
                plan.Realized is { } element)
            {
                return element;
            }
        }

        foreach (CodeBlockActionPlan plan in _codeBlockActionPlans)
        {
            if (plan.FocusIdentity is { } identity &&
                SameFocusableIdentity(identity, item) &&
                plan.Realized is { } element)
            {
                return element;
            }
        }

        return null;
    }

    private void RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan)
    {
        if (FocusState == FocusState.Unfocused ||
            plan.Realized is null ||
            plan.FocusIdentity is not { } planIdentity ||
            !_codeBlockActionPlans.Contains(plan) ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return;
        }

        var focusedItem = items[_focusedItemIndex];
        if (SameFocusableIdentity(focusedItem, planIdentity))
            _ = TryFocusHostedElement(focusedItem, reverse: false);
    }

    private void PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
    {
        if (_isUnloaded || _isDisposed)
            return;

        DependencyObject? focused = null;
        try
        {
            if (XamlRoot is not null)
                focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        }
        catch
        {
        }

        if (focused is null || !IsElementWithin(focused, element))
            return;

        // Keep the renderer's logical focus index while the native copy button
        // is recycled. If the action comes back into the realize band,
        // RestoreFocusedCodeBlockActionIfNeeded transfers focus back to it.
        _ = Focus(FocusState.Keyboard);
        UpdateFocusRing();
    }

    // These controls never cross a WinRT collection boundary. Private array
    // storage avoids generating projection adapters for this focus-only path.
    private sealed class HostedFocusableControlBuffer
    {
        private Control[] _items = Array.Empty<Control>();

        public int Count { get; private set; }

        public Control this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _items[index];
            }
        }

        public void Add(Control control)
        {
            if (Count == _items.Length)
            {
                int capacity = _items.Length == 0
                    ? 4
                    : _items.Length > Array.MaxLength / 2
                        ? Array.MaxLength
                        : _items.Length * 2;
                if (capacity <= Count)
                    throw new OutOfMemoryException();
                Array.Resize(ref _items, capacity);
            }

            _items[Count++] = control;
        }

        public int IndexOf(Control control)
        {
            for (int i = 0; i < Count; i++)
            {
                if (EqualityComparer<Control>.Default.Equals(_items[i], control))
                    return i;
            }

            return -1;
        }
    }

    private static bool TryMoveFocusWithinHostedEmbed(FrameworkElement host, object originalSource, bool reverse)
    {
        var focusables = CollectHostedFocusableControls(host);
        if (focusables.Count <= 1)
            return false;

        var current = FindCurrentHostedFocusable(host, originalSource, focusables);
        if (current is null)
            return false;

        int index = focusables.IndexOf(current);
        if (index < 0)
            return false;

        int next = reverse ? index - 1 : index + 1;
        if (next < 0 || next >= focusables.Count)
            return false;

        return focusables[next].Focus(FocusState.Keyboard);
    }

    private static bool TryFocusHostedDescendant(FrameworkElement host, bool reverse)
    {
        var focusables = CollectHostedFocusableControls(host);
        if (focusables.Count == 0)
            return false;

        var target = reverse ? focusables[^1] : focusables[0];
        return target.Focus(FocusState.Keyboard);
    }

    private static Control? FindCurrentHostedFocusable(
        FrameworkElement host,
        object originalSource,
        HostedFocusableControlBuffer focusables)
    {
        DependencyObject? focused = null;
        try
        {
            if (host.XamlRoot is not null)
                focused = FocusManager.GetFocusedElement(host.XamlRoot) as DependencyObject;
        }
        catch
        {
        }

        var current = FindFocusableAncestorWithin(host, focused);
        if (current is not null && ContainsHostedFocusable(focusables, current))
            return current;

        current = FindFocusableAncestorWithin(host, originalSource as DependencyObject);
        return current is not null && ContainsHostedFocusable(focusables, current) ? current : null;
    }

    private static bool ContainsHostedFocusable(HostedFocusableControlBuffer focusables, Control control)
    {
        for (int i = 0; i < focusables.Count; i++)
        {
            if (ReferenceEquals(focusables[i], control))
                return true;
        }

        return false;
    }

    private static Control? FindFocusableAncestorWithin(FrameworkElement host, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, host))
                return node is Control hostControl && IsHostedFocusableControl(hostControl) ? hostControl : null;

            if (node is Control control && IsHostedFocusableControl(control))
                return control;

            DependencyObject? parent = null;
            try { parent = VisualTreeHelper.GetParent(node); }
            catch { }
            node = parent;
        }

        return null;
    }

    private static HostedFocusableControlBuffer CollectHostedFocusableControls(DependencyObject root)
    {
        var result = new HostedFocusableControlBuffer();
        CollectHostedFocusableControls(root, result);
        return result;
    }

    private static void CollectHostedFocusableControls(DependencyObject node, HostedFocusableControlBuffer result)
    {
        if (node is Control control && IsHostedFocusableControl(control))
            result.Add(control);

        int childCount;
        try { childCount = VisualTreeHelper.GetChildrenCount(node); }
        catch { return; }

        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child;
            try { child = VisualTreeHelper.GetChild(node, i); }
            catch { continue; }
            CollectHostedFocusableControls(child, result);
        }
    }

    private static bool IsHostedFocusableControl(Control control) =>
        control.Visibility == Visibility.Visible &&
        control.IsEnabled &&
        control.IsTabStop;

    private bool TrySetFocusedItemForHostedElement(FrameworkElement element)
    {
        if (TryCaptureHostedFocusTuple(out HostedFocusTuple tuple) != HostedFocusPlanStatus.Current)
            return false;

        Layout.FocusableItem? focusIdentity = null;
        foreach (EmbedPlan plan in _embedPlans)
        {
            if (ReferenceEquals(plan.Realized, element) && plan.FocusIdentity is { } identity)
            {
                focusIdentity = identity;
                break;
            }
        }

        if (focusIdentity is null)
        {
            foreach (CodeBlockActionPlan plan in _codeBlockActionPlans)
            {
                if (ReferenceEquals(plan.Realized, element) && plan.FocusIdentity is { } identity)
                {
                    focusIdentity = identity;
                    break;
                }
            }
        }

        if (focusIdentity is not { } focusedIdentity ||
            !IsCurrentHostedFocusTuple(tuple))
        {
            return false;
        }

        int focusedIndex = FindFocusableItemIndex(tuple.Items, focusedIdentity);
        if (focusedIndex < 0 || !IsCurrentHostedFocusTuple(tuple))
            return false;

        _focusedItemIndex = focusedIndex;
        return true;
    }

    private bool TryGetVectorPointerState(
        Point point,
        out bool linked,
        out bool selectable)
    {
        if (_snapshot is not null)
        {
            foreach (BlockBox block in _snapshot.Blocks)
            {
                if (TryGetVectorPointerStateInBlock(block, point, out linked, out selectable))
                    return true;
            }
        }

        linked = false;
        selectable = false;
        return false;
    }

    private static bool TryGetVectorPointerStateInBlock(
        BlockBox box,
        Point point,
        out bool linked,
        out bool selectable)
    {
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox vector when vector.ContainsSceneViewport(point):
                linked = vector.TryGetLinkAt(point, out _);
                selectable = vector.IsSelectableAt(point);
                return true;
            case Layout.Boxes.ListItemBox listItem:
                if (TryGetVectorPointerStateInBlock(listItem.Marker, point, out linked, out selectable))
                    return true;
                return TryGetVectorPointerStateInBlock(listItem.Content, point, out linked, out selectable);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorPointerStateInBlock(cell, point, out linked, out selectable))
                        return true;
                }
                break;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorPointerStateInBlock(child, point, out linked, out selectable))
                        return true;
                }
                break;
        }

        linked = false;
        selectable = false;
        return false;
    }

    private LinkTarget? FindVectorLinkTargetAtPoint(Point point)
    {
        return _snapshot is { } snapshot
            ? FindVectorLinkTargetAtPoint(snapshot, point)
            : null;
    }

    private static LinkTarget? FindVectorLinkTargetAtPoint(LayoutSnapshot snapshot, Point point)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            if (TryGetVectorLinkAtPointInBlock(block, point, out var vector, out var link))
            {
                MarkdownVectorSemanticItem? semantic = vector.GetSemanticItem(link.SemanticIndex);
                return new LinkTarget(
                    link.Target ?? string.Empty,
                    semantic?.Description,
                    semantic?.SourceSpan ?? vector.SourceRange,
                    null,
                    link.Action,
                    link.External);
            }
        }

        return null;
    }

    private static bool TryGetVectorLinkAtPointInBlock(
        BlockBox box,
        Point point,
        out Layout.Boxes.VectorSceneBox vector,
        out MarkdownVectorLinkAction link)
    {
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox candidate
                when candidate.TryGetLinkAt(point, out var candidateLink) && candidateLink is not null:
                vector = candidate;
                link = candidateLink;
                return true;
            case Layout.Boxes.ListItemBox listItem:
                return TryGetVectorLinkAtPointInBlock(listItem.Marker, point, out vector, out link) ||
                       TryGetVectorLinkAtPointInBlock(listItem.Content, point, out vector, out link);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorLinkAtPointInBlock(cell, point, out vector, out link))
                        return true;
                }
                break;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorLinkAtPointInBlock(child, point, out vector, out link))
                        return true;
                }
                break;
        }

        vector = null!;
        link = null!;
        return false;
    }

    private LinkTarget? FindLinkTargetAt(DocumentPosition pos, Point? point = null)
    {
        return _snapshot is { } snapshot
            ? FindLinkTargetAt(snapshot, pos, point)
            : null;
    }

    private static LinkTarget? FindLinkTargetAt(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        Point? point = null)
    {
        foreach (var b in snapshot.Blocks)
        {
            if (FindLinkTargetInBlock(b, pos, point) is { } found) return found;
        }
        return null;
    }

    private bool TryGetFocusedLink(out Layout.Boxes.InlineContainerBox inline, out LinkRun link)
    {
        inline = null!;
        link = null!;
        if (!TryGetFocusedLinkedAtomic(out inline, out InlineRun run) || run is not LinkRun focusedLink)
            return false;
        link = focusedLink;
        return true;
    }

    private bool TryGetFocusedLinkedAtomic(
        out Layout.Boxes.InlineContainerBox inline,
        out InlineRun run)
    {
        inline = null!;
        run = null!;
        IReadOnlyList<Layout.FocusableItem>? items = _focusableItems;
        LayoutSnapshot? snapshot = _snapshot;
        if (items is null || snapshot is null ||
            _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        return TryGetLinkedAtomicForFocusable(snapshot, items[_focusedItemIndex], out inline, out run);
    }

    private bool TryGetFocusedVectorSemantic(
        out Layout.Boxes.VectorSceneBox box,
        out int semanticIndex)
    {
        box = null!;
        semanticIndex = -1;
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        return TryGetVectorSemanticForFocusable(items[_focusedItemIndex], out box, out semanticIndex);
    }

    private bool TryGetFocusedHorizontalOverflow(out IHorizontalOverflowBox overflow)
    {
        overflow = null!;
        var items = _focusableItems;
        if (items is null ||
            _snapshot is null ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count ||
            !items[_focusedItemIndex].IsHorizontalOverflow ||
            !_snapshot.TryGetHorizontalOverflowForBlockIndex(
                items[_focusedItemIndex].BlockIndex,
                out IHorizontalOverflowBox? focusedOverflow) ||
            focusedOverflow is null)
        {
            return false;
        }

        overflow = focusedOverflow;
        return true;
    }

    private bool TryGetFocusableIndexForLink(LinkRun run, out int index)
        => TryGetFocusableIndexForLinkedAtomic(run, out index);

    private bool TryGetFocusableIndexForLinkedAtomic(InlineRun run, out int index)
    {
        index = -1;
        IReadOnlyList<Layout.FocusableItem>? items = _focusableItems;
        if (items is null || _snapshot is null)
            return false;

        int equivalentLinkIndex = -1;
        for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            if (!items[itemIndex].IsLink ||
                !TryGetLinkedAtomicForFocusable(_snapshot, items[itemIndex], out _, out InlineRun candidate))
            {
                continue;
            }

            if (ReferenceEquals(candidate, run))
            {
                index = itemIndex;
                return true;
            }

            if (run is LinkRun requestedLink &&
                candidate is LinkRun candidateLink &&
                equivalentLinkIndex < 0 &&
                AreEquivalentLinks(candidateLink, requestedLink))
            {
                equivalentLinkIndex = itemIndex;
            }
        }

        index = equivalentLinkIndex;
        return index >= 0;
    }

    private bool TryGetFocusableIndexForVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex,
        out int index)
    {
        index = -1;
        var items = _focusableItems;
        if (items is null)
            return false;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsVectorSemantic &&
                items[i].BlockIndex == box.BlockIndex &&
                items[i].InlineIndex == semanticIndex &&
                TryGetVectorSemanticForFocusable(items[i], out var candidate, out _) &&
                ReferenceEquals(candidate, box))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private bool TryGetFocusableIndexForHorizontalOverflow(
        IHorizontalOverflowBox overflow,
        out int index)
    {
        var items = _focusableItems;
        var snapshot = _snapshot;
        if (items is null || snapshot is null)
        {
            index = -1;
            return false;
        }

        return TryFindHorizontalOverflowFocusableIndex(snapshot, items, overflow, out index);
    }

    internal static bool TryFindHorizontalOverflowFocusableIndex(
        LayoutSnapshot snapshot,
        IReadOnlyList<Layout.FocusableItem> items,
        IHorizontalOverflowBox overflow,
        out int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(overflow);
        index = -1;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsHorizontalOverflow &&
                snapshot.TryGetHorizontalOverflowForBlockIndex(
                    items[i].BlockIndex,
                    out IHorizontalOverflowBox? candidate) &&
                ReferenceEquals(candidate, overflow))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private bool TryGetVectorSemanticForFocusable(
        Layout.FocusableItem item,
        out Layout.Boxes.VectorSceneBox vector,
        out int semanticIndex)
    {
        vector = null!;
        semanticIndex = -1;
        if (!item.IsVectorSemantic || _snapshot is null)
            return false;

        foreach (BlockBox block in _snapshot.Blocks)
        {
            if (TryGetVectorSemanticForFocusable(block, item, out vector, out semanticIndex))
                return true;
        }

        return false;
    }

    private static bool TryGetVectorSemanticForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.VectorSceneBox vector,
        out int semanticIndex)
    {
        vector = null!;
        semanticIndex = -1;
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox candidate
                when candidate.BlockIndex == item.BlockIndex &&
                     candidate.GetSemanticItem(item.InlineIndex) is { } semantic &&
                     MarkdownVectorSemanticPolicy.IsKeyboardFocusable(semantic.Flags):
                vector = candidate;
                semanticIndex = item.InlineIndex;
                return true;
            case Layout.Boxes.ListItemBox listItem:
                return TryGetVectorSemanticForFocusable(listItem.Marker, item, out vector, out semanticIndex) ||
                       TryGetVectorSemanticForFocusable(listItem.Content, item, out vector, out semanticIndex);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorSemanticForFocusable(cell, item, out vector, out semanticIndex))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorSemanticForFocusable(child, item, out vector, out semanticIndex))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool AreEquivalentLinks(LinkRun left, LinkRun right) =>
        ReferenceEquals(left, right) ||
        (left.IsDisclosure && right.IsDisclosure &&
         string.Equals(left.DisclosureId, right.DisclosureId, StringComparison.Ordinal)) ||
        (string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
         string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
         string.Equals(left.Title, right.Title, StringComparison.Ordinal));

    internal static bool TryGetLinkedAtomicForFocusable(
        LayoutSnapshot snapshot,
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out InlineRun run)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        inline = null!;
        run = null!;
        if (!item.IsLink)
            return false;

        foreach (BlockBox block in snapshot.Blocks)
        {
            if (TryGetLinkedAtomicForFocusable(block, item, out inline, out run))
                return true;
        }

        return false;
    }

    private static bool TryGetLinkedAtomicForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out InlineRun run)
    {
        inline = null!;
        run = null!;

        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == item.BlockIndex:
                foreach (InlineRun candidate in icb.Runs)
                {
                    if (candidate.InlineIndex == item.InlineIndex && IsLinkedRun(candidate))
                    {
                        inline = icb;
                        run = candidate;
                        return true;
                    }
                }
                return false;
            case Layout.Boxes.ListItemBox lib:
                return TryGetLinkedAtomicForFocusable(lib.Marker, item, out inline, out run)
                       || TryGetLinkedAtomicForFocusable(lib.Content, item, out inline, out run);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (TryGetLinkedAtomicForFocusable(cell, item, out inline, out run))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (TryGetLinkedAtomicForFocusable(c, item, out inline, out run))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    // ---- Word / line selection helpers ----

    /// <summary>
    /// Expands selection to the word (maximal non-whitespace run) that contains
    /// <paramref name="pos"/> in its inline container.
    /// Returns the (start, end) document positions of the selected word.
    /// </summary>
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToWord(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        long snapshotGeneration,
        long layoutRevision)
    {
        var icb = FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
        {
            // Block has no inline container (code block, embed row, etc.).
            // Set anchor at the click position so subsequent drag ExtendTo calls
            // always have a valid anchor; this makes the selection empty (start==end)
            // while allowing drag to extend from this point.
            _selection.SetAnchor(pos);
            if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                return (pos, pos);
            // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
            return (pos, pos);
        }
        var (start, end) = icb.GetWordBoundaries(pos);
        _selection.SetAnchor(start);
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return (start, end);
        _selection.ExtendTo(end);
        // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
        return (start, end);
    }

    /// <summary>
    /// Expands selection to the entire inline container (paragraph/heading line)
    /// that contains <paramref name="pos"/>.
    /// Returns the (start, end) document positions of the selected block.
    /// </summary>
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToBlock(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        long snapshotGeneration,
        long layoutRevision)
    {
        var icb = FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
        {
            // Block has no inline container (code block, embed row, etc.).
            // Set anchor at the click position so subsequent drag ExtendTo calls
            // always have a valid anchor; this makes the selection empty (start==end)
            // while allowing drag to extend from this point.
            _selection.SetAnchor(pos);
            if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                return (pos, pos);
            // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
            return (pos, pos);
        }
        var (start, end) = icb.GetBlockBoundaries();
        _selection.SetAnchor(start);
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return (start, end);
        _selection.ExtendTo(end);
        // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
        return (start, end);
    }

    private static Layout.Boxes.InlineContainerBox? FindInlineContainerAt(LayoutSnapshot snapshot, int blockIndex)
    {
        foreach (var b in snapshot.Blocks)
        {
            var found = FindIcbInBlock(b, blockIndex);
            if (found is not null) return found;
        }
        return null;
    }

    private static Layout.Boxes.InlineContainerBox? FindIcbInBlock(BlockBox box, int blockIndex)
    {
        if (box is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == blockIndex) return icb;
        if (box is Layout.Boxes.CodeBlockBox codeBlock)
        {
            foreach (var chunk in codeBlock.Chunks)
            {
                if (chunk.BlockIndex == blockIndex) return chunk;
            }
        }
        if (box is Layout.Boxes.ListItemBox lib)
            return FindIcbInBlock(lib.Marker, blockIndex) ?? FindIcbInBlock(lib.Content, blockIndex);
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                var found = FindIcbInBlock(c, blockIndex);
                if (found is not null) return found;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                var found = FindIcbInBlock(cell, blockIndex);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // ---- Right-click context menu ----

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_canvas is not null)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.RightTap, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void OnSelectionHandleRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Handles live in the overlay, a sibling of the canvas, so their
        // right-taps never reach OnRightTapped.
        if (_canvas is null ||
            !MarkdownPointerGesturePolicy.ForwardsSelectionHandleRightTap(GetPointerModality(e.PointerDeviceType)))
        {
            return;
        }

        e.Handled |= DispatchRecognizedGesture(PointerInputKind.RightTap, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void ProcessRightTapped(ref PointerInput e)
    {
        if (_canvas is null || e.InteractionSnapshot is not { } snapshot) return;
        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        long layoutRevision = snapshot.LayoutRevision;
        var pt = e.GesturePosition;
        if (IsPointOverEmbed(pt) || IsPointOverCodeBlockAction(pt))
            return;

        bool touchOrPen = e.GestureDevice is Microsoft.UI.Input.PointerDeviceType.Touch or
            Microsoft.UI.Input.PointerDeviceType.Pen;
        if (touchOrPen && (!_selection.IsActive || !IsPointInsideSelection(snapshot, pt)))
        {
            _ = TrySelectWordForTouchContext(
                pt,
                snapshot,
                snapshotGeneration,
                layoutRevision,
                e.HasGestureContact ? e.GestureContact : null);
        }
        if (!IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;
        e.ShowContextMenuAfterInteraction = true;
        e.TouchSelectionContextMenu = touchOrPen;
        e.Handled = true;
    }

    private bool ShowSelectionContextMenu(
        Point placementPoint,
        bool touchSelection = false,
        LayoutSnapshot? requiredSnapshot = null,
        long requiredSnapshotGeneration = 0,
        long requiredLayoutRevision = 0)
    {
        UIElement? canvas = _canvas;
        if (canvas is null)
        {
            return false;
        }

        // Pointer gestures pass the exact geometry generation they used while
        // interpreting the gesture. Keyboard invocation captures one tuple at
        // entry, so a later relayout cannot silently retarget the menu.
        LayoutSnapshot? expectedSnapshot = requiredSnapshot ?? _snapshot;
        long expectedSnapshotGeneration = requiredSnapshot is null
            ? Volatile.Read(ref _snapshotGeneration)
            : requiredSnapshotGeneration;
        long expectedLayoutRevision = requiredSnapshot is null
            ? expectedSnapshot?.LayoutRevision ?? 0
            : requiredLayoutRevision;
        if (!IsCurrentContextMenuSnapshot(
                expectedSnapshot,
                expectedSnapshotGeneration,
                expectedLayoutRevision) ||
            !ReferenceEquals(canvas, _canvas))
        {
            return false;
        }

        CloseSelectionContextMenu();
        if (!IsCurrentContextMenuSnapshot(
                expectedSnapshot,
                expectedSnapshotGeneration,
                expectedLayoutRevision) ||
            !ReferenceEquals(canvas, _canvas))
        {
            return false;
        }

        var menu = new MenuFlyout();
        ContextMenuTarget target = new();
        try
        {
            if (!FindContextMenuTarget(
                    placementPoint,
                    expectedSnapshot,
                    expectedSnapshotGeneration,
                    expectedLayoutRevision,
                    out target))
            {
                return false;
            }

            _selectionContextMenu = menu;
            _contextMenuOpen = true;
            if (touchSelection)
                _touchSelection.OpenContextMenu();
            menu.Closed += (_, _) => OnSelectionContextMenuClosed(menu, touchSelection);

            bool hasSelectionItems = _selection.IsActive;
            if (hasSelectionItems)
            {
                var copyItem = new MenuFlyoutItem
                {
                    Text = ResolveLocalizedString(
                        MarkdownStringKeys.Copy,
                        MarkdownLocalizedStrings.ContextMenuCopy),
                    IsEnabled = CanExecuteSelectionCommandOrDefault(
                        MarkdownCommandKind.CopyRendered),
                };
                AutomationProperties.SetAutomationId(copyItem, "MarkdownContextCopy");
                copyItem.Click += (_, _) =>
                    ExecuteSelectionCommand(MarkdownCommandKind.CopyRendered);
                menu.Items.Add(copyItem);

                var copyMarkdownItem = new MenuFlyoutItem
                {
                    Text = ResolveLocalizedString(
                        MarkdownStringKeys.CopyMarkdown,
                        MarkdownLocalizedStrings.ContextMenuCopyMarkdown),
                    IsEnabled = CanExecuteSelectionCommandOrDefault(
                        MarkdownCommandKind.CopyMarkdown),
                };
                AutomationProperties.SetAutomationId(copyMarkdownItem, "MarkdownContextCopyMarkdown");
                copyMarkdownItem.Click += (_, _) =>
                    ExecuteSelectionCommand(MarkdownCommandKind.CopyMarkdown);
                menu.Items.Add(copyMarkdownItem);
            }

            if (!ReferenceEquals(_selectionContextMenu, menu) ||
                !IsCurrentContextMenuSnapshot(
                    expectedSnapshot,
                    expectedSnapshotGeneration,
                    expectedLayoutRevision) ||
                !ReferenceEquals(canvas, _canvas))
            {
                DisposeContextMenuTarget(target);
                OnSelectionContextMenuClosed(menu, touchSelection);
                return false;
            }

            bool hasTargetItems = AddTargetContextMenuItems(menu, target);
            if ((hasSelectionItems || hasTargetItems) && menu.Items.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());

            var selectAllItem = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.SelectAll,
                    MarkdownLocalizedStrings.ContextMenuSelectAll),
                IsEnabled = IsSelectionEnabled,
            };
            AutomationProperties.SetAutomationId(selectAllItem, "MarkdownContextSelectAll");
            selectAllItem.Click += (_, _) =>
            {
                if (!IsSelectionEnabled) return;
                if (_selectionHandleCaptureTarget is not null)
                    ResetSelectionHandleDrag();
                _touchTapSuppression.Reset();
                _touchSelection.Reset();
                _selection.SetAnchor(DocumentPosition.Zero);
                _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
            };
            menu.Items.Add(selectAllItem);

            if (!ReferenceEquals(_selectionContextMenu, menu) || !_contextMenuOpen ||
                !IsCurrentContextMenuSnapshot(
                    expectedSnapshot,
                    expectedSnapshotGeneration,
                    expectedLayoutRevision) ||
                !ReferenceEquals(canvas, _canvas))
            {
                DisposeContextMenuTarget(target);
                OnSelectionContextMenuClosed(menu, touchSelection);
                return false;
            }

            // Command providers and MenuFlyout construction can reenter the
            // control. Fence again immediately before exposing the flyout.
            if (!IsCurrentContextMenuSnapshot(
                    expectedSnapshot,
                    expectedSnapshotGeneration,
                    expectedLayoutRevision) ||
                !ReferenceEquals(canvas, _canvas) ||
                !ReferenceEquals(_selectionContextMenu, menu) ||
                !_contextMenuOpen)
            {
                DisposeContextMenuTarget(target);
                OnSelectionContextMenuClosed(menu, touchSelection);
                return false;
            }

            menu.ShowAt(canvas, placementPoint);
            return true;
        }
        catch (Exception ex)
        {
            DisposeContextMenuTarget(target);
            OnSelectionContextMenuClosed(menu, touchSelection);
            try { menu.Hide(); } catch { }
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] context menu failed: {ex.Message}");
            return false;
        }
    }

    private void OnSelectionContextMenuClosed(MenuFlyout menu, bool touchSelection)
    {
        ReleaseTargetContextMenuCommands(menu);
        if (!ReferenceEquals(_selectionContextMenu, menu))
            return;

        _contextMenuOpen = false;
        _selectionContextMenu = null;
        if (touchSelection)
            _touchSelection.CloseContextMenu(_selection.IsActive);
        UpdateSelectionHandles();
    }

    private void CloseSelectionContextMenu()
    {
        MenuFlyout? menu = _selectionContextMenu;
        _selectionContextMenu = null;
        _contextMenuOpen = false;
        if (menu is null)
            return;

        try
        {
            ReleaseTargetContextMenuCommands(menu);
            _touchSelection.CloseContextMenu(_selection.IsActive);
            UpdateSelectionHandles();
        }
        finally
        {
            // Detached native state can reject a handle update. The flyout
            // reference has already been removed from the owner, so hide this
            // captured instance even if preceding cleanup fails.
            try { menu.Hide(); }
            catch { }
        }
    }

    private void ReleaseTargetContextMenuCommands(MenuFlyout menu)
    {
        foreach (object? entry in menu.Items)
        {
            if (entry is MenuFlyoutItem item &&
                item.Tag is ValueTuple<MarkdownTargetCommand, ContextMenuTarget>)
            {
                var command = (ValueTuple<MarkdownTargetCommand, ContextMenuTarget>)item.Tag;
                item.Click -= OnTargetContextMenuItemClick;
                item.Tag = null;
                DisposeContextMenuTarget(command.Item2);
            }
        }
    }

    private static void DisposeContextMenuTarget(ContextMenuTarget target)
    {
        try
        {
            target.Dispose();
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] context-menu target cleanup failed: {exception.Message}");
        }
    }

    private bool AddTargetContextMenuItems(MenuFlyout menu, ContextMenuTarget target)
    {
        bool added = false;
        if (!string.IsNullOrWhiteSpace(target.LinkUrl))
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyLink,
                target.LinkSourceRange,
                target.LinkUrl);
            bool isEnabled = CanExecuteTargetCommandOrDefault(action, hasDefault: true);
            if (!ReferenceEquals(_selectionContextMenu, menu))
                return added;
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyLink,
                    MarkdownLocalizedStrings.ContextMenuCopyLink),
                IsEnabled = isEnabled,
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyLink");
            AttachTargetCommand(item, action, target);
            menu.Items.Add(item);
            added = true;
            if (!ReferenceEquals(_selectionContextMenu, menu))
                return added;
        }

        if (target.ImageUrl is not null || target.ImageBitmapLease is not null)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyImage,
                target.ImageSourceRange,
                target.ImageUrl);
            bool isEnabled = CanExecuteTargetCommandOrDefault(
                action,
                hasDefault: !string.IsNullOrWhiteSpace(target.ImageUrl) ||
                            target.ImageBitmapLease is not null);
            if (!ReferenceEquals(_selectionContextMenu, menu))
                return added;
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyImage,
                    MarkdownLocalizedStrings.ContextMenuCopyImage),
                IsEnabled = isEnabled,
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyImage");
            AttachTargetCommand(item, action, target);
            menu.Items.Add(item);
            added = true;
            if (!ReferenceEquals(_selectionContextMenu, menu))
                return added;
        }

        if (target.CodeText is not null)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyCode,
                target.CodeSourceRange,
                Language: target.CodeLanguage);
            bool isEnabled = CanExecuteTargetCommandOrDefault(
                action,
                hasDefault: target.CodeText.Length > 0);
            if (!ReferenceEquals(_selectionContextMenu, menu))
                return added;
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyCode,
                    MarkdownLocalizedStrings.ContextMenuCopyCode),
                IsEnabled = isEnabled,
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyCode");
            AttachTargetCommand(item, action, target);
            menu.Items.Add(item);
            added = true;
            if (!ReferenceEquals(_selectionContextMenu, menu))
                return added;
        }

        if (target.TableSnapshot is { } tableSnapshot)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyTable,
                target.TableSourceRange);
            bool isEnabled = CanExecuteTargetCommandOrDefault(
                action,
                hasDefault: tableSnapshot.HasText);
            if (!ReferenceEquals(_selectionContextMenu, menu))
                return added;
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyTable,
                    MarkdownLocalizedStrings.ContextMenuCopyTable),
                IsEnabled = isEnabled,
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyTable");
            AttachTargetCommand(item, action, target);
            menu.Items.Add(item);
            added = true;
        }

        return added;
    }

    private void AttachTargetCommand(
        MenuFlyoutItem item,
        MarkdownTargetCommand action,
        ContextMenuTarget target)
    {
        item.Tag = (action, target);
        item.Click += OnTargetContextMenuItemClick;
    }

    private async void OnTargetContextMenuItemClick(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem item ||
            item.Tag is not ValueTuple<MarkdownTargetCommand, ContextMenuTarget> command)
        {
            return;
        }

        try
        {
            using ContextMenuTarget executionTarget = command.Item2.CloneForExecution();
            await ExecuteTargetCommandOrDefaultAsync(command.Item1, executionTarget).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Context-menu command failed: {ex.Message}");
        }
    }

    private bool CanExecuteTargetCommandOrDefault(
        MarkdownTargetCommand action,
        bool hasDefault)
    {
        try
        {
            bool canExecute = MarkdownTargetCommandDispatcher.CanExecute(
                CommandProvider,
                action,
                out bool isProvided);
            return isProvided ? canExecute : hasDefault;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{action.Kind}' query failed: {ex.Message}");
            return hasDefault;
        }
    }

    private async Task<bool> ExecuteTargetCommandOrDefaultAsync(
        MarkdownTargetCommand action,
        ContextMenuTarget target)
    {
        try
        {
            MarkdownTargetCommandDispatchResult dispatch =
                MarkdownTargetCommandDispatcher.Execute(CommandProvider, action);
            if (dispatch == MarkdownTargetCommandDispatchResult.Executed)
                return true;
            if (dispatch == MarkdownTargetCommandDispatchResult.Disabled)
                return false;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{action.Kind}' failed: {ex.Message}");
        }

        return action.Kind switch
        {
            MarkdownCommandKind.CopyLink => CopyTextTargetToClipboard(action.Target),
            MarkdownCommandKind.CopyImage when target.ImageUrl is not null ||
                                               target.ImageBitmapLease is not null =>
                await CopyImageTargetToClipboardAsync(
                    target.ImageUrl,
                    target.ImageBitmapLease?.Bitmap).ConfigureAwait(true),
            MarkdownCommandKind.CopyCode when target.CodeText is not null =>
                CopyCodeTargetToClipboard(target.CodeText),
            MarkdownCommandKind.CopyTable when target.TableSnapshot is { } tableSnapshot =>
                await CopyTableTargetToClipboardAsync(tableSnapshot).ConfigureAwait(true),
            _ => false,
        };
    }

    private static bool CopyTextTargetToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool CopyCodeTargetToClipboard(string codeText)
    {
        bool succeeded = CopyTextTargetToClipboard(codeText);
        RaiseCopyCompleted(MarkdownCopyKind.CodeBlock, succeeded);
        return succeeded;
    }

    private static async Task<bool> CopyImageTargetToClipboardAsync(
        string? imageUrl,
        CanvasBitmap? bitmap)
    {
        if (bitmap is null && string.IsNullOrWhiteSpace(imageUrl))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            using var stream = new InMemoryRandomAccessStream();
            bool hasBitmap = false;
            if (bitmap is { } sourceBitmap)
            {
                await sourceBitmap.SaveAsync(
                    stream,
                    CanvasBitmapFileFormat.Png,
                    1f);
                stream.Seek(0);
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                hasBitmap = true;
            }
            else if (Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) &&
                     uri.Scheme is "http" or "https" or "file" or "ms-appx")
            {
                package.SetBitmap(RandomAccessStreamReference.CreateFromUri(uri));
                hasBitmap = true;
            }

            if (!string.IsNullOrWhiteSpace(imageUrl))
                package.SetText(imageUrl);
            if (!hasBitmap && string.IsNullOrWhiteSpace(imageUrl))
                return false;

            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CopyTableTargetToClipboard(MarkdownTableClipboardPayload payload)
    {
        if (string.IsNullOrEmpty(payload.Text))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(payload.Text);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(payload.Html));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> CopyTableTargetToClipboardAsync(
        MarkdownTableClipboardSnapshot snapshot)
    {
        MarkdownTableClipboardPayload payload =
            await Task.Run(() => snapshot.Format()).ConfigureAwait(true);
        return CopyTableTargetToClipboard(payload);
    }

    private static MarkdownTableClipboardSnapshot BuildTableClipboardSnapshot(
        Layout.Boxes.TableBox table)
    {
        var rows = new string[table.RowCount][];
        for (int row = 0; row < rows.Length; row++)
        {
            var cells = new string[table.ColumnCount];
            Array.Fill(cells, string.Empty);
            rows[row] = cells;
        }

        foreach (var cell in table.GetCellInfos())
            rows[cell.Row][cell.Column] = GetInlineClipboardText(cell.Box);

        return new MarkdownTableClipboardSnapshot(rows, table.HeaderRowCount);
    }

    private static string GetInlineClipboardText(Layout.Boxes.InlineContainerBox box)
    {
        var text = new StringBuilder();
        foreach (InlineRun run in box.Runs)
        {
            text.Append(run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun
                ? run.AccessibleText
                : run.Text);
        }
        return text.ToString();
    }

    private bool IsCurrentContextMenuSnapshot(
        LayoutSnapshot? snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        return !_isDisposed && !_isUnloaded &&
            ReferenceEquals(snapshot, _snapshot) &&
            snapshotGeneration == Volatile.Read(ref _snapshotGeneration) &&
            (snapshot is null || snapshot.LayoutRevision == layoutRevision);
    }

    private bool FindContextMenuTarget(
        Point point,
        LayoutSnapshot? expectedSnapshot,
        long expectedSnapshotGeneration,
        long expectedLayoutRevision,
        out ContextMenuTarget target)
    {
        target = new ContextMenuTarget();
        if (!IsCurrentContextMenuSnapshot(
                expectedSnapshot,
                expectedSnapshotGeneration,
                expectedLayoutRevision))
        {
            return false;
        }

        LayoutSnapshot? snapshot = expectedSnapshot;
        if (snapshot is null)
            return true;

        // Busy means target-specific commands may be omitted, but only if the
        // originally observed tuple is still current. Never substitute a newer
        // snapshot/revision here.
        if (!snapshot.TryBeginInteraction())
            return IsCurrentContextMenuSnapshot(
                expectedSnapshot,
                expectedSnapshotGeneration,
                expectedLayoutRevision);

        bool captured = false;
        try
        {
            try
            {
                if (IsCurrentContextMenuSnapshot(
                        expectedSnapshot,
                        expectedSnapshotGeneration,
                        expectedLayoutRevision))
                {
                    foreach (BlockBox block in snapshot.Blocks)
                    {
                        Point physicalPoint = snapshot.ToPhysicalPointForTopLevelBlockUnderPaint(block, point);
                        using LayoutPlacementTransformContext.Scope placementScope =
                            snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(block);
                        if (PopulateContextMenuTarget(block, physicalPoint, target))
                            break;
                    }
                    captured = true;
                }
            }
            finally
            {
                snapshot.EndInteraction();
            }
        }
        catch
        {
            DisposeContextMenuTarget(target);
            target = new ContextMenuTarget();
            throw;
        }

        if (!captured || !IsCurrentContextMenuSnapshot(
                expectedSnapshot,
                expectedSnapshotGeneration,
                expectedLayoutRevision))
        {
            DisposeContextMenuTarget(target);
            target = new ContextMenuTarget();
            return false;
        }

        return true;
    }

    private static bool PopulateContextMenuTarget(
        BlockBox box,
        Point point,
        ContextMenuTarget target)
    {
        if (!box.Bounds.Contains(point))
            return false;

        switch (box)
        {
            case Layout.Boxes.CodeBlockBox code:
                target.CodeText = code.CodeText;
                target.CodeLanguage = code.CodeLanguage;
                target.CodeSourceRange = GetSourceRange(code);
                return true;
            case Layout.Boxes.ImageBox image:
                target.ImageUrl = image.Url;
                target.ImageSourceRange = GetSourceRange(image);
                target.SetImageBitmapLease(image.AcquireBitmapLease());
                return true;
            case Layout.Boxes.VectorSceneBox vector:
                if (vector.TryGetLinkAt(point, out var vectorLink) && vectorLink is not null)
                {
                    var semantic = vector.GetSemanticItem(vectorLink.SemanticIndex);
                    target.LinkUrl = vectorLink.Target ?? string.Empty;
                    target.LinkSourceRange = semantic?.SourceSpan ?? vector.SourceRange;
                }
                return true;
            case Layout.Boxes.InlineContainerBox inline:
                PopulateInlineContextMenuTarget(inline, point, target);
                return true;
            case Layout.Boxes.TableBox table:
                target.TableSourceRange = GetSourceRange(table);
                target.TableSnapshot = BuildTableClipboardSnapshot(table);
                foreach (var cell in table.GetCellBoxes())
                {
                    if (PopulateContextMenuTarget(cell, point, target))
                        break;
                }
                return true;
            case Layout.Boxes.ListItemBox listItem:
                if (PopulateContextMenuTarget(listItem.Marker, point, target))
                    return true;
                PopulateContextMenuTarget(listItem.Content, point, target);
                return true;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (PopulateContextMenuTarget(child, point, target))
                        return true;
                }
                return true;
            default:
                return true;
        }
    }

    private static void PopulateInlineContextMenuTarget(
        Layout.Boxes.InlineContainerBox inline,
        Point point,
        ContextMenuTarget target)
    {
        InlineRun? run = inline.RunAtVisualPoint(point);
        if (run is InlineImageRun image)
        {
            target.ImageUrl = image.Image.Url;
            target.ImageSourceRange = image.SourceSpan;
            target.SetImageBitmapLease(image.Image.AcquireBitmapLease());
        }

        if (run is not null && TryCreateLinkedAtomicTarget(run, out LinkTarget link))
        {
            target.LinkUrl = link.Url;
            target.LinkSourceRange = link.SourceSpan;
        }
    }

    private static SourceSpan GetSourceRange(BlockBox box)
    {
        int start = int.MaxValue;
        int end = 0;
        CollectSourceRange(box, ref start, ref end);
        return start == int.MaxValue || end <= start
            ? SourceSpan.Empty
            : new SourceSpan(start, end - start);
    }

    private static void CollectSourceRange(BlockBox box, ref int start, ref int end)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox inline:
                foreach (InlineRun run in inline.Runs)
                    IncludeSourceSpan(run.SourceSpan, ref start, ref end);
                return;
            case Layout.Boxes.CodeBlockBox code:
                foreach (var chunk in code.Chunks)
                    CollectSourceRange(chunk, ref start, ref end);
                return;
            case Layout.Boxes.TableBox table:
                foreach (var cell in table.GetCellBoxes())
                    CollectSourceRange(cell, ref start, ref end);
                return;
            case Layout.Boxes.ListItemBox listItem:
                CollectSourceRange(listItem.Marker, ref start, ref end);
                CollectSourceRange(listItem.Content, ref start, ref end);
                return;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                    CollectSourceRange(child, ref start, ref end);
                return;
            case Layout.Boxes.VectorSceneBox vector:
                IncludeSourceSpan(vector.SourceRange, ref start, ref end);
                return;
        }

        if (box is Layout.Boxes.ImageBox image)
        {
            foreach (var entry in image.Context.SourceMap.Entries)
            {
                if (entry.BlockIndex == image.BlockIndex)
                    IncludeSourceSpan(entry.Span, ref start, ref end);
            }
        }
    }

    private static void IncludeSourceSpan(SourceSpan span, ref int start, ref int end)
    {
        if (span.IsEmpty)
            return;
        start = Math.Min(start, span.Start);
        end = Math.Max(end, span.End);
    }

    private Point GetKeyboardContextMenuPoint()
    {
        TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        if (_focusableItems is { } items &&
            _focusedItemIndex >= 0 &&
            _focusedItemIndex < items.Count &&
            // This returns a copied Rect after its nonblocking snapshot lease
            // ends; the flyout and command providers are invoked later.
            GetFocusableItemRect(items[_focusedItemIndex]) is { } focusedRect)
        {
            return new Point(
                Math.Clamp(focusedRect.Left + focusedRect.Width / 2.0, 0, Math.Max(0, viewportWidth - 1)),
                Math.Clamp(focusedRect.Bottom, viewportTop, Math.Max(viewportTop, viewportBottom - 1)));
        }

        Rect selectionRect = default;
        foreach (Rect rectangle in _selectionAdornerRects)
        {
            if (rectangle.Bottom < viewportTop || rectangle.Top > viewportBottom)
            {
                continue;
            }

            selectionRect = rectangle;
            break;
        }

        if (selectionRect.Width <= 0 || selectionRect.Height <= 0)
        {
            return new Point(12, viewportTop + 12);
        }

        return new Point(
            Math.Clamp(selectionRect.Left + 8, 0, Math.Max(0, viewportWidth - 1)),
            Math.Clamp(selectionRect.Bottom, viewportTop, Math.Max(viewportTop, viewportBottom - 1)));
    }

    internal static LinkTarget? FindLinkTargetInBlock(BlockBox box, DocumentPosition pos, Point? point)
    {
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox vector when vector.BlockIndex == pos.BlockIndex && point is { } vectorPoint:
                if (vector.TryGetLinkAt(vectorPoint, out var vectorLink) && vectorLink is not null)
                {
                    var semantic = vector.GetSemanticItem(vectorLink.SemanticIndex);
                    return new LinkTarget(
                        vectorLink.Target ?? string.Empty,
                        semantic?.Description,
                        semantic?.SourceSpan ?? vector.SourceRange,
                        null,
                        vectorLink.Action,
                        vectorLink.External);
                }
                return null;
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == pos.BlockIndex:
                foreach (var r in icb.Runs)
                {
                    if (r.InlineIndex != pos.InlineIndex)
                        continue;

                    if (point is { } p && !icb.IsPointInsideRunBounds(r, p))
                        return null;

                    return TryCreateLinkedAtomicTarget(r, out LinkTarget target)
                        ? target
                        : null;
                }
                return null;
            case Layout.Boxes.ListItemBox lib:
                if (FindLinkTargetInBlock(lib.Marker, pos, point) is { } lm) return lm;
                return FindLinkTargetInBlock(lib.Content, pos, point);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (FindLinkTargetInBlock(cell, pos, point) is { } tf) return tf;
                }
                return null;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (FindLinkTargetInBlock(c, pos, point) is { } f) return f;
                }
                return null;
        }
        return null;
    }

    private static bool IsLinkedRun(InlineRun? run)
        => run is LinkRun or InlineImageRun { IsLinked: true } or InlineVectorSceneRun { IsLinked: true };

    private static MarkdownLinkInputKind GetLinkInputKind(Microsoft.UI.Input.PointerDeviceType deviceType)
        => deviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Touch => MarkdownLinkInputKind.Touch,
            Microsoft.UI.Input.PointerDeviceType.Pen => MarkdownLinkInputKind.Pen,
            _ => MarkdownLinkInputKind.Mouse,
        };

    private static MarkdownInputModifiers GetCurrentInputModifiers()
    {
        MarkdownInputModifiers result = MarkdownInputModifiers.None;
        if (IsVirtualKeyDown(VirtualKey.Control)) result |= MarkdownInputModifiers.Control;
        if (IsVirtualKeyDown(VirtualKey.Shift)) result |= MarkdownInputModifiers.Shift;
        if (IsVirtualKeyDown(VirtualKey.Menu)) result |= MarkdownInputModifiers.Alt;
        if (IsVirtualKeyDown(VirtualKey.LeftWindows) || IsVirtualKeyDown(VirtualKey.RightWindows))
            result |= MarkdownInputModifiers.Windows;
        return result;
    }

    private static MarkdownInputModifiers MapInputModifiers(VirtualKeyModifiers modifiers)
    {
        MarkdownInputModifiers result = MarkdownInputModifiers.None;
        if ((modifiers & VirtualKeyModifiers.Control) != 0) result |= MarkdownInputModifiers.Control;
        if ((modifiers & VirtualKeyModifiers.Shift) != 0) result |= MarkdownInputModifiers.Shift;
        if ((modifiers & VirtualKeyModifiers.Menu) != 0) result |= MarkdownInputModifiers.Alt;
        if ((modifiers & VirtualKeyModifiers.Windows) != 0) result |= MarkdownInputModifiers.Windows;
        return result;
    }

    private static bool IsVirtualKeyDown(VirtualKey key)
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    private static MarkdownLinkDisposition GetRequestedDisposition(MarkdownInputModifiers modifiers)
    {
        if ((modifiers & MarkdownInputModifiers.Alt) != 0)
            return MarkdownLinkDisposition.External;
        if ((modifiers & MarkdownInputModifiers.Control) != 0 &&
            (modifiers & MarkdownInputModifiers.Shift) == 0)
        {
            return MarkdownLinkDisposition.BackgroundView;
        }
        if ((modifiers & MarkdownInputModifiers.Shift) != 0)
            return MarkdownLinkDisposition.NewView;
        return MarkdownLinkDisposition.CurrentView;
    }

    private bool TryActivateDisclosure(LinkRun? run)
    {
        if (run is not { IsDisclosure: true })
        {
            return false;
        }

        SetDisclosureExpanded(run, !run.IsExpanded);
        return true;
    }

    private readonly struct LazyLayoutWorkResult
    {
        internal readonly LazyLayoutCommit Commit;
        internal readonly (int BlockIndex, double OffsetFromTop)? ScrollAnchor;

        internal LazyLayoutWorkResult(
            LazyLayoutCommit commit,
            (int BlockIndex, double OffsetFromTop)? scrollAnchor)
        {
            Commit = commit;
            ScrollAnchor = scrollAnchor;
        }
    }

    private sealed class ContextMenuTarget : IDisposable
    {
        private Layout.Boxes.SharedCanvasBitmapCache.Lease? _imageBitmapLease;

        public string? LinkUrl { get; set; }
        public SourceSpan LinkSourceRange { get; set; }
        public string? ImageUrl { get; set; }
        public SourceSpan ImageSourceRange { get; set; }
        public string? CodeText { get; set; }
        public string? CodeLanguage { get; set; }
        public SourceSpan CodeSourceRange { get; set; }
        public MarkdownTableClipboardSnapshot? TableSnapshot { get; set; }
        public SourceSpan TableSourceRange { get; set; }

        public Layout.Boxes.SharedCanvasBitmapCache.Lease? ImageBitmapLease =>
            Volatile.Read(ref _imageBitmapLease);

        public void SetImageBitmapLease(Layout.Boxes.SharedCanvasBitmapCache.Lease? lease)
        {
            Layout.Boxes.SharedCanvasBitmapCache.Lease? previous =
                Interlocked.Exchange(ref _imageBitmapLease, lease);
            previous?.Dispose();
        }

        public ContextMenuTarget CloneForExecution() => new()
        {
            LinkUrl = LinkUrl,
            LinkSourceRange = LinkSourceRange,
            ImageUrl = ImageUrl,
            ImageSourceRange = ImageSourceRange,
            CodeText = CodeText,
            CodeLanguage = CodeLanguage,
            CodeSourceRange = CodeSourceRange,
            TableSnapshot = TableSnapshot,
            TableSourceRange = TableSourceRange,
            _imageBitmapLease = ImageBitmapLease?.TryClone(),
        };

        public void Dispose() =>
            Interlocked.Exchange(ref _imageBitmapLease, null)?.Dispose();
    }

    private sealed class MarkdownTableClipboardSnapshot
    {
        private readonly string[][] _rows;
        private readonly int _headerRowCount;

        internal MarkdownTableClipboardSnapshot(
            string[][] rows,
            int headerRowCount)
        {
            _rows = rows;
            _headerRowCount = headerRowCount;
        }

        internal MarkdownTableClipboardPayload Format() =>
            MarkdownTableClipboardFormatter.Format(_rows, _headerRowCount);

        internal bool HasText
        {
            get
            {
                if (_rows.Length == 0)
                    return false;
                if (_rows.Length > 1)
                    return true;

                string[] row = _rows[0];
                if (row.Length > 1)
                    return true;

                return row.Length > 0 && row[0].Length > 0;
            }
        }
    }

    internal readonly struct LinkTarget
    {
        internal readonly string Url;
        internal readonly string? Title;
        internal readonly SourceSpan SourceSpan;
        internal readonly LinkRun? Run;
        internal readonly string? Action;
        internal readonly bool External;

        internal LinkTarget(
            string url,
            string? title,
            SourceSpan sourceSpan,
            LinkRun? run,
            string? action = null,
            bool external = false)
        {
            Url = url;
            Title = title;
            SourceSpan = sourceSpan;
            Run = run;
            Action = action;
            External = external;
        }
    }

    private readonly struct LinkActivationIntent
    {
        internal readonly string Url;
        internal readonly string? Title;
        internal readonly SourceSpan SourceSpan;
        internal readonly string? Action;
        internal readonly bool External;
        internal readonly MarkdownLinkInputKind InputKind;
        internal readonly MarkdownInputModifiers Modifiers;
        internal readonly string? DisclosureId;
        internal readonly bool DisclosureExpanded;

        internal LinkActivationIntent(
            LinkTarget link,
            MarkdownLinkInputKind inputKind,
            MarkdownInputModifiers modifiers)
        {
            Url = link.Url;
            Title = link.Title;
            SourceSpan = link.SourceSpan;
            Action = link.Action;
            External = link.External;
            InputKind = inputKind;
            Modifiers = modifiers;
            if (link.Run is { IsDisclosure: true, DisclosureId: { Length: > 0 } id } run)
            {
                DisclosureId = id;
                DisclosureExpanded = !run.IsExpanded;
            }
            else
            {
                DisclosureId = null;
                DisclosureExpanded = false;
            }
        }
    }
}

/// <summary>Event data for markdown link activation.</summary>
public sealed class MarkdownLinkClickEventArgs : EventArgs
{
    /// <summary>Initializes link activation event data.</summary>
    public MarkdownLinkClickEventArgs(string url, string? title)
        : this(
            url,
            title,
            SourceSpan.Empty,
            MarkdownLinkInputKind.Programmatic,
            MarkdownInputModifiers.None,
            MarkdownLinkDisposition.CurrentView)
    {
    }

    /// <summary>Initializes complete link activation event data.</summary>
    public MarkdownLinkClickEventArgs(
        string url,
        string? title,
        SourceSpan sourceRange,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers,
        MarkdownLinkDisposition requestedDisposition,
        string? action = null)
    {
        Url = url ?? string.Empty;
        Title = title;
        SourceRange = sourceRange;
        InputKind = inputKind;
        Modifiers = modifiers;
        RequestedDisposition = requestedDisposition;
        Action = string.IsNullOrWhiteSpace(action) ? null : action;
        if (Uri.TryCreate(Url, UriKind.RelativeOrAbsolute, out var uri))
            Uri = uri;
    }
    /// <summary>Gets the link URL.</summary>
    public string Url { get; }

    /// <summary>Gets the optional link title.</summary>
    public string? Title { get; }

    /// <summary>Gets the parsed relative or absolute URI, when valid.</summary>
    public Uri? Uri { get; }

    /// <summary>Gets the declarative host action identifier, when present.</summary>
    public string? Action { get; }

    /// <summary>Gets the half-open UTF-16 source range of the activated link.</summary>
    public SourceSpan SourceRange { get; }

    /// <summary>Gets the input modality that activated the link.</summary>
    public MarkdownLinkInputKind InputKind { get; }

    /// <summary>Gets keyboard modifiers active for the activation.</summary>
    public MarkdownInputModifiers Modifiers { get; }

    /// <summary>Gets the navigation disposition requested by the gesture.</summary>
    public MarkdownLinkDisposition RequestedDisposition { get; }

    /// <summary>Gets or sets whether the host handled the activation.</summary>
    public bool Handled { get; set; }
}

/// <summary>Identifies the input modality used to activate markdown content.</summary>
public enum MarkdownLinkInputKind
{
    /// <summary>The activation was raised by application code.</summary>
    Programmatic,
    /// <summary>Mouse input.</summary>
    Mouse,
    /// <summary>Touch input.</summary>
    Touch,
    /// <summary>Pen input.</summary>
    Pen,
    /// <summary>Keyboard input.</summary>
    Keyboard,
    /// <summary>A UI Automation client invoked the element.</summary>
    Automation,
}

/// <summary>Keyboard modifiers captured for a markdown command or activation.</summary>
[Flags]
public enum MarkdownInputModifiers
{
    /// <summary>No modifier keys.</summary>
    None = 0,
    /// <summary>Control key.</summary>
    Control = 1,
    /// <summary>Shift key.</summary>
    Shift = 2,
    /// <summary>Alt/Menu key.</summary>
    Alt = 4,
    /// <summary>Windows key.</summary>
    Windows = 8,
}

/// <summary>Navigation disposition requested by a link gesture.</summary>
public enum MarkdownLinkDisposition
{
    /// <summary>Navigate in the host's current view.</summary>
    CurrentView,
    /// <summary>Open a new foreground view or window.</summary>
    NewView,
    /// <summary>Open a new background view when the host supports it.</summary>
    BackgroundView,
    /// <summary>Delegate navigation to the system or external application.</summary>
    External,
}

/// <summary>Event data for safe HTML disclosure activation.</summary>
public sealed class MarkdownDisclosureToggledEventArgs : EventArgs
{
    /// <summary>Initializes disclosure event data.</summary>
    public MarkdownDisclosureToggledEventArgs(bool isExpanded) => IsExpanded = isExpanded;

    /// <summary>Gets whether the disclosure is expanded after activation.</summary>
    public bool IsExpanded { get; }
}

/// <summary>Identifies the Markdown content copied to the clipboard.</summary>
public enum MarkdownCopyKind
{
    /// <summary>The active text selection.</summary>
    Selection,

    /// <summary>An entire fenced or indented code block.</summary>
    CodeBlock,
}

/// <summary>Event data for a Markdown clipboard copy attempt.</summary>
public sealed class MarkdownCopyCompletedEventArgs : EventArgs
{
    /// <summary>Initializes clipboard copy event data.</summary>
    public MarkdownCopyCompletedEventArgs(MarkdownCopyKind kind, bool succeeded)
    {
        Kind = kind;
        Succeeded = succeeded;
    }

    /// <summary>Gets the copied content kind.</summary>
    public MarkdownCopyKind Kind { get; }

    /// <summary>Gets whether the clipboard accepted the content.</summary>
    public bool Succeeded { get; }
}

/// <summary>Event data for a Markdown document render failure.</summary>
public sealed class MarkdownRenderFailedEventArgs : EventArgs
{
    /// <summary>Initializes render-failure event data.</summary>
    public MarkdownRenderFailedEventArgs(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>Gets the exception raised by the render pipeline.</summary>
    public Exception Exception { get; }
}

