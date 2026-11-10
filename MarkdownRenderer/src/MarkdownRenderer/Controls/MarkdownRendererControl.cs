using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The native Win2D + DirectWrite markdown renderer shared by
/// <see cref="MarkdownScrollView"/>, which owns its scroll viewport, and
/// <see cref="MarkdownDocumentView"/>, which uses an ancestor's viewport.
/// Hosts a <see cref="CanvasVirtualControl"/> for paint and a sibling
/// <see cref="Canvas"/> overlay for hosted WinUI embeds.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it is the renderer's public
/// surface: some thirty dependency properties with their CLR wrappers and
/// documentation, which must be declared on this type, and its events,
/// methods and disposal. <see cref="MarkdownRendererComposition"/> builds the
/// owners that hold its behavior; UI Automation peers reach it through
/// <see cref="AutomationHost"/>.
/// </remarks>
public abstract class MarkdownRendererControl : UserControl, IDisposable, IAsyncDisposable,
    IMarkdownPerformanceLegacyParser, IRendererEventSink
{
    private readonly MarkdownRendererComposition _parts;

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
            (d, _) => ((MarkdownRendererControl)d)._parts.DocumentInputs.OnMarkdownChanged());

    /// <summary>Gets or sets the markdown source text to render.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Document"/>.</summary>
    public static readonly DependencyProperty DocumentProperty =
        Register<MarkdownRenderer.Document.MarkdownDocument>(nameof(Document), null,
            (d, e) => ((MarkdownRendererControl)d)._parts.DocumentInputs.OnDocumentChanged(e.NewValue));

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
            (d, e) => ((MarkdownRendererControl)d)._parts.DocumentInputs.OnEngineChanged(e.OldValue, e.NewValue));

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
    internal void SetOwnedEngine(MarkdownEngine engine) => _parts.DocumentInputs.SetOwnedEngine(engine);

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
            (d, _) => ((MarkdownRendererControl)d)._parts.InputChanges.OnStringProviderChanged());

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
            (d, _) => ((MarkdownRendererControl)d)._parts.InputChanges.OnHostedElementFactoryChanged());

    /// <summary>Gets or sets the viewport-aware factory for declarative hosted content.</summary>
    public IMarkdownHostedElementFactory? HostedElementFactory
    {
        get => (IMarkdownHostedElementFactory?)GetValue(HostedElementFactoryProperty);
        set => SetValue(HostedElementFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Theme"/>.</summary>
    public static readonly DependencyProperty ThemeProperty =
        Register<MarkdownTheme>(nameof(Theme), null,
            (d, e) => ((MarkdownRendererControl)d)._parts.Theme.OnThemePropertyChanged(e));

    /// <summary>Gets or sets the renderer theme.</summary>
    public MarkdownTheme? Theme
    {
        get => (MarkdownTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    internal static readonly DependencyProperty ExtensionRegistryProperty =
        Register<MarkdownExtensionRegistry>(nameof(ExtensionRegistry), null,
            (d, _) => ((MarkdownRendererControl)d)._parts.DocumentInputs.OnParserConfigurationChanged());

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
            static (d, e) => ((MarkdownRendererControl)d)._parts.SvgRenderer.OnChanged(e));

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
            (d, e) => ((MarkdownRendererControl)d)._parts.PerformanceSession.OnChanged(e));

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
            static (d, e) => ((MarkdownRendererControl)d)._parts.SelectionChanges.OnSelectionEnabledChanged((bool)e.NewValue));

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
            (d, _) => ((MarkdownRendererControl)d)._parts.Embeds.UpdateCodeBlockCopyButtonLabels());

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
            (d, _) => ((MarkdownRendererControl)d)._parts.Embeds.UpdateCodeBlockCopyButtonLabels());

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
            (d, e) => ((MarkdownRendererControl)d)._parts.Embeds.UpdateCodeBlockCopyButtonStyles((Style?)e.NewValue));

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
            static (d, e) => ((MarkdownRendererControl)d)._parts.InputChanges.OnCodeHighlighterChanged(e));

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
    internal IMarkdownAutomationHost AutomationHost => _parts.Automation;

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
    public Size? RenderedContentSize => _parts.Published.Snapshot?.Size;

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
    public MarkdownThemeVariant? RenderedThemeVariant => _parts.Published.ThemeVariant;

    /// <summary>
    /// Raised after every embed realisation pass (initial layout commit,
    /// scroll, resize). Subscribers can use it to surface
    /// <see cref="RealizedEmbedCount"/> for diagnostics or UI-automation
    /// tests without polluting the control's UIA surface.
    /// </summary>
    public event EventHandler? EmbedsRealizationChanged;

    internal bool OwnsScrollViewport => _parts.Surface.OwnsScrollViewport;

    /// <summary>
    /// Internal gate-harness signal used to ensure a warm scroll trace starts
    /// only after queued/background band realization has retired.
    /// </summary>
    internal bool HasPendingLazyLayoutWork => _parts.RecoveryWork.HasPendingLazyLayoutWork;

    /// <summary>
    /// Returns a completion task for every renderer-owned resource or work item
    /// retired before this call. Failures from that drain epoch are surfaced once; later drains
    /// are not permanently poisoned by an earlier failed retirement.
    /// Production teardown remains nonblocking; the performance harness uses
    /// this internal seam to prevent disposal work from contaminating the next
    /// independent sample.
    /// </summary>
    internal Task DrainRetiredSnapshotsAsync()
        => _parts.SnapshotRetirements.DrainAsync();

    /// <summary>Releases device-bound snapshot resources before a deliberate Win2D device replacement.</summary>
    internal void PrepareForSyntheticDeviceReset() => _parts.DeviceReset.Prepare();

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new MarkdownAutomationPeer(this);

    /// <summary>
    /// Internal accessor for the current layout snapshot. Used by the
    /// automation peer to walk the document structure (headings, links, etc.).
    /// May be null before the first layout completes.
    /// </summary>
    internal LayoutSnapshot? CurrentSnapshot => _parts.Published.Snapshot;

    /// <summary>UI Automation's scroll pattern and ScrollIntoView requests.</summary>
    internal AutomationScrolling AutomationScrolling => _parts.AutomationScrolling;

    /// <summary>Strings and culture resolved from the language snapshot of the committed layout.</summary>
    internal RendererLocalization Localization => _parts.Localization;

    /// <summary>
    /// Number of currently-realised hosted embed elements (block + inline).
    /// Exposed for UI-automation tests that want to validate virtualisation
    /// without relying on heuristic descendant counts.
    /// </summary>
    public int RealizedEmbedCount => _parts.Embeds.RealizedEmbedCount;

    internal void SetDisclosureExpanded(LinkRun run, bool expanded) => _parts.DocumentInputs.SetDisclosureExpanded(run, expanded);

    internal void CopyCodeBlockFromAutomation(Layout.Boxes.CodeBlockBox box) => _parts.CodeBlockClipboard.CopyFromAutomation(box);

    internal VisibleImageReadiness ImageReadiness => _parts.ImageReadiness;

    internal DeferredSceneReadiness DeferredSceneReadiness => _parts.DeferredScenes;

    /// <summary>
    /// Initializes a renderer with either an owned scroll viewport or an
    /// externally supplied effective viewport.
    /// </summary>
    /// <param name="ownsScrollViewport">
    /// True to create the scroll viewer; false to participate in the nearest
    /// ancestor viewport through EffectiveViewportChanged.
    /// </param>
    /// <param name="diagnostics">The diagnostics this renderer runs with for its lifetime.</param>
    private protected MarkdownRendererControl(bool ownsScrollViewport, MarkdownRendererDiagnosticOptions diagnostics)
    {
        _parts = new MarkdownRendererComposition(this, ownsScrollViewport, diagnostics, cursor => ProtectedCursor = cursor);
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += (_, _) => _parts.Attachment.Load();
        Unloaded += (_, _) => _parts.Attachment.Unload();
        ActualThemeChanged += (_, _) => _parts.Theme.OnThemeChanged();
        // Selection changes update the XAML overlay (not the DirectWrite canvas),
        // so tiles are never invalidated during a drag.
        _parts.Selection.Changed += (_, _) => _parts.SelectionChanges.OnSelectionChanged();
        new InheritedLayoutInputWatch(this, _parts.Rebuild).Register();
        BuildVisualTree();
    }

    private void BuildVisualTree()
    {
        _parts.CanvasEvents.EnsureCanvas();
        GettingFocus += _parts.FocusEvents.OnGettingFocus;
        GotFocus += _parts.FocusEvents.OnGotFocus;
        LosingFocus += _parts.FocusEvents.OnLosingFocus;
        KeyDown += _parts.Keyboard.OnKeyDown;

        if (!_parts.Surface.OwnsScrollViewport)
            EffectiveViewportChanged += _parts.Scrolling.OnEffectiveViewportChanged;
        Content = _parts.Surface.ContentElement;
    }

    internal bool TryGetViewport(out double top, out double height, out double width)
        => _parts.Viewport.TryGet(out top, out height, out width);

    /// <summary>
    /// Copies the active selection to the clipboard.
    /// </summary>
    /// <param name="options">Optional copy format options. Defaults use rendered plain text and add CF_HTML.</param>
    /// <returns>True when a selection was copied successfully.</returns>
    public bool CopySelectionToClipboard(MarkdownCopyOptions? options = null)
        => _parts.SelectionCommands.CopyToClipboard(options);

    /// <summary>Copies the active selection as exact markdown source plus CF_HTML.</summary>
    public bool CopySelectionAsMarkdown()
        => _parts.SelectionCommands.CopyAsMarkdown();

    internal void ClearSelectionFromCoordinator() => _parts.InteractionReset.ClearSelectionForExternalInteraction();

    /// <summary>
    /// Releases renderer resources and detaches Win2D/XAML event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_parts.Lifetime.IsDisposed)
            return;

        _parts.Lifetime.MarkDisposing();
        _parts.TouchRestore.Cancel();
        _parts.TouchTapSuppression.Reset();
        _parts.DeviceRecovery.Stop();
        _parts.Handles.ResetDrag();
        _parts.TouchSelection.Dispose();
        _parts.Handles.StopFrameClock();
        _parts.WindowDestroying.Detach();
        _parts.Attachment.Unload();
        _parts.Adorner.ReleaseForDispose();
        _parts.Published.LayoutDevice = null;
        _parts.Published.RenderResources = null;
        EffectiveViewportChanged -= _parts.Scrolling.OnEffectiveViewportChanged;
        DetachCanvasHandlers();
        _parts.DragShield.Release();
        Content = null;
        _parts.Surface.Detach();
        _parts.DocumentInputs.ReleaseOwnedEngine();
        _parts.Lifetime.MarkDisposalComplete();
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
        if (!_parts.Lifetime.IsDisposalComplete)
        {
            throw new InvalidOperationException(
                "The markdown view did not finish an earlier Dispose call and may still hold resources.");
        }

        return new ValueTask(_parts.SnapshotRetirements.DrainAsync());
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
        _parts.CanvasEvents.DetachRenderHandlers();
        _parts.CanvasEvents.DetachInputHandlers();
        GettingFocus -= _parts.FocusEvents.OnGettingFocus;
        GotFocus -= _parts.FocusEvents.OnGotFocus;
        LosingFocus -= _parts.FocusEvents.OnLosingFocus;
        KeyDown -= _parts.Keyboard.OnKeyDown;
        _parts.Handles.DetachHandlers();
    }

    /// <summary>Kicks off (or re-kicks) the parse + layout pipeline.</summary>
    public void RequestRebuild()
        => RequestRebuild(RebuildReason.Full);

    private void RequestRebuild(RebuildReason reason) => _parts.Rebuild.Request(reason);

    /// <summary>
    /// Clears this control's cached WinUI resource-key discovery and rebuilds
    /// its presentation. Call this after replacing resource dictionary keys without
    /// changing the dictionary's count; ordinary value replacement, theme
    /// properties, and override mutations do not require it.
    /// </summary>
    public void InvalidateThemeResources()
    {
        if (_parts.Lifetime.IsDisposed)
            return;

        _parts.Pipeline.InvalidateThemeResourceKeys();
        RequestRebuild(RebuildReason.Restyle);
    }

    Task<ParsedMarkdown?> IMarkdownPerformanceLegacyParser.ParseLegacyMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken) =>
        _parts.Pipeline.ParseLegacyMarkdownAsync(source, registry, cancellationToken);

    /// <summary>The stage timings of the committed snapshot's pipeline.</summary>
    internal MarkdownPipelineTimingSnapshot GetLastPipelineTimingSnapshot() =>
        _parts.Pipeline.LastTimings;

    internal void ScheduleVisibleDeferredScenes() => _parts.VisibleWork.ScheduleDeferredScenes();

    internal void RequestRebuildAfterDeferredScene()
        => RequestRebuild(RebuildReason.SceneMaterialized);

    /// <summary>Re-runs the visible deferred-scene probe after the next successful paint.</summary>
    internal void RequestDeferredSceneProbeAfterPaint()
        => _parts.Painter.RequestDeferredSceneProbe();

    /// <summary>
    /// Scrolls the document so the block with <paramref name="blockIndex"/>
    /// is near the top of the viewport.
    /// </summary>
    public void ScrollToBlock(int blockIndex) => _parts.BlockScroll.ScrollTo(blockIndex);

}
