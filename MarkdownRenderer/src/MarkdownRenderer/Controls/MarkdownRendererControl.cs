using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
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
using MarkdownRenderer.Controls.Threading;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The native Win2D + DirectWrite markdown renderer shared by
/// <see cref="MarkdownScrollView"/>, which owns its scroll viewport, and
/// <see cref="MarkdownDocumentView"/>, which uses an ancestor's viewport.
/// Hosts a <see cref="CanvasVirtualControl"/> for paint and a sibling
/// <see cref="Canvas"/> overlay for hosted WinUI embeds.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it is the composition root: the
/// public dependency properties, events and methods, the constructor that
/// builds each owner in dependency order, and disposal. Its behavior lives in
/// those owners; UI Automation peers reach it through <see cref="AutomationHost"/>.
/// </remarks>
public abstract class MarkdownRendererControl : UserControl, IDisposable, IAsyncDisposable,
    IMarkdownPerformanceLegacyParser, IRendererEventSink
{
    private readonly MarkdownViewLifetime _lifetime = new();
    private readonly PublishedLayout _published = new();
    private readonly SnapshotRetirementTracker _snapshotRetirements = new(
        static exception => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] retirement failed: {exception.GetType().Name} " +
            $"(0x{exception.HResult:X8})."));
    private readonly MarkdownAutomationDocumentGeneration _automationDocumentGenerations = new();
    private readonly LayoutCommitState _layoutCommit = new();
    private readonly PointerHoverState _hover = new();
    private readonly SelectionController _selection = new();
    private readonly MarkdownTouchSelectionSession _touchSelection = new();
    private readonly TouchTapSuppressionPolicy _touchTapSuppression = new();

    // Owners the control reads after construction: property callbacks,
    // public and automation members, and disposal.
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly WindowDestroyingWatch _windowDestroying;
    private readonly RendererLocalization _localization;
    private readonly RendererAutomation _automation;
    private readonly AutomationScrolling _automationScrolling;
    private readonly RebuildScheduler _rebuild;
    private readonly RebuildPipeline _pipeline;
    private readonly DocumentInputs _documentInputs;
    private readonly CanvasTilePainter _painter;
    private readonly CanvasEvents _canvasEvents;
    private readonly CanvasInvalidation _canvasInvalidation;
    private readonly CanvasDeviceRecovery _deviceRecovery;
    private readonly SyntheticDeviceReset _deviceReset;
    private readonly RecoveryClockWork _recoveryWork;
    private readonly VisibleWorkScheduler _visibleWork;
    private readonly ScrollViewTracking _scrolling;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly BlockScrollNavigation _blockScroll;
    private readonly VisibleImageReadiness _imageReadiness;
    private readonly DeferredSceneReadiness _deferredScenes;
    private readonly EmbedRealization _embeds;
    private readonly CodeBlockHighlighting _highlighting;
    private readonly CodeBlockClipboard _codeBlockClipboard;
    private readonly PresentationInputChanges _inputChanges;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly SelectionCommands _selectionCommands;
    private readonly SelectionDragShield _dragShield;
    private readonly SelectionHandlesPresenter _handles;
    private readonly SelectionChangeHandling _selectionChanges;
    private readonly TouchSelectionRestore _touchRestore;
    private readonly InteractionReset _interactionReset;
    private readonly KeyboardInput _keyboard;
    private readonly RendererFocusEvents _focusEvents;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly PerformanceSessionSubscription _performanceSession;
    private readonly SvgRendererSubscription _svgRenderer;
    private readonly ThemeSubscription _theme;
    private readonly EnvironmentSubscription _environment;
    private readonly ViewAttachment _attachment;

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

    private static DependencyProperty Register<T>(string name, object? defaultValue, PropertyChangedCallback changed) =>
        DependencyProperty.Register(name, typeof(T), typeof(MarkdownRendererControl), new PropertyMetadata(defaultValue, changed));

    private static void OnFullRebuildInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Full);

    private static void OnRestyleInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Restyle);

    /// <summary>Dependency property backing <see cref="Markdown"/>.</summary>
    public static readonly DependencyProperty MarkdownProperty =
        Register<string>(nameof(Markdown), string.Empty,
            (d, _) => ((MarkdownRendererControl)d)._documentInputs.OnMarkdownChanged());

    /// <summary>Gets or sets the markdown source text to render.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Document"/>.</summary>
    public static readonly DependencyProperty DocumentProperty =
        Register<MarkdownRenderer.Document.MarkdownDocument>(nameof(Document), null,
            (d, e) => ((MarkdownRendererControl)d)._documentInputs.OnDocumentChanged(e.NewValue));

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
        Register<MarkdownEngine>(nameof(Engine), null,
            (d, e) => ((MarkdownRendererControl)d)._documentInputs.OnEngineChanged(e.OldValue, e.NewValue));

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
        Register<MarkdownStyleSheet>(nameof(StyleSheet), null, OnRestyleInputChanged);

    /// <summary>Gets or sets immutable semantic style rules layered over the active theme.</summary>
    public MarkdownStyleSheet? StyleSheet
    {
        get => (MarkdownStyleSheet?)GetValue(StyleSheetProperty);
        set => SetValue(StyleSheetProperty, value);
    }

    /// <summary>Dependency property backing <see cref="StringProvider"/>.</summary>
    public static readonly DependencyProperty StringProviderProperty =
        Register<IMarkdownStringProvider>(nameof(StringProvider), null,
            (d, _) => ((MarkdownRendererControl)d)._inputChanges.OnStringProviderChanged());

    /// <summary>Gets or sets a host provider for localized visible and automation strings.</summary>
    public IMarkdownStringProvider? StringProvider
    {
        get => (IMarkdownStringProvider?)GetValue(StringProviderProperty);
        set => SetValue(StringProviderProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CommandProvider"/>.</summary>
    public static readonly DependencyProperty CommandProviderProperty =
        Register<IMarkdownCommandProvider>(nameof(CommandProvider), null, OnFullRebuildInputChanged);

    /// <summary>Gets or sets a host provider for target-aware markdown commands.</summary>
    public IMarkdownCommandProvider? CommandProvider
    {
        get => (IMarkdownCommandProvider?)GetValue(CommandProviderProperty);
        set => SetValue(CommandProviderProperty, value);
    }

    /// <summary>Dependency property backing <see cref="HostedElementFactory"/>.</summary>
    public static readonly DependencyProperty HostedElementFactoryProperty =
        Register<IMarkdownHostedElementFactory>(nameof(HostedElementFactory), null,
            (d, _) => ((MarkdownRendererControl)d)._inputChanges.OnHostedElementFactoryChanged());

    /// <summary>Gets or sets the viewport-aware factory for declarative hosted content.</summary>
    public IMarkdownHostedElementFactory? HostedElementFactory
    {
        get => (IMarkdownHostedElementFactory?)GetValue(HostedElementFactoryProperty);
        set => SetValue(HostedElementFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Theme"/>.</summary>
    public static readonly DependencyProperty ThemeProperty =
        Register<MarkdownTheme>(nameof(Theme), null,
            (d, e) => ((MarkdownRendererControl)d)._theme.OnThemePropertyChanged(e));

    /// <summary>Gets or sets the renderer theme.</summary>
    public MarkdownTheme? Theme
    {
        get => (MarkdownTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    internal static readonly DependencyProperty ExtensionRegistryProperty =
        Register<MarkdownExtensionRegistry>(nameof(ExtensionRegistry), null,
            (d, _) => ((MarkdownRendererControl)d)._documentInputs.OnParserConfigurationChanged());

    internal MarkdownExtensionRegistry? ExtensionRegistry
    {
        get => (MarkdownExtensionRegistry?)GetValue(ExtensionRegistryProperty);
        set => SetValue(ExtensionRegistryProperty, value);
    }

    internal static readonly DependencyProperty EmbedFactoryProperty =
        Register<IMarkdownEmbedFactory>(nameof(EmbedFactory), null, OnFullRebuildInputChanged);

    internal IMarkdownEmbedFactory? EmbedFactory
    {
        get => (IMarkdownEmbedFactory?)GetValue(EmbedFactoryProperty);
        set => SetValue(EmbedFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageResolver"/>.</summary>
    public static readonly DependencyProperty ImageResolverProperty =
        Register<IMarkdownImageResolver>(nameof(ImageResolver), null, OnFullRebuildInputChanged);

    /// <summary>Gets or sets the host-specific resolver consulted before public image loading.</summary>
    public IMarkdownImageResolver? ImageResolver
    {
        get => (IMarkdownImageResolver?)GetValue(ImageResolverProperty);
        set => SetValue(ImageResolverProperty, value);
    }

    /// <summary>Dependency property backing <see cref="SvgRenderer"/>.</summary>
    public static readonly DependencyProperty SvgRendererProperty =
        Register<IMarkdownSvgRenderer>(nameof(SvgRenderer), null,
            static (d, e) => ((MarkdownRendererControl)d)._svgRenderer.OnChanged(e));

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
        Register<IMarkdownRasterDecoder>(nameof(RasterDecoder), null, OnFullRebuildInputChanged);

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
        Register<IMarkdownPerformanceSession>(nameof(PerformanceSession), null,
            (d, e) => ((MarkdownRendererControl)d)._performanceSession.OnChanged(e));

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
        Register<Uri>(nameof(ImageBaseUri), null, OnFullRebuildInputChanged);

    /// <summary>Gets or sets the base URI used to resolve relative image sources.</summary>
    public Uri? ImageBaseUri
    {
        get => (Uri?)GetValue(ImageBaseUriProperty);
        set => SetValue(ImageBaseUriProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageDocumentPath"/>.</summary>
    public static readonly DependencyProperty ImageDocumentPathProperty =
        Register<string>(nameof(ImageDocumentPath), null, OnFullRebuildInputChanged);

    /// <summary>Gets or sets the source document path used by host image resolvers.</summary>
    public string? ImageDocumentPath
    {
        get => (string?)GetValue(ImageDocumentPathProperty);
        set => SetValue(ImageDocumentPathProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageDocumentSource"/>.</summary>
    public static readonly DependencyProperty ImageDocumentSourceProperty =
        Register<MarkdownDocumentSource>(nameof(ImageDocumentSource), null, OnFullRebuildInputChanged);

    /// <summary>Gets or sets the stable identity and repository context of this document.</summary>
    public MarkdownDocumentSource? ImageDocumentSource
    {
        get => (MarkdownDocumentSource?)GetValue(ImageDocumentSourceProperty);
        set => SetValue(ImageDocumentSourceProperty, value);
    }

    /// <summary>Dependency property backing <see cref="AllowThirdPartyRemoteImages"/>.</summary>
    public static readonly DependencyProperty AllowThirdPartyRemoteImagesProperty =
        Register<bool>(nameof(AllowThirdPartyRemoteImages), false, OnFullRebuildInputChanged);

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
        Register<bool>(nameof(IsSelectionEnabled), true,
            static (d, e) => ((MarkdownRendererControl)d)._selectionChanges.OnSelectionEnabledChanged((bool)e.NewValue));

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
        Register<bool>(nameof(IsCodeBlockCopyEnabled), true, OnFullRebuildInputChanged);

    /// <summary>Gets or sets whether code blocks include an always-visible copy button.</summary>
    public bool IsCodeBlockCopyEnabled
    {
        get => (bool)GetValue(IsCodeBlockCopyEnabledProperty);
        set => SetValue(IsCodeBlockCopyEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockCopyButtonLabel"/>.</summary>
    public static readonly DependencyProperty CodeBlockCopyButtonLabelProperty =
        Register<string>(nameof(CodeBlockCopyButtonLabel), null,
            (d, _) => ((MarkdownRendererControl)d)._embeds.UpdateCodeBlockCopyButtonLabels());

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
        Register<string>(nameof(CodeBlockCopiedButtonLabel), null,
            (d, _) => ((MarkdownRendererControl)d)._embeds.UpdateCodeBlockCopyButtonLabels());

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
        Register<Style>(nameof(CodeBlockCopyButtonStyle), null,
            (d, e) => ((MarkdownRendererControl)d)._embeds.UpdateCodeBlockCopyButtonStyles((Style?)e.NewValue));

    /// <summary>Gets or sets the host-provided style for code-block copy buttons.</summary>
    public Style? CodeBlockCopyButtonStyle
    {
        get => (Style?)GetValue(CodeBlockCopyButtonStyleProperty);
        set => SetValue(CodeBlockCopyButtonStyleProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsCodeBlockSyntaxHighlightingEnabled"/>.</summary>
    public static readonly DependencyProperty IsCodeBlockSyntaxHighlightingEnabledProperty =
        Register<bool>(nameof(IsCodeBlockSyntaxHighlightingEnabled), true, OnFullRebuildInputChanged);

    /// <summary>Gets or sets whether code blocks may request syntax highlighting from a configured provider.</summary>
    public bool IsCodeBlockSyntaxHighlightingEnabled
    {
        get => (bool)GetValue(IsCodeBlockSyntaxHighlightingEnabledProperty);
        set => SetValue(IsCodeBlockSyntaxHighlightingEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsTaskListEditingEnabled"/>.</summary>
    public static readonly DependencyProperty IsTaskListEditingEnabledProperty =
        Register<bool>(nameof(IsTaskListEditingEnabled), false, OnFullRebuildInputChanged);

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
        Register<ICodeHighlighter>(nameof(CodeHighlighter), null,
            static (d, e) => ((MarkdownRendererControl)d)._inputChanges.OnCodeHighlighterChanged(e));

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
        Register<CodeBlockLineNumberMode>(nameof(CodeBlockLineNumberMode), CodeBlockLineNumberMode.AutoMultiline,
            OnFullRebuildInputChanged);

    /// <summary>Gets or sets when code blocks show line numbers.</summary>
    public CodeBlockLineNumberMode CodeBlockLineNumberMode
    {
        get => (CodeBlockLineNumberMode)GetValue(CodeBlockLineNumberModeProperty);
        set => SetValue(CodeBlockLineNumberModeProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockChromeMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockChromeModeProperty =
        Register<CodeBlockChromeMode>(nameof(CodeBlockChromeMode), CodeBlockChromeMode.Enhanced,
            OnFullRebuildInputChanged);

    /// <summary>Gets or sets whether code blocks use enhanced headers or plain Markdown chrome.</summary>
    public CodeBlockChromeMode CodeBlockChromeMode
    {
        get => (CodeBlockChromeMode)GetValue(CodeBlockChromeModeProperty);
        set => SetValue(CodeBlockChromeModeProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockWrappingMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockWrappingModeProperty =
        Register<CodeBlockWrappingMode>(nameof(CodeBlockWrappingMode), CodeBlockWrappingMode.NoWrap,
            OnFullRebuildInputChanged);

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

    /// <summary>What UI Automation peers ask of this renderer.</summary>
    internal IMarkdownAutomationHost AutomationHost => _automation;

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
    public MarkdownThemeVariant? RenderedThemeVariant => _published.ThemeVariant;

    /// <summary>
    /// Raised after every embed realisation pass (initial layout commit,
    /// scroll, resize). Subscribers can use it to surface
    /// <see cref="RealizedEmbedCount"/> for diagnostics or UI-automation
    /// tests without polluting the control's UIA surface.
    /// </summary>
    public event EventHandler? EmbedsRealizationChanged;

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

    /// <summary>Releases device-bound snapshot resources before a deliberate Win2D device replacement.</summary>
    internal void PrepareForSyntheticDeviceReset() => _deviceReset.Prepare();

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new MarkdownAutomationPeer(this);

    /// <summary>
    /// Internal accessor for the current layout snapshot. Used by the
    /// automation peer to walk the document structure (headings, links, etc.).
    /// May be null before the first layout completes.
    /// </summary>
    internal LayoutSnapshot? CurrentSnapshot => _published.Snapshot;

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

    internal void CopyCodeBlockFromAutomation(Layout.Boxes.CodeBlockBox box) => _codeBlockClipboard.CopyFromAutomation(box);

    internal VisibleImageReadiness ImageReadiness => _imageReadiness;

    internal DeferredSceneReadiness DeferredSceneReadiness => _deferredScenes;

    /// <summary>
    /// Initializes a renderer with either an owned scroll viewport or an
    /// externally supplied effective viewport.
    /// </summary>
    /// <param name="ownsScrollViewport">
    /// True to create the scroll viewer; false to participate in the nearest
    /// ancestor viewport through EffectiveViewportChanged.
    /// </param>
    /// <param name="diagnostics">The diagnostics this renderer runs with for its lifetime.</param>
    /// <remarks>This constructor stays whole because it is the composition root: one line per
    /// owner in dependency order, and readonly owner fields can be set only in a constructor.</remarks>
    private protected MarkdownRendererControl(bool ownsScrollViewport, MarkdownRendererDiagnosticOptions diagnostics)
    {
        IMarkdownUiScheduler? uiScheduler = DispatcherQueueUiScheduler.For(DispatcherQueue);
        var events = new RendererEvents(this, _lifetime, DispatcherQueue);
        var pipelineHosts = new PipelineHosts(this);
        var layoutHosts = new LayoutHosts(this);
        var imageHosts = new ImageHosts(this);
        var focusHosts = new FocusHosts(this);
        var embedHosts = new EmbedHosts(this);
        var pointerHosts = new PointerHosts(this);
        var selectionHosts = new SelectionHosts(this);
        var imagePrefetch = new ImageSourcePrefetch();
        var completedImageCarry = new CompletedImageCarryLedger();
        var imagePreparationOwnerToken = new object();
        // Hosted-focus readers consume only realization plans published for the
        // exact snapshot generation and layout revision. The attempt epoch also
        // invalidates a plan while a same-snapshot rebuild is being transferred.
        var hostedFocusPlan = new HostedFocusPlanPublication();
        var cancellationRetirement = new PipelineCancellationRetirement(_snapshotRetirements);
        var imageLifetime = new ImageLifetimeScope(cancellationRetirement);
        _touchRestore = new TouchSelectionRestore(_published, _selection, _touchSelection);
        _localization = new RendererLocalization(pipelineHosts, _published);
        _surface = new MarkdownViewSurface(ownsScrollViewport);
        _windowDestroying = new WindowDestroyingWatch(this, _lifetime);
        var recoveryClock = new RecoveryClock(layoutHosts, _lifetime, uiScheduler);
        _viewport = new MarkdownViewport(this, _surface);
        var vectorCapacity = new VectorCapacityRetry(layoutHosts, _lifetime, _published, _viewport);
        var cursor = new PointerCursorPresenter(cursor => ProtectedCursor = cursor);
        var abbreviationTooltip = new AbbreviationTooltipPresenter(this, _surface, _lifetime, _hover);
        _adorner = new SelectionAdornerPresenter(selectionHosts, this, _surface, _lifetime, _published, _viewport, _selection, _hover);
        _deviceRecovery = new CanvasDeviceRecovery(layoutHosts, _lifetime, _published, _surface, _adorner, DispatcherQueue);
        _canvasInvalidation = new CanvasInvalidation(this, _lifetime, _surface, _deviceRecovery);
        _selectionCommands = new SelectionCommands(selectionHosts, _published, _selection);
        var selectionDismissal = new SelectionDismissalHook(selectionHosts, this, _surface, _lifetime, _published, _selection, _selectionCommands);
        var autoScroll = new SelectionDragAutoScroll(_surface, _viewport);
        _dragShield = new SelectionDragShield(_surface, _viewport, pointerHosts);
        _handles = new SelectionHandlesPresenter(selectionHosts, pointerHosts, this, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _adorner, autoScroll);
        var contextMenu = new SelectionContextMenuPresenter(selectionHosts, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _handles, _adorner, _selectionCommands);
        var horizontalOverflowDrag = new HorizontalOverflowPointerDrag(pointerHosts, _surface, _published, _touchTapSuppression);
        var document = new DocumentPointerInteraction(pointerHosts, _surface, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, horizontalOverflowDrag, _handles, autoScroll, _dragShield, _adorner, contextMenu, new PointerHoverTracker(pointerHosts, _published, _hover, cursor, abbreviationTooltip), cursor, abbreviationTooltip);
        var extent = new SnapshotExtent(this, _surface, _dragShield, document);
        var touchGestures = new TouchGestureInteraction(pointerHosts, this, _surface, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, horizontalOverflowDrag, document);
        var pointerInput = new PointerInputRouter(pointerHosts, pointerHosts, DispatcherQueue, _surface, _lifetime, _published, _viewport, _selection, _touchSelection, _touchTapSuppression, _handles, document, horizontalOverflowDrag, touchGestures, contextMenu, _adorner, _dragShield);
        _rebuild = new RebuildScheduler(pipelineHosts, _lifetime, _surface, pointerInput, cancellationRetirement);
        var canvasPointerEvents = new CanvasPointerEvents(_surface, pointerInput);
        _embeds = new EmbedRealization(embedHosts, embedHosts, _surface, _lifetime, _published, _viewport, DispatcherQueue, OnHostedEmbedKeyDown);
        // Lazy-load queue: all ImageBox instances in the current snapshot.
        // EnsureLoading() is called for each when its bounds enter the
        // viewport + LazyImageOverscanPx band.  Images already in the
        // in-memory cache start loading (no-op) immediately after build.
        var imagePlans = new ImagePlanSet(_lifetime, _published);
        var imageSubscriptions = new ImageSubscriptions(imageHosts, _lifetime, _published, imagePlans);
        var imageResidency = new ImageResidency(imageHosts, _lifetime, _published, imagePlans, _embeds);
        _highlighting = new CodeBlockHighlighting(embedHosts, _lifetime, _published, _viewport, _embeds);
        _inputChanges = new PresentationInputChanges(_lifetime, _rebuild, _embeds, _highlighting, _handles);
        var focus = new KeyboardFocusModel(_lifetime, _published);
        var focusGeometry = new FocusGeometryReader(_lifetime, _published);
        var focusRing = new FocusRingPresenter(focusHosts, focus, focusGeometry, _surface, _lifetime, _published, DispatcherQueue);
        _interactionReset = new InteractionReset(_surface, _selection, _touchSelection, _touchRestore, _touchTapSuppression, pointerInput, document, _handles, autoScroll, _dragShield, contextMenu, _adorner, _hover, abbreviationTooltip, cursor, focus, focusRing);
        var rasterPlacement = new RasterPlacementPublisher(imageHosts, _lifetime, _published, _layoutCommit, _surface, pointerInput, imageSubscriptions, imagePlans, imageResidency, _embeds, focus, focusRing, hostedFocusPlan, _snapshotRetirements);
        // A deferred focus-plan refresh is value-only: it never keeps a snapshot,
        // box, or native resource alive while a geometry lease is busy.
        var focusRefresh = new FocusableItemsRefreshRequest(_published);
        var focusableItems = new FocusableItemsPublication(layoutHosts, _lifetime, _published, focusRefresh, focus, recoveryClock);
        // Used when neither the control, document nor engine supplies a registry.
        var fallbackRegistry = new MarkdownExtensionRegistry();
        var layoutSetup = new RebuildLayoutSetup(pipelineHosts, diagnostics, _snapshotRetirements, imagePrefetch, completedImageCarry, imagePlans, _embeds, focusableItems, imagePreparationOwnerToken, fallbackRegistry);
        var hostedFocus = new HostedElementFocus(focusHosts, _lifetime, _published, focus, hostedFocusPlan, _embeds, focusRing);
        _overflowScroll = new HorizontalOverflowScrollVisuals(focusHosts, _surface, _lifetime, _published, _selection, focus, _embeds, DispatcherQueue);
        var reveal = new FocusedItemReveal(focusHosts, _surface, _lifetime, _published, _viewport, focus, _overflowScroll, DispatcherQueue);
        _automationScrolling = new AutomationScrolling(_surface, reveal);
        var focusCommit = new FocusedItemCommit(focusHosts, _lifetime, _published, focus, focusRing, reveal, hostedFocus, _embeds, DispatcherQueue);
        var queuedFocusKeys = new SpatialFocusKeyQueue();
        var navigator = new KeyboardFocusNavigator(focusHosts, _lifetime, _published, focus, focusGeometry, focusRing, _overflowScroll, _embeds, focusCommit, queuedFocusKeys, DispatcherQueue);
        var focusGeometryWork = new PendingFocusGeometryWork(focusHosts, _lifetime, _published, focus, focusGeometry, focusRing, reveal, queuedFocusKeys, navigator);
        var focusEntry = new RendererFocusEntry(focusHosts, _lifetime, focus, focusRing, focusCommit, navigator, DispatcherQueue);
        var layoutPublisher = new LayoutCommitPublisher(layoutHosts, _lifetime, _published, _layoutCommit, _surface, recoveryClock, pointerInput, _embeds, imageSubscriptions, imagePlans, imageResidency, focus, focusRing, focusCommit, focusRefresh, hostedFocusPlan);
        _lazyLayout = new LazyLayoutRealization(layoutHosts, _lifetime, _published, _layoutCommit, layoutPublisher, _surface, _viewport, recoveryClock, vectorCapacity, pointerInput, focus, focusCommit);
        _blockScroll = new BlockScrollNavigation(_lifetime, _published, _surface, _viewport, _lazyLayout, reveal);
        var links = new LinkActivation(pipelineHosts, _blockScroll);
        _automation = new RendererAutomation(this, _lifetime, _surface, _viewport, _published, _automationDocumentGenerations, _localization, _selection, focus, focusRing, focusCommit, focusEntry, links, _handles, _overflowScroll);
        _deferredScenes = new DeferredSceneReadiness(pipelineHosts, _lifetime, _published, _viewport, _rebuild);
        _imageReadiness = new VisibleImageReadiness(_published, imagePlans, _viewport);
        _painter = new CanvasTilePainter(layoutHosts, _lifetime, _published, _surface, _lazyLayout, _deviceRecovery, _rebuild, _deferredScenes);
        _deviceReset = new SyntheticDeviceReset(_published, _snapshotRetirements, _automationDocumentGenerations, _layoutCommit, _automation, _painter, _lazyLayout, vectorCapacity);
        _canvasEvents = new CanvasEvents(_lifetime, _surface, canvasPointerEvents, _painter, _rebuild);
        var imageRelayout = new ImageRelayout(imageHosts, _lifetime, _published, _layoutCommit, imageSubscriptions, recoveryClock, uiScheduler);
        var imageLoadCompletion = new ImageLoadCompletion(imageHosts, _lifetime, _published, _layoutCommit, imageSubscriptions, imageRelayout, uiScheduler);
        imageSubscriptions.LoadCompleted += imageLoadCompletion.OnLoadCompleted;
        var geometryRecovery = new GeometryRecovery(_lifetime, _published, _surface, pointerInput, vectorCapacity, horizontalOverflowDrag, focusGeometryWork, _overflowScroll, focusRing, _adorner, _layoutCommit, imageSubscriptions, _lazyLayout, imageRelayout, _interactionReset, _dragShield, contextMenu, abbreviationTooltip, cursor, _handles, _automation, _deviceRecovery, _rebuild);
        _visibleWork = new VisibleWorkScheduler(this, _published, _highlighting, geometryRecovery);
        _documentInputs = new DocumentInputs(pipelineHosts, uiScheduler, _snapshotRetirements);
        _keyboard = new KeyboardInput(this, _published, _selection, _selectionCommands, contextMenu, focus, focusRing, focusCommit, hostedFocus, navigator, geometryRecovery);
        _recoveryWork = new RecoveryClockWork(layoutHosts, _lifetime, _published, recoveryClock, _layoutCommit, imageSubscriptions, imageRelayout, focusRefresh);
        var textAdorner = new InteractiveTextAdorner(_surface, _published, _selection, _touchSelection, _hover, focus, _adorner, geometryRecovery);
        _scrolling = new ScrollViewTracking(_surface, _lifetime, _viewport, _lazyLayout, _adorner, textAdorner, _embeds, _visibleWork);
        _performanceSession = new PerformanceSessionSubscription(this, _lifetime, _snapshotRetirements, _rebuild, _selection, _adorner, textAdorner);
        _svgRenderer = new SvgRendererSubscription(this, _lifetime, _rebuild, _canvasInvalidation);
        _theme = new ThemeSubscription(this, _lifetime, _rebuild);
        _environment = new EnvironmentSubscription(this, _lifetime, _rebuild);
        var replacementPublisher = new ReplacementPublisher(this, _published, _surface, _rebuild, _touchRestore, _automationDocumentGenerations, _selection, _lazyLayout, _layoutCommit, _scrolling, _painter, _localization, focusRefresh, vectorCapacity, geometryRecovery, imageSubscriptions, imageRelayout, _blockScroll, imagePlans, focus);
        var width = new LayoutWidthWatch(this, _rebuild);
        var inputCapture = new RebuildInputCapture(this, _published, imagePreparationOwnerToken, _documentInputs, _localization, _environment, fallbackRegistry);
        var replacementPresentation = new ReplacementPresentation(_surface, _snapshotRetirements, _rebuild, _layoutCommit, layoutPublisher, _documentInputs, extent, _scrolling, _hover, _selection, _touchRestore, contextMenu, _dragShield, _adorner, textAdorner, _handles, _embeds, imageSubscriptions, imagePlans, imageResidency, focus, focusRing, focusCommit, _highlighting, _visibleWork, _canvasInvalidation, _automation, _deferredScenes);
        _pipeline = new RebuildPipeline(this, this, _lifetime, _surface, _viewport, _rebuild, inputCapture, layoutSetup, width, imagePrefetch, _automation, replacementPublisher, replacementPresentation);
        _codeBlockClipboard = new CodeBlockClipboard(this, _embeds, events);
        var visibleImages = new VisibleImageLoading(this, _lazyLayout, imageResidency, imagePlans, _embeds);
        var pointerResume = new PointerInteractionResume(_lifetime, _rebuild, imageRelayout);
        selectionHosts.Connect(new SelectionHosts.Parts(
            events, _published, _deviceRecovery, _handles, pointerInput, _embeds, _rebuild, geometryRecovery,
            textAdorner, _touchRestore, _localization, _interactionReset, pointerResume, focus, focusGeometry,
            focusEntry));
        pointerHosts.Connect(new PointerHosts.Parts(
            _published, pointerInput, _embeds, geometryRecovery, textAdorner, _blockScroll, links, _layoutCommit,
            _lazyLayout, _touchRestore, _interactionReset, pointerResume, focus, _overflowScroll,
            focusGeometryWork, focusEntry, focusRing));
        embedHosts.Connect(new EmbedHosts.Parts(
            events, _published, cancellationRetirement, _rebuild, _canvasInvalidation, _visibleWork, _automation,
            layoutPublisher, rasterPlacement, _lazyLayout, hostedFocusPlan, _localization, _codeBlockClipboard,
            visibleImages, hostedFocus, imagePlans, imageSubscriptions));
        focusHosts.Connect(new FocusHosts.Parts(
            _surface, _published, _viewport, recoveryClock, _rebuild, geometryRecovery, textAdorner, links,
            _automation, _layoutCommit, _lazyLayout, focusGeometryWork, focusRing));
        imageHosts.Connect(new ImageHosts.Parts(
            events, _hover, cursor, abbreviationTooltip, _adorner, _deviceRecovery, recoveryClock,
            _snapshotRetirements, cancellationRetirement, _rebuild, _canvasInvalidation, geometryRecovery,
            textAdorner, _recoveryWork, _visibleWork, _scrolling, _automation, imageLifetime, _layoutCommit,
            imagePreparationOwnerToken, inputCapture, extent, _surface, pointerInput, layoutPublisher,
            rasterPlacement));
        layoutHosts.Connect(new LayoutHosts.Parts(
            _deviceRecovery, cancellationRetirement, _rebuild, _canvasInvalidation, geometryRecovery, textAdorner,
            _recoveryWork, _visibleWork, _scrolling, _deferredScenes, _blockScroll, _automation, _lazyLayout, extent,
            layoutPublisher, _overflowScroll, focusGeometryWork, focusableItems, pointerInput));
        pipelineHosts.Connect(new PipelineHosts.Parts(
            events, _documentInputs, _automation, imageLifetime, imageRelayout, _lazyLayout, _pipeline,
            _touchRestore, _interactionReset, _performanceSession, _environment, _rebuild, _embeds, _highlighting));
        _focusEvents = new RendererFocusEvents(this, contextMenu, document, focus, focusEntry, focusRing);
        _selectionChanges = new SelectionChangeHandling(selectionDismissal, cursor, _rebuild, textAdorner, _automation, _selection, _touchRestore, _touchTapSuppression, _interactionReset);
        _attachment = new ViewAttachment(this, _surface, _lifetime, _published, cursor, abbreviationTooltip, _adorner, _deviceRecovery, selectionDismissal, _dragShield, _handles, contextMenu, horizontalOverflowDrag, pointerInput, _embeds, _windowDestroying, _snapshotRetirements, _rebuild, _painter, _canvasEvents, geometryRecovery, _recoveryWork, _scrolling, _deferredScenes, _blockScroll, _automation, imageLifetime, _automationDocumentGenerations, _layoutCommit, imageRelayout, vectorCapacity, _lazyLayout, width, _touchSelection, _touchRestore, _touchTapSuppression, _overflowScroll, focusRing, _performanceSession, _svgRenderer, _theme, _environment, imagePlans, imageSubscriptions, imageResidency, imagePrefetch, _highlighting);
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += (_, _) => _attachment.Load();
        Unloaded += (_, _) => _attachment.Unload();
        ActualThemeChanged += (_, _) => _theme.OnThemeChanged();
        // Selection changes update the XAML overlay (not the DirectWrite canvas),
        // so tiles are never invalidated during a drag.
        _selection.Changed += (_, _) => _selectionChanges.OnSelectionChanged();
        new InheritedLayoutInputWatch(this, _rebuild).Register();
        BuildVisualTree();
    }

    // Embeds subscribe hosted elements before the keyboard owner exists.
    private void OnHostedEmbedKeyDown(object sender, KeyRoutedEventArgs e) => _keyboard.OnHostedEmbedKeyDown(sender, e);

    private void BuildVisualTree()
    {
        _canvasEvents.EnsureCanvas();
        GettingFocus += _focusEvents.OnGettingFocus;
        GotFocus += _focusEvents.OnGotFocus;
        LosingFocus += _focusEvents.OnLosingFocus;
        KeyDown += _keyboard.OnKeyDown;

        if (!_surface.OwnsScrollViewport)
            EffectiveViewportChanged += _scrolling.OnEffectiveViewportChanged;
        Content = _surface.ContentElement;
    }

    internal bool TryGetViewport(out double top, out double height, out double width)
        => _viewport.TryGet(out top, out height, out width);

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

    internal void ClearSelectionFromCoordinator() => _interactionReset.ClearSelectionForExternalInteraction();

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
        _attachment.Unload();
        _adorner.ReleaseForDispose();
        _published.LayoutDevice = null;
        _published.RenderResources = null;
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
        GettingFocus -= _focusEvents.OnGettingFocus;
        GotFocus -= _focusEvents.OnGotFocus;
        LosingFocus -= _focusEvents.OnLosingFocus;
        KeyDown -= _keyboard.OnKeyDown;
        _handles.DetachHandlers();
    }

    /// <summary>Kicks off (or re-kicks) the parse + layout pipeline.</summary>
    public void RequestRebuild()
        => RequestRebuild(RebuildReason.Full);

    private void RequestRebuild(RebuildReason reason) => _rebuild.Request(reason);

    /// <summary>
    /// Clears this control's cached WinUI resource-key discovery and rebuilds
    /// its presentation. Call this after replacing resource dictionary keys without
    /// changing the dictionary's count; ordinary value replacement, theme
    /// properties, and override mutations do not require it.
    /// </summary>
    public void InvalidateThemeResources()
    {
        if (_lifetime.IsDisposed)
            return;

        _pipeline.InvalidateThemeResourceKeys();
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

    /// <summary>
    /// Scrolls the document so the block with <paramref name="blockIndex"/>
    /// is near the top of the viewport.
    /// </summary>
    public void ScrollToBlock(int blockIndex) => _blockScroll.ScrollTo(blockIndex);

}
