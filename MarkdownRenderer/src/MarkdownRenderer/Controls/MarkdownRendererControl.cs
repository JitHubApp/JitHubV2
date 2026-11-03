using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
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
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
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
/// The native Win2D + DirectWrite markdown renderer shared by
/// <see cref="MarkdownScrollView"/>, which owns its scroll viewport, and
/// <see cref="MarkdownDocumentView"/>, which uses an ancestor's viewport.
/// Hosts a <see cref="CanvasVirtualControl"/> for paint and a sibling
/// <see cref="Canvas"/> overlay for hosted WinUI embeds.
/// </summary>
public abstract class MarkdownRendererControl : UserControl, IDisposable, IAsyncDisposable,
    IMarkdownPerformanceLegacyParser, ISelectionAdornerHost, ISelectionCommandsHost, ISelectionDismissalHost,
    ISelectionHandlesHost, ISelectionContextMenuHost, IPointerInputSink, IPointerInteractionHost,
    IHorizontalOverflowPointerHost, IPointerInputRouterHost, IDeferredGestureLayoutHost, IEmbedRealizationHost,
    ICodeBlockCopyButtonHost, IFocusRingHost, IHostedElementFocusHost,
    IHorizontalOverflowScrollHost, IFocusedItemRevealHost, IFocusedItemCommitHost,
    IFocusGeometryRecoveryHost, IRendererFocusEntryHost, IImageSubscriptionsHost, IImageResidencyHost,
    ICanvasDeviceRecoveryHost, ICodeBlockHighlightingHost, IRecoveryClockHost, IRasterPlacementHost,
    IImageRelayoutHost, IImageLoadCompletionHost, IVectorCapacityRetryHost,
    ILazyLayoutHost, ILayoutCommitHost, IFocusableItemsPublicationHost,
    IRebuildSchedulerHost, IRebuildLayoutSetupHost, ICanvasTilePainterHost,
    IDeferredSceneReadinessHost, ILinkActivationHost, IRendererLocalizationHost, IRendererEventSink
{
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime = new();
    private readonly RendererEvents _events;
    private readonly PublishedLayout _published = new();
    private readonly MarkdownViewport _viewport;
    private readonly PointerHoverState _hover = new();
    private readonly PointerCursorPresenter _cursor;
    private readonly AbbreviationTooltipPresenter _abbreviationTooltip;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly CanvasDeviceRecovery _deviceRecovery;
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
    private readonly EmbedRealization _embeds;
    private readonly WindowDestroyingWatch _windowDestroying;
    private readonly RecoveryClock _recoveryClock;

    private readonly SnapshotRetirementTracker _snapshotRetirements = new(
        static exception => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] retirement failed: {exception.GetType().Name} " +
            $"(0x{exception.HResult:X8})."));
    private readonly PipelineCancellationRetirement _cancellationRetirement;
    private readonly RebuildScheduler _rebuild;
    private readonly RebuildLayoutSetup _layoutSetup;
    private readonly CanvasTilePainter _painter;
    private readonly CanvasEvents _canvasEvents;
    private readonly CanvasInvalidation _canvasInvalidation;
    private readonly GeometryRecovery _geometryRecovery;
    private readonly InteractiveTextAdorner _textAdorner;
    private readonly RecoveryClockWork _recoveryWork;
    private readonly VisibleWorkScheduler _visibleWork;
    private readonly ScrollViewTracking _scrolling;
    private readonly KeyboardInput _keyboard;
    private readonly DocumentInputs _documentInputs;
    private readonly VisibleImageReadiness _imageReadiness;
    private readonly DeferredSceneReadiness _deferredScenes;
    private readonly BlockScrollNavigation _blockScroll;
    private readonly LinkActivation _links;
    private readonly RendererAutomation _automation;
    private readonly ImageLifetimeScope _imageLifetime;
    private readonly MarkdownAutomationDocumentGeneration _automationDocumentGenerations = new();
    // Opt-in used only by the standalone quick-profile harness. This does not
    // attach a PerformanceSession or alter normal layout scheduling; it merely
    // enables the existing build-stage counters for this control instance.
    internal bool EnableStandaloneLayoutDiagnostics { get; set; }
    private readonly LayoutCommitState _layoutCommit = new();
    private readonly LayoutCommitPublisher _layoutPublisher;
    private readonly object _imagePreparationOwnerToken = new();
    private readonly RasterPlacementPublisher _rasterPlacement;
    private readonly ImageRelayout _imageRelayout;
    private readonly ImageLoadCompletion _imageLoadCompletion;
    private readonly VectorCapacityRetry _vectorCapacity;
    private readonly LazyLayoutRealization _lazyLayout;
    // Hosted-focus readers consume only realization plans published for the
    // exact snapshot generation and layout revision. The attempt epoch also
    // invalidates a plan while a same-snapshot rebuild is being transferred.
    private readonly HostedFocusPlanPublication _hostedFocusPlan = new();
    private readonly LayoutWidthWatch _width;
    private readonly RebuildInputCapture _inputCapture;
    private readonly RebuildPipeline _pipeline;
    private readonly SelectionController _selection = new();
    private readonly MarkdownTouchSelectionSession _touchSelection = new();
    private readonly TouchSelectionRestore _touchRestore;
    private readonly RendererLocalization _localization;
    private readonly AutomationScrolling _automationScrolling;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression = new();
    private readonly InteractionReset _interactionReset;

    private readonly SnapshotExtent _extent;
    private readonly ReplacementPublisher _replacementPublisher;
    private readonly ReplacementPresentation _replacementPresentation;
    private readonly CodeBlockClipboard _codeBlockClipboard;
    private readonly VisibleImageLoading _visibleImages;
    private readonly PointerInteractionResume _pointerResume;

    // ---- Keyboard navigation ----
    private readonly KeyboardFocusModel _focus;
    private readonly FocusGeometryReader _focusGeometry;
    private readonly HostedElementFocus _hostedFocus;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly FocusedItemReveal _reveal;
    private readonly FocusedItemCommit _focusCommit;
    private readonly KeyboardFocusNavigator _navigator;
    private readonly PendingFocusGeometryWork _focusGeometryWork;
    private readonly RendererFocusEntry _focusEntry;
    // A deferred focus-plan refresh is value-only: it never keeps a snapshot,
    // box, or native resource alive while a geometry lease is busy.
    private readonly FocusableItemsRefreshRequest _focusRefresh;
    private readonly FocusableItemsPublication _focusableItems;
    private readonly FocusRingPresenter _focusRing;

    private readonly PerformanceSessionSubscription _performanceSession;
    private readonly SvgRendererSubscription _svgRenderer;
    private readonly ThemeSubscription _theme;
    private readonly EnvironmentSubscription _environment;

    // Lazy-load queue: all ImageBox instances in the current snapshot.
    // EnsureLoading() is called for each when its bounds enter the
    // viewport + LazyImageOverscanPx band.  Images already in the
    // in-memory cache start loading (no-op) immediately after build.
    private readonly ImagePlanSet _imagePlans;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImageResidency _imageResidency;
    private readonly ImageSourcePrefetch _imagePrefetch = new();
    private readonly CodeBlockHighlighting _highlighting;
    private readonly CompletedImageCarryLedger _completedImageCarry = new();

    /// <summary>
    /// Overscan band (pixels, each direction) within which off-screen images
    /// are preemptively loaded.  Wider than the embed virtualisation overscan
    /// so images appear before they scroll into view.
    /// </summary>
    public const double LazyImageOverscanPx = 800;

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

    // ---- Dependency properties ----

    /// <summary>Dependency property backing <see cref="Markdown"/>.</summary>
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownRendererControl),
            new PropertyMetadata(string.Empty, (d, _) => ((MarkdownRendererControl)d)._documentInputs.OnMarkdownChanged()));

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
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d)._documentInputs.OnDocumentChanged(e)));

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
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d)._documentInputs.OnEngineChanged(e)));

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
    internal void SetOwnedEngine(MarkdownEngine engine) => _documentInputs.SetOwnedEngine(engine);

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
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d)._theme.OnThemePropertyChanged(e)));

    /// <summary>Gets or sets the renderer theme.</summary>
    public MarkdownTheme? Theme
    {
        get => (MarkdownTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    internal static readonly DependencyProperty ExtensionRegistryProperty =
        DependencyProperty.Register(nameof(ExtensionRegistry), typeof(MarkdownExtensionRegistry),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d)._documentInputs.OnParserConfigurationChanged()));

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
                ((MarkdownRendererControl)d)._svgRenderer.OnChanged(e)));

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
                ((MarkdownRendererControl)d)._performanceSession.OnChanged(e)));

    /// <summary>
    /// Gets or sets the optional, host-owned resource preparation session. The
    /// control borrows this session and never disposes it.
    /// </summary>
    public IMarkdownPerformanceSession? PerformanceSession
    {
        get => (IMarkdownPerformanceSession?)GetValue(PerformanceSessionProperty);
        set => SetValue(PerformanceSessionProperty, value);
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

    internal MarkdownLinkPeer GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run) =>
        _automation.GetOrCreateLinkPeer(parent, run);

    internal void InvalidateAutomationChildPeerCaches() => _automation.InvalidateChildPeerCaches();

    internal MarkdownLinkedImagePeer GetOrCreateLinkedImagePeer(MarkdownBlockPeer parent, InlineImageRun run) =>
        _automation.GetOrCreateLinkedImagePeer(parent, run);

    internal bool TryGetLinkedImagePeer(InlineImageRun run, out MarkdownLinkedImagePeer peer) =>
        _automation.TryGetLinkedImagePeer(run, out peer);

    internal bool IsKeyboardFocusOnLink(LinkRun run) => _automation.IsKeyboardFocusOnLink(run);

    internal bool IsKeyboardFocusOnLinkedImage(InlineImageRun run) => _automation.IsKeyboardFocusOnLinkedImage(run);

    internal bool IsKeyboardFocusOnLinkedInlineVectorScene(InlineVectorSceneRun run) =>
        _automation.IsKeyboardFocusOnLinkedInlineVectorScene(run);

    internal bool IsKeyboardFocusOnVectorSemantic(Layout.Boxes.VectorSceneBox box, int semanticIndex) =>
        _automation.IsKeyboardFocusOnVectorSemantic(box, semanticIndex);

    internal bool IsKeyboardFocusOnHorizontalOverflow(IHorizontalOverflowBox overflow) =>
        _automation.IsKeyboardFocusOnHorizontalOverflow(overflow);

    internal bool IsHorizontalOverflowKeyboardFocusable(IHorizontalOverflowBox overflow) =>
        _automation.IsHorizontalOverflowKeyboardFocusable(overflow);

    internal bool HasKeyboardFocusOnVirtualChild => _automation.HasKeyboardFocusOnVirtualChild;

    internal bool FocusLinkFromAutomation(LinkRun run) => _automation.FocusLinkedAtomic(run);

    internal bool FocusLinkedImageFromAutomation(InlineImageRun run) => _automation.FocusLinkedAtomic(run);

    internal bool FocusInlineVectorSceneFromAutomation(InlineVectorSceneRun run) => _automation.FocusLinkedAtomic(run);

    internal bool FocusVectorSemanticFromAutomation(Layout.Boxes.VectorSceneBox box, int semanticIndex) =>
        _automation.FocusVectorSemantic(box, semanticIndex);

    internal bool FocusHorizontalOverflowFromAutomation(IHorizontalOverflowBox overflow) =>
        _automation.FocusHorizontalOverflow(overflow);

    internal bool FocusDocumentFromAutomation() => _automation.FocusDocument();

    /// <summary>Invoked by <see cref="MarkdownLinkPeer.Invoke"/> so UIA clients
    /// can activate a link the same way as pointer interaction. UIA callers
    /// can arrive on the RPC thread, so marshal back to the UI dispatcher
    /// before raising the public event.</summary>
    internal void RaiseLinkClickFromAutomation(LinkRun run) => _automation.ActivateLinkedAtomic(run);

    internal void RaiseLinkedImageClickFromAutomation(InlineImageRun run) => _automation.ActivateLinkedAtomic(run);

    internal void RaiseInlineVectorSceneClickFromAutomation(InlineVectorSceneRun run) =>
        _automation.ActivateLinkedAtomic(run);

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
        _published.Snapshot is null || _published.Theme is not { } theme
            ? null
            : theme.IsHighContrast
                ? MarkdownThemeVariant.HighContrast
                : theme.IsDark ? MarkdownThemeVariant.Dark : MarkdownThemeVariant.Light;

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
    internal bool HasPendingLazyLayoutWork => _recoveryWork.HasPendingLazyLayoutWork;

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
        _vectorCapacity.Reset();
        _lazyLayout.Cancel();
        _layoutCommit.ClearPending();
        LayoutSnapshot? snapshot = _published.Snapshot;
        _published.Snapshot = null;
        _automationDocumentGenerations.Invalidate();
        _automation.NotifySnapshotChanged();
        _painter.CancelFirstViewportMeasurement();
        _snapshotRetirements.Retire(snapshot);
    }

    /// <summary>Raises a host-approved vector-scene link/action from UI Automation.</summary>
    internal void RaiseVectorSceneLinkClickFromAutomation(Layout.Boxes.VectorSceneBox box, int semanticIndex) =>
        _automation.ActivateVectorSceneLink(box, semanticIndex);

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
    internal Theming.ThemeSnapshot? CurrentThemeSnapshot => _published.Theme;

    /// <summary>Effective document-language LCID exposed through UI Automation.</summary>
    internal int AutomationCultureLcid => _localization.Culture.LCID;

    /// <summary>UI Automation's scroll pattern and ScrollIntoView requests.</summary>
    internal AutomationScrolling AutomationScrolling => _automationScrolling;

    /// <summary>Strings and culture resolved from the language snapshot of the committed layout.</summary>
    internal RendererLocalization Localization => _localization;

    /// <summary>
    /// Number of currently-realised hosted embed elements (block + inline).
    /// Exposed for UI-automation tests that want to validate virtualisation
    /// without relying on heuristic descendant counts.
    /// </summary>
    public int RealizedEmbedCount => _embeds.RealizedEmbedCount;

    internal void SetDisclosureExpanded(LinkRun run, bool expanded) => _documentInputs.SetDisclosureExpanded(run, expanded);

    internal string ResolvedCodeBlockCopyButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopyButtonLabel)
            ? _localization.Resolve(MarkdownStringKeys.CopyCode, MarkdownLocalizedStrings.CodeBlockCopyAutomationName)
            : CodeBlockCopyButtonLabel!;

    private string ResolvedCodeBlockCopiedButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopiedButtonLabel)
            ? _localization.Resolve(MarkdownStringKeys.CodeCopied, MarkdownLocalizedStrings.CodeBlockCopied)
            : CodeBlockCopiedButtonLabel!;

    private void OnStringProviderChanged()
    {
        _handles.UpdateLocalization();
        RequestRebuild(RebuildReason.Restyle);
    }

    private void UpdateCodeBlockCopyButtonLabels() => _embeds.UpdateCodeBlockCopyButtonLabels();

    private void UpdateCodeBlockCopyButtonStyles() => _embeds.UpdateCodeBlockCopyButtonStyles(CodeBlockCopyButtonStyle);

    internal void CopyCodeBlockFromAutomation(Layout.Boxes.CodeBlockBox box) => _codeBlockClipboard.CopyFromAutomation(box);

    internal DocumentRange CurrentSelectionRange => _selection.Range;

    internal void SelectAutomationRange(DocumentRange range) => _automation.Select(range);

    internal void ClearAutomationSelection() => _selection.Clear();

    internal VisibleImageReadiness ImageReadiness => _imageReadiness;

    internal DeferredSceneReadiness DeferredSceneReadiness => _deferredScenes;

    internal bool TryGetVisibleDocumentRect(out Windows.Foundation.Rect rect)
    {
        bool hasViewport = _viewport.TryGet(out double top, out double height, out double width);
        rect = new Windows.Foundation.Rect(0, top, width, height);
        return hasViewport;
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
        _events = new RendererEvents(this, _lifetime, DispatcherQueue);
        _cancellationRetirement = new PipelineCancellationRetirement(_snapshotRetirements);
        _imageLifetime = new ImageLifetimeScope(_cancellationRetirement);
        _touchRestore = new TouchSelectionRestore(_published, _selection, _touchSelection);
        _localization = new RendererLocalization(this, _published);
        _surface = new MarkdownViewSurface(ownsScrollViewport);
        _windowDestroying = new WindowDestroyingWatch(this, _lifetime);
        _recoveryClock = new RecoveryClock(this, _lifetime);
        _viewport = new MarkdownViewport(this, _surface);
        _vectorCapacity = new VectorCapacityRetry(this, _lifetime, _published, _viewport);
        _cursor = new PointerCursorPresenter(cursor => ProtectedCursor = cursor);
        _abbreviationTooltip = new AbbreviationTooltipPresenter(this, _surface, _lifetime, _hover);
        _adorner = new SelectionAdornerPresenter(this, this, _surface, _lifetime, _published, _viewport, _selection, _hover);
        _deviceRecovery = new CanvasDeviceRecovery(this, _lifetime, _published, _surface, _adorner, DispatcherQueue);
        _canvasInvalidation = new CanvasInvalidation(this, _lifetime, _surface, _deviceRecovery);
        _selectionCommands = new SelectionCommands(this, _published, _selection);
        _selectionDismissal = new SelectionDismissalHook(this, this, _surface, _lifetime, _published, _selection, _selectionCommands);
        _autoScroll = new SelectionDragAutoScroll(_surface, _viewport);
        _dragShield = new SelectionDragShield(_surface, _viewport, this);
        _handles = new SelectionHandlesPresenter(this, this, this, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _adorner, _autoScroll);
        _contextMenu = new SelectionContextMenuPresenter(this, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _handles, _adorner, _selectionCommands);
        _horizontalOverflowDrag = new HorizontalOverflowPointerDrag(this, _surface, _published, _touchTapSuppression);
        _document = new DocumentPointerInteraction(this, _surface, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _horizontalOverflowDrag, _handles, _autoScroll, _dragShield, _adorner, _contextMenu, new PointerHoverTracker(this, _published, _hover, _cursor, _abbreviationTooltip), _cursor, _abbreviationTooltip);
        _extent = new SnapshotExtent(this, _surface, _dragShield, _document);
        _touchGestures = new TouchGestureInteraction(this, this, _surface, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _horizontalOverflowDrag, _document);
        _pointerInput = new PointerInputRouter(this, this, DispatcherQueue, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _handles, _document, _horizontalOverflowDrag, _touchGestures, _contextMenu, _adorner, _dragShield);
        _rebuild = new RebuildScheduler(this, _lifetime, _surface, _pointerInput, _cancellationRetirement);
        _canvasPointerEvents = new CanvasPointerEvents(_surface, _pointerInput);
        _embeds = new EmbedRealization(this, this, _surface, _lifetime, _published, _viewport, DispatcherQueue, OnHostedEmbedKeyDown);
        _imagePlans = new ImagePlanSet(_lifetime, _published);
        _imageSubscriptions = new ImageSubscriptions(this, _lifetime, _published, _imagePlans);
        _imageResidency = new ImageResidency(this, _lifetime, _published, _imagePlans, _embeds);
        _highlighting = new CodeBlockHighlighting(this, _lifetime, _published, _viewport, _embeds);
        _focus = new KeyboardFocusModel(_lifetime, _published);
        _focusGeometry = new FocusGeometryReader(_lifetime, _published);
        _focusRing = new FocusRingPresenter(this, _focus, _focusGeometry, _surface, _lifetime, _published, DispatcherQueue);
        _interactionReset = new InteractionReset(_surface, _selection, _touchSelection, _touchRestore, _touchTapSuppression, _pointerInput, _document, _handles, _autoScroll, _dragShield, _contextMenu, _adorner, _hover, _abbreviationTooltip, _cursor, _focus, _focusRing);
        _rasterPlacement = new RasterPlacementPublisher(this, _lifetime, _published, _layoutCommit, _surface, _pointerInput, _imageSubscriptions, _imagePlans, _imageResidency, _embeds, _focus, _focusRing, _hostedFocusPlan, _snapshotRetirements);
        _focusRefresh = new FocusableItemsRefreshRequest(_published);
        _focusableItems = new FocusableItemsPublication(this, _lifetime, _published, _focusRefresh, _focus, _recoveryClock);
        _layoutSetup = new RebuildLayoutSetup(this, _snapshotRetirements, _imagePrefetch, _completedImageCarry, _imagePlans, _embeds, _focusableItems, _imagePreparationOwnerToken);
        _hostedFocus = new HostedElementFocus(this, _lifetime, _published, _focus, _hostedFocusPlan, _embeds, _focusRing);
        _overflowScroll = new HorizontalOverflowScrollVisuals(this, _surface, _lifetime, _published, _selection, _focus, _embeds, DispatcherQueue);
        _reveal = new FocusedItemReveal(this, _surface, _lifetime, _published, _viewport, _focus, _overflowScroll, DispatcherQueue);
        _automationScrolling = new AutomationScrolling(_surface, _reveal);
        _focusCommit = new FocusedItemCommit(this, _lifetime, _published, _focus, _focusRing, _reveal, _hostedFocus, _embeds, DispatcherQueue);
        var queuedFocusKeys = new SpatialFocusKeyQueue();
        _navigator = new KeyboardFocusNavigator(this, _lifetime, _published, _focus, _focusGeometry, _focusRing, _overflowScroll, _embeds, _focusCommit, queuedFocusKeys, DispatcherQueue);
        _focusGeometryWork = new PendingFocusGeometryWork(this, _lifetime, _published, _focus, _focusGeometry, _focusRing, _reveal, queuedFocusKeys, _navigator);
        _focusEntry = new RendererFocusEntry(this, _lifetime, _focus, _focusRing, _focusCommit, _navigator, DispatcherQueue);
        _layoutPublisher = new LayoutCommitPublisher(this, _lifetime, _published, _layoutCommit, _surface, _recoveryClock, _pointerInput, _embeds, _imageSubscriptions, _imagePlans, _imageResidency, _focus, _focusRing, _focusCommit, _focusRefresh, _hostedFocusPlan);
        _lazyLayout = new LazyLayoutRealization(this, _lifetime, _published, _layoutCommit, _layoutPublisher, _surface, _viewport, _recoveryClock, _vectorCapacity, _pointerInput, _focus, _focusCommit);
        _blockScroll = new BlockScrollNavigation(_lifetime, _published, _surface, _viewport, _lazyLayout, _reveal);
        _links = new LinkActivation(this, _blockScroll);
        _automation = new RendererAutomation(this, _lifetime, _selection, _focus, _focusRing, _focusCommit, _focusEntry, _links);
        _deferredScenes = new DeferredSceneReadiness(this, _lifetime, _published, _viewport, _rebuild);
        _imageReadiness = new VisibleImageReadiness(_published, _imagePlans, _viewport);
        _painter = new CanvasTilePainter(this, _lifetime, _published, _surface, _lazyLayout, _deviceRecovery, _rebuild, _deferredScenes);
        _canvasEvents = new CanvasEvents(_lifetime, _surface, _canvasPointerEvents, _painter, _rebuild);
        _imageRelayout = new ImageRelayout(this, _lifetime, _published, _layoutCommit, _layoutPublisher, _surface, _pointerInput, _imageSubscriptions, _rasterPlacement, _recoveryClock);
        _imageLoadCompletion = new ImageLoadCompletion(this, _lifetime, _published, _layoutCommit, _imageSubscriptions, _imageRelayout);
        _imageSubscriptions.LoadCompleted += _imageLoadCompletion.OnLoadCompleted;
        _geometryRecovery = new GeometryRecovery(_lifetime, _published, _surface, _pointerInput, _vectorCapacity, _horizontalOverflowDrag, _focusGeometryWork, _overflowScroll, _focusRing, _adorner, _layoutCommit, _imageSubscriptions, _lazyLayout, _imageRelayout, _interactionReset, _dragShield, _contextMenu, _abbreviationTooltip, _cursor, _handles, _automation, _deviceRecovery, _rebuild);
        _visibleWork = new VisibleWorkScheduler(this, _published, _highlighting, _geometryRecovery);
        _documentInputs = new DocumentInputs(this, _events, _interactionReset, _embeds, _highlighting, _rebuild, _snapshotRetirements);
        _keyboard = new KeyboardInput(this, _published, _selection, _selectionCommands, _contextMenu, _focus, _focusRing, _focusCommit, _hostedFocus, _navigator, _geometryRecovery);
        _recoveryWork = new RecoveryClockWork(_lifetime, _published, _recoveryClock, _layoutCommit, _layoutPublisher, _lazyLayout, _imageSubscriptions, _imageRelayout, _overflowScroll, _focusGeometryWork, _focusRefresh, _focusableItems, _pointerInput, _geometryRecovery, _rebuild);
        _textAdorner = new InteractiveTextAdorner(_surface, _published, _selection, _touchSelection, _hover, _focus, _adorner, _geometryRecovery);
        _scrolling = new ScrollViewTracking(_surface, _lifetime, _viewport, _lazyLayout, _adorner, _textAdorner, _embeds, _visibleWork);
        _performanceSession = new PerformanceSessionSubscription(this, _lifetime, _snapshotRetirements, _rebuild, _selection, _adorner, _textAdorner);
        _svgRenderer = new SvgRendererSubscription(this, _lifetime, _rebuild, _canvasInvalidation);
        _theme = new ThemeSubscription(this, _lifetime, _rebuild);
        _environment = new EnvironmentSubscription(this, _lifetime, _rebuild);
        _replacementPublisher = new ReplacementPublisher(this, _published, _surface, _rebuild, _touchRestore, _automationDocumentGenerations, _selection, _lazyLayout, _layoutCommit, _scrolling, _painter, _localization, _focusRefresh, _vectorCapacity, _geometryRecovery, _imageSubscriptions, _imageRelayout, _blockScroll, _imagePlans, _focus);
        _width = new LayoutWidthWatch(this, _rebuild);
        _inputCapture = new RebuildInputCapture(this, _published, _imagePreparationOwnerToken, _documentInputs, _localization, _environment);
        _replacementPresentation = new ReplacementPresentation(_surface, _snapshotRetirements, _rebuild, _layoutCommit, _layoutPublisher, _documentInputs, _extent, _scrolling, _hover, _selection, _touchRestore, _contextMenu, _dragShield, _adorner, _textAdorner, _handles, _embeds, _imageSubscriptions, _imagePlans, _imageResidency, _focus, _focusRing, _focusCommit, _highlighting, _visibleWork, _canvasInvalidation, _automation, _deferredScenes);
        _pipeline = new RebuildPipeline(this, this, _lifetime, _surface, _viewport, _rebuild, _inputCapture, _layoutSetup, _width, _imagePrefetch, _automation, _replacementPublisher, _replacementPresentation);
        _codeBlockClipboard = new CodeBlockClipboard(this, _embeds, _events);
        _visibleImages = new VisibleImageLoading(this, _lazyLayout, _imageResidency, _imagePlans, _embeds);
        _pointerResume = new PointerInteractionResume(_lifetime, _rebuild, _imageRelayout);
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += (_, _) => OnLoadedInternal();
        Unloaded += (_, _) => OnUnloaded();
        ActualThemeChanged += (_, _) => _theme.OnThemeChanged();
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

    // Embeds subscribe hosted elements before the keyboard owner exists.
    private void OnHostedEmbedKeyDown(object sender, KeyRoutedEventArgs e) => _keyboard.OnHostedEmbedKeyDown(sender, e);

    private void BuildVisualTree()
    {
        _canvasEvents.EnsureCanvas();
        GettingFocus += OnGettingFocus;
        GotFocus += OnGotFocus;
        LosingFocus += OnLosingFocus;
        KeyDown += _keyboard.OnKeyDown;

        if (!_surface.OwnsScrollViewport)
            EffectiveViewportChanged += _scrolling.OnEffectiveViewportChanged;
        Content = _surface.ContentElement;
    }

    internal bool TryGetViewport(out double top, out double height, out double width)
        => _viewport.TryGet(out top, out height, out width);

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        if (ReferenceEquals(e.NewFocusedElement, this))
            _focusEntry.NoteGettingFocus(e.InputDevice, e.Direction);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this))
            _focusEntry.OnGotFocus(FocusState);
    }

    private void OnLosingFocus(UIElement sender, LosingFocusEventArgs e)
    {
        _focusEntry.OnLosingFocus();

        if (e.NewFocusedElement is DependencyObject next && IsElementWithinRenderer(next))
            return;

        _focus.ClearFocus();
        _focusRing.Update();
        if (!_contextMenu.IsOpen)
        {
            _document.ForgetDragAnchor();
        }
    }

    private void OnHostedElementFactoryChanged()
    {
        _embeds.ClearHostedElementFallbacks();
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

        _highlighting.Retire(restart: !_lifetime.IsUnloaded && !_lifetime.IsDisposed);
        _highlighting.ClearCache();
        RequestRebuild(RebuildReason.Full);
    }

    private bool IsElementWithinRenderer(DependencyObject element)
        => VisualTreeAncestry.IsWithin(element, this);

    bool ISelectionDismissalHost.IsElementWithinCodeBlockAction(DependencyObject element)
    {
        foreach (var plan in _embeds.CodeBlockActionPlans)
        {
            if (plan.Realized is DependencyObject realized && VisualTreeAncestry.IsWithin(element, realized))
                return true;
        }

        return false;
    }

    bool ISelectionDismissalHost.IsElementWithinSelectionHandle(DependencyObject element)
        => _handles.ContainsElement(element);

    bool ISelectionDismissalHost.IsPointOverEmbed(Point canvasPoint) => _embeds.HitTargets.IsPointOverEmbed(canvasPoint);

    void ISelectionDismissalHost.ClearSelectionForExternalInteraction() => _interactionReset.ClearSelectionForExternalInteraction();

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
        => _events.RaiseCopyCompleted(kind, succeeded);

    Theming.ThemeSnapshot? ISelectionHandlesHost.ThemeSnapshot => _published.Theme;

    string ISelectionHandlesHost.ResolveLocalizedString(string key, string fallback)
        => _localization.Resolve(key, fallback);

    bool ISelectionHandlesHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => _geometryRecovery.RequestIfRequired(snapshot);

    void ISelectionHandlesHost.FocusRendererForPointerInteraction() => _focusEntry.FocusWithoutPromotion();

    bool ISelectionHandlesHost.TryHitTestEmbed(Point point, out DocumentPosition position)
        => _embeds.HitTargets.TryHitTest(
            point, FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft, out position);

    bool ISelectionHandlesHost.HasDeferredInputFor(uint pointerId)
        => _pointerInput.HasDeferredInputFor(pointerId);

    void ISelectionHandlesHost.ResumePendingRebuildAfterPointerInteraction()
        => _pointerResume.ResumePendingWork();

    void ISelectionHandlesHost.CancelPendingTouchSelectionRestore()
        => _touchRestore.Cancel();

    bool ISelectionContextMenuHost.IsSelectionEnabled => IsSelectionEnabled;

    IMarkdownCommandProvider? ISelectionContextMenuHost.CommandProvider => CommandProvider;

    string ISelectionContextMenuHost.ResolveLocalizedString(string key, string fallback)
        => _localization.Resolve(key, fallback);

    void ISelectionContextMenuHost.RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
        => _events.RaiseCopyCompleted(kind, succeeded);

    Rect? ISelectionContextMenuHost.GetFocusedItemRect()
        => _focus.Items is { } items &&
           _focus.FocusedIndex >= 0 &&
           _focus.FocusedIndex < items.Count
            ? _focusGeometry.GetCurrentItemRect(items[_focus.FocusedIndex])
            : null;

    bool IPointerInteractionHost.IsSelectionEnabled => IsSelectionEnabled;

    bool IPointerInteractionHost.IsCurrentScrollTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision)
        => _blockScroll.IsCurrentTarget(snapshot, snapshotGeneration, layoutRevision);

    void IPointerInteractionHost.CancelPendingTouchSelectionRestore()
        => _touchRestore.Cancel();

    void IPointerInteractionHost.RememberFocusResumePoint(Point documentPoint)
        => _focusGeometryWork.RememberResumePoint(documentPoint);

    void IPointerInteractionHost.FocusRendererForPointerInteraction() => _focusEntry.FocusWithoutPromotion();

    void IPointerInteractionHost.ClearKeyboardFocusItem()
    {
        if (_focus.FocusedIndex >= 0)
        {
            _focus.ClearFocus();
            _focusRing.Update();
        }
    }

    bool IPointerInteractionHost.IsPointOverCodeBlockAction(Point point) => _embeds.HitTargets.IsPointOverCodeBlockAction(point);

    bool IPointerInteractionHost.IsPointOverEmbed(Point point) => _embeds.HitTargets.IsPointOverEmbed(point);

    bool IPointerInteractionHost.TryHitTestEmbed(Point point, out DocumentPosition position)
        => _embeds.HitTargets.TryHitTest(
            point, FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft, out position);

    bool IPointerInteractionHost.TryGetVectorPointerState(Point point, out bool linked, out bool selectable)
        => TryGetVectorPointerState(point, out linked, out selectable);

    void IPointerInteractionHost.InvalidateInteractiveTextAdorner() => _textAdorner.Invalidate();

    void IPointerInteractionHost.UpdateSelectionOverlay() => _textAdorner.UpdateSelectionOverlay();

    void IPointerInteractionHost.ClearSelectionForExternalInteraction() => _interactionReset.ClearSelectionForExternalInteraction();

    void IPointerInteractionHost.ResumePendingRebuildAfterPointerInteraction()
        => _pointerResume.ResumePendingWork();

    void IHorizontalOverflowPointerHost.RefreshHorizontalOverflowVisualState(IHorizontalOverflowBox target, double oldOffset)
        => _overflowScroll.Refresh(target, oldOffset);

    void IPointerInputSink.DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
        => _pointerInput.DispatchPointerInput(kind, sender, args);

    bool IPointerInputSink.DispatchRecognizedGesture(PointerInputKind kind, Point point, Microsoft.UI.Input.PointerDeviceType device)
        => _pointerInput.DispatchRecognizedGesture(kind, point, device);

    bool IPointerInputRouterHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => _geometryRecovery.RequestIfRequired(snapshot);

    void IPointerInputRouterHost.ResumePendingRebuildAfterPointerInteraction()
        => _pointerResume.ResumePendingWork();

    void IPointerInputRouterHost.ClearSelectionForExternalInteraction(bool resetClickTracking, bool resetDeferredPointerInput)
        => _interactionReset.ClearSelectionForExternalInteraction(resetClickTracking, resetDeferredPointerInput);

    bool IPointerInputRouterHost.ActivateLinkIntent(
        LinkActivationIntent intent,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
        => _links.ActivateIntent(intent, snapshot, snapshotGeneration, layoutRevision);

    CancellationToken IEmbedRealizationHost.PipelineToken => _rebuild.Token;

    long IEmbedRealizationHost.PipelineGeneration => _rebuild.Generation;

    IMarkdownHostedElementFactory? IEmbedRealizationHost.HostedElementFactory => HostedElementFactory;

    ViewportBandIndex? IEmbedRealizationHost.ImagePlanIndex => _imagePlans.Index;

    void IEmbedRealizationHost.AddImagePlan(ImageBox image) => AddImagePlan(image);

    bool IEmbedRealizationHost.RealizeVisibleImages(
        in VisibleRealizationPass pass,
        double top,
        double viewportHeight,
        double bottom)
        => _visibleImages.Realize(pass, top, viewportHeight, bottom);

    bool IEmbedRealizationHost.IsCurrentPlanPublication(
        LayoutSnapshot snapshot,
        long generation,
        long hostedFocusPlanEpoch,
        long layoutRevision)
        => _layoutPublisher.IsCurrentPlanPublication(snapshot, generation, hostedFocusPlanEpoch, layoutRevision);

    bool IEmbedRealizationHost.IsCurrentRasterCodeActionPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
        => _rasterPlacement.IsCurrentPublication(candidate, generation, layoutRevision, hostedFocusPlanEpoch);

    void IEmbedRealizationHost.InvalidateHostedFocusPlanPublication() => _hostedFocusPlan.Invalidate();

    void IEmbedRealizationHost.CopyCodeBlockToClipboard(CodeBlockActionPlan plan) => _codeBlockClipboard.Copy(plan);

    void IEmbedRealizationHost.RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan)
        => _hostedFocus.RestoreCodeBlockActionIfNeeded(plan);

    void IEmbedRealizationHost.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
        => _hostedFocus.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(element);

    void IEmbedRealizationHost.RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan)
        => _hostedFocus.RestoreHostedElementIfNeeded(plan);

    void IEmbedRealizationHost.RequestFallbackRebuild() => RequestRebuild(RebuildReason.Restyle);

    void IEmbedRealizationHost.InvalidateAutomationLayout() => _automation.InvalidateLayout();

    void IEmbedRealizationHost.RaiseEmbedsRealizationChanged() => _events.RaiseEmbedsRealizationChanged();

    Style? ICodeBlockCopyButtonHost.CodeBlockCopyButtonStyle => CodeBlockCopyButtonStyle;

    string ICodeBlockCopyButtonHost.ResolveCodeBlockCopyButtonLabel(bool copied) =>
        copied ? ResolvedCodeBlockCopiedButtonLabel : ResolvedCodeBlockCopyButtonLabel;

    XamlRoot? IFocusRingHost.XamlRoot => XamlRoot;

    Windows.UI.Color? IFocusRingHost.FocusVisualColor => _published.Theme?.FocusVisualColor;

    void IFocusRingHost.InvalidateInteractiveTextAdorner() => _textAdorner.Invalidate();

    void IFocusRingHost.RequestFocusGeometryRetry() => _focusGeometryWork.RequestRetry();

    FocusState IHostedElementFocusHost.FocusState => FocusState;

    XamlRoot? IHostedElementFocusHost.XamlRoot => XamlRoot;

    long IHostedElementFocusHost.AppliedLazyLayoutRevision => _layoutCommit.AppliedRevision;

    bool IHostedElementFocusHost.FocusRenderer() => Focus(FocusState.Keyboard);

    void IHorizontalOverflowScrollHost.UpdateSelectionOverlay() => _textAdorner.UpdateSelectionOverlay();

    void IHorizontalOverflowScrollHost.UpdateFocusRing() => _focusRing.Update();

    void IHorizontalOverflowScrollHost.StartRecoveryClock() => _recoveryClock.Start();

    bool IHorizontalOverflowScrollHost.HasRecoveryClock => _recoveryClock.IsRunning;

    void IHorizontalOverflowScrollHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    bool IHorizontalOverflowScrollHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => _geometryRecovery.RequestIfRequired(snapshot);

    MarkdownAutomationPeer? IHorizontalOverflowScrollHost.FindAutomationPeer()
        => _automation.FindPeer();

    void IFocusedItemRevealHost.RequestFocusGeometryRetry() => _focusGeometryWork.RequestRetry();

    bool IFocusedItemCommitHost.FocusRenderer() => Focus(FocusState.Keyboard);

    bool IFocusedItemCommitHost.FocusRendererProgrammatically() => Focus(FocusState.Programmatic);

    bool IRendererFocusEntryHost.FocusRendererProgrammatically() => Focus(FocusState.Programmatic);

    void IFocusedItemCommitHost.RealizeLazyFocusTarget(LayoutSnapshot snapshot, LazyLayoutBand estimatedBand)
    {
        var realizationBand = LazyLayoutBand.FromViewport(
            estimatedBand.Top,
            Math.Max(1, estimatedBand.Bottom - estimatedBand.Top),
            MarkdownLazyLayoutPolicy.OverscanPx);
        LazyLayoutBand visibleVectorBand = _viewport.GetVisibleBand();
        _lazyLayout.QueueForBand(
            snapshot,
            LazyLayoutBand.Union(realizationBand, visibleVectorBand),
            visibleVectorBand,
            preserveScrollAnchor: _surface.Scroll is not null);
    }

    LinkTarget? IFocusedItemCommitHost.FindLinkTargetAt(DocumentPosition position) => FindLinkTargetAt(position);

    bool IFocusedItemCommitHost.ActivateLink(
        LinkTarget link,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers)
        => _links.Activate(link, inputKind, modifiers);

    MarkdownAutomationPeer? IFocusedItemCommitHost.GetOrCreateAutomationPeer()
        => _automation.GetOrCreatePeer();

    bool IFocusGeometryRecoveryHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => _geometryRecovery.RequestIfRequired(snapshot);

    void IFocusGeometryRecoveryHost.StartRecoveryClock() => _recoveryClock.Start();

    bool IFocusGeometryRecoveryHost.HasRecoveryClock => _recoveryClock.IsRunning;

    void IFocusGeometryRecoveryHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    void IImageSubscriptionsHost.StartRecoveryClock() => _recoveryClock.Start();

    Microsoft.UI.Dispatching.DispatcherQueue? IImageResidencyHost.DispatcherQueue => DispatcherQueue;

    IMarkdownPerformanceSession? IImageResidencyHost.PerformanceSession => PerformanceSession;

    long IImageResidencyHost.AppliedLazyLayoutRevision => _layoutCommit.AppliedRevision;

    void IImageResidencyHost.InvalidateCanvas() => _canvasInvalidation.Invalidate();

    void ICanvasDeviceRecoveryHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    void ICanvasDeviceRecoveryHost.InvalidateCanvas() => _canvasInvalidation.Invalidate();

    Microsoft.UI.Dispatching.DispatcherQueue? ICodeBlockHighlightingHost.DispatcherQueue => DispatcherQueue;

    ICodeHighlighter? ICodeBlockHighlightingHost.CodeHighlighter => CodeHighlighter;

    bool ICodeBlockHighlightingHost.IsCodeBlockSyntaxHighlightingEnabled => IsCodeBlockSyntaxHighlightingEnabled;

    IMarkdownPerformanceSession? ICodeBlockHighlightingHost.PerformanceSession => PerformanceSession;

    Theming.ThemeSnapshot? ICodeBlockHighlightingHost.ThemeSnapshot => _published.Theme;

    double ICodeBlockHighlightingHost.LazyScrollVelocityPixelsPerSecond => _lazyLayout.ScrollVelocity;

    void ICodeBlockHighlightingHost.ScheduleVisibleCodeBlockHighlighting() => _visibleWork.ScheduleCodeBlockHighlighting();

    void ICodeBlockHighlightingHost.InvalidateCanvas() => _canvasInvalidation.Invalidate();

    Task ICodeBlockHighlightingHost.RetireCancellationTokenSource(CancellationTokenSource? cts, Task? lifetime) =>
        _cancellationRetirement.Retire(cts, lifetime);

    Microsoft.UI.Dispatching.DispatcherQueue? IRecoveryClockHost.DispatcherQueue => DispatcherQueue;

    void IRecoveryClockHost.ProcessRecoveryTick() => _recoveryWork.ProcessTick();

    bool IRasterPlacementHost.HasCompetingLayoutWork =>
        _deviceRecovery.IsQueued || _rebuild.HasPendingOrBuilding || HasPendingLazyLayoutWork;

    float IRasterPlacementHost.LayoutWidth => (float)Math.Max(50, ActualWidth);

    object IRasterPlacementHost.ImagePreparationOwner => _imagePreparationOwnerToken;

    CancellationToken IRasterPlacementHost.ImageLifetime => _imageLifetime.Token;

    bool IRasterPlacementHost.IsScrollViewIntermediate => _scrolling.IsIntermediate;

    bool IRasterPlacementHost.MatchesLayoutInputs(MarkdownLayoutContext context) =>
        _inputCapture.MatchesLayoutInputs(context);

    void IRasterPlacementHost.RetireSnapshot(LayoutSnapshot snapshot) => _snapshotRetirements.Retire(snapshot);

    void IRasterPlacementHost.ForgetHoverTarget()
    {
        _hover.Clear();
        _abbreviationTooltip.ForgetTarget();
    }

    void IRasterPlacementHost.ScheduleVisibleCodeBlockHighlighting() => _visibleWork.ScheduleCodeBlockHighlighting();

    void IRasterPlacementHost.NotifyAutomationSnapshotChanged() => _automation.NotifySnapshotChanged();

    void IRasterPlacementHost.HideAbbreviationTooltip() => _abbreviationTooltip.Hide();

    void IRasterPlacementHost.ResetCursor() => _cursor.SetShape(null);

    void IRasterPlacementHost.ApplySnapshotSize(Size size) => _extent.Apply(size);

    void IRasterPlacementHost.RefreshInteractiveTextAdornerAfterLayoutChange() =>
        _textAdorner.RefreshAfterLayoutChange();

    void IRasterPlacementHost.InvalidateAutomationLayout() => _automation.InvalidateLayout();

    void IRasterPlacementHost.InvalidateCanvas() => _canvasInvalidation.Invalidate();

    void IRasterPlacementHost.RaiseImageUnavailable(
        string url, MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgReason) =>
        _events.RaiseImageUnavailable(url, reason, svgReason);

    Microsoft.UI.Dispatching.DispatcherQueue? IImageRelayoutHost.DispatcherQueue => DispatcherQueue;

    IMarkdownPerformanceSession? IImageRelayoutHost.PerformanceSession => PerformanceSession;

    float IImageRelayoutHost.LayoutWidth => (float)Math.Max(50, ActualWidth);

    bool IImageRelayoutHost.DefersToRebuild() => _rebuild.DefersToRebuild();

    bool IImageRelayoutHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        _geometryRecovery.RequestIfRequired(snapshot);

    void IImageRelayoutHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    void IImageRelayoutHost.StopRecoveryClock(bool force) => _recoveryWork.Stop(force);

    Task IImageRelayoutHost.RetireCancellationTokenSource(CancellationTokenSource? cts, Task? lifetime) =>
        _cancellationRetirement.Retire(cts, lifetime);

    Microsoft.UI.Dispatching.DispatcherQueue? IImageLoadCompletionHost.DispatcherQueue => DispatcherQueue;

    IMarkdownPerformanceSession? IImageLoadCompletionHost.PerformanceSession => PerformanceSession;

    float IImageLoadCompletionHost.LayoutWidth => (float)Math.Max(50, ActualWidth);

    bool IImageLoadCompletionHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        _geometryRecovery.RequestIfRequired(snapshot);

    void IImageLoadCompletionHost.NotifyImageStatusChanged(Layout.Boxes.ImageBox image) =>
        _automation.FindPeer()?
            .NotifyImageStatusChanged(image);

    void IImageLoadCompletionHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    void IImageLoadCompletionHost.RecordRejectedCrossThreadFullRebuild() =>
        _rebuild.RecordRejectedCrossThread(RebuildReason.Full);

    void IImageLoadCompletionHost.InvalidatePaint()
    {
        _canvasInvalidation.Invalidate();
        _adorner.Invalidate();
    }

    Microsoft.UI.Dispatching.DispatcherQueue? IVectorCapacityRetryHost.DispatcherQueue => DispatcherQueue;

    IMarkdownPerformanceSession? IVectorCapacityRetryHost.PerformanceSession => PerformanceSession;

    bool IVectorCapacityRetryHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        _geometryRecovery.RequestIfRequired(snapshot);

    void IVectorCapacityRetryHost.QueueLazyLayoutForViewport(bool forceCapacityRetry) =>
        _lazyLayout.QueueForViewport(forceCapacityRetry);

    Microsoft.UI.Dispatching.DispatcherQueue? ILazyLayoutHost.DispatcherQueue => DispatcherQueue;

    IMarkdownPerformanceSession? ILazyLayoutHost.PerformanceSession => PerformanceSession;

    object ILazyLayoutHost.DeferredSceneOwner => this;

    long ILazyLayoutHost.PipelineGeneration => _rebuild.Generation;

    CancellationToken ILazyLayoutHost.PipelineToken => _rebuild.Token;

    bool ILazyLayoutHost.IsScrollViewIntermediate => _scrolling.IsIntermediate;

    bool ILazyLayoutHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        _geometryRecovery.RequestIfRequired(snapshot);

    void ILazyLayoutHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    void ILazyLayoutHost.HandleDeviceLost(Exception failure) => _deviceRecovery.HandleDeviceLost(failure);

    void ILazyLayoutHost.TryResumePendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration) =>
        _blockScroll.TryResumePending(snapshot, snapshotGeneration);

    void ILazyLayoutHost.NotifyVisibleDeferredSceneItemStatusMayHaveChanged() =>
        _deferredScenes.NotifyItemStatusMayHaveChanged();

    void ILazyLayoutHost.ScheduleVisibleDeferredScenes() => _visibleWork.ScheduleDeferredScenes();

    void ILazyLayoutHost.InvalidateAutomationLayout() => _automation.InvalidateLayout();

    void ILazyLayoutHost.InvalidateCanvas() => _canvasInvalidation.Invalidate();

    void ILazyLayoutHost.StopRecoveryClockIfIdle() => _recoveryWork.StopIfIdle();

    Task ILazyLayoutHost.RetireCancellationTokenSource(CancellationTokenSource? cts, Task? lifetime) =>
        _cancellationRetirement.Retire(cts, lifetime);

    bool ILayoutCommitHost.IsScrollViewIntermediate => _scrolling.IsIntermediate;

    bool ILayoutCommitHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        _geometryRecovery.RequestIfRequired(snapshot);

    void ILayoutCommitHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    void ILayoutCommitHost.ApplySnapshotSize(Size size) => _extent.Apply(size);

    void ILayoutCommitHost.ScheduleVisibleCodeBlockHighlighting() => _visibleWork.ScheduleCodeBlockHighlighting();

    void ILayoutCommitHost.RefreshInteractiveTextAdornerAfterLayoutChange() =>
        _textAdorner.RefreshAfterLayoutChange();

    void ILayoutCommitHost.InvalidateAutomationLayout() => _automation.InvalidateLayout();

    void ILayoutCommitHost.InvalidateCanvas() => _canvasInvalidation.Invalidate();

    void ILayoutCommitHost.TryResumePendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration) =>
        _blockScroll.TryResumePending(snapshot, snapshotGeneration);

    bool IFocusableItemsPublicationHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        _geometryRecovery.RequestIfRequired(snapshot);

    void IFocusableItemsPublicationHost.RequestFullRebuild() => RequestRebuild(RebuildReason.Full);

    void IFocusableItemsPublicationHost.InvalidateAutomationLayout() => _automation.InvalidateLayout();

    Microsoft.UI.Dispatching.DispatcherQueue? IRebuildSchedulerHost.DispatcherQueue => DispatcherQueue;

    bool IRebuildSchedulerHost.IsLoaded => IsLoaded;

    string IRebuildSchedulerHost.EffectiveSource => _documentInputs.EffectiveSource;

    void IRebuildSchedulerHost.AdvanceDeferredSceneGeneration() => _performanceSession.AdvanceDeferredSceneGeneration();

    void IRebuildSchedulerHost.CancelSnapshotWork()
    {
        _lazyLayout.Cancel();
        _imageRelayout.CancelActive();
    }

    void IRebuildSchedulerHost.ResetInteractionForRebuild()
    {
        _touchRestore.Capture();
        _interactionReset.ResetTransientState(clearSelection: false);
    }

    void IRebuildSchedulerHost.DiscardTouchSelectionRestore() => _touchRestore.Cancel();

    Task IRebuildSchedulerHost.RunRebuildAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes) =>
        _pipeline.RunAsync(ct, reason, generation, pipelineStartTimestamp, sourceUtf16Bytes);

    Microsoft.UI.Dispatching.DispatcherQueue? IRebuildLayoutSetupHost.DispatcherQueue => DispatcherQueue;

    FrameworkElement IRebuildLayoutSetupHost.ThemeHost => this;

    object IRebuildLayoutSetupHost.DocumentOwner => this;

    CancellationTokenSource? IRebuildLayoutSetupHost.ImageLifetimeCts => _imageLifetime.Source;

    bool IRebuildLayoutSetupHost.EnableStandaloneLayoutDiagnostics => EnableStandaloneLayoutDiagnostics;

    void IRebuildLayoutSetupHost.RaiseImageUnavailable(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason) =>
        _events.RaiseImageUnavailable(source, reason, svgFailureReason);

    Microsoft.UI.Dispatching.DispatcherQueue? ICanvasTilePainterHost.DispatcherQueue => DispatcherQueue;

    bool ICanvasTilePainterHost.IsLoaded => IsLoaded;

    bool ICanvasTilePainterHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
        _geometryRecovery.RequestIfRequired(snapshot);

    void ICanvasTilePainterHost.ScheduleVisibleDeferredScenes() => _visibleWork.ScheduleDeferredScenes();

    MarkdownRendererControl IDeferredSceneReadinessHost.DocumentOwner => this;

    IMarkdownPerformanceSessionInternal? IDeferredSceneReadinessHost.PerformanceSession =>
        PerformanceSession as IMarkdownPerformanceSessionInternal;

    MarkdownAutomationPeer? IDeferredSceneReadinessHost.FindAutomationPeer() => _automation.FindPeer();

    IMarkdownCommandProvider? ILinkActivationHost.CommandProvider => CommandProvider;

    void ILinkActivationHost.SetDisclosureExpanded(LinkRun run, bool expanded) =>
        _documentInputs.SetDisclosureExpanded(run, expanded);

    void ILinkActivationHost.SetDisclosureExpanded(string disclosureId, bool expanded) =>
        _documentInputs.SetDisclosureExpanded(disclosureId, expanded);

    void ILinkActivationHost.RaiseLinkClick(MarkdownLinkClickEventArgs args) => _events.RaiseLinkClick(args);

    string? IRendererLocalizationHost.EnvironmentLanguage => _environment.Snapshot.Language;

    double IDeferredGestureLayoutHost.RasterizationScale => XamlRoot?.RasterizationScale ?? 1;

    bool IDeferredGestureLayoutHost.IsPointOverEmbed(Point point) => _embeds.HitTargets.IsPointOverEmbed(point);

    bool IDeferredGestureLayoutHost.IsPointOverCodeBlockAction(Point point) => _embeds.HitTargets.IsPointOverCodeBlockAction(point);

    bool IDeferredGestureLayoutHost.IsLayoutPublicationPending(LayoutSnapshot snapshot) =>
        _layoutCommit.IsPublishing ||
        _layoutCommit.Pending is not null || _layoutCommit.AppliedRevision < snapshot.LayoutRevision;

    bool IDeferredGestureLayoutHost.IsLazyLayoutBandCovered(LayoutSnapshot snapshot, LazyLayoutBand band) =>
        _lazyLayout.IsBandCovered(snapshot, band);

    bool IDeferredGestureLayoutHost.HasSupersedingLazyLayoutRequest(LayoutSnapshot snapshot) =>
        _lazyLayout.HasSupersedingRequest(snapshot);

    void IDeferredGestureLayoutHost.QueueLazyLayoutForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor)
        => _lazyLayout.QueueForBand(snapshot, band, visibleVectorBand, preserveScrollAnchor);

    internal void ClearSelectionFromCoordinator()
    {
        _interactionReset.ClearSelectionForExternalInteraction();
    }

    private void OnSelectionChanged()
    {
        _touchRestore.OnSelectionChanged();

        if (_selection.IsActive)
            MarkdownSelectionCoordinator.ClearSelectionsExcept(this);

        _textAdorner.UpdateSelectionOverlay();
        _automation.QueueSelectionChanged();
    }

    private void OnSelectionEnabledChanged(bool isEnabled)
    {
        if (!isEnabled)
        {
            _touchTapSuppression.Reset();
            _interactionReset.ClearSelectionForExternalInteraction();
        }

        // TextPattern remains available for document reading, but its
        // SupportedTextSelection value changes between Single and None.
        // Recreate the peer-facing semantic snapshot and refresh cursor state.
        RequestRebuild(RebuildReason.Full);
        _automation.InvalidateLayout();
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
        _canvasEvents.EnsureCanvas();
        _windowDestroying.Attach(XamlRoot);
        _canvasEvents.AttachRenderHandlers();
        _imageLifetime.Renew();
        MarkdownSelectionCoordinator.Register(this);
        _selectionDismissal.Register();
        _environment.Acquire();
        _width.Attach();
        _theme.Attach();
        _svgRenderer.Attach();
        _performanceSession.Attach();
        RequestRebuild(RebuildReason.Full);
    }

    private void OnUnloaded()
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
        // An unloaded view is reusable and no longer belongs to this window.
        // Do not let a former host destroy it, or retain weak-event wrappers
        // for every virtualized view until the window eventually closes.
        _windowDestroying.Detach();
        // Win2D can retain queued native draw/resource callbacks after XAML starts
        // removing this control. Stop those callbacks before releasing any snapshot
        // or DirectWrite resources they may reference.
        _canvasEvents.DetachRenderHandlers();
        _canvasEvents.DetachInputHandlers();
        // Win2D requires RemoveFromVisualTree from the containing control's
        // Unloaded boundary. At this point XAML has begun detaching the renderer,
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
        MarkdownSelectionCoordinator.Unregister(this);
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

    /// <summary>
    /// Releases renderer resources and detaches Win2D/XAML event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_lifetime.IsDisposed)
            return;

        _lifetime.MarkDisposing();
        _touchRestore.Cancel();
        _touchTapSuppression.Reset();
        _deviceRecovery.Stop();
        _handles.ResetDrag();
        _touchSelection.Dispose();
        _handles.StopFrameClock();
        _windowDestroying.Detach();
        OnUnloaded();
        _adorner.ReleaseForDispose();
        _published.LayoutDevice = null;
        EffectiveViewportChanged -= _scrolling.OnEffectiveViewportChanged;
        DetachCanvasHandlers();
        _dragShield.Release();
        Content = null;
        _surface.Detach();
        _documentInputs.ReleaseOwnedEngine();
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

    void IRendererEventSink.InvokeLinkClick(MarkdownLinkClickEventArgs args) => LinkClick?.Invoke(this, args);

    void IRendererEventSink.InvokeDisclosureToggled(MarkdownDisclosureToggledEventArgs args) =>
        DisclosureToggled?.Invoke(this, args);

    void IRendererEventSink.InvokeCopyCompleted(MarkdownCopyCompletedEventArgs args) => CopyCompleted?.Invoke(this, args);

    void IRendererEventSink.InvokeImageUnavailable(MarkdownImageUnavailableEventArgs args) =>
        ImageUnavailable?.Invoke(this, args);

    void IRendererEventSink.InvokeEmbedsRealizationChanged() => EmbedsRealizationChanged?.Invoke(this, EventArgs.Empty);

    void IRendererEventSink.InvokeRenderCompleted() => RenderCompleted?.Invoke(this, EventArgs.Empty);

    void IRendererEventSink.InvokeRenderFailed(MarkdownRenderFailedEventArgs args) => RenderFailed?.Invoke(this, args);

    private void DetachCanvasHandlers()
    {
        _canvasEvents.DetachRenderHandlers();
        _canvasEvents.DetachInputHandlers();
        GettingFocus -= OnGettingFocus;
        GotFocus -= OnGotFocus;
        LosingFocus -= OnLosingFocus;
        KeyDown -= _keyboard.OnKeyDown;
        _handles.DetachHandlers();
    }

    /// <summary>Kicks off (or re-kicks) the parse + layout pipeline.</summary>
    public void RequestRebuild()
        => RequestRebuild(RebuildReason.Full);

    private void RequestRebuild(RebuildReason reason) => _rebuild.Request(reason);

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

    Task<ParsedMarkdown?> IMarkdownPerformanceLegacyParser.ParseLegacyMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken) =>
        _pipeline.ParseLegacyMarkdownAsync(source, registry, cancellationToken);

    /// <summary>The stage timings of the committed snapshot's pipeline.</summary>
    internal MarkdownPipelineTimingSnapshot GetLastPipelineTimingSnapshot() =>
        _pipeline.LastTimings;

    internal void ScheduleVisibleDeferredScenes() => _visibleWork.ScheduleDeferredScenes();

    internal void RequestRebuildAfterDeferredScene()
        => RequestRebuild(RebuildReason.SceneMaterialized);

    /// <summary>Re-runs the visible deferred-scene probe after the next successful paint.</summary>
    internal void RequestDeferredSceneProbeAfterPaint()
        => _painter.RequestDeferredSceneProbe();

    private void AddImagePlan(Layout.Boxes.ImageBox image)
    {
        if (!_imageSubscriptions.Subscribe(image))
            return;

        _imagePlans.Add(image);
    }

    internal void ScrollHorizontalOverflowForAutomation(
        IHorizontalOverflowBox target,
        ScrollAmount amount)
        => _overflowScroll.ScrollForAutomation(target, amount);

    internal void SetHorizontalOverflowPercentForAutomation(
        IHorizontalOverflowBox target,
        double percent)
        => _overflowScroll.SetPercentForAutomation(target, percent);

    bool ISelectionAdornerHost.IsDeviceRecoveryQueued => _deviceRecovery.IsQueued;

    bool ISelectionAdornerHost.HasFocusedLink => _focus.TryGetFocusedLink(out _, out _);

    bool ISelectionAdornerHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot snapshot)
        => _geometryRecovery.RequestIfRequired(snapshot);

    void ISelectionAdornerHost.HandleDeviceLost(Exception exception) => _deviceRecovery.HandleDeviceLost(exception);

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
        => _textAdorner.PaintInteractiveDocumentState(snapshot, drawingSession, viewport, selectionBackgrounds);

    /// <summary>
    /// Scrolls the document so the block with <paramref name="blockIndex"/>
    /// is near the top of the viewport.
    /// </summary>
    public void ScrollToBlock(int blockIndex) => _blockScroll.ScrollTo(blockIndex);

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
}
