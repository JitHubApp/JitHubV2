using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
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
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Selection;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The native Win2D + DirectWrite markdown renderer shared by
/// <see cref="MarkdownScrollView"/>, which owns its scroll viewport, and
/// <see cref="MarkdownDocumentView"/>, which uses an ancestor's viewport.
/// Hosts a <see cref="CanvasVirtualControl"/> for paint and a sibling
/// <see cref="Canvas"/> overlay for hosted WinUI embeds.
/// </summary>
public abstract partial class MarkdownRendererControl : UserControl, IDisposable, IAsyncDisposable, IMarkdownEnvironmentListener,
    IMarkdownPerformanceLegacyParser, ISelectionAdornerHost, ISelectionCommandsHost, ISelectionDismissalHost,
    ISelectionHandlesHost, ISelectionContextMenuHost, IPointerInputSink, IPointerInteractionHost,
    IHorizontalOverflowPointerHost, IPointerInputRouterHost, IDeferredGestureLayoutHost, IEmbedPlanHost
{
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime = new();
    private readonly PublishedLayout _published = new();
    private readonly MarkdownViewport _viewport;
    private readonly PointerHoverState _hover = new();
    private readonly PointerCursorPresenter _cursor;
    private readonly AbbreviationTooltipPresenter _abbreviationTooltip;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly SelectionCommands _selectionCommands;
    private readonly SelectionDismissalHook _selectionDismissal;
    private readonly SelectionDragAutoScroll _autoScroll;
    private readonly SelectionDragShield _dragShield;
    private readonly SelectionHandlesPresenter _handles;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly DocumentPointerInteraction _document;
    private readonly TouchGestureInteraction _touchGestures;
    private readonly PointerInputRouter _pointerInput;
    private readonly CanvasPointerEvents _canvasPointerEvents;
    private readonly EmbedPlanContext _embedContext;
    private AppWindow? _windowLifecycleAppWindow;
    private WindowLifecycleSubscription? _windowLifecycleSubscription;
    private bool _scrollViewIntermediate;
    private MarkdownEngine? _ownedEngine;

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
    private readonly MarkdownTouchSelectionSession _touchSelection = new();
    private TouchSelectionPresentationRestoreIntent? _pendingTouchSelectionPresentationRestore;
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
    // Lives on the overlay at ZIndex 1 (above selection at -1, below embeds at 0).
    private Microsoft.UI.Xaml.Controls.Border? _focusRing;
    private Windows.UI.Color _focusRingBrushColor; // cached to avoid per-keystroke SolidColorBrush allocations

    // The provider may raise CacheInvalidated from a worker or system-event
    // callback. Keep the subscribed instance outside DependencyObject storage
    // so the callback never reads a thread-affine dependency property.
    private IMarkdownSvgRenderer? _subscribedSvgRenderer;
    private IMarkdownPerformanceSessionInternal? _subscribedPerformanceSession;
    private bool _canvasRenderHandlersAttached;
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

    private List<(Layout.Boxes.CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> _codeBlockActionRects = new();

    private readonly List<EmbedPlan> _embedPlans = new();
    private ViewportBandIndex? _embedPlanIndex;
    // Document ordinals stay sorted so hit-test rectangles retain their source
    // order while scroll work visits only the small active realization band.
    private readonly List<int> _activeEmbedOrdinals = new();
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
            _adorner.InvalidateGeometry();
    }

    private void AttachPerformanceSessionDisposalNotification()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
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
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, Volatile.Read(ref _subscribedPerformanceSession)) &&
                !_lifetime.IsDisposed && !_lifetime.IsUnloaded)
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
        if (!_lifetime.IsUnloaded && args.NewValue is IMarkdownSvgRenderer current)
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
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded)
            return;

        void InvalidateProviderRasters()
        {
            if (!ReferenceEquals(sender, Volatile.Read(ref _subscribedSvgRenderer)) ||
                _lifetime.IsDisposed ||
                _lifetime.IsUnloaded)
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
    /// <param name="codeHighlighter">Optional code-block syntax highlighter.</param>
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
        ICodeHighlighter? codeHighlighter = null,
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
            .WithCodeHighlighter(codeHighlighter)
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
               LinkTargets.AreEquivalentLinks(focusedRun, run);
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
        if (LinkTargets.TryCreateLinkedAtomicTarget(run, out LinkTarget target))
            DispatchAutomationLinkActivation(target);
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

    /// <summary>
    /// Gets the size, in device-independent pixels, of the laid-out document
    /// the control displays, or null when it displays none.
    /// </summary>
    /// <remarks>
    /// The value describes the layout on screen: after the available width
    /// changes it keeps the previous size until the new layout is displayed,
    /// and its height can change while parts of a long document are still
    /// being laid out. It is current when <see cref="RenderCompleted"/> is
    /// raised.
    /// </remarks>
    public Size? RenderedContentSize => _published.Snapshot?.Size;

    /// <summary>
    /// Gets the theme the displayed document was rendered for, or null when
    /// the control displays none.
    /// </summary>
    /// <remarks>
    /// It is <see cref="MarkdownThemeVariant.HighContrast"/> when a Windows
    /// contrast theme is on or the theme's
    /// <see cref="MarkdownTheme.ForcedContrastColors"/> are set, and otherwise
    /// follows the control's actual light or dark theme. Like
    /// <see cref="RenderedContentSize"/>, it changes when a new rendering is
    /// displayed, not when the setting changes.
    /// </remarks>
    public MarkdownThemeVariant? RenderedThemeVariant =>
        _published.Snapshot is null || _themeSnapshot is not { } theme
            ? null
            : theme.IsHighContrast
                ? MarkdownThemeVariant.HighContrast
                : theme.IsDark ? MarkdownThemeVariant.Dark : MarkdownThemeVariant.Light;

    private void RaiseImageUnavailable(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason)
    {
        if (_lifetime.IsDisposed || reason == MarkdownImageUnavailableReason.None)
            return;

        void Raise()
        {
            if (!_lifetime.IsDisposed)
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
    internal double CurrentScrollOffsetY => _surface.Scroll?.VerticalOffset ?? 0;

    internal bool OwnsScrollViewport => _surface.OwnsScrollViewport;

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
        LayoutSnapshot? snapshot = _published.Snapshot;
        _published.Snapshot = null;
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
        if (_surface.Scroll is null)
        {
            verticalOffset = 0;
            viewportHeight = 0;
            extentHeight = 0;
            return false;
        }

        verticalOffset = _surface.Scroll.VerticalOffset;
        viewportHeight = _surface.Scroll.ViewportHeight;
        extentHeight = _surface.Scroll.ExtentHeight;
        return true;
    }

    internal void ScrollFromAutomation(ScrollAmount amount)
    {
        if (_surface.Scroll is null || amount == ScrollAmount.NoAmount)
            return;
        NoteViewportRequest();

        double delta = amount switch
        {
            ScrollAmount.LargeDecrement => -Math.Max(48, _surface.Scroll.ViewportHeight * 0.8),
            ScrollAmount.SmallDecrement => -48,
            ScrollAmount.SmallIncrement => 48,
            ScrollAmount.LargeIncrement => Math.Max(48, _surface.Scroll.ViewportHeight * 0.8),
            _ => 0,
        };
        double maximum = Math.Max(0, _surface.Scroll.ExtentHeight - _surface.Scroll.ViewportHeight);
        double target = Math.Clamp(_surface.Scroll.VerticalOffset + delta, 0, maximum);
        _surface.Scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    internal void SetAutomationVerticalScrollPercent(double percent)
    {
        if (_surface.Scroll is null)
            return;
        NoteViewportRequest();

        double maximum = Math.Max(0, _surface.Scroll.ExtentHeight - _surface.Scroll.ViewportHeight);
        double target = maximum * (Math.Clamp(percent, 0, 100) / 100);
        _surface.Scroll.ChangeView(null, target, null, disableAnimation: true);
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
    internal LayoutSnapshot? CurrentSnapshot => _published.Snapshot;
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
        {
            try { _surface.Overlay?.Children.Remove(button); } catch { }
            return;
        }
        if (_codeBlockCopyButtonPool.Count < CodeBlockCopyButtonPoolCapacity)
        {
            _codeBlockCopyButtonPool.Push((button,
                keepAttached && ReferenceEquals(VisualTreeHelper.GetParent(button), _surface.Overlay)));
            return;
        }

        try { _surface.Overlay?.Children.Remove(button); } catch { }
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
                _published.Snapshot is not null,
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
        _handles.UpdateLocalization();
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
                _embedContext, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        // Localization can call a host StringProvider. Resolve it before the
        // first visual mutation, then revalidate before touching this recycled
        // element. Every subsequent XAML property update is independently
        // fenced because dependency-property callbacks may reenter the host.
        string label = ResolvedCodeBlockCopyButtonLabel;
        if (!plan.IsCurrentRasterActionOwner(
                _embedContext, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        if (button.Content is SymbolIcon icon)
        {
            icon.Symbol = Symbol.Copy;
            if (!plan.IsCurrentRasterActionOwner(
                    _embedContext, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }
        else
        {
            button.Content = new SymbolIcon(Symbol.Copy);
            if (!plan.IsCurrentRasterActionOwner(
                    _embedContext, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
                return false;
        }

        AutomationProperties.SetName(button, label);
        if (!plan.IsCurrentRasterActionOwner(
                _embedContext, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element))
            return false;

        ToolTipService.SetToolTip(button, label);
        return plan.IsCurrentRasterActionOwner(
            _embedContext, candidate, generation, layoutRevision, hostedFocusPlanEpoch, element);
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
            plan.ShowCopiedFeedback(_embedContext);
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
            if (HostedFocusNavigation.TryMoveFocusWithinHostedEmbed(element, e.OriginalSource, shift))
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
        bool hasViewport = _viewport.TryGet(out double top, out double height, out double width);
        rect = new Windows.Foundation.Rect(0, top, width, height);
        return hasViewport;
    }

    internal bool HasVisibleLoadingImagesForAutomation()
    {
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return false;

        bool hasViewport = TryGetVisibleDocumentRect(out Windows.Foundation.Rect viewport);
        long snapshotGeneration = _published.Generation;
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
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(snapshot, _published.Snapshot) &&
        snapshotGeneration == _published.Generation &&
        ReferenceEquals(publication, Volatile.Read(ref _imagePlanPublication)) &&
        ReferenceEquals(snapshot, publication.Snapshot) &&
        snapshotGeneration == publication.SnapshotGeneration &&
        snapshot.LayoutRevision == publication.LayoutRevision;

    internal MarkdownVisibleSceneReadiness GetVisibleDeferredSceneStatusForAutomation()
    {
        IMarkdownPerformanceSessionInternal? session =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return session?.HasRegisteredDeferredSceneWork(this) == true
                ? MarkdownVisibleSceneReadiness.NotReady : MarkdownVisibleSceneReadiness.Ready;

        long generation = _pipelineGeneration;
        long revision = snapshot.LayoutRevision;
        if (!TryGetVisibleDocumentRect(out Rect viewport) || !IsFiniteNonEmptyAutomationRect(viewport))
        {
            return _published.Generation != generation ||
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        var peer = FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer;
        peer?.NotifyLoadingStatusMayHaveChanged();
        LayoutSnapshot? snapshot = _published.Snapshot;
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
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded)
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
        ReferenceEquals(_published.Snapshot, snapshot) &&
        _pipelineGeneration == generation &&
        _published.Generation == generation &&
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
        if (_surface.Scroll is null)
        {
            BringDocumentRectIntoExternalViewport(rect, animate: false, alignToTop ? 0 : 0.5);
            return;
        }
        const double margin = 24.0;
        double target = alignToTop
            ? rect.Top
            : rect.Top < _surface.Scroll.VerticalOffset + margin
                ? rect.Top - margin
                : rect.Bottom > _surface.Scroll.VerticalOffset + _surface.Scroll.ViewportHeight - margin
                    ? rect.Bottom - _surface.Scroll.ViewportHeight + margin
                    : _surface.Scroll.VerticalOffset;
        // UI Automation's ScrollIntoView contract is immediate. An animated
        // change can be superseded by lazy-layout realization before its first
        // frame, leaving the requested range offscreen.
        _surface.Scroll.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }

    /// <summary>
    /// Initializes a renderer with either an owned scroll viewport or an
    /// externally supplied effective viewport.
    /// </summary>
    /// <param name="ownsScrollViewport">
    /// True to create the scroll viewer; false to participate in the nearest
    /// ancestor viewport through EffectiveViewportChanged.
    /// </param>
    private protected MarkdownRendererControl(bool ownsScrollViewport)
    {
        _surface = new MarkdownViewSurface(ownsScrollViewport);
        _viewport = new MarkdownViewport(this, _surface);
        _cursor = new PointerCursorPresenter(cursor => ProtectedCursor = cursor);
        _abbreviationTooltip = new AbbreviationTooltipPresenter(this, _surface, _lifetime, _hover);
        _adorner = new SelectionAdornerPresenter(this, this, _surface, _lifetime, _published, _viewport, _selection, _hover);
        _selectionCommands = new SelectionCommands(this, _published, _selection);
        _selectionDismissal = new SelectionDismissalHook(this, this, _surface, _lifetime, _published, _selection, _selectionCommands);
        _autoScroll = new SelectionDragAutoScroll(_surface, _viewport, rect => BringDocumentRectIntoExternalViewport(rect, animate: false));
        _dragShield = new SelectionDragShield(_surface, _viewport, this);
        _handles = new SelectionHandlesPresenter(this, this, this, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _adorner, _autoScroll);
        _contextMenu = new SelectionContextMenuPresenter(this, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _handles, _adorner, _selectionCommands);
        _horizontalOverflowDrag = new HorizontalOverflowPointerDrag(this, _surface, _published, _touchTapSuppression);
        _document = new DocumentPointerInteraction(this, _surface, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _horizontalOverflowDrag, _handles, _autoScroll, _dragShield, _adorner, _contextMenu, new PointerHoverTracker(this, _published, _hover, _cursor, _abbreviationTooltip), _cursor, _abbreviationTooltip);
        _touchGestures = new TouchGestureInteraction(this, this, _surface, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _horizontalOverflowDrag, _document);
        _pointerInput = new PointerInputRouter(this, this, DispatcherQueue, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _handles, _document, _horizontalOverflowDrag, _touchGestures, _contextMenu, _adorner, _dragShield);
        _canvasPointerEvents = new CanvasPointerEvents(_surface, _pointerInput);
        _embedContext = new EmbedPlanContext(this, _surface, _lifetime, _published, DispatcherQueue, OnHostedEmbedKeyDown);
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
        EnsureCanvas();
        GettingFocus += OnGettingFocus;
        GotFocus += OnGotFocus;
        LosingFocus += OnLosingFocus;
        KeyDown += OnKeyDown;

        if (!_surface.OwnsScrollViewport)
            EffectiveViewportChanged += OnEffectiveViewportChanged;
        Content = _surface.ContentElement;
    }

    private void EnsureCanvas()
    {
        if (_lifetime.IsDisposed || _surface.TryCreateCanvas() is not { } canvas)
            return;
        _canvasPointerEvents.Attach(canvas);
        AttachCanvasRenderHandlers();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (_surface.OwnsScrollViewport || _lifetime.IsDisposed)
            return;

        if (!_viewport.TryAcceptExternal(args.EffectiveViewport, out bool materiallyChanged) ||
            !materiallyChanged || _lifetime.IsUnloaded)
            return;

        QueueLazyLayoutForViewport();
        _adorner.InvalidateGeometry();
        _adorner.Invalidate();
        RealizeVisibleEmbeds();
        ScheduleVisibleCodeBlockHighlighting();
    }

    internal bool TryGetViewport(out double top, out double height, out double width)
        => _viewport.TryGet(out top, out height, out width);

    private bool BringDocumentRectIntoExternalViewport(Rect rect, bool animate, double alignmentRatio = 0)
    {
        if (_surface.OwnsScrollViewport || _surface.Root is null)
            return false;

        try
        {
            _surface.Root.StartBringIntoView(new BringIntoViewOptions
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
        if (_promotingKeyboardFocusEntry || _lifetime.IsUnloaded)
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
        if (!_contextMenu.IsOpen)
        {
            _document.ForgetDragAnchor();
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
        _pointerInput.Reset();
        ResetTransientInteractionState(clearSelection: false);
        _touchTapSuppression.RequireAcceptedContactForRecognizedGesture();
        _handles.Update();
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

    private void OnCodeHighlighterChanged(DependencyPropertyChangedEventArgs e)
        => OnEffectiveCodeHighlighterChanged(
            e.OldValue as ICodeHighlighter,
            e.NewValue as ICodeHighlighter);

    private void OnEffectiveCodeHighlighterChanged(
        ICodeHighlighter? previous,
        ICodeHighlighter? current)
    {
        if (ReferenceEquals(previous, current))
            return;

        RetireCodeBlockHighlighting(restart: !_lifetime.IsUnloaded && !_lifetime.IsDisposed);
        ClearCodeBlockHighlightCache();
        RequestRebuild(RebuildReason.Full);
    }

    private bool IsElementWithinRenderer(DependencyObject element)
        => VisualTreeAncestry.IsWithin(element, this);

    bool ISelectionDismissalHost.IsElementWithinCodeBlockAction(DependencyObject element)
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is DependencyObject realized && VisualTreeAncestry.IsWithin(element, realized))
                return true;
        }

        return false;
    }

    bool ISelectionDismissalHost.IsElementWithinSelectionHandle(DependencyObject element)
        => _handles.ContainsElement(element);

    bool ISelectionDismissalHost.IsPointOverEmbed(Point canvasPoint) => IsPointOverEmbed(canvasPoint);

    void ISelectionDismissalHost.ClearSelectionForExternalInteraction() => ClearSelectionForExternalInteraction();

    /// <summary>
    /// Copies the active selection to the clipboard.
    /// </summary>
    /// <param name="options">Optional copy format options. Defaults use rendered plain text and add CF_HTML.</param>
    /// <returns>True when a selection was copied successfully.</returns>
    public bool CopySelectionToClipboard(MarkdownCopyOptions? options = null)
        => _selectionCommands.CopyToClipboard(options);

    /// <summary>Copies the active selection as exact markdown source plus CF_HTML.</summary>
    public bool CopySelectionAsMarkdown()
        => _selectionCommands.CopyAsMarkdown();

    IMarkdownCommandProvider? ISelectionCommandsHost.CommandProvider => CommandProvider;

    void ISelectionCommandsHost.RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
        => RaiseCopyCompleted(kind, succeeded);

    Theming.ThemeSnapshot? ISelectionHandlesHost.ThemeSnapshot => _themeSnapshot;

    string ISelectionHandlesHost.ResolveLocalizedString(string key, string fallback)
        => ResolveLocalizedString(key, fallback);

    bool ISelectionHandlesHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => RequestGeometryRecoveryIfRequired(snapshot);

    void ISelectionHandlesHost.FocusRendererForPointerInteraction() => FocusRendererForPointerInteraction();

    bool ISelectionHandlesHost.TryHitTestEmbed(Point point, out DocumentPosition position)
        => TryHitTestEmbed(point, out position);

    bool ISelectionHandlesHost.HasDeferredInputFor(uint pointerId)
        => _pointerInput.HasDeferredInputFor(pointerId);

    void ISelectionHandlesHost.ResumePendingRebuildAfterPointerInteraction()
        => ResumePendingRebuildAfterPointerInteraction();

    void ISelectionHandlesHost.CancelPendingTouchSelectionRestore()
        => _pendingTouchSelectionPresentationRestore = null;

    bool ISelectionContextMenuHost.IsSelectionEnabled => IsSelectionEnabled;

    IMarkdownCommandProvider? ISelectionContextMenuHost.CommandProvider => CommandProvider;

    string ISelectionContextMenuHost.ResolveLocalizedString(string key, string fallback)
        => ResolveLocalizedString(key, fallback);

    void ISelectionContextMenuHost.RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
        => RaiseCopyCompleted(kind, succeeded);

    Rect? ISelectionContextMenuHost.GetFocusedItemRect()
        => _focusableItems is { } items &&
           _focusedItemIndex >= 0 &&
           _focusedItemIndex < items.Count
            ? GetFocusableItemRect(items[_focusedItemIndex])
            : null;

    bool IPointerInteractionHost.IsSelectionEnabled => IsSelectionEnabled;

    bool IPointerInteractionHost.IsCurrentScrollTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision)
        => IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision);

    void IPointerInteractionHost.CancelPendingTouchSelectionRestore()
        => _pendingTouchSelectionPresentationRestore = null;

    void IPointerInteractionHost.RememberFocusResumePoint(Point documentPoint)
        => RememberFocusResumePoint(documentPoint);

    void IPointerInteractionHost.FocusRendererForPointerInteraction() => FocusRendererForPointerInteraction();

    void IPointerInteractionHost.ClearKeyboardFocusItem()
    {
        if (_focusedItemIndex >= 0)
        {
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            UpdateFocusRing();
        }
    }

    bool IPointerInteractionHost.IsPointOverCodeBlockAction(Point point) => IsPointOverCodeBlockAction(point);

    bool IPointerInteractionHost.IsPointOverEmbed(Point point) => IsPointOverEmbed(point);

    bool IPointerInteractionHost.TryHitTestEmbed(Point point, out DocumentPosition position)
        => TryHitTestEmbed(point, out position);

    bool IPointerInteractionHost.TryGetVectorPointerState(Point point, out bool linked, out bool selectable)
        => TryGetVectorPointerState(point, out linked, out selectable);

    void IPointerInteractionHost.InvalidateInteractiveTextAdorner() => InvalidateInteractiveTextAdorner();

    void IPointerInteractionHost.UpdateSelectionOverlay() => UpdateSelectionOverlay();

    void IPointerInteractionHost.ClearSelectionForExternalInteraction() => ClearSelectionForExternalInteraction();

    void IPointerInteractionHost.ResumePendingRebuildAfterPointerInteraction()
        => ResumePendingRebuildAfterPointerInteraction();

    void IHorizontalOverflowPointerHost.RefreshHorizontalOverflowVisualState(IHorizontalOverflowBox target, double oldOffset)
        => RefreshHorizontalOverflowVisualState(target, oldOffset);

    void IPointerInputSink.DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
        => _pointerInput.DispatchPointerInput(kind, sender, args);

    bool IPointerInputSink.DispatchRecognizedGesture(PointerInputKind kind, Point point, Microsoft.UI.Input.PointerDeviceType device)
        => _pointerInput.DispatchRecognizedGesture(kind, point, device);

    bool IPointerInputRouterHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => RequestGeometryRecoveryIfRequired(snapshot);

    void IPointerInputRouterHost.ResumePendingRebuildAfterPointerInteraction()
        => ResumePendingRebuildAfterPointerInteraction();

    void IPointerInputRouterHost.ClearSelectionForExternalInteraction(bool resetClickTracking, bool resetDeferredPointerInput)
        => ClearSelectionForExternalInteraction(resetClickTracking, resetDeferredPointerInput);

    bool IPointerInputRouterHost.ActivateLinkIntent(
        LinkActivationIntent intent,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
        => ActivateLinkIntent(intent, snapshot, snapshotGeneration, layoutRevision);

    CancellationToken IEmbedPlanHost.PipelineToken => _pipelineCts?.Token ?? CancellationToken.None;

    long IEmbedPlanHost.PipelineGeneration => _pipelineGeneration;

    (Button Button, bool Attached) IEmbedPlanHost.RentCodeBlockCopyButton() => RentCodeBlockCopyButton();

    void IEmbedPlanHost.ReturnCodeBlockCopyButton(Button button) => ReturnCodeBlockCopyButton(button);

    void IEmbedPlanHost.SetCodeBlockCopyButtonState(Button button, bool copied)
        => SetCodeBlockCopyButtonState(button, copied);

    bool IEmbedPlanHost.SetCodeBlockCopyButtonStateForRasterCandidate(
        CodeBlockActionPlan plan,
        Button button,
        FrameworkElement element,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
        => SetCodeBlockCopyButtonStateForRasterCandidate(
            plan, button, element, candidate, generation, layoutRevision, hostedFocusPlanEpoch);

    bool IEmbedPlanHost.IsCurrentRasterCodeActionPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
        => IsCurrentRasterCodeActionPublication(candidate, generation, layoutRevision, hostedFocusPlanEpoch);

    void IEmbedPlanHost.CopyCodeBlockToClipboard(CodeBlockActionPlan plan) => CopyCodeBlockToClipboard(plan);

    void IEmbedPlanHost.RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan)
        => RestoreFocusedCodeBlockActionIfNeeded(plan);

    void IEmbedPlanHost.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
        => PreserveLogicalFocusBeforeDerealizingCodeBlockAction(element);

    MarkdownHostedElementRequest IEmbedPlanHost.CreateHostedElementRequest(
        DeclarativeHostedElementBox box,
        Rect layoutBounds)
        => CreateHostedElementRequest(box, layoutBounds);

    bool IEmbedPlanHost.CanAttachHostedElement(DeclarativeHostedElementPlan plan, long pipelineGeneration)
        => CanAttachHostedElement(plan, pipelineGeneration);

    bool IEmbedPlanHost.CanActivateHostedElementFallback(DeclarativeHostedElementPlan plan, long pipelineGeneration)
        => CanActivateHostedElementFallback(plan, pipelineGeneration);

    void IEmbedPlanHost.ActivateHostedElementFallback(DeclarativeHostedElementPlan plan)
        => ActivateHostedElementFallback(plan);

    void IEmbedPlanHost.RealizeVisibleEmbeds() => RealizeVisibleEmbeds();

    void IEmbedPlanHost.RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan)
        => RestoreFocusedHostedElementIfNeeded(plan);

    double IDeferredGestureLayoutHost.RasterizationScale => XamlRoot?.RasterizationScale ?? 1;

    bool IDeferredGestureLayoutHost.IsPointOverEmbed(Point point) => IsPointOverEmbed(point);

    bool IDeferredGestureLayoutHost.IsPointOverCodeBlockAction(Point point) => IsPointOverCodeBlockAction(point);

    bool IDeferredGestureLayoutHost.IsLayoutPublicationPending(LayoutSnapshot snapshot) =>
        _publishingLayoutCommit ||
        _pendingLayoutCommit is not null || _appliedLazyLayoutRevision < snapshot.LayoutRevision;

    bool IDeferredGestureLayoutHost.IsLazyLayoutBandCovered(LayoutSnapshot snapshot, LazyLayoutBand band)
    {
        bool active = _lazyLayoutCts is { IsCancellationRequested: false } &&
            ReferenceEquals(snapshot, _activeLazyLayoutSnapshot) &&
            _activeLazyLayoutRevision == snapshot.LayoutRevision &&
            band.Top >= _activeLazyLayoutBand.Top && band.Bottom <= _activeLazyLayoutBand.Bottom;
        bool pending = _hasPendingLazyLayoutBand &&
            ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot) &&
            band.Top >= _pendingLazyLayoutBand.Top && band.Bottom <= _pendingLazyLayoutBand.Bottom;
        return active || pending;
    }

    bool IDeferredGestureLayoutHost.HasSupersedingLazyLayoutRequest(LayoutSnapshot snapshot) =>
        (_lazyLayoutCts is { IsCancellationRequested: false } &&
         ReferenceEquals(snapshot, _activeLazyLayoutSnapshot)) ||
        (_hasPendingLazyLayoutBand && ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot)) ||
        (_lazyLayoutDispatchQueued && ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot));

    void IDeferredGestureLayoutHost.QueueLazyLayoutForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor)
        => QueueLazyLayoutForBand(snapshot, band, visibleVectorBand, preserveScrollAnchor);

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
        _contextMenu.Close();
        if (resetDeferredPointerInput)
            _pointerInput.Reset();
        _document.ResetSession();
        _handles.ResetDrag();
        _touchSelection.Reset();
        _document.ForgetDragAnchor();
        _dragShield.SetActive(false);
        if (resetClickTracking)
        {
            _document.ResetClickTracking();
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
        if (_selectionAutomationEventQueued || _lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        _selectionAutomationEventQueued = true;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
            {
                _selectionAutomationEventQueued = false;
                if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
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
        _cursor.SetShape(null);
    }

    private void OnLoadedInternal()
    {
        if (_lifetime.IsDisposed)
            return;

        _lifetime.MarkLoaded();
        // RemoveFromVisualTree physically removes the Win2D child from the root.
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
        _selectionDismissal.Register();
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
        if (_lifetime.IsUnloaded)
            return;

        // Set before any unsubscription so dispatcher-queued lambdas
        // (e.g. from OnImageLoadCompleted) that are already in-flight know
        // not to call RequestRebuild after we've torn down.
        _lifetime.MarkUnloaded();
        _pendingTouchSelectionPresentationRestore = null;
        // Keep the recognizer barrier while the owner epoch is still intact.
        // A queued Cancel must apply its per-contact selection disposition
        // before that identity is discarded; a queued Release keeps selection.
        _touchTapSuppression.PrepareForReset();
        StopCanvasDeviceRecovery();
        StopImageRelayoutRecovery(force: true);
        DetachPerformanceSessionDisposalNotification();
        _pointerInput.Reset();
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
        _surface.RemoveCanvas();
        ReleaseLoadedResources();
    }

    private void ReleaseLoadedResources()
    {
        ResetVectorCapacityObserver();
        ClearPendingFocusGeometryWork();
        Interlocked.Exchange(ref _deferredSceneStatusRefreshAfterPaint, 0);
        Interlocked.Exchange(ref _deferredSceneProbeAfterPaint, 0);
        _contextMenu.Close();
        _horizontalOverflowDrag.Clear();
        _abbreviationTooltip.Hide();
        _abbreviationTooltip.ReleaseTimers();
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
        _selectionDismissal.Unregister();
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
        if (_surface.Scroll is not null) _surface.Scroll.ViewChanged -= OnScrollViewChanged;
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
        _focusRing = null; // evicted from overlay above; lazily re-created on re-attach
        var snap = _published.Snapshot;
        _published.Snapshot = null;
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
        if (_lifetime.IsDisposed)
            return;

        _lifetime.MarkDisposing();
        _pendingTouchSelectionPresentationRestore = null;
        _touchTapSuppression.Reset();
        StopCanvasDeviceRecovery();
        _handles.ResetDrag();
        _touchSelection.Dispose();
        _handles.StopFrameClock();
        DetachWindowLifecycle();
        OnUnloaded();
        _adorner.ReleaseForDispose();
        _published.LayoutDevice = null;
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        DetachCanvasHandlers();
        _dragShield.Release();
        Content = null;
        _surface.Detach();
        MarkdownEngine? ownedEngine = _ownedEngine;
        _ownedEngine = null;
        if (ownedEngine is not null)
            RetireOwnedEngine(ownedEngine);
        _lifetime.MarkDisposalComplete();
        try { Disposed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] disposal observer failed: " +
                $"{ex.GetType().Name} (0x{ex.HResult:X8}).");
        }
    }

    /// <summary>
    /// Disposes the view, then completes when every native resource it has
    /// released is freed. Call it on the UI thread.
    /// </summary>
    /// <returns>
    /// A task that completes when the released resources are freed, and faults
    /// if one of them could not be released.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Teardown did not finish, here or in an earlier <see cref="Dispose"/> call,
    /// so the view may still hold resources.
    /// </exception>
    public ValueTask DisposeAsync()
    {
        Dispose();
        if (!_lifetime.IsDisposalComplete)
        {
            throw new InvalidOperationException(
                "The markdown view did not finish an earlier Dispose call and may still hold resources.");
        }

        return new ValueTask(_snapshotRetirements.DrainAsync());
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="Dispose"/> finishes teardown,
    /// whether the app or the view's window started it.
    /// </summary>
    public event EventHandler? Disposed;

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
                !owner._lifetime.IsDisposed)
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
        _handles.DetachHandlers();
    }

    private void DetachCanvasInputHandlers()
    {
        if (_surface.Canvas is not null)
            _canvasPointerEvents.Detach(_surface.Canvas);
    }

    private void AttachCanvasRenderHandlers()
    {
        if (_surface.Canvas is null || _canvasRenderHandlersAttached)
            return;

        _surface.Canvas.RegionsInvalidated += OnRegionsInvalidated;
        _surface.Canvas.CreateResources += OnCanvasCreateResources;
        _canvasRenderHandlersAttached = true;
    }

    private void DetachCanvasRenderHandlers()
    {
        if (_surface.Canvas is null || !_canvasRenderHandlersAttached)
            return;

        _surface.Canvas.RegionsInvalidated -= OnRegionsInvalidated;
        _surface.Canvas.CreateResources -= OnCanvasCreateResources;
        _canvasRenderHandlersAttached = false;
    }

    private void OnThemeChanged()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
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
        if (dq.HasThreadAccess) { if (!_lifetime.IsUnloaded) RequestRebuild(RebuildReason.Restyle); return; }
        dq.TryEnqueue(() => { if (!_lifetime.IsUnloaded) RequestRebuild(RebuildReason.Restyle); });
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
        if (_lifetime.IsDisposed)
            return;

        ThemeResolver.InvalidateResourceKeyCache();
        RequestRebuild(RebuildReason.Restyle);
    }

    private void RequestRebuild(
        RebuildReason reason)
    {
        if (_lifetime.IsDisposed)
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
                if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded)
                {
                    RecordRejectedCrossThreadRebuildRequest(reason);
                    MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Dispatcher rejected cross-thread rebuild request.");
                }
            }
            return;
        }

        if (_lifetime.IsUnloaded)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || dispatcher is not null && !dispatcher.HasThreadAccess)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !_rebuildCoordinator.TryGetDispatchReason(out MarkdownRebuildCoordinatorReason pendingReason) ||
            !IsLoaded || _surface.Canvas is null || _surface.Overlay is null || _surface.Root is null)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !IsLoaded)
            return;

        // Image completion, theme changes, and width changes may request a
        // relayout while the user is actively selecting text. Releasing pointer
        // capture here terminates the gesture and visibly drops the selection.
        // Keep the latest merged rebuild request pending until pointer release.
        if (_pointerInput.HasSnapshotInteraction)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_rebuildCoordinator.CompletePipeline(generation))
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
        if (_published.Snapshot is null || !_selection.IsActive)
        {
            _pendingTouchSelectionPresentationRestore = null;
            return;
        }

        long snapshotGeneration = _published.Generation;
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

    private void ResetTransientInteractionState(bool clearSelection)
    {
        // Clear logical capture ownership before touching XAML. A disconnected
        // input site must not keep a replacement blocked by a stale gesture.
        _touchTapSuppression.Reset();
        _handles.ResetDrag();
        _touchSelection.Reset();
        _document.Reset();
        _autoScroll.Reset();
        _hover.Clear();
        _abbreviationTooltip.ForgetTarget();
        _contextMenu.Close();
        _dragShield.SetActive(false);
        _abbreviationTooltip.Hide();

        _cursor.SetShape(null);

        try { _surface.Canvas?.ReleasePointerCaptures(); } catch { }

        if (!clearSelection)
            return;

        _selection.Clear();
        _adorner.InvalidateGeometry();
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || generation != _pipelineGeneration)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || generation != _pipelineGeneration)
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
        if (_surface.Canvas is null || _surface.Overlay is null || _surface.Root is null) return;
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
        // passing the canvas directly would crash if layout runs before first draw.
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

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
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
        // `committed` tracks whether the snapshot was published; the catch
        // block must only dispose it if not yet committed (once published, _published owns it).
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
            _surface.Scroll is { VerticalOffset: > 0 } scrollSnap &&
            _published.Snapshot is { } prevSnap)
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
                scrollAnchor = (null, 0, vTop, _surface.Root?.Height ?? 0);
            }
        }

        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        // Atomically swap snapshots, then retire the old one so its
        // CanvasTextLayout / placeholder handles are released without making
        // this UI-thread commit wait for an in-flight lazy measure.
        var old = _published.Snapshot;
        long touchSelectionRestoreSourceGeneration = _published.Generation;
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
        _published.Snapshot = snapshot;
        _published.LayoutDevice = device;
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
        _published.Generation = generation;
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
            _contextMenu.Close();
        // Dependency-property publication above can reenter Dispose before
        // this old snapshot is retired. Tie its retirement to this context's
        // pipeline lifetime as well as the control's global drain.
        ctx.RegisterRetirement(RetireSnapshot(old));
        // Clear stale hover references so OnPointerExited/OnPointerMoved after the
        // rebuild don't use Bounds from the now-disposed old snapshot for invalidation.
        _hover.Clear();
        ApplySnapshotSize(snapshot.Size);

        // Restore scroll anchor: find the anchor block's new Y in the new layout
        // and adjust the scroll offset so the user's read position is unchanged.
        if (scrollAnchor is { } anchor &&
            _surface.Scroll is { } scrollRestore &&
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
        // viewport. Hooking ScrollViewer.ViewChanged drives subsequent realisation
        // as the user scrolls.
        DerealizeAllEmbeds();
        _dragShield.SetActive(false);
        // Keep the attached adorner surface and its placeholder across
        // commits. Repaint against the new snapshot instead of replacing the
        // surface on every relayout.
        _adorner.InvalidatePresentation();
        _adorner.Collapse();
        _adorner.RemoveOtherOverlayChildren();
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
        _adorner.ClearGeometry();
        _handles.MarkEndpointsUnavailable();
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
                _handles.Update();
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
        if (_surface.Scroll is not null)
        {
            _surface.Scroll.ViewChanged -= OnScrollViewChanged;
            _surface.Scroll.ViewChanged += OnScrollViewChanged;
        }
        RealizeVisibleEmbeds();
        long embedRealizationEnded = Stopwatch.GetTimestamp();
        bool resumedPendingHostedFocus = ResumePendingLazyFocus(snapshot);
        _ = RestartCodeBlockHighlighting();
        long highlightRetirementEnded = Stopwatch.GetTimestamp();
        ScheduleVisibleCodeBlockHighlighting();
        long highlightSchedulingEnded = Stopwatch.GetTimestamp();
        _adorner.InvalidateGeometry();
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
            // the unpublished Win2D layout. Once committed, _published owns it.
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
                _adorner.InvalidateGeometry();
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

    private void ScheduleVisibleCodeBlockHighlighting()
    {
        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return;
        ScheduleVisibleDeferredScenes();

        if (!_hasMeasuredCodeBlocks ||
            !IsCodeBlockSyntaxHighlightingEnabled ||
            CodeHighlighter is not { } highlighter ||
            _published.Snapshot is not { } snapshot ||
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
                _lifetime.IsDisposed,
                _lifetime.IsUnloaded,
                operationToken))
        {
            return;
        }

        bool appliedCached = false;
        List<(CodeBlockHighlightCacheKey Key, string CodeText)>? pendingHighlights = null;
        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
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
                        _lifetime.IsDisposed,
                        _lifetime.IsUnloaded,
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
        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
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

                var request = new CodeBlockHighlightRequest(key.Language, codeText, key.ThemeVariant);
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
                        !ReferenceEquals(_published.Snapshot, callbackSnapshot);
                    bool providerCurrent = false;
                    if (CodeBlockHighlightOperationLifetime.IsCurrentAfterProviderCallback(
                            CodeHighlighter,
                            highlighter,
                            _codeBlockHighlightGeneration,
                            generation,
                            _lifetime.IsDisposed,
                            _lifetime.IsUnloaded,
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
                                _lifetime.IsDisposed,
                                _lifetime.IsUnloaded,
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
                            _lifetime.IsUnloaded,
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
                _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
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
        MarkdownThemeVariant variant,
        int providerIdentity,
        int providerRevision)
        => new(block.CodeLanguage, block.Metadata.CodeTextHash, block.CodeText.Length, variant, providerIdentity, providerRevision);

    private void ClearCodeBlockHighlightCache()
        => _codeBlockHighlightCache.Clear();

    private static MarkdownThemeVariant ResolveCodeBlockThemeVariant(Theming.ThemeSnapshot theme)
    {
        if (theme.IsHighContrast)
            return MarkdownThemeVariant.HighContrast;

        var bg = theme.SurfaceColor;
        double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance < 0.5 ? MarkdownThemeVariant.Dark : MarkdownThemeVariant.Light;
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
        if (_surface.Canvas is null || _surface.Root is null || _surface.Overlay is null) return;
        _surface.Canvas.Width = Math.Max(1, committedSize.Width);
        // Round the height up to whole device pixels so layout rounding never
        // clips the document's last partial pixel row. Width stays
        // as committed: rounding it up could add a horizontal scroll range.
        _surface.Canvas.Height = Math.Max(1, SelectionController.SnapExtentUpToPixelGrid(
            committedSize.Height,
            XamlRoot?.RasterizationScale ?? 1.0));
        _surface.Root.Width = _surface.Canvas.Width;
        _surface.Root.Height = _surface.Canvas.Height;
        _surface.Overlay.Width = _surface.Canvas.Width;
        _surface.Overlay.Height = _surface.Canvas.Height;
        _dragShield.SetActive(_document.IsDragSelecting);
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
        var snapshot = _published.Snapshot;
        if (RequestGeometryRecoveryIfRequired(snapshot) || snapshot is null)
            return;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
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
            preserveScrollAnchor: _surface.Scroll is not null);
    }

    private void QueueLazyLayoutForPaintRegion(Rect region)
    {
        var snapshot = _published.Snapshot;
        if (RequestGeometryRecoveryIfRequired(snapshot) || snapshot is null)
            return;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
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
            preserveScrollAnchor: _surface.Scroll is not null);
    }

    private void QueueLazyLayoutForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot))
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
             (_surface.Scroll is not null &&
              _activeLazyLayoutAnchorOffset is { } activeOffset &&
              Math.Abs(_surface.Scroll.VerticalOffset - activeOffset) < 0.5)))
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_hasPendingLazyLayoutBand)
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

        if (snapshot is null || !ReferenceEquals(snapshot, _published.Snapshot))
            return;

        var oldCts = _lazyLayoutCts;
        var oldTask = _lazyLayoutTask;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _pipelineCts?.Token ?? CancellationToken.None);
        long realizationGeneration = ++_lazyLayoutGeneration;
        _lazyLayoutPublicationHandoff.Reset(realizationGeneration);
        long pipelineGeneration = _pipelineGeneration;
        CancellationToken operationToken = cts.Token;
        double? anchorOffset = preserveScrollAnchor && _surface.Scroll is not null
            ? _surface.Scroll.VerticalOffset
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
                _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
            return;
        }

        if (realizationGeneration != _lazyLayoutGeneration)
        {
            if (snapshot is not null)
                _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
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
        if (_lifetime.IsDisposed ||
            _lifetime.IsUnloaded ||
            operationWasCanceled ||
            pipelineGeneration != _pipelineGeneration ||
            snapshot is null ||
            !ReferenceEquals(snapshot, _published.Snapshot))
        {
            StopImageRelayoutRecoveryIfIdle();
            if (snapshot is not null)
                _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
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
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: false);
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
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
            StopImageRelayoutRecoveryIfIdle();
            return;
        }

        // A user can jump to an already-measured band while this worker is
        // finishing. That jump need not queue replacement work, so the old
        // completion still arrives. Never restore its captured scroll anchor
        // over a newer viewport position.
        bool restoreAnchor = LazyLayoutScrollAnchorPolicy.ShouldRestore(
            anchorOffset, _surface.Scroll?.VerticalOffset, _scrollViewIntermediate);
        ApplyLazyLayoutCommit(snapshot, restoreAnchor ? result.ScrollAnchor : null);
        SynchronizeVectorCapacityObserver(snapshot);
        if (result.Commit.NativeSceneResourcesChanged)
            NotifyVisibleDeferredSceneItemStatusMayHaveChanged();
        _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot))
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
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot))
                return;
            ApplySnapshotSize(size);
            if (anchorOffset is { } target && _surface.Scroll is { } scroll &&
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

        TryResumePendingScrollToBlock(snapshot, _published.Generation);
        if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
            ReferenceEquals(snapshot, _published.Snapshot) &&
            _pendingLayoutCommit is null &&
            _appliedLazyLayoutRevision >= snapshot.LayoutRevision)
        {
            // Release logical input waiters only after plans/anchor are applied,
            // not when a worker first updates the snapshot's measured mask.
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
        }
    }

    private void DeferLayoutCommit((int BlockIndex, double OffsetFromTop)? scrollAnchor)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is null)
            return;
        // Retain only value metadata, never a retired graph. Keep old plans and
        // native children until one coherent geometry read is admitted. The
        // existing weak recovery timer also covers a withheld UI dispatch.
        _pendingLayoutCommit = (_published.Generation, scrollAnchor, _surface.Scroll?.VerticalOffset);
        ++_lastLayoutCommitTicket;
        StartImageRelayoutRecovery();
        if (!HasImageRelayoutRecoveryClock)
            RequestRebuild(RebuildReason.Full);
    }

    private void PublishPendingLayoutCommit()
    {
        if (_pendingLayoutCommit is not { } pending)
            return;
        if (pending.Generation != _published.Generation || _published.Snapshot is not { } snapshot)
        {
            _pendingLayoutCommit = null;
            return;
        }
        bool restoreAnchor = LazyLayoutScrollAnchorPolicy.ShouldRestore(
            pending.ScrollOffset, _surface.Scroll?.VerticalOffset, _scrollViewIntermediate);
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
                plan.Derealize(_embedContext);
            foreach (CodeBlockActionPlan plan in actionPlans)
            {
                if (plan.Realized is not null) plan.Derealize(_embedContext);
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
        long planGeneration = _published.Generation;
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

                        newPlan.AdoptRealizedFrom(oldPlan, _embedContext);
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
                oldPlan.Derealize(_embedContext);
            foreach (CodeBlockActionPlan oldPlan in oldActionPlans)
            {
                if (oldPlan.Realized is not null)
                    oldPlan.Derealize(_embedContext);
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
            ? FocusableItemQueries.FindFocusableItemIndex(collectedFocusableItems, identity)
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
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded && generation == _published.Generation &&
        ReferenceEquals(snapshot, _published.Snapshot);

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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
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
                if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
                    break;

                RealizeVisibleEmbedsCore();
                if (!_realizeVisibleEmbedsRequested)
                    break;
            }
            queueContinuation = _realizeVisibleEmbedsRequested &&
                !_lifetime.IsDisposed && !_lifetime.IsUnloaded && !_derealizingAllEmbeds;
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
            _lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
        {
            _realizeVisibleEmbedsRequested = false;
            return;
        }

        RealizeVisibleEmbeds();
    }

    private void RealizeVisibleEmbedsCore()
    {
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return;

        long snapshotGeneration = _published.Generation;
        long realizationPassEpoch = _realizationPassEpoch;
        ViewportBandIndex? imageIndex = _imagePlanIndex;
        ViewportBandIndex? embedIndex = _embedPlanIndex;
        ViewportBandIndex? actionIndex = _codeBlockActionPlanIndex;
        if (!IsCurrentVisibleRealizationPass(
                snapshot, snapshotGeneration, realizationPassEpoch,
                imageIndex, embedIndex, actionIndex))
            return;

        _viewport.TryGet(out double top, out double viewportHeight, out _);
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
                plan.Realize(_embedContext);
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
            plan.Derealize(_embedContext);
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
                plan.Realize(_embedContext);
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
            plan.Derealize(_embedContext);
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
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded && !_derealizingAllEmbeds &&
        snapshotGeneration == _published.Generation &&
        realizationPassEpoch == _realizationPassEpoch &&
        ReferenceEquals(snapshot, _published.Snapshot) &&
        ReferenceEquals(imageIndex, _imagePlanIndex) &&
        ReferenceEquals(embedIndex, _embedPlanIndex) &&
        ReferenceEquals(actionIndex, _codeBlockActionPlanIndex);

    private void GetCodeBlockActionRealizationCounts(out int realizedCount, out int offscreenCount)
    {
        _viewport.TryGet(out double top, out double viewportHeight, out _);
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
        _viewport.TryGet(out double viewportTop, out double viewportHeight, out double viewportWidth);
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
        if (_lifetime.IsUnloaded ||
            pipelineGeneration != _pipelineGeneration ||
            _surface.Overlay is null ||
            !_embedPlans.Contains(plan))
            return false;

        _viewport.TryGet(out double top, out double viewportHeight, out _);
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
        => !_lifetime.IsUnloaded &&
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
        if (FocusableItemQueries.SameFocusableIdentity(focusedItem, planIdentity))
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
        if (!enqueued && !_lifetime.IsDisposed && !_lifetime.IsUnloaded)
            RecordRejectedCrossThreadRebuildRequest(RebuildReason.Full);
    }

    private void ApplyImageLoadCompleted(Layout.Boxes.ImageBox? completedImage, bool layoutInvalidated,
        bool bitmapOutputRecoverySignal = false)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;
        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
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
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_subscribedImages.Contains(completedImage))
                return;
            if (layoutInvalidated && !_publishingLayoutCommit &&
                PerformanceSession is IMarkdownPerformanceSessionInternal session &&
                !session.IsDisposed && ReferenceEquals(session, completedImage.Context.PerformanceSession) &&
                _published.Snapshot is { } snapshot &&
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
            _adorner.Invalidate();
        }
    }

    private void QueueImageRelayout(int blockIndex)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is null)
            return;

        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
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
            if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded)
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
            if (!control._lifetime.IsDisposed && !control._lifetime.IsUnloaded)
            {
                if (control.RequestGeometryRecoveryIfRequired(control._published.Snapshot))
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
                    if (!control.RequestGeometryRecoveryIfRequired(control._published.Snapshot))
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
            if (!control.RequestGeometryRecoveryIfRequired(control._published.Snapshot))
                control.RequestRebuild(RebuildReason.Full);
            else
                control.ClearFocusableItemsRefreshForCurrentSnapshot();
            if (control._published.Snapshot is { } failedLayoutSnapshot)
                control._pointerInput.OnLazyLayoutCompletion(failedLayoutSnapshot, succeeded: false);
        }
        finally
        {
            if (control._lifetime.IsDisposed || control._lifetime.IsUnloaded)
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

        LayoutSnapshot? snapshot = _published.Snapshot;
        if (RequestGeometryRecoveryIfRequired(snapshot))
            return;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || snapshot is null ||
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
        if (_pointerInput.HasSnapshotInteraction)
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
            if (_surface.Scroll is { VerticalOffset: > 0 } scroll &&
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
                ? new RasterPlacementInputs(receiver, _published.Generation,
                    _imagePreparationOwnerToken, _imageLifetimeCts?.Token ?? CancellationToken.None,
                    _snapshotRetirements, _surface.Scroll?.VerticalOffset)
                : null;
            _imageRelayoutTask = RunImageRelayoutAsync(
                payload, anchor, _published.Generation, cancellation, DispatcherQueue,
                receiver, session, rasterPlacement, snapshot.PointerInputEpoch,
                _surface.Scroll?.VerticalOffset);
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _taskCommandAvailabilityRefreshQueued)
            return;

        _taskCommandAvailabilityRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(RefreshTaskCommandAvailability))
            _taskCommandAvailabilityRefreshQueued = false;
    }

    private void RefreshTaskCommandAvailability()
    {
        _taskCommandAvailabilityRefreshQueued = false;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot)
            return;

        MarkFocusableItemsRefreshPending(snapshot, _published.Generation);
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return false;

        LayoutSnapshot? snapshot = _published.Snapshot;
        long generation = _published.Generation;
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
            else if (!ReferenceEquals(snapshot, _published.Snapshot) || generation != _published.Generation)
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
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            generation != _published.Generation ||
            automationGeneration != snapshot.AutomationDocumentGeneration ||
            revisionUnderLease != revisionBefore ||
            revisionAfter != revisionBefore ||
            !IsCurrentFocusableItemsRefresh(
                request,
                snapshot,
                generation,
                automationGeneration))
        {
            if (ReferenceEquals(snapshot, _published.Snapshot) &&
                generation == _published.Generation &&
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
            ? FocusableItemQueries.FindFocusableItemIndex(collected, identity)
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
            ReferenceEquals(snapshot, _published.Snapshot) &&
            generation == _published.Generation &&
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
            ReferenceEquals(snapshot, _published.Snapshot) &&
            generation == _published.Generation &&
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
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
        {
            ResetPendingFocusableItemsRefresh();
            return;
        }

        var pending = _pendingFocusableItemsRefresh;
        if (pending.IsPending &&
            pending.SnapshotGeneration == _published.Generation &&
            pending.AutomationDocumentGeneration == snapshot.AutomationDocumentGeneration)
        {
            ClearFocusableItemsRefresh(pending.RequestVersion);
        }
    }

    private void ResetPendingFocusableItemsRefresh() =>
        _pendingFocusableItemsRefresh = default;

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !IsLoaded || !ReferenceEquals(sender, _surface.Canvas))
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

        if (_published.Snapshot is not { } snapshot) return;
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
            !_lifetime.IsDisposed && !_lifetime.IsUnloaded)
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
            _published.Generation == _pipelineGeneration &&
            _snapshotPipelineStartTimestamp != 0)
        {
            long pipelineStartTimestamp = _snapshotPipelineStartTimestamp;
            long sourceUtf16Bytes = _snapshotSourceUtf16Bytes;
            long generation = _published.Generation;
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
            if (current && snapshot is not null && !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                (!canceled || rootMeasureCommitted) &&
                generation == _published.Generation && ReferenceEquals(snapshot, _published.Snapshot))
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
                    if (_pointerInput.HasSnapshotInteraction)
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
                        (canceled || capturedScrollOffset != _surface.Scroll?.VerticalOffset) ? null : anchor);
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
            if (current && canceled && !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                ReferenceEquals(snapshot, _published.Snapshot) && snapshot?.GeometryRecoveryRequired != true)
            {
                foreach (var image in _subscribedImages)
                    _pendingImageRelayoutBlockIndices.Add(image.BlockIndex);
            }
            // The pending set is cleared before every snapshot replacement.
            // New-snapshot completions may accumulate while an old task drains.
            if (current && _pendingImageRelayoutBlockIndices.Count > 0 && !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
                _published.Snapshot?.GeometryRecoveryRequired != true)
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
        CanvasDevice? layoutDevice = _published.LayoutDevice;
        CanvasDevice? surfaceDevice = _adorner.SurfaceDevice;
        CanvasDevice? canvasDevice = null;
        try { canvasDevice = _surface.Canvas?.Device; }
        catch (Exception deviceException)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] canvas device query failed: {deviceException.Message}");
        }

        _adorner.TryReleaseSurface();
        // Snapshot bitmaps are cached under the layout (shared) device, which
        // need not be the CanvasVirtualControl device.
        ReleaseImageDeviceResources(layoutDevice);
        if (!ReferenceEquals(canvasDevice, layoutDevice))
            ReleaseImageDeviceResources(canvasDevice);
        // Exceptions were caught instead of reaching Win2D, so notify each
        // genuinely lost device as the manual device-loss contract requires.
        NotifyLostCanvasDevices(failureHResult, layoutDevice, surfaceDevice, canvasDevice);

        if (_canvasDeviceRecoveryQueued || _lifetime.IsDisposed || _lifetime.IsUnloaded)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(sender, _surface.Canvas))
            return;

        // Initial resource creation can be deferred until the first input frame in
        // packaged WinUI. Rebuilding there resets pointer capture and selection
        // state, which makes the first click/drag flash and get swallowed. Layout
        // uses CanvasDevice.GetSharedDevice(), so initial creation needs no rebuild.
        // Later resource creation means device/DPI changed, so rebuild layouts.
        if (args.Reason != Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.FirstTime)
            RequestRebuild(RebuildReason.Full);
    }

    private void RefreshHorizontalOverflowVisualState(
        IHorizontalOverflowBox target,
        double oldOffset)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot ||
            target is not BlockBox targetBlock)
        {
            return;
        }

        long generation = _published.Generation;
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
        else if (ReferenceEquals(snapshot, _published.Snapshot) &&
                 generation == _published.Generation)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            state.Snapshot is null || state.Target is null ||
            !state.Snapshot.TryGetTarget(out snapshot) ||
            !state.Target.TryGetTarget(out target) ||
            !IsCurrentScrollTarget(snapshot, state.SnapshotGeneration, state.LayoutRevision))
        {
            if (snapshot is not null &&
                ReferenceEquals(snapshot, _published.Snapshot) &&
                state.SnapshotGeneration == _published.Generation)
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

        if (_surface.Canvas is { } canvas)
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
        if (snapshot is null || !ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation)
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
        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
        {
            _hasPendingHorizontalOverflowRefresh = false;
            return;
        }
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot ||
            _pendingHorizontalOverflowRefreshGeneration != _published.Generation)
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
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded ||
            !ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation ||
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

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } currentSnapshot)
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
        if (_published.Snapshot is not { } snapshot)
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
        if (_published.Snapshot is not { } snapshot || !double.IsFinite(percent))
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

    private void InvalidateInteractiveTextAdorner()
    {
        if (_surface.Overlay is null)
            return;

        if (!_adorner.IsCreated && !_selection.IsActive &&
            _hover.Run is not LinkRun && !TryGetFocusedLink(out _, out _))
            return;

        _adorner.Invalidate();
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

        _adorner.InvalidateGeometry();
        _adorner.Invalidate();
    }

    bool ISelectionAdornerHost.IsDeviceRecoveryQueued => _canvasDeviceRecoveryQueued;

    bool ISelectionAdornerHost.HasFocusedLink => TryGetFocusedLink(out _, out _);

    bool ISelectionAdornerHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot snapshot)
        => RequestGeometryRecoveryIfRequired(snapshot);

    void ISelectionAdornerHost.HandleDeviceLost(Exception exception) => HandleCanvasDeviceLost(exception);

    void ISelectionAdornerHost.RequestRebuildForDeviceChange() => RequestRebuild(RebuildReason.Full);

    void ISelectionAdornerHost.UpdateSelectionHandlesUnderPaint(LayoutSnapshot snapshot, Rect viewport)
        => _handles.UpdateUnderPaint(snapshot, viewport);

    internal void AppendVisibleSelectionHandleAutomationPeers(IList<AutomationPeer> peers)
        => _handles.AppendVisibleAutomationPeers(peers);

    void ISelectionAdornerHost.PaintInteractiveDocumentState(
        LayoutSnapshot snapshot,
        CanvasDrawingSession drawingSession,
        Rect viewport,
        IReadOnlyList<SelectionBackgroundGeometry> selectionBackgrounds)
        => PaintInteractiveDocumentState(snapshot, drawingSession, viewport, selectionBackgrounds);

    private void InvalidateCanvas()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !IsLoaded)
            return;

        try
        {
            _surface.Canvas?.Invalidate();
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
        _adorner.MarkGeometryDirty();
        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return;
        _adorner.Invalidate();
    }

    private bool HasInteractiveTextAdornerContent()
        => (_selection.IsActive && _adorner.Rectangles.Count > 0) ||
           _hover.HasLink ||
           TryGetFocusedLink(out _, out _);

    private bool ShouldRefreshSelectionAdornerForScroll()
    {
        bool hasHoveredLink = _hover.HasLink;
        IReadOnlyList<Layout.FocusableItem>? focusableItems = _focusableItems;
        bool hasFocusedLink = focusableItems is not null &&
            (uint)_focusedItemIndex < (uint)focusableItems.Count &&
            focusableItems[_focusedItemIndex].Kind == Layout.FocusableItemKind.Link;
        bool hasVisibleAdorner = _adorner.IsVisible;
        bool hasRetainedRectangles = _adorner.Rectangles.Count > 0;
        bool hasPendingRefresh = _adorner.HasPendingRefresh;

        return SelectionAdornerScrollRefreshPolicy.ShouldRefreshForScroll(
            hasActiveSelection: _selection.IsActive,
            hasHoveredLink: hasHoveredLink,
            hasFocusedLink: hasFocusedLink,
            hasVisibleAdorner: hasVisibleAdorner,
            hasRetainedRectangles: hasRetainedRectangles,
            hasTouchHandles: _touchSelection.ShouldShowHandles,
            hasPendingRefresh: hasPendingRefresh);
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
        if (_hover.TryGetLink(out var hover, out var hoverBox))
            hoverBox.PaintLinkStateForeground(drawingSession, hover, focused: false, viewport);

        if (TryGetFocusedLink(out var focusedBox, out var focusedLink))
            focusedBox.PaintLinkStateForeground(drawingSession, focusedLink, focused: true, viewport);
    }

    private bool ActivateLinkIntent(
        LinkActivationIntent intent,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        bool IsCurrent() => IsCurrentScrollTarget(
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
                : LinkTargets.GetRequestedDisposition(modifiers),
            link.Action));
        return true;
    }

    private void ResumePendingRebuildAfterPointerInteraction()
    {
        if (!_rebuildDispatchState.IsQueued && !_lifetime.IsUnloaded)
            QueuePendingRebuild();
        if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded && _pendingImageRelayoutBlockIndices.Count > 0)
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
        if (_published.Snapshot is not { } snapshot) return;
        long snapshotGeneration = _published.Generation;
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

        if (_surface.Scroll is not null)
            _surface.Scroll.ChangeView(null, Math.Max(0, targetTop - 24), null, disableAnimation: true);
        else
            BringDocumentRectIntoExternalViewport(
                new Rect(0, Math.Max(0, targetTop - 24), 1, Math.Max(1, target.OwnerHeight)),
                animate: false);
    }

    private bool IsCurrentScrollTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(snapshot, _published.Snapshot) &&
        snapshotGeneration == _published.Generation &&
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
        if (_published.Snapshot is null) return false;
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
                ? _published.Snapshot.FootnoteDefBlock(order)
                : _published.Snapshot.FootnoteRefBlock(order);
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
            if (_published.Snapshot.FragmentTargetBlock(id) is { } idx)
            {
                ScrollToBlock(idx);
                return true;
            }
        }

        return false;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_published.Snapshot is null || RequestGeometryRecoveryIfRequired(_published.Snapshot)) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        switch (e.Key)
        {
            case VirtualKey.C when ctrl && _selection.IsActive:
                e.Handled = _selectionCommands.Execute(MarkdownCommandKind.CopyRendered);
                return;
            case VirtualKey.A when MarkdownKeyboardInputPolicy.ShouldSelectAll(ctrl, IsSelectionEnabled):
                _selection.SetAnchor(DocumentPosition.Zero);
                _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
                e.Handled = true;
                return;
            case VirtualKey.F10 when shift && _selection.IsActive:
                e.Handled = _contextMenu.Show(_contextMenu.GetKeyboardPoint());
                return;
            case VirtualKey.Application when _selection.IsActive:
                e.Handled = _contextMenu.Show(_contextMenu.GetKeyboardPoint());
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
        if (_published.Snapshot is not { } snapshot ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return FocusGeometryReadStatus.Ready;
        }

        long generation = _published.Generation;
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
        if (_published.Snapshot is not { } snapshot ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return FocusGeometryReadStatus.Ready;
        }

        long generation = _published.Generation;
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
                HostedFocusNavigation.SuppressHostedTabStops(realized, suppressed, seen);
        }
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is { } realized)
                HostedFocusNavigation.SuppressHostedTabStops(realized, suppressed, seen);
        }

        if (suppressed.Count == 0)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var (control, isTabStop) in suppressed)
                control.IsTabStop = isTabStop;
        });
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

        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return FocusGeometryReadStatus.Stale;
        long generation = _published.Generation;
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
        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return false;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot ||
            _focusableItems is not { } items || items.Count == 0)
        {
            return false;
        }

        long generation = _published.Generation;
        if (_pendingSpatialFocusKeys.Count > 0 &&
            (_pendingSpatialFocusGeneration != generation ||
             !_hasPendingSpatialFocusOrigin ||
             GetCurrentFocusableIdentity() is not { } current ||
             !FocusableItemQueries.SameFocusableIdentity(current, _pendingSpatialFocusOrigin)))
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
        if (_processingPendingFocusGeometry || _lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot)
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
                if (_pendingSpatialFocusGeneration != _published.Generation ||
                    !_hasPendingSpatialFocusOrigin ||
                    GetCurrentFocusableIdentity() is not { } current ||
                    !FocusableItemQueries.SameFocusableIdentity(current, _pendingSpatialFocusOrigin))
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
                long generation = _published.Generation;
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
                        _focusResumeItemIndex = FocusableItemQueries.FindNearestFocusableItem(map, point);
                    }
                    else if (status == FocusGeometryReadStatus.Stale &&
                             generation != _published.Generation)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        if (RequestGeometryRecoveryIfRequired(_published.Snapshot))
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

    private bool CommitFocusedItem(bool reverse)
    {
        var items = _focusableItems;
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null ||
            items is null ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            _pendingLazyFocus = null;
            return false;
        }

        long snapshotGeneration = _published.Generation;
        if (!ReferenceEquals(snapshot, _published.Snapshot))
            return true;

        Layout.FocusableItem item = items[_focusedItemIndex];
        FocusTargetLayoutState layoutState = snapshot.TryGetFocusTargetLayoutState(
            item.BlockIndex,
            out LazyLayoutBand estimatedBand);
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation)
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
                    preserveScrollAnchor: _surface.Scroll is not null);
            }

            return true;
        }

        if (layoutState == FocusTargetLayoutState.Unavailable)
        {
            // A retiring snapshot or malformed custom block index must never
            // turn an already-consumed Tab into focus escape.
            _pendingLazyFocus = null;
            _ = Focus(FocusState.Keyboard);
            if (!ReferenceEquals(snapshot, _published.Snapshot) ||
                snapshotGeneration != _published.Generation)
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
                if (_lifetime.IsUnloaded) return;
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
            pending.SnapshotGeneration != _published.Generation)
        {
            _pendingLazyFocus = null;
            return false;
        }

        int index = FocusableItemQueries.FindFocusableItemIndex(_focusableItems, pending.Item);
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

    private void ScrollLazyFocusTargetIntoView(LazyLayoutBand band, bool reverse)
    {
        _viewport.TryGet(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        const double margin = 24;
        if (band.Top >= viewportTop + margin && band.Bottom <= viewportBottom - margin)
            return;

        if (_surface.Scroll is not null)
        {
            double target = reverse
                ? band.Bottom - viewportHeight + margin
                : band.Top - margin;
            double maximum = Math.Max(0, (_published.Snapshot?.Size.Height ?? band.Bottom) - viewportHeight);
            _surface.Scroll.ChangeView(
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
        MarkdownInputModifiers modifiers = InputModifierState.GetCurrentInputModifiers();
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
        if (_published.Snapshot is null) return false;
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

        if (_published.Snapshot is not { } snapshot)
        {
            HideFocusRing();
            return;
        }

        long generation = _published.Generation;
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
            _lifetime.IsDisposed || _lifetime.IsUnloaded ||
            snapshotGeneration != _published.Generation)
        {
            return;
        }

        if (item is not { } expected ||
            rect is not { Width: > 0, Height: > 0 } r ||
            GetCurrentFocusableIdentity() is not { } current ||
            !FocusableItemQueries.SameFocusableIdentity(current, expected))
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
            _published.Generation,
            item: null,
            rect: null);
    }

    private bool EnsureFocusRing()
    {
        if (_focusRing is not null)
            return true;

        if (_surface.Overlay is null || _lifetime.IsUnloaded || XamlRoot is null)
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
            _surface.Overlay.Children.Add(focusRing);
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
        if (_published.Snapshot is not { } snapshot)
            return null;
        return TryCaptureFocusableItemRect(
            snapshot,
            _published.Generation,
            snapshot.LayoutRevision,
            item,
            out Rect? rect) == FocusGeometryReadStatus.Ready
            ? rect
            : null;
    }

    private void RememberFocusResumePoint(Point documentPoint)
    {
        if (_published.Snapshot is not { } snapshot)
        {
            _focusResumeItemIndex = -1;
            _hasPendingFocusResumePoint = false;
            return;
        }

        long generation = _published.Generation;
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
        _focusResumeItemIndex = FocusableItemQueries.FindNearestFocusableItem(map, documentPoint);
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
                if (FocusableItemQueries.GetFocusableItemRectFromBlock(block, item) is { } nested)
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

    /// <summary>Scrolls the focused item into view if it's outside the current viewport.</summary>
    /// <summary>Records an explicit scroll request; pending focus reveals yield to it.</summary>
    private void NoteViewportRequest() => _viewportRequestSerial++;

    /// <summary>
    /// Starts revealing the focused item from the current viewport. Geometry
    /// retries continue it only while <see cref="IsFocusRevealCurrent"/> holds.
    /// </summary>
    private bool BeginFocusedItemReveal()
    {
        _viewport.TryGet(out double top, out _, out _);
        _focusReveal = new FocusRevealOrigin(_viewportRequestSerial, top, null);
        return ScrollFocusedItemIntoView();
    }

    private bool IsFocusRevealCurrent()
    {
        _viewport.TryGet(out double top, out _, out _);
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
        if (_published.Snapshot is not { } snapshot)
            return false;

        long generation = _published.Generation;
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
                FocusableItemQueries.SameFocusableIdentity(items[focusedIndex], item))
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

        _viewport.TryGet(out double top, out double viewportHeight, out _);
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !weakSnapshot.TryGetTarget(out LayoutSnapshot? snapshot) ||
            !ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation)
        {
            return;
        }
        if (snapshot.LayoutRevision != layoutRevision)
        {
            RequestFocusGeometryRetry();
            return;
        }
        if (GetCurrentFocusableIdentity() is not { } current ||
            !FocusableItemQueries.SameFocusableIdentity(current, item))
            return;
        if (!IsFocusRevealCurrent())
            return;

        _viewport.TryGet(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        const double margin = 24.0;
        double? revealTop = null;
        if (rect.Top < top + margin)
        {
            revealTop = _surface.Scroll is not null ? Math.Max(0, rect.Top - margin) : rect.Top;
            _ = _surface.Scroll is not null
                ? _surface.Scroll.ChangeView(null, Math.Max(0, rect.Top - margin), null, disableAnimation: false)
                : BringDocumentRectIntoExternalViewport(rect, animate: true);
        }
        else if (rect.Bottom > bottom - margin)
        {
            revealTop = _surface.Scroll is not null
                ? rect.Bottom - viewportHeight + margin
                : rect.Bottom - viewportHeight;
            _ = _surface.Scroll is not null
                ? _surface.Scroll.ChangeView(null, rect.Bottom - viewportHeight + margin, null, disableAnimation: false)
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
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot ||
            _focusableItems is not { } items)
        {
            return HostedFocusPlanStatus.Stale;
        }

        long generation = _published.Generation;
        long revision = snapshot.LayoutRevision;
        long epoch = Volatile.Read(ref _hostedFocusPlanEpoch);
        tuple = new HostedFocusTuple(snapshot, generation, revision, epoch, items);
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            generation != _published.Generation)
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
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(tuple.Snapshot, _published.Snapshot) &&
        tuple.SnapshotGeneration == _published.Generation &&
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
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(tuple.Snapshot, _published.Snapshot) &&
        tuple.SnapshotGeneration == _published.Generation &&
        tuple.LayoutRevision == tuple.Snapshot.LayoutRevision &&
        tuple.PlanEpoch == Volatile.Read(ref _hostedFocusPlanEpoch) &&
        ReferenceEquals(tuple.Items, _focusableItems) &&
        FocusableItemQueries.IsFocusedIdentity(tuple.Items, _focusedItemIndex, item);

    private bool IsCurrentPendingLazyFocus(in PendingLazyFocus pending) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(pending.Snapshot, _published.Snapshot) &&
        pending.SnapshotGeneration == _published.Generation &&
        _pendingLazyFocus is { } current &&
        FocusableItemQueries.SamePendingLazyFocus(current, pending);

    private bool IsCurrentHostedFocusOperation(
        in HostedFocusTuple tuple,
        Layout.FocusableItem item) =>
        IsCurrentHostedFocusTuple(tuple) &&
        FocusableItemQueries.IsFocusedIdentity(tuple.Items, _focusedItemIndex, item);

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

        if (HostedFocusNavigation.TryFocusHostedDescendant(element, reverse))
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
                FocusableItemQueries.SameFocusableIdentity(identity, item) &&
                plan.Realized is { } element)
            {
                return element;
            }
        }

        foreach (CodeBlockActionPlan plan in _codeBlockActionPlans)
        {
            if (plan.FocusIdentity is { } identity &&
                FocusableItemQueries.SameFocusableIdentity(identity, item) &&
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
        if (FocusableItemQueries.SameFocusableIdentity(focusedItem, planIdentity))
            _ = TryFocusHostedElement(focusedItem, reverse: false);
    }

    private void PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
    {
        if (_lifetime.IsUnloaded || _lifetime.IsDisposed)
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

        if (focused is null || !VisualTreeAncestry.IsWithin(focused, element))
            return;

        // Keep the renderer's logical focus index while the native copy button
        // is recycled. If the action comes back into the realize band,
        // RestoreFocusedCodeBlockActionIfNeeded transfers focus back to it.
        _ = Focus(FocusState.Keyboard);
        UpdateFocusRing();
    }

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

        int focusedIndex = FocusableItemQueries.FindFocusableItemIndex(tuple.Items, focusedIdentity);
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
        if (_published.Snapshot is not null)
        {
            foreach (BlockBox block in _published.Snapshot.Blocks)
            {
                if (DocumentHitTesting.TryGetVectorPointerStateInBlock(block, point, out linked, out selectable))
                    return true;
            }
        }

        linked = false;
        selectable = false;
        return false;
    }

    private LinkTarget? FindLinkTargetAt(DocumentPosition pos, Point? point = null)
    {
        return _published.Snapshot is { } snapshot
            ? LinkTargets.FindLinkTargetAt(snapshot, pos, point)
            : null;
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
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (items is null || snapshot is null ||
            _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        return FocusableItemQueries.TryGetLinkedAtomicForFocusable(snapshot, items[_focusedItemIndex], out inline, out run);
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
            _published.Snapshot is null ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count ||
            !items[_focusedItemIndex].IsHorizontalOverflow ||
            !_published.Snapshot.TryGetHorizontalOverflowForBlockIndex(
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
        if (items is null || _published.Snapshot is null)
            return false;

        int equivalentLinkIndex = -1;
        for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            if (!items[itemIndex].IsLink ||
                !FocusableItemQueries.TryGetLinkedAtomicForFocusable(_published.Snapshot, items[itemIndex], out _, out InlineRun candidate))
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
                LinkTargets.AreEquivalentLinks(candidateLink, requestedLink))
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
        var snapshot = _published.Snapshot;
        if (items is null || snapshot is null)
        {
            index = -1;
            return false;
        }

        return FocusableItemQueries.TryFindHorizontalOverflowFocusableIndex(snapshot, items, overflow, out index);
    }

    private bool TryGetVectorSemanticForFocusable(
        Layout.FocusableItem item,
        out Layout.Boxes.VectorSceneBox vector,
        out int semanticIndex)
    {
        vector = null!;
        semanticIndex = -1;
        if (!item.IsVectorSemantic || _published.Snapshot is null)
            return false;

        foreach (BlockBox block in _published.Snapshot.Blocks)
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

    // ---- Word / line selection helpers ----

    // ---- Right-click context menu ----

    private bool TryActivateDisclosure(LinkRun? run)
    {
        if (run is not { IsDisclosure: true })
        {
            return false;
        }

        SetDisclosureExpanded(run, !run.IsExpanded);
        return true;
    }
}
