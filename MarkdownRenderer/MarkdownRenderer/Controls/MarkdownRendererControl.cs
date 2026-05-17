using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Markdig;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The native Win2D + DirectWrite markdown renderer. Hosts a
/// <see cref="CanvasVirtualControl"/> for paint and a sibling <see cref="Canvas"/>
/// overlay for hosted WinUI embeds.
/// </summary>
public sealed partial class MarkdownRendererControl : UserControl
{
    private CanvasVirtualControl? _canvas;
    private Canvas? _overlay;
    private ScrollViewer? _scroll;
    private Grid? _root;

    private volatile LayoutSnapshot? _snapshot;
    private volatile CancellationTokenSource? _pipelineCts;
    private bool _rebuildQueued;
    private bool _hasPendingRebuild;
    private RebuildReason _pendingRebuildReason = RebuildReason.Restyle;
    private float _lastWidth;
    private static readonly MarkdownTheme _defaultTheme = new();
    private static readonly MarkdownExtensionRegistry _defaultRegistry = new();
    private readonly object _parseCacheGate = new();
    private ParsedMarkdown? _parseCache;
    private string? _parseCacheSource;
    private MarkdownExtensionRegistry? _parseCacheRegistry;
    private int _parseCacheRegistryRevision = -1;
    private MarkdownRenderer.Document.MarkdownDocument _document = MarkdownRenderer.Document.MarkdownDocument.Empty;
    private SizeChangedEventHandler? _sizeChangedHandler;
    private readonly SelectionController _selection = new();
    private DocumentPosition? _selectionAnchor;

    // Single Win2D adorner for text selection. It draws both the native
    // selection background and selected glyph foreground above the already
    // painted document, while the base CanvasVirtualControl remains untouched
    // during selection drag. Keeping this to one stable XAML child avoids the
    // measure/compositor churn that caused the historical text-shake bug.
    private readonly List<Rect> _selectionAdornerRects = new();
    private CanvasControl? _selectionAdorner;
    private double _selectionAdornerOffsetY;
    private Border? _selectionDragShield;

    // Background color used to clear each canvas tile before painting.  Captured
    // from ActualTheme at rebuild time so that OnRegionsInvalidated (UI thread,
    // but outside the rebuild flow) always uses the correct theme-aware color.
    // Relying on ds.Clear(Colors.Transparent) doesn't work reliably because
    // CanvasVirtualControl may not alpha-composite with the XAML compositor
    // depending on the platform's DirectX swap-chain configuration.
    private Windows.UI.Color _canvasBackground = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    // Last committed theme snapshot. Used by UI operations (focus ring, etc.)
    // that need theme colors after the rebuild is complete.
    private Theming.ThemeSnapshot? _themeSnapshot;
    private ThemeSettings? _themeSettings;
    private bool _canvasDeviceRecoveryQueued;
    private int _canvasDeviceRecoveryAttempt;

    // ---- Keyboard navigation ----
    // Ordered list of focusable items (LinkRuns + InlineEmbedRuns) in the current
    // snapshot.  Rebuilt after each snapshot commit.  -1 means "nothing focused".
    private System.Collections.Generic.IReadOnlyList<Layout.FocusableItem>? _focusableItems;
    private int _focusedItemIndex = -1;
    private int _focusResumeItemIndex = -1;
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
    private bool _isUnloaded;
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
    private ClickMode _clickMode;
    private enum RebuildReason { Full, Restyle }
    private readonly record struct PointerSession(uint PointerId, bool IsPrimary)
    {
        public bool IsActive => PointerId != 0;
    }
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
    private readonly List<(Layout.Boxes.EmbedBox Box, Rect Rect)> _blockEmbedRects = new();

    // Code block copy buttons are overlay-hosted native controls, but they are
    // renderer chrome rather than user-authored embeds. Keep them on a separate
    // realization track so RealizedEmbedCount remains embed-only.
    private enum CodeBlockHostedElementKind
    {
        Copy,
    }

    private readonly List<(Layout.Boxes.CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> _codeBlockActionRects = new();

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
        public abstract void Realize(MarkdownRendererControl owner);
        public abstract void Derealize(MarkdownRendererControl owner);
        public abstract bool IsSameLogicalEmbed(EmbedPlan other);
        public abstract void UpdatePlacement();
    }
    private sealed class BlockEmbedPlan : EmbedPlan
    {
        public Layout.Boxes.EmbedBox Box = null!;
        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;
            try
            {
                var fe = Box.Factory.CreateBlock(Box.SourceBlock);
                Realized = fe;
                Box.RealizedElement = fe;
                UpdatePlacement();
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
            fe.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Box.Factory.RecycleBlock(Box.SourceBlock, fe); } catch { }
            Box.RealizedElement = null;
            Realized = null;
            // Refresh _blockEmbedRects defensively (rebuilt fully each Realize cycle from realised plans).
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is BlockEmbedPlan block && ReferenceEquals(Box, block.Box);

        public override void UpdatePlacement()
        {
            if (Realized is null) return;
            double w = Math.Round(Box.Bounds.Width  - Box.Margin.Left - Box.Margin.Right);
            double h = Math.Round(Box.Bounds.Height - Box.Margin.Top  - Box.Margin.Bottom);
            Realized.Width = w;
            Realized.Height = h;
            Canvas.SetLeft(Realized, Math.Round(Box.Bounds.X + Box.Margin.Left));
            Canvas.SetTop(Realized, Math.Round(Box.Bounds.Y + Box.Margin.Top));
            Canvas.SetZIndex(Realized, 2);
        }
    }
    private sealed class InlineEmbedPlan : EmbedPlan
    {
        public Layout.Boxes.InlineContainerBox Icb = null!;
        public InlineEmbedRun Run = null!;
        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;
            try
            {
                var fe = Run.ElementFactory();
                Realized = fe;
                Run.RealizedElement = fe;
                UpdatePlacement();
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
            fe.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Run.Recycle?.Invoke(fe); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] inline embed Recycle threw: {ex.Message}");
            }
            Run.RealizedElement = null;
            Realized = null;
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is InlineEmbedPlan inline &&
               ReferenceEquals(Icb, inline.Icb) &&
               ReferenceEquals(Run, inline.Run);

        public override void UpdatePlacement()
        {
            if (Realized is null) return;
            double iLeft = Math.Round(Rect.X);
            double iTop  = Math.Round(Rect.Y);
            double iW    = Math.Round(Rect.X + Rect.Width)  - iLeft;
            double iH    = Math.Round(Rect.Y + Rect.Height) - iTop;
            Realized.Width = iW;
            Realized.Height = iH;
            Canvas.SetLeft(Realized, iLeft);
            Canvas.SetTop(Realized, iTop);
            Canvas.SetZIndex(Realized, 2);
        }
    }
    private readonly List<EmbedPlan> _embedPlans = new();
    private sealed class CodeBlockActionPlan
    {
        public Layout.Boxes.CodeBlockBox Box = null!;
        public Rect Rect;
        public CodeBlockHostedElementKind Kind;
        public FrameworkElement? Realized;
        private RoutedEventHandler? _clickHandler;
        private int _feedbackVersion;

        public void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;

            var button = owner.CreateCodeBlockCopyButton();
            AttachHandlers(button, owner);
            button.KeyDown += owner.OnHostedEmbedKeyDown;
            Realized = button;
            Box.RealizedCopyButton = button;
            UpdatePlacement();
            owner._overlay!.Children.Add(button);
        }

        public void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            if (fe is Button button)
            {
                if (_clickHandler is not null)
                    button.Click -= _clickHandler;
                button.KeyDown -= owner.OnHostedEmbedKeyDown;
            }

            try { owner._overlay!.Children.Remove(fe); } catch { }
            Box.RealizedCopyButton = null;
            Realized = null;
            _clickHandler = null;
            _feedbackVersion++;
        }

        public void AdoptRealizedFrom(CodeBlockActionPlan oldPlan, MarkdownRendererControl owner)
        {
            if (oldPlan.Realized is null) return;

            var fe = oldPlan.Realized;
            if (fe is Button button)
            {
                if (oldPlan._clickHandler is not null)
                    button.Click -= oldPlan._clickHandler;
                button.KeyDown -= owner.OnHostedEmbedKeyDown;
                AttachHandlers(button, owner);
                button.KeyDown += owner.OnHostedEmbedKeyDown;
                owner.SetCodeBlockCopyButtonState(button, copied: false);
            }

            oldPlan._clickHandler = null;
            oldPlan._feedbackVersion++;
            oldPlan.Box.RealizedCopyButton = null;
            oldPlan.Realized = null;

            Realized = fe;
            Box.RealizedCopyButton = fe;
            UpdatePlacement();
        }

        private void AttachHandlers(Button button, MarkdownRendererControl owner)
        {
            _clickHandler = (_, _) => owner.CopyCodeBlockToClipboard(this);
            button.Click += _clickHandler;
        }

        public bool IsSameLogicalAction(CodeBlockActionPlan other)
            => ReferenceEquals(Box, other.Box) && Kind == other.Kind;

        public void UpdatePlacement()
        {
            if (Realized is null) return;
            double left = Math.Round(Rect.X);
            double top = Math.Round(Rect.Y);
            double width = Math.Round(Rect.X + Rect.Width) - left;
            double height = Math.Round(Rect.Y + Rect.Height) - top;
            Realized.Width = width;
            Realized.Height = height;
            Canvas.SetLeft(Realized, left);
            Canvas.SetTop(Realized, top);
            Canvas.SetZIndex(Realized, 2);
        }

        public void ShowCopiedFeedback(MarkdownRendererControl owner)
        {
            if (Realized is not Button button)
                return;

            int version = ++_feedbackVersion;
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
                        owner.SetCodeBlockCopyButtonState(realizedButton, copied: false);
                });
            });
        }
    }

    private readonly List<CodeBlockActionPlan> _codeBlockActionPlans = new();
    private readonly BoundedCodeBlockHighlightCache<CodeBlockHighlightCacheKey> _codeBlockHighlightCache = new(CodeBlockHighlightCacheMaxEntries);
    private readonly HashSet<CodeBlockHighlightCacheKey> _codeBlockHighlightInFlight = new();
    private readonly SemaphoreSlim _codeBlockHighlightSemaphore = new(2, 2);
    private CancellationTokenSource? _codeBlockHighlightCts;
    private int _codeBlockHighlightGeneration;
    private bool _promotingKeyboardFocusEntry;
    private bool _lastFocusEntryWasKeyboardTraversal;
    private bool _lastFocusEntryWasKeyboardInput;
    private bool _lastFocusEntryWasReverse;
    private bool _suppressNextFocusPromotion;
    private bool _contextMenuOpen;
    private UIElement? _selectionDismissalRoot;
    private PointerEventHandler? _selectionDismissalPointerPressedHandler;
    private KeyEventHandler? _selectionCopyKeyDownHandler;

    // Lazy-load queue: all ImageBox instances in the current snapshot.
    // EnsureLoading() is called for each when its bounds enter the
    // viewport + LazyImageOverscanPx band.  Images already in the
    // in-memory cache start loading (no-op) immediately after build.
    private readonly List<Layout.Boxes.ImageBox> _imagePlans = new();

    private readonly record struct CodeBlockHighlightCacheKey(
        string? Language,
        ulong CodeHash,
        int CodeLength,
        CodeBlockThemeVariant ThemeVariant,
        int ProviderIdentity,
        int ProviderRevision);

    private const int CodeBlockHighlightCacheMaxEntries = 128;
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
    /// Documents at or above this top-level block count use viewport-relative
    /// layout so first paint does not wait for every paragraph/table/code block
    /// to create native text layouts.
    /// </summary>
    internal const int LazyLayoutBlockThreshold = 500;

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

    /// <summary>Dependency property backing <see cref="ExtensionRegistry"/>.</summary>
    public static readonly DependencyProperty ExtensionRegistryProperty =
        DependencyProperty.Register(nameof(ExtensionRegistry), typeof(MarkdownExtensionRegistry),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the markdown extension registry.</summary>
    public MarkdownExtensionRegistry? ExtensionRegistry
    {
        get => (MarkdownExtensionRegistry?)GetValue(ExtensionRegistryProperty);
        set => SetValue(ExtensionRegistryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="EmbedFactory"/>.</summary>
    public static readonly DependencyProperty EmbedFactoryProperty =
        DependencyProperty.Register(nameof(EmbedFactory), typeof(IMarkdownEmbedFactory),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the block embed factory.</summary>
    public IMarkdownEmbedFactory? EmbedFactory
    {
        get => (IMarkdownEmbedFactory?)GetValue(EmbedFactoryProperty);
        set => SetValue(EmbedFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageResolver"/>.</summary>
    public static readonly DependencyProperty ImageResolverProperty =
        DependencyProperty.Register(nameof(ImageResolver), typeof(IMarkdownImageResolver),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the host-specific resolver consulted before public image loading.</summary>
    public IMarkdownImageResolver? ImageResolver
    {
        get => (IMarkdownImageResolver?)GetValue(ImageResolverProperty);
        set => SetValue(ImageResolverProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageBaseUri"/>.</summary>
    public static readonly DependencyProperty ImageBaseUriProperty =
        DependencyProperty.Register(nameof(ImageBaseUri), typeof(Uri),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

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
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the source document path used by host image resolvers.</summary>
    public string? ImageDocumentPath
    {
        get => (string?)GetValue(ImageDocumentPathProperty);
        set => SetValue(ImageDocumentPathProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsSelectionEnabled"/>.</summary>
    public static readonly DependencyProperty IsSelectionEnabledProperty =
        DependencyProperty.Register(nameof(IsSelectionEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true));

    /// <summary>Gets or sets whether text selection gestures are enabled.</summary>
    public bool IsSelectionEnabled
    {
        get => (bool)GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsCodeBlockCopyEnabled"/>.</summary>
    public static readonly DependencyProperty IsCodeBlockCopyEnabledProperty =
        DependencyProperty.Register(nameof(IsCodeBlockCopyEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

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

    /// <summary>Dependency property backing <see cref="IsCodeBlockSyntaxHighlightingEnabled"/>.</summary>
    public static readonly DependencyProperty IsCodeBlockSyntaxHighlightingEnabledProperty =
        DependencyProperty.Register(nameof(IsCodeBlockSyntaxHighlightingEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets whether code blocks may request syntax highlighting from a configured provider.</summary>
    public bool IsCodeBlockSyntaxHighlightingEnabled
    {
        get => (bool)GetValue(IsCodeBlockSyntaxHighlightingEnabledProperty);
        set => SetValue(IsCodeBlockSyntaxHighlightingEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockSyntaxHighlighter"/>.</summary>
    public static readonly DependencyProperty CodeBlockSyntaxHighlighterProperty =
        DependencyProperty.Register(nameof(CodeBlockSyntaxHighlighter), typeof(ICodeBlockSyntaxHighlighter),
            typeof(MarkdownRendererControl), new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnCodeBlockSyntaxHighlighterChanged()));

    /// <summary>Gets or sets the optional code-block syntax-highlighting provider.</summary>
    public ICodeBlockSyntaxHighlighter? CodeBlockSyntaxHighlighter
    {
        get => (ICodeBlockSyntaxHighlighter?)GetValue(CodeBlockSyntaxHighlighterProperty);
        set => SetValue(CodeBlockSyntaxHighlighterProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockLineNumberMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockLineNumberModeProperty =
        DependencyProperty.Register(nameof(CodeBlockLineNumberMode), typeof(CodeBlockLineNumberMode),
            typeof(MarkdownRendererControl), new PropertyMetadata(CodeBlockLineNumberMode.AutoMultiline, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets when code blocks show line numbers.</summary>
    public CodeBlockLineNumberMode CodeBlockLineNumberMode
    {
        get => (CodeBlockLineNumberMode)GetValue(CodeBlockLineNumberModeProperty);
        set => SetValue(CodeBlockLineNumberModeProperty, value);
    }

    /// <summary>
    /// Gets the latest parsed document facade committed by the renderer.
    /// </summary>
    public MarkdownRenderer.Document.MarkdownDocument Document => _document;

    /// <summary>
    /// Creates a renderer configured for the core CommonMark feature set.
    /// </summary>
    /// <param name="markdown">Initial markdown source text.</param>
    /// <param name="theme">Theme to assign, or null to use the renderer default.</param>
    /// <param name="extensionRegistry">Extension registry to assign, or null to use the renderer default.</param>
    /// <param name="embedFactory">Embed factory to assign, or null to disable hosted block embeds.</param>
    /// <param name="isSelectionEnabled">True to enable text selection.</param>
    /// <param name="isCodeBlockCopyEnabled">True to show copy buttons on code blocks.</param>
    /// <param name="codeBlockSyntaxHighlighter">Optional code-block syntax-highlighting provider.</param>
    /// <param name="codeBlockCopyButtonLabel">Accessible label and tooltip for code-block copy buttons, or null for the localized default.</param>
    /// <param name="codeBlockCopiedButtonLabel">Accessible label and tooltip after copy succeeds, or null for the localized default.</param>
    /// <param name="imageResolver">Optional host-specific image resolver used before public URI loading.</param>
    /// <param name="imageBaseUri">Optional base URI used to resolve relative image sources.</param>
    /// <param name="imageDocumentPath">Optional source document path used by host image resolvers.</param>
    /// <returns>A new configured renderer control.</returns>
    public static MarkdownRendererControl CreateDefault(
        string? markdown = null,
        MarkdownTheme? theme = null,
        MarkdownExtensionRegistry? extensionRegistry = null,
        IMarkdownEmbedFactory? embedFactory = null,
        bool isSelectionEnabled = true,
        bool isCodeBlockCopyEnabled = true,
        ICodeBlockSyntaxHighlighter? codeBlockSyntaxHighlighter = null,
        string? codeBlockCopyButtonLabel = null,
        string? codeBlockCopiedButtonLabel = null,
        IMarkdownImageResolver? imageResolver = null,
        Uri? imageBaseUri = null,
        string? imageDocumentPath = null)
        => new MarkdownRendererControlBuilder()
            .WithMarkdown(markdown)
            .WithTheme(theme)
            .WithExtensionRegistry(extensionRegistry)
            .WithEmbedFactory(embedFactory)
            .WithImageResolver(imageResolver)
            .WithImageBaseUri(imageBaseUri)
            .WithImageDocumentPath(imageDocumentPath)
            .WithSelectionEnabled(isSelectionEnabled)
            .WithCodeBlockCopyEnabled(isCodeBlockCopyEnabled)
            .WithCodeBlockCopyButtonLabel(codeBlockCopyButtonLabel)
            .WithCodeBlockCopiedButtonLabel(codeBlockCopiedButtonLabel)
            .WithCodeBlockSyntaxHighlighter(codeBlockSyntaxHighlighter)
            .Build();

    internal MarkdownLinkPeer GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run)
    {
        // GetValue is atomic for concurrent UIA callers and avoids the
        // TryGetValue+Add race. The factory captures the *current* parent
        // peer; this is acceptable because the parent peer's bounding-rect
        // computation only reads through to the live owner control + box,
        // so a "stale" parent reference still resolves to the right rect.
        return _linkPeerCache.GetValue(run, r => new MarkdownLinkPeer(this, parent, r));
    }

    internal bool IsKeyboardFocusOnLink(LinkRun run)
    {
        return TryGetFocusedLink(out _, out var focusedRun) &&
               ReferenceEquals(focusedRun, run);
    }

    internal bool HasKeyboardFocusOnPaintedLink => TryGetFocusedLink(out _, out _);

    internal bool FocusLinkFromAutomation(LinkRun run)
    {
        if (!TryGetFocusableIndexForLink(run, out var index))
            return Focus(FocusState.Programmatic);

        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        ScrollFocusedItemIntoView();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusDocumentFromAutomation()
    {
        _suppressNextFocusPromotion = true;
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
        if (run is null) return;
        var args = new MarkdownLinkClickEventArgs(run.Url, run.Title);
        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => LinkClick?.Invoke(this, args));
        }
        else
        {
            LinkClick?.Invoke(this, args);
        }
    }

    /// <summary>Raised when the user activates a non-internal markdown link.</summary>
    public event EventHandler<MarkdownLinkClickEventArgs>? LinkClick;

    /// <summary>
    /// Raised after every embed realisation pass (initial layout commit,
    /// scroll, resize). Subscribers can use it to surface
    /// <see cref="RealizedEmbedCount"/> for diagnostics or UI-automation
    /// tests without polluting the control's UIA surface.
    /// </summary>
    public event EventHandler? EmbedsRealizationChanged;

    /// <summary>Current vertical scroll offset of the host scroll viewer.</summary>
    internal double CurrentScrollOffsetY => _scroll?.VerticalOffset ?? 0;

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

    /// <summary>
    /// Last committed theme snapshot. Used by automation peers to expose UIA
    /// text attributes that match the pixels currently painted on the canvas.
    /// </summary>
    internal Theming.ThemeSnapshot? CurrentThemeSnapshot => _themeSnapshot;

    /// <summary>
    /// Number of currently-realised hosted embed elements (block + inline).
    /// Exposed for UI-automation tests that want to validate virtualisation
    /// without relying on heuristic descendant couits.
    /// </summary>
    public int RealizedEmbedCount
    {
        get
        {
            int n = 0;
            foreach (var p in _embedPlans) if (p.Realized is not null) n++;
            return n;
        }
    }

    private Button CreateCodeBlockCopyButton()
    {
        var button = new Button
        {
            Content = new SymbolIcon(Symbol.Copy),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(button, "MarkdownCodeBlockCopyButton");
        SetCodeBlockCopyButtonState(button, copied: false);
        return button;
    }

    private string ResolvedCodeBlockCopyButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopyButtonLabel)
            ? MarkdownLocalizedStrings.CodeBlockCopyAutomationName
            : CodeBlockCopyButtonLabel!;

    private string ResolvedCodeBlockCopiedButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopiedButtonLabel)
            ? MarkdownLocalizedStrings.CodeBlockCopied
            : CodeBlockCopiedButtonLabel!;

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

    private void UpdateCodeBlockCopyButtonLabels()
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is Button button)
                SetCodeBlockCopyButtonState(button, copied: false);
        }
    }

    private void CopyCodeBlockToClipboard(CodeBlockActionPlan plan)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(plan.Box.CodeText);
            Clipboard.SetContent(package);
            plan.ShowCopiedFeedback(this);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block copy failed: {ex.Message}");
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
        if (_scroll is null)
        {
            rect = default;
            return false;
        }

        rect = new Windows.Foundation.Rect(0, _scroll.VerticalOffset, ActualWidth, _scroll.ViewportHeight);
        return true;
    }

    internal void ScrollDocumentRectIntoView(Windows.Foundation.Rect rect, bool alignToTop)
    {
        if (_scroll is null) return;
        const double margin = 24.0;
        double target = alignToTop
            ? rect.Top
            : rect.Top < _scroll.VerticalOffset + margin
                ? rect.Top - margin
                : rect.Bottom > _scroll.VerticalOffset + _scroll.ViewportHeight - margin
                    ? rect.Bottom - _scroll.ViewportHeight + margin
                    : _scroll.VerticalOffset;
        _scroll.ChangeView(null, Math.Max(0, target), null, disableAnimation: false);
    }

    /// <summary>Initializes a new markdown renderer control.</summary>
    public MarkdownRendererControl()
    {
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
            if (IsLoaded) RequestRebuild();
        });
        BuildVisualTree();
    }

    private void BuildVisualTree()
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
        _root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _canvas = new CanvasVirtualControl
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
        _root.Children.Add(_canvas);
        _root.Children.Add(_overlay);
        _scroll.Content = _root;

        _canvas.RegionsInvalidated += OnRegionsInvalidated;
        _canvas.CreateResources += (_, _) => RequestRebuild(); // rebuild layouts after GPU device loss/recreation
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerExited += OnPointerExited;
        _canvas.PointerCanceled += OnPointerCanceledOrCaptureLost;
        _canvas.PointerCaptureLost += OnPointerCanceledOrCaptureLost;
        _canvas.RightTapped += OnRightTapped;
        GettingFocus += OnGettingFocus;
        GotFocus += OnGotFocus;
        LosingFocus += OnLosingFocus;
        KeyDown += OnKeyDown;

        Content = _scroll;
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        if (!ReferenceEquals(e.NewFocusedElement, this))
            return;

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

        if (_suppressNextFocusPromotion)
        {
            _suppressNextFocusPromotion = false;
            _lastFocusEntryWasKeyboardTraversal = false;
            _lastFocusEntryWasKeyboardInput = false;
            return;
        }

        if (!_lastFocusEntryWasKeyboardTraversal &&
            !_lastFocusEntryWasKeyboardInput &&
            FocusState != FocusState.Keyboard)
            return;

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
        ClearCodeBlockHighlightCache();
        RequestRebuild();
    }

    private void OnCodeBlockSyntaxHighlighterChanged()
    {
        ClearCodeBlockHighlightCache();
        RequestRebuild();
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

            if (!IsPointerOverCanvasTextSurface(e, out var canvasPoint) || IsPointOverEmbed(canvasPoint))
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

        if (CopySelectionToClipboard())
            e.Handled = true;
    }

    /// <summary>
    /// Copies the active selection to the clipboard.
    /// </summary>
    /// <param name="options">Optional copy format options. Defaults preserve exact markdown source as plain text and add HTML.</param>
    /// <returns>True when a selection was copied successfully.</returns>
    public bool CopySelectionToClipboard(MarkdownCopyOptions? options = null)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !_selection.IsActive)
            return false;

        string? renderedText = null;
        if ((options ?? MarkdownCopyOptions.Default).PlainTextMode == MarkdownPlainTextCopyMode.RenderedText)
            renderedText = GetRenderedSelectionText(snapshot, _selection.Range);

        return MarkdownClipboardWriter.Copy(snapshot.SourceMap, _selection.Range, options, renderedText);
    }

    private static string GetRenderedSelectionText(LayoutSnapshot snapshot, DocumentRange range)
    {
        var semantic = MarkdownSemanticDocument.Build(snapshot);
        var normalized = range.Normalized();
        int start = semantic.TextOffsetFromDocumentPosition(normalized.Start);
        int end = semantic.TextOffsetFromDocumentPosition(normalized.End);
        if (end < start)
            (start, end) = (end, start);

        start = Math.Clamp(start, 0, semantic.Text.Length);
        end = Math.Clamp(end, start, semantic.Text.Length);
        return semantic.Text.Substring(start, end - start);
    }

    internal void ClearSelectionFromCoordinator()
    {
        ClearSelectionForExternalInteraction();
    }

    private void ClearSelectionForExternalInteraction(bool resetClickTracking = true)
    {
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
        if (_selection.IsActive)
            MarkdownSelectionCoordinator.ClearSelectionsExcept(this);

        UpdateSelectionOverlay();
    }

    private void OnLoadedInternal()
    {
        _isUnloaded = false;
        MarkdownSelectionCoordinator.Register(this);
        RegisterSelectionDismissalHook();
        EnsureThemeSettingsSubscription();
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
        RequestRebuild();
    }

    private void OnUnloaded()
    {
        // Set before any unsubscription so dispatcher-queued lambdas
        // (e.g. from OnImageLoadCompleted) that are already in-flight know
        // not to call RequestRebuild after we've torn down.
        _isUnloaded = true;
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
        if (_themeSettings is not null)
        {
            _themeSettings.Changed -= OnThemeSettingsChanged;
            _themeSettings = null;
        }
        UnregisterSelectionDismissalHook();
        MarkdownSelectionCoordinator.Unregister(this);
        // Cancel and dispose the CTS. At unload no new RequestRebuild can be called
        // (the control is being torn down), so there is no concurrent ContinueWith
        // disposal race. Disposing explicitly avoids leaking the WaitHandle until GC.
        var oldCts = _pipelineCts;
        _pipelineCts = null;
        oldCts?.Cancel();
        oldCts?.Dispose();
        _rebuildQueued = false;
        _hasPendingRebuild = false;
        _pendingRebuildReason = RebuildReason.Restyle;
        var oldHighlightCts = _codeBlockHighlightCts;
        _codeBlockHighlightCts = null;
        oldHighlightCts?.Cancel();
        oldHighlightCts?.Dispose();
        _codeBlockHighlightGeneration++;
        _codeBlockHighlightInFlight.Clear();
        // Unsubscribe scroll handler so scroll-inertia events after visual-tree
        // removal don't fire OnScrollViewChanged on a partially-torn-down control.
        if (_scroll is not null) _scroll.ViewChanged -= OnScrollViewChanged;
        // Unsubscribe all in-flight image load handlers before clearing the plan
        // list; otherwise an async load completing after unload fires
        // OnImageLoadCompleted → RequestRebuild on a torn-down control, causing a
        // zombie rebuild cycle that re-subscribes handlers indefinitely.
        foreach (var img in _imagePlans) img.LoadCompleted -= OnImageLoadCompleted;
        // Tear down embed plans before clearing the overlay so block embed
        // factories get RecycleBlock callbacks and inline embeds release
        // their Run.RealizedElement references — otherwise hosted controls
        // and their event handlers would leak past detach.
        DerealizeAllEmbeds();
        _embedPlans.Clear();
        _codeBlockActionPlans.Clear();
        _imagePlans.Clear();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        SetSelectionDragShieldActive(false);
        // Release native resources & hosted embeds so re-attaching the
        // control to a new visual parent doesn't leak DirectWrite layouts
        // or keep stale FrameworkElements alive.
        _overlay?.Children.Clear();
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
        if (_selectionAdorner is not null)
        {
            _selectionAdorner.Draw -= OnSelectionAdornerDraw;
            _selectionAdorner = null;
        }
        _focusRing = null; // evicted from overlay above; lazily re-created on re-attach
        var snap = _snapshot;
        _snapshot = null;
        snap?.Dispose();
    }

    private void OnThemeChanged()
    {
        // t.Invalidate() fires Theme.Changed → OnThemeRevisionChanged → RequestRebuild.
        // Do NOT call RequestRebuild() here again — that would start two simultaneous
        // builds and immediately cancel the first one on every theme change.
        if (Theme is { } t) t.Invalidate();
        else RequestRebuild(RebuildReason.Restyle); // no Theme object: must trigger rebuild directly
    }

    private void OnThemeSettingsChanged(ThemeSettings sender, object args)
    {
        var dq = DispatcherQueue;
        if (dq is null) return;
        if (dq.HasThreadAccess)
        {
            if (IsLoaded) OnThemeChanged();
            return;
        }

        dq.TryEnqueue(() =>
        {
            if (IsLoaded) OnThemeChanged();
        });
    }

    private void EnsureThemeSettingsSubscription()
    {
        if (_themeSettings is not null) return;
        try
        {
            if (XamlRoot?.ContentIslandEnvironment is null) return;
            _themeSettings = ThemeSettings.CreateForWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            _themeSettings.Changed += OnThemeSettingsChanged;
        }
        catch { }
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
        // Theme.Invalidate() was called externally (consumer mutated overrides).
        // Rebuild — but do NOT call Theme.Invalidate again, that would loop.
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

    private void RequestRebuild(RebuildReason reason)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => RequestRebuild(reason));
            return;
        }

        if (_isUnloaded)
            return;

        _pendingRebuildReason = MergeRebuildReason(_pendingRebuildReason, reason);
        _hasPendingRebuild = true;

        if (!IsLoaded || _canvas is null || _overlay is null || _root is null)
            return;

        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        if (dispatcher is null)
        {
            ProcessQueuedRebuild();
            return;
        }

        if (!dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ProcessQueuedRebuild))
        {
            _rebuildQueued = false;
        }
    }

    private static RebuildReason MergeRebuildReason(RebuildReason current, RebuildReason incoming)
        => current == RebuildReason.Full || incoming == RebuildReason.Full
            ? RebuildReason.Full
            : RebuildReason.Restyle;

    private void ProcessQueuedRebuild()
    {
        _rebuildQueued = false;
        if (_isUnloaded || !IsLoaded || !_hasPendingRebuild)
            return;

        var reason = _pendingRebuildReason;
        _pendingRebuildReason = RebuildReason.Restyle;
        _hasPendingRebuild = false;

        ResetTransientInteractionState(clearSelection: true);

        // Cancel the in-flight build. Dispose is deferred to ContinueWith so the
        // in-flight task (which may still be executing ct.Register() callbacks
        // inside Task.Run/TaskScheduler internals) doesn't encounter a disposed
        // CancellationTokenSource mid-flight.
        var oldCts = _pipelineCts;
        oldCts?.Cancel();
        _pipelineCts = new CancellationTokenSource();
        var cts = _pipelineCts;
        _ = RebuildAsync(cts.Token, reason).ContinueWith(t =>
        {
            // Dispose the superseded CTS now that its task has fully completed:
            // no more ct.Register() calls can fire on it.
            oldCts?.Dispose();
            if (t.IsFaulted)
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild faulted: {t.Exception}");
        }, TaskScheduler.Default);
    }

    private void ResetTransientInteractionState(bool clearSelection)
    {
        _pointerSession = default;
        _selectionAnchor = null;
        _clickMode = ClickMode.Single;
        _dragAnchorStart = default;
        _dragAnchorEnd = default;
        _consecutiveClickCount = 0;
        _lastPressTickMs = 0;
        _lastPressPoint = default;
        SetSelectionDragShieldActive(false);
        HideAbbreviationTooltip();

        if (_snapshot is { } snapshot)
        {
            foreach (var block in snapshot.Blocks)
                ClearHover(block);
        }

        _lastHoveredRun = null;
        _lastHoveredBox = null;
        SetCursorShape(null);

        try { _canvas?.ReleasePointerCaptures(); } catch { }

        if (!clearSelection)
            return;

        _selection.Clear();
        _selectionAdornerRects.Clear();
        try { _selectionAdorner?.Invalidate(); } catch { }
    }

    private async Task RebuildAsync(CancellationToken ct, RebuildReason reason)
    {
        try
        {
            await RebuildInternalAsync(ct, reason).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* expected – a new build was requested */ }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild failed: {ex}");
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

    private async Task RebuildInternalAsync(CancellationToken ct, RebuildReason reason)
    {
        if (_canvas is null || _overlay is null || _root is null) return;
        var width = (float)Math.Max(50, ActualWidth);
        _lastWidth = width;

        var registry = ExtensionRegistry ?? _defaultRegistry;
        var source = Markdown ?? string.Empty;

        if (ct.IsCancellationRequested)
            return;

        var parsed = await GetParsedMarkdownAsync(source, registry, ct).ConfigureAwait(true);
        if (parsed is null || ct.IsCancellationRequested)
            return;

        var sourceMap = new MarkdownSourceMap(parsed.SourceText);
        var theme = Theme ?? _defaultTheme;
        var themeSnapshot = new ThemeResolver(this, theme).CreateSnapshot();
        // Capture the resolved surface color on the UI thread. In high
        // contrast this is the native Window color, not a light/dark token.
        _canvasBackground = themeSnapshot.SurfaceColor;
        // Use the shared CanvasDevice (always available, no visual-tree required).
        // CanvasVirtualControl only has a device after CreateResources fires, so
        // passing _canvas directly would crash if layout runs before first draw.
        var device = CanvasDevice.GetSharedDevice();
        // Capture the host's rasterization scale so raster-fallback image
        // paths (e.g. SvgSkiaRasterizer) render at device-pixel resolution.
        // XamlRoot is null until the control is loaded; default to 1.0.
        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        var ctx = new MarkdownLayoutContext(device, themeSnapshot, sourceMap, registry, FlowDirection, DispatcherQueue)
        {
            RasterizationScale = rasterScale,
            // Layout runs off the UI thread and its result is checked against
            // the rebuild token before commit. Keep the layout context itself
            // non-canceling so superseded navigations don't flood the debugger
            // with first-chance OperationCanceledException/TaskCanceledException
            // noise from every nested block and image.
            CancellationToken = CancellationToken.None,
            IsCodeBlockCopyEnabled = IsCodeBlockCopyEnabled,
            CodeBlockLineNumberMode = CodeBlockLineNumberMode,
            ImageResolver = ImageResolver,
            ImageBaseUri = ImageBaseUri,
            ImageDocumentPath = ImageDocumentPath,
        };
        var builder = new LayoutBuilder(ctx, EmbedFactory);

        if (ct.IsCancellationRequested)
            return;

        double viewportTop = _scroll?.VerticalOffset ?? 0;
        double viewportHeight = _scroll?.ViewportHeight > 0
            ? _scroll.ViewportHeight
            : Math.Max(ActualHeight, 600);
        // Lazy band extension currently runs synchronously from scroll/paint
        // events. Keep custom block embed measurement on the background build
        // path so IMarkdownEmbedFactory.MeasureHeight never runs on the UI
        // dispatcher thread.
        bool useLazyLayout = EmbedFactory is null && parsed.Document.Count >= LazyLayoutBlockThreshold;
        var snapshot = await Task.Run(
            () => BuildSnapshotOrNullOnCancellation(
                builder,
                parsed.Document,
                width,
                viewportTop,
                viewportHeight,
                useLazyLayout,
                CancellationToken.None),
            CancellationToken.None).ConfigureAwait(true);
        if (snapshot is null || ct.IsCancellationRequested)
        {
            snapshot?.Dispose();
            return;
        }

        // From this point the snapshot holds GPU-side CanvasTextLayout objects.
        // If we are cancelled before committing, dispose it to avoid a native-memory leak.
        // `committed` tracks whether the snapshot was written to _snapshot; the catch
        // block must only dispose it if not yet committed (post-commit _snapshot owns it).
        bool committed = false;
        try
        {
        if (ct.IsCancellationRequested)
            return;

        // Scroll anchoring: capture the current read position before the canvas
        // height changes.  If the user has scrolled down, we identify the first
        // visible block (its top edge closest to viewport top) and how far it is
        // from the viewport top.  After committing the new layout we restore the
        // same offset so content above the fold shifting (e.g. an image loading)
        // doesn't jump the reader's position.
        (int? BlockIndex, double OffsetFromTop, double OldOffset, double OldHeight)? scrollAnchor = null;
        if (_scroll is { VerticalOffset: > 0 } scrollSnap && _snapshot is { } prevSnap)
        {
            double vTop = scrollSnap.VerticalOffset;
            scrollAnchor = (null, 0, vTop, prevSnap.Size.Height);
            foreach (var b in prevSnap.Blocks)
            {
                if (b.Bounds.Bottom < vTop) continue;
                scrollAnchor = (b.BlockIndex, b.Bounds.Top - vTop, vTop, prevSnap.Size.Height);
                break;
            }
        }

        if (ct.IsCancellationRequested)
            return;

        // Atomically swap snapshots, then dispose the old one so its
        // CanvasTextLayout / placeholder handles are released.
        var old = _snapshot;
        _snapshot = snapshot;
        committed = true;
        _document = MarkdownRenderer.Document.MarkdownDocument.FromParsed(parsed.SourceText, parsed.Document);
        // Update _themeSnapshot after commit so it always reflects the committed
        // theme. Updating before the await yields the UI thread where UpdateFocusRing
        // reads _themeSnapshot against stale _snapshot/_focusableItems from the old build.
        _themeSnapshot = themeSnapshot;
        old?.Dispose();
        // Clear stale hover references so OnPointerExited/OnPointerMoved after the
        // rebuild don't use Bounds from the now-disposed old snapshot for invalidation.
        _lastHoveredRun = null;
        _lastHoveredBox = null;
        ApplySnapshotSize(snapshot);

        // Restore scroll anchor: find the anchor block's new Y in the new layout
        // and adjust the scroll offset so the user's read position is unchanged.
        if (scrollAnchor is { } anchor && _scroll is { } scrollRestore)
        {
            double? newY = null;
            if (anchor.BlockIndex is { } anchorBlock)
            {
                foreach (var b in snapshot.Blocks)
                {
                    if (b.BlockIndex == anchorBlock)
                    {
                        newY = b.Bounds.Top - anchor.OffsetFromTop;
                        break;
                    }
                }

                newY ??= FindNearestScrollAnchor(snapshot, anchorBlock, anchor.OffsetFromTop);
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

        // UI thread: collect embed plans (don't realise yet), hook image
        // LoadCompleted, then realise only embeds that fall in the current
        // viewport. Hooking _scroll.ViewChanged drives subsequent realisation
        // as the user scrolls.
        DerealizeAllEmbeds();
        SetSelectionDragShieldActive(false);
        _overlay.Children.Clear();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();
        _embedPlans.Clear();
        _codeBlockActionPlans.Clear();
        foreach (var img in _imagePlans) img.LoadCompleted -= OnImageLoadCompleted;
        _imagePlans.Clear();
        // Identities change across rebuild even when the count happens to
        // match — reset so the first post-rebuild realisation always fires.
        _lastFiredRealizedCount = -1;
        _selectionAdornerRects.Clear();
        EnsureSelectionAdorner();
        _selection.Clear();             // stale selection no longer valid after re-layout
        _focusedItemIndex = -1;         // selection/focus stale after re-layout
        _focusResumeItemIndex = -1;
        _focusRing = null;              // evicted from overlay; will be lazily re-created on next Tab
        _focusableItems = snapshot.CollectFocusableItems();
        RebuildRealizationPlans(snapshot, preserveRealized: false);
        if (_scroll is not null)
        {
            _scroll.ViewChanged -= OnScrollViewChanged;
            _scroll.ViewChanged += OnScrollViewChanged;
        }
        RealizeVisibleEmbeds();
        RestartCodeBlockHighlighting();
        ScheduleVisibleCodeBlockHighlighting();
        UpdateSelectionAdornerViewport();

        _canvas.Invalidate();
        } // end of snapshot try-block
        catch
        {
            // Only dispose if we never committed snapshot to _snapshot.
            // After commit, _snapshot owns it; disposing here would use-after-free.
            if (!committed) snapshot.Dispose();
            throw;
        }
    }

    private static LayoutSnapshot? BuildSnapshotOrNullOnCancellation(
        LayoutBuilder builder,
        Markdig.Syntax.MarkdownDocument document,
        float width,
        double viewportTop,
        double viewportHeight,
        bool useLazyLayout,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        try
        {
            return useLazyLayout
                ? builder.BuildLazy(document, width, viewportTop, viewportHeight, LazyLayoutOverscanPx, ct)
                : builder.Build(document, width, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        EnsureLazyLayoutForViewport();
        UpdateSelectionAdornerViewport();
        _selectionAdorner?.Invalidate();
        // Run a final realisation pass after intermediate-view bursts settle
        // so we don't thrash during fling/inertia. ViewChanged with
        // IsIntermediate=false fires at the end of inertia; we also realise on
        // intermediate ticks to keep the visual current.
        RealizeVisibleEmbeds();
        ScheduleVisibleCodeBlockHighlighting();
    }

    private void RestartCodeBlockHighlighting()
    {
        var old = _codeBlockHighlightCts;
        old?.Cancel();
        old?.Dispose();
        _codeBlockHighlightCts = new CancellationTokenSource();
        _codeBlockHighlightGeneration++;
        _codeBlockHighlightInFlight.Clear();
    }

    private void ScheduleVisibleCodeBlockHighlighting()
    {
        if (!IsCodeBlockSyntaxHighlightingEnabled ||
            CodeBlockSyntaxHighlighter is not { } highlighter ||
            _snapshot is not { } snapshot ||
            _scroll is null ||
            _themeSnapshot is not { } theme ||
            theme.IsHighContrast)
        {
            return;
        }

        var cts = _codeBlockHighlightCts;
        if (cts is null || cts.IsCancellationRequested)
            return;

        var variant = ResolveCodeBlockThemeVariant(theme);
        int generation = _codeBlockHighlightGeneration;
        int providerIdentity = RuntimeHelpers.GetHashCode(highlighter);
        int providerRevision = highlighter.Revision;
        bool appliedCached = false;
        double highlightTop = _scroll.VerticalOffset - CodeBlockHighlightOverscanPx;
        double highlightBottom = _scroll.VerticalOffset + _scroll.ViewportHeight + CodeBlockHighlightOverscanPx;
        foreach (var block in EnumerateCodeBlocks(snapshot))
        {
            if (!IsCodeBlockInHighlightBand(block, highlightTop, highlightBottom))
                continue;

            if (block.CodeText.Length > 200_000 || block.LineCount > 5_000)
                continue;

            var key = CreateHighlightCacheKey(block, variant, providerIdentity, providerRevision);
            if (_codeBlockHighlightCache.TryGetValue(key, out var cached))
            {
                block.ApplySyntaxHighlighting(cached.Spans);
                appliedCached = true;
                continue;
            }

            if (!_codeBlockHighlightInFlight.Add(key))
                continue;

            _ = HighlightCodeBlockAsync(snapshot, block, key, variant, highlighter, providerIdentity, providerRevision, generation, cts.Token);
        }

        if (appliedCached)
            _canvas?.Invalidate();
    }

    private async Task HighlightCodeBlockAsync(
        LayoutSnapshot snapshot,
        Layout.Boxes.CodeBlockBox block,
        CodeBlockHighlightCacheKey key,
        CodeBlockThemeVariant variant,
        ICodeBlockSyntaxHighlighter highlighter,
        int providerIdentity,
        int providerRevision,
        int generation,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            DispatcherQueue.TryEnqueue(() => RemoveCodeBlockHighlightInFlight(key, generation));
            return;
        }

        try
        {
            await _codeBlockHighlightSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (token.IsCancellationRequested)
                    return;

                var request = new CodeBlockHighlightRequest(block.CodeLanguage, block.CodeText, variant, token);
                var result = await highlighter.HighlightAsync(request).ConfigureAwait(false)
                    ?? CodeBlockHighlightResult.Empty;
                if (token.IsCancellationRequested)
                    return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded || token.IsCancellationRequested || !ReferenceEquals(_snapshot, snapshot))
                        return;
                    _codeBlockHighlightCache.Set(key, result);
                    RemoveCodeBlockHighlightInFlight(key, generation);
                    ApplyCodeBlockHighlightResult(snapshot, key, variant, providerIdentity, providerRevision, result);
                    _canvas?.Invalidate();
                    ScheduleVisibleCodeBlockHighlighting();
                });
            }
            finally
            {
                _codeBlockHighlightSemaphore.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DispatcherQueue.TryEnqueue(() => RemoveCodeBlockHighlightInFlight(key, generation));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block highlighting failed: {ex.Message}");
            DispatcherQueue.TryEnqueue(() => RemoveCodeBlockHighlightInFlight(key, generation));
        }
    }

    private void RemoveCodeBlockHighlightInFlight(CodeBlockHighlightCacheKey key, int generation)
    {
        if (generation == _codeBlockHighlightGeneration)
            _codeBlockHighlightInFlight.Remove(key);
    }

    private void ApplyCodeBlockHighlightResult(
        LayoutSnapshot snapshot,
        CodeBlockHighlightCacheKey key,
        CodeBlockThemeVariant variant,
        int providerIdentity,
        int providerRevision,
        CodeBlockHighlightResult result)
    {
        foreach (var candidate in EnumerateCodeBlocks(snapshot))
        {
            if (CreateHighlightCacheKey(candidate, variant, providerIdentity, providerRevision).Equals(key))
                candidate.ApplySyntaxHighlighting(result.Spans);
        }
    }

    private static bool IsCodeBlockInHighlightBand(Layout.Boxes.CodeBlockBox block, double top, double bottom)
        => block.Bounds.Bottom >= top && block.Bounds.Top <= bottom;

    private static IEnumerable<Layout.Boxes.CodeBlockBox> EnumerateCodeBlocks(LayoutSnapshot snapshot)
    {
        foreach (var block in snapshot.GetMeasuredTopLevelBlocks())
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
        => new(block.CodeLanguage, Fnv1A64(block.CodeText), block.CodeText.Length, variant, providerIdentity, providerRevision);

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

    private void ApplySnapshotSize(LayoutSnapshot snapshot)
    {
        if (_canvas is null || _root is null || _overlay is null) return;
        _canvas.Width = Math.Max(1, snapshot.Size.Width);
        _canvas.Height = Math.Max(1, snapshot.Size.Height);
        _root.Width = _canvas.Width;
        _root.Height = _canvas.Height;
        _overlay.Width = _canvas.Width;
        _overlay.Height = _canvas.Height;
        SetSelectionDragShieldActive(_selectionAnchor is not null);
    }

    private static (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(LayoutSnapshot snapshot, double verticalOffset)
    {
        if (verticalOffset <= 0)
            return null;

        foreach (var b in snapshot.Blocks)
        {
            if (b.Bounds.Bottom < verticalOffset) continue;
            return (b.BlockIndex, b.Bounds.Top - verticalOffset);
        }

        return null;
    }

    private void RestoreScrollAnchor(LayoutSnapshot snapshot, (int BlockIndex, double OffsetFromTop)? anchor)
    {
        if (anchor is not { } value || _scroll is null)
            return;

        foreach (var b in snapshot.Blocks)
        {
            if (b.BlockIndex != value.BlockIndex)
                continue;

            double target = Math.Max(0, b.Bounds.Top - value.OffsetFromTop);
            if (Math.Abs(target - _scroll.VerticalOffset) >= 0.5)
                _scroll.ChangeView(null, target, null, disableAnimation: true);
            return;
        }
    }

    private bool EnsureLazyLayoutForViewport()
    {
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.IsLazyLayoutEnabled || _scroll is null)
            return false;

        var anchor = CaptureScrollAnchor(snapshot, _scroll.VerticalOffset);
        var commit = snapshot.EnsureMeasuredViewport(
            _scroll.VerticalOffset,
            _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : Math.Max(ActualHeight, 600),
            LazyLayoutOverscanPx,
            CancellationToken.None);

        if (!commit.Changed)
            return false;

        ApplySnapshotSize(snapshot);
        RestoreScrollAnchor(snapshot, anchor);
        RebuildRealizationPlans(snapshot, preserveRealized: true);
        _focusableItems = snapshot.CollectFocusableItems();
        UpdateSelectionAdornerViewport();
        UpdateFocusRing();
        _canvas?.Invalidate();
        return true;
    }

    private void DerealizeAllEmbeds()
    {
        foreach (var plan in _embedPlans)
        {
            if (plan.Realized is not null) plan.Derealize(this);
        }
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is not null) plan.Derealize(this);
        }
    }

    private void RebuildRealizationPlans(LayoutSnapshot snapshot, bool preserveRealized)
    {
        var oldPlans = preserveRealized ? _embedPlans.ToArray() : Array.Empty<EmbedPlan>();
        var oldActionPlans = preserveRealized ? _codeBlockActionPlans.ToArray() : Array.Empty<CodeBlockActionPlan>();

        foreach (var img in _imagePlans)
            img.LoadCompleted -= OnImageLoadCompleted;
        _imagePlans.Clear();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();
        _embedPlans.Clear();
        _codeBlockActionPlans.Clear();

        foreach (var b in snapshot.GetMeasuredTopLevelBlocks())
            CollectEmbedPlans(b);

        if (oldPlans.Length > 0)
        {
            var adopted = new HashSet<EmbedPlan>();
            foreach (var newPlan in _embedPlans)
            {
                foreach (var oldPlan in oldPlans)
                {
                    if (adopted.Contains(oldPlan) || oldPlan.Realized is null)
                        continue;
                    if (!newPlan.IsSameLogicalEmbed(oldPlan))
                        continue;

                    newPlan.Realized = oldPlan.Realized;
                    oldPlan.Realized = null;
                    AttachRealizedElement(newPlan);
                    newPlan.UpdatePlacement();
                    adopted.Add(oldPlan);
                    break;
                }
            }

            foreach (var oldPlan in oldPlans)
            {
                if (oldPlan.Realized is not null)
                    oldPlan.Derealize(this);
            }
        }

        if (oldActionPlans.Length > 0)
        {
            var adopted = new HashSet<CodeBlockActionPlan>();
            foreach (var newPlan in _codeBlockActionPlans)
            {
                foreach (var oldPlan in oldActionPlans)
                {
                    if (adopted.Contains(oldPlan) || oldPlan.Realized is null)
                        continue;
                    if (!newPlan.IsSameLogicalAction(oldPlan))
                        continue;

                    newPlan.AdoptRealizedFrom(oldPlan, this);
                    adopted.Add(oldPlan);
                    break;
                }
            }

            foreach (var oldPlan in oldActionPlans)
            {
                if (oldPlan.Realized is not null)
                    oldPlan.Derealize(this);
            }
        }

        _lastFiredRealizedCount = -1;
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
        }
    }

    /// <summary>
    /// Realise embeds whose rect intersects the realisation band (viewport +
    /// overscan) and derealise embeds that have left the wider derealisation
    /// band. Called from snapshot commit, scroll, and resize.
    /// </summary>
    internal void RealizeVisibleEmbeds()
    {
        if (_scroll is null) return;
        double top = _scroll.VerticalOffset;
        double bottom = top + _scroll.ViewportHeight;

        // Lazy image loading: trigger EnsureLoading for images within the
        // viewport + LazyImageOverscanPx band.  Images already started (cached
        // or previously triggered) are silently skipped by EnsureLoading().
        double imgLoadTop    = top    - LazyImageOverscanPx;
        double imgLoadBottom = bottom + LazyImageOverscanPx;
        foreach (var img in _imagePlans)
        {
            if (img.Bounds.Bottom >= imgLoadTop && img.Bounds.Top <= imgLoadBottom)
                img.EnsureLoading();
        }

        // Drop old realisation-side caches; they'll be repopulated from realised plans.
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();

        foreach (var plan in _codeBlockActionPlans)
        {
            // Copy buttons are tiny and expected on every code block. Do not
            // virtualize them with hosted embeds: markdown controls often size
            // naturally inside an outer page ScrollViewer, so their internal
            // ScrollViewer may never produce ViewChanged events for lower code
            // blocks.
            plan.Realize(this);

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

        foreach (var plan in _embedPlans)
        {
            double pTop = plan.Rect.Top;
            double pBottom = plan.Rect.Bottom;
            bool inRealize = EmbedVisibility.IsInRealizeBand(pTop, pBottom, top, bottom, EmbedVirtualizationOverscanPx);
            bool inDerealize = EmbedVisibility.IsInDerealizeBand(pTop, pBottom, top, bottom, EmbedVirtualizationDerealizeOverscanPx);
            if (inRealize)
            {
                plan.Realize(this);
            }
            else if (!inDerealize)
            {
                plan.Derealize(this);
            }
            // else: outside realise band but inside derealise band → keep current state (hysteresis).

            // Rebuild hit-rect caches from realised plans.
            if (plan.Realized is not null)
            {
                if (plan is BlockEmbedPlan bp)
                {
                    double left = Math.Round(bp.Box.Bounds.X + bp.Box.Margin.Left);
                    double t = Math.Round(bp.Box.Bounds.Y + bp.Box.Margin.Top);
                    double w = Math.Round(bp.Box.Bounds.Width  - bp.Box.Margin.Left - bp.Box.Margin.Right);
                    double h = Math.Round(bp.Box.Bounds.Height - bp.Box.Margin.Top  - bp.Box.Margin.Bottom);
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
            }
        }

        // Surface the realised count to subscribers (e.g. sample app exposing
        // it via a hidden TextBlock for UI-automation tests) — but only when
        // the count actually changed. Firing on every scroll tick causes a
        // property-change event storm for any UIA listener bound to a Name
        // property mirror in the subscriber's UI.
        int realised = 0;
        foreach (var p in _embedPlans) if (p.Realized is not null) realised++;
        if (realised != _lastFiredRealizedCount)
        {
            _lastFiredRealizedCount = realised;
            try { EmbedsRealizationChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* subscriber threw — swallow to keep scroll pipeline alive. */ }
        }
    }

    private void CollectEmbedPlans(Layout.BlockBox box)
    {
        switch (box)
        {
            case Layout.Boxes.EmbedBox eb:
            {
                double left = eb.Bounds.X + eb.Margin.Left;
                double top  = eb.Bounds.Y + eb.Margin.Top;
                double w = eb.Bounds.Width  - eb.Margin.Left - eb.Margin.Right;
                double h = eb.Bounds.Height - eb.Margin.Top  - eb.Margin.Bottom;
                _embedPlans.Add(new BlockEmbedPlan { Box = eb, Rect = new Rect(left, top, w, h) });
                break;
            }
            case Layout.Boxes.ImageBox ib:
            {
                AddImagePlan(ib);
                break;
            }
            case Layout.Boxes.InlineContainerBox icb:
            {
                foreach (var (run, rect) in icb.EnumerateEmbedRects())
                {
                    _embedPlans.Add(new InlineEmbedPlan { Icb = icb, Run = run, Rect = rect });
                }
                foreach (var (run, _) in icb.EnumerateInlineImageRects())
                {
                    AddImagePlan(run.Image);
                }
                break;
            }
            case Layout.Boxes.CodeBlockBox codeBlock:
            {
                if (codeBlock.IsCopyButtonEnabled && codeBlock.CopyButtonBounds.Width > 0 && codeBlock.CopyButtonBounds.Height > 0)
                    _codeBlockActionPlans.Add(new CodeBlockActionPlan { Box = codeBlock, Rect = codeBlock.CopyButtonBounds, Kind = CodeBlockHostedElementKind.Copy });
                break;
            }
            case Layout.Boxes.ListItemBox lib:
                CollectEmbedPlans(lib.Marker);
                CollectEmbedPlans(lib.Content);
                break;
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) CollectEmbedPlans(cell);
                break;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children) CollectEmbedPlans(c);
                break;
        }
    }

    private void AddImagePlan(Layout.Boxes.ImageBox image)
    {
        if (_imagePlans.Contains(image))
            return;

        image.LoadCompleted -= OnImageLoadCompleted;
        image.LoadCompleted += OnImageLoadCompleted;
        _imagePlans.Add(image);
    }

    private void OnImageLoadCompleted(object? sender, Layout.Boxes.LoadCompletedEventArgs e)
    {
        // CanvasBitmap.LoadAsync continues on a thread-pool thread.  Always
        // marshal to the UI thread — RequestRebuild manipulates the canvas
        // and CTS state, both of which require thread-affinity.  Drop the
        // event silently if we have no dispatcher (control already unloaded).
        var dq = DispatcherQueue;
        if (dq is null) return;
        bool layoutInvalidated = e?.LayoutInvalidated ?? true;
        dq.TryEnqueue(() =>
        {
            // Guard against the TOCTOU window where this lambda was already
            // dispatched before OnUnloaded ran its unsubscription.
            if (_isUnloaded) return;
            if (sender is Layout.Boxes.ImageBox image && !_imagePlans.Contains(image)) return;
            if (layoutInvalidated)
            {
                // Initial load / intrinsic-size change → coalesce through the
                // rebuild path. Subsequent calls cancel the prior CTS.
                RequestRebuild();
            }
            else
            {
                // Paint-only (e.g. SVG re-parsed against current device). Don't
                // rebuild: doing so would dispose the freshly-parsed _svg and
                // start the reparse cycle over again.
                _canvas?.Invalidate();
            }
        });
    }

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        try
        {
            EnsureLazyLayoutForViewport();
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            HandleCanvasDeviceLost(ex);
            return;
        }

        if (_snapshot is null) return;
        var frame = ShakeLogger.NextFrame();
        int regionCount = 0;
        foreach (var region in args.InvalidatedRegions)
        {
            regionCount++;
            if (ShakeLogger.IsEnabled)
                ShakeLogger.LogPaint(
                    "region", regionCount, region.X, region.Y, region.Width, region.Height);
            try
            {
                using var ds = sender.CreateDrawingSession(region);
                // Clear to the theme-appropriate background color so that switching between
                // light and dark mode (or any theme change) fully overwrites old tile content.
                // We use an opaque theme color rather than Colors.Transparent because
                // CanvasVirtualControl may not alpha-composite with the XAML compositor
                // depending on the DirectX swap-chain configuration of the platform; on
                // such configurations transparent pixels show as black rather than letting
                // the XAML background show through.
                ds.Clear(_canvasBackground);
                // Force grayscale text anti-aliasing.  ClearType is *colour-aware*:
                // the same glyph rendered onto a white background versus an
                // alpha-blended selection-tinted background produces subtly
                // different sub-pixel RGB values.  Switching to grayscale makes
                // glyph edges background-independent.
                ds.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
                // Selection is rendered by the separate Win2D adorner, not here;
                // base document tiles are never dirtied during a drag.
                _snapshot.Paint(ds, region);
            }
            catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
            {
                HandleCanvasDeviceLost(ex);
                return;
            }
        }
        _canvasDeviceRecoveryAttempt = 0;
        _canvasDeviceRecoveryQueued = false;
        if (ShakeLogger.IsEnabled)
            ShakeLogger.Log("frame-end",
                $"frame={frame} regions={regionCount} hovered={(_lastHoveredRun is null ? "null" : _lastHoveredRun.GetType().Name)} dragging={_selectionAnchor is not null}");
    }

    private void HandleCanvasDeviceLost(Exception exception)
    {
        if (_canvasDeviceRecoveryQueued || _isUnloaded)
            return;

        _canvasDeviceRecoveryQueued = true;
        _canvasDeviceRecoveryAttempt = Math.Min(_canvasDeviceRecoveryAttempt + 1, 6);
        int delayMs = Math.Min(5000, 250 << (_canvasDeviceRecoveryAttempt - 1));

        MarkdownDiagnostics.WriteLine(
            "[MarkdownRendererControl] Win2D device lost while painting; " +
            $"HRESULT={GraphicsDeviceErrors.FormatHResult(exception.HResult)}. " +
            $"Retrying canvas rebuild in {delayMs}ms.");

        var dispatcher = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs).ConfigureAwait(false); }
            catch { return; }

            dispatcher?.TryEnqueue(() =>
            {
                if (_isUnloaded) return;

                _canvasDeviceRecoveryQueued = false;
                RequestRebuild();
                try { _canvas?.Invalidate(); } catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex)) { }
                try { _selectionAdorner?.Invalidate(); } catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex)) { }
            });
        });
    }

    // ---- Input ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_snapshot is null || _canvas is null) return;
        // Only process left (primary) button presses; right-clicks are handled by
        // OnRightTapped and must not affect the multi-click counter or selection anchor.
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        var pt = e.GetCurrentPoint(_canvas).Position;
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
            if (_focusedItemIndex >= 0) { _focusedItemIndex = -1; UpdateFocusRing(); }
            return;
        }

        // Dismiss any keyboard focus ring — mouse clicks and keyboard nav are
        // separate modalities; clicking anywhere in the document should clear
        // the ring so the focus indicator doesn't linger after the user
        // switches from keyboard to mouse.
        if (_focusedItemIndex >= 0) { _focusedItemIndex = -1; UpdateFocusRing(); }

        // Track consecutive clicks for word (double) and line (triple) selection.
        long nowMs = Environment.TickCount64;
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
        if (_snapshot.HitTest(pt, out var pos))
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
                if (!_canvas.CapturePointer(e.Pointer)) _pointerSession = default;
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
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToBlock(_snapshot, pos);
                // Selection is rendered by the XAML overlay; do not dirty canvas
                // text during mouse-down. Repainting DirectWrite text here causes
                // visible shake on selection starts, especially on the embeds page.
                if (!_canvas.CapturePointer(e.Pointer)) { _pointerSession = default; _selectionAnchor = null; }
                else SetSelectionDragShieldActive(true);
                return;
            }
            if (_consecutiveClickCount == 2)
            {
                // Double-click: select the word under the cursor.
                _clickMode = ClickMode.Word;
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToWord(_snapshot, pos);
                // Selection is rendered by the XAML overlay; do not dirty canvas
                // text during mouse-down.
                if (!_canvas.CapturePointer(e.Pointer)) { _pointerSession = default; _selectionAnchor = null; }
                else SetSelectionDragShieldActive(true);
                return;
            }
            _clickMode = ClickMode.Single;
            _selection.SetAnchor(pos);
            // Selection is rendered by the XAML overlay; do not dirty canvas text
            // for the empty anchor state.
            if (!_canvas.CapturePointer(e.Pointer)) { _pointerSession = default; _selectionAnchor = null; }
            else SetSelectionDragShieldActive(true);
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
            _selection.Clear();
            // No canvas invalidate: selection clear is overlay-only.
        }
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

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_snapshot is null || _canvas is null) return;
        var pt = e.GetCurrentPoint(_canvas).Position;

        // Drag-select.
        if (_selectionAnchor is not null)
        {
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
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.Log("ptr-move-drag-embed",
                        $"px={dragPoint.X:F4} py={dragPoint.Y:F4} pos=blk{embedPos.BlockIndex}/inl{embedPos.InlineIndex}/c{embedPos.CharacterOffset}");
                _selection.ExtendTo(embedPos);
                // No _canvas.Invalidate(): selection is on the XAML overlay.
            }
            else if (_snapshot.HitTest(dragPoint, out var pos))
            {
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.Log("ptr-move-drag",
                        $"px={dragPoint.X:F4} py={dragPoint.Y:F4} pos=blk{pos.BlockIndex}/inl{pos.InlineIndex}/c{pos.CharacterOffset}");
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
                // partial-tile repaiits reveal DirectWrite glyph-position
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
        foreach (var b in _snapshot.Blocks)
        {
            if (FindInlineHover(b, pt) is var h && h.Run is not null)
            {
                hovered = h.Run;
                hoveredBox = h.Box;
                break;
            }
        }

        if (IsLinkedRun(hovered) && hoveredBox?.IsPointInsideRunBounds(hovered!, pt) != true)
        {
            hovered = null;
            hoveredBox = null;
        }

        var hoveredLinkedRun = IsLinkedRun(hovered) ? hovered : null;
        var lastLinkedRun = IsLinkedRun(_lastHoveredRun) ? _lastHoveredRun : null;
        bool linkChanged = !ReferenceEquals(hoveredLinkedRun, lastLinkedRun);
        var wantedShape = hoveredLinkedRun is not null
            ? Microsoft.UI.Input.InputSystemCursorShape.Hand
            : IsSelectionEnabled
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

        EnsureSelectionAdorner();
        UpdateSelectionAdornerViewport();
        try
        {
            _selectionAdorner?.Invalidate();
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            HandleCanvasDeviceLost(ex);
        }
    }

    private Point PrepareSelectionDragPoint(Point point)
    {
        if (_scroll is null || _canvas is null)
            return point;

        double viewportTop = _scroll.VerticalOffset;
        double viewportHeight = _scroll.ViewportHeight;
        double delta = SelectionAutoScroll.ComputeDelta(point.Y, viewportTop, viewportHeight);
        if (Math.Abs(delta) >= 0.5)
        {
            double maxOffset = Math.Max(0, _canvas.ActualHeight - viewportHeight);
            double target = Math.Clamp(viewportTop + delta, 0, maxOffset);
            if (Math.Abs(target - viewportTop) >= 0.5)
            {
                _scroll.ChangeView(null, target, null, disableAnimation: true);
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
                    ? (rightHalf ? 0 : run.Text.Length)
                    : (rightHalf ? run.Text.Length : 0);
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
    /// Win2D child layered above the document text and below hosted controls.
    /// Selection changes only mutate this in-memory rect list and invalidate
    /// the adorner; they never invalidate the base document canvas or mutate
    /// the XAML child tree during a drag.
    /// </summary>
    private void UpdateSelectionOverlay()
    {
        if (_overlay is null) return;

        var snapshot = _snapshot;
        if (snapshot is null || !_selection.IsActive)
        {
            _selectionAdornerRects.Clear();
            _selectionAdorner?.Invalidate();
            return;
        }

        // Snap to *physical pixels*, not DIPs. At fractional DPI (125%, 150%)
        // a DIP edge lands at a fractional physical pixel. The adorner uses the
        // same snapped rectangles for background fill, clipping, diagnostics,
        // and foreground overpaint so all selection pixels move together.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        _selectionAdornerRects.Clear();
        foreach (var rect in _selection.GetHighlightRects(snapshot))
        {
            double pxX = Math.Floor(rect.X * scale);
            double pxY = Math.Floor(rect.Y * scale);
            double pxR = Math.Ceiling((rect.X + rect.Width) * scale);
            double pxB = Math.Ceiling((rect.Y + rect.Height) * scale);
            double x = pxX / scale;
            double y = pxY / scale;
            double w = (pxR - pxX) / scale;
            double h = (pxB - pxY) / scale;
            if (w <= 0 || h <= 0)
                continue;

            _selectionAdornerRects.Add(new Rect(x, y, w, h));

            // Diagnostic: log the *physical-pixel* coords for the first stripe
            // so we can verify they stay rock-stable across drag frames at
            // fractional DPI. If shake reappears these numbers will jitter.
            if (_selectionAdornerRects.Count == 1)
            {
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.LogPaint(
                        "sel-rect-phys", -1, (float)(x * scale), (float)(y * scale),
                        (float)(w * scale), (float)(h * scale));
            }
        }

        EnsureSelectionAdorner();
        UpdateSelectionAdornerViewport();
        _selectionAdorner?.Invalidate();
    }

    private void EnsureSelectionAdorner()
    {
        if (_overlay is null)
            return;

        if (_selectionAdorner is null)
        {
            _selectionAdorner = new CanvasControl
            {
                IsHitTestVisible = false,
                UseLayoutRounding = true,
            };
            _selectionAdorner.Draw += OnSelectionAdornerDraw;
            Canvas.SetZIndex(_selectionAdorner, 0);
        }

        if (VisualTreeHelper.GetParent(_selectionAdorner) is null)
            _overlay.Children.Add(_selectionAdorner);
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

        if (_selectionDragShield is null)
        {
            _selectionDragShield = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                IsHitTestVisible = true,
            };
            _selectionDragShield.PointerMoved += OnPointerMoved;
            _selectionDragShield.PointerReleased += OnPointerReleased;
            _selectionDragShield.PointerCanceled += OnPointerCanceledOrCaptureLost;
            _selectionDragShield.PointerCaptureLost += OnPointerCanceledOrCaptureLost;
            Canvas.SetLeft(_selectionDragShield, 0);
            Canvas.SetTop(_selectionDragShield, 0);
            Canvas.SetZIndex(_selectionDragShield, 4);
        }

        _selectionDragShield.Width = Math.Max(1, _overlay.Width);
        _selectionDragShield.Height = Math.Max(1, _overlay.Height);
        if (VisualTreeHelper.GetParent(_selectionDragShield) is null)
            _overlay.Children.Add(_selectionDragShield);
        _selectionDragShield.Visibility = Visibility.Visible;
    }

    private void UpdateSelectionAdornerViewport()
    {
        if (_selectionAdorner is null)
            return;

        double width = Math.Max(1.0, _scroll?.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth);
        double height = Math.Max(1.0, _scroll?.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight);
        double top = _scroll?.VerticalOffset ?? 0.0;

        if (double.IsNaN(_selectionAdorner.Width) || Math.Abs(_selectionAdorner.Width - width) > 0.5)
            _selectionAdorner.Width = width;
        if (double.IsNaN(_selectionAdorner.Height) || Math.Abs(_selectionAdorner.Height - height) > 0.5)
            _selectionAdorner.Height = height;

        double currentLeft = Canvas.GetLeft(_selectionAdorner);
        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft) > 0.1)
            Canvas.SetLeft(_selectionAdorner, 0);

        double currentTop = Canvas.GetTop(_selectionAdorner);
        if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
            Canvas.SetTop(_selectionAdorner, top);

        _selectionAdornerOffsetY = top;
    }

    private void OnSelectionAdornerDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        args.DrawingSession.Clear(Color.FromArgb(0, 0, 0, 0));

        var snapshot = _snapshot;
        if (snapshot is null)
            return;

        bool hasSelection = _selection.IsActive && _selectionAdornerRects.Count > 0;
        bool hasHoveredLink = _lastHoveredRun is LinkRun && _lastHoveredBox is not null;
        bool hasFocusedLink = TryGetFocusedLink(out _, out _);
        if (!hasSelection && !hasHoveredLink && !hasFocusedLink)
            return;

        if (ShakeLogger.IsEnabled)
            ShakeLogger.Log("sel-adorner-draw",
                $"rects={_selectionAdornerRects.Count} actual={sender.ActualWidth:0.####}x{sender.ActualHeight:0.####} size={sender.Width:0.####}x{sender.Height:0.####}");

        var selectedBackground = _themeSnapshot?.SelectionHighlightColor
                                 ?? Color.FromArgb(0xFF, 0x66, 0xAA, 0xE8);
        var selectedForeground = _themeSnapshot?.SelectionForegroundColor
                                 ?? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var range = _selection.Range.Normalized();
        double yOffset = _selectionAdornerOffsetY;
        var viewport = new Rect(0, yOffset, sender.ActualWidth, sender.ActualHeight);

        args.DrawingSession.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
        args.DrawingSession.Transform = Matrix3x2.CreateTranslation(0, (float)-yOffset);

        // Native text controls effectively cover the old glyph pixels with the
        // selection fill and then draw selected glyphs on top. Doing both in this
        // adorner gives the same visual ordering without repainting document text.
        if (hasSelection)
        {
            foreach (var rect in _selectionAdornerRects)
            {
                if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
                    rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
                {
                    continue;
                }

                args.DrawingSession.FillRectangle(rect, selectedBackground);
            }

            foreach (var rect in _selectionAdornerRects)
            {
                if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
                    rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
                {
                    continue;
                }

                using var layer = args.DrawingSession.CreateLayer(1.0f, rect);
                snapshot.PaintSelectionForeground(args.DrawingSession, range, selectedForeground, rect);
            }
        }

        PaintLinkStateOverlay(args.DrawingSession, viewport);

        args.DrawingSession.Transform = Matrix3x2.Identity;
    }

    private void PaintLinkStateOverlay(CanvasDrawingSession drawingSession, Rect viewport)
    {
        LinkRun? hoveredLink = null;
        if (_lastHoveredRun is LinkRun hover && _lastHoveredBox is { } hoverBox)
        {
            hoveredLink = hover;
            hoverBox.PaintLinkStateForeground(drawingSession, hover, focused: false, viewport);
        }

        if (TryGetFocusedLink(out var focusedBox, out var focusedLink) &&
            !ReferenceEquals(focusedLink, hoveredLink))
        {
            focusedBox.PaintLinkStateForeground(drawingSession, focusedLink, focused: true, viewport);
        }
        else if (TryGetFocusedLink(out focusedBox, out focusedLink))
        {
            focusedBox.PaintLinkStateForeground(drawingSession, focusedLink, focused: true, viewport);
        }
    }

    private void OnPointerCanceledOrCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // True interruption (system-level cancel or capture taken by another element):
        // tear down drag state so a phantom anchor doesn't survive into the next gesture.
        // We deliberately do NOT do this on plain PointerExited — with capture, the
        // pointer can briefly leave canvas bounds during a normal drag, and clearing
        // the anchor there would kill drag-select on the very first vertical move
        // when the pointer crosses into a sibling overlay region (e.g. over a hosted
        // inline embed). PointerReleased handles the normal end-of-drag cleanup.
        _pointerSession = default;
        _selectionAnchor = null;
        _clickMode = ClickMode.Single;
        SetSelectionDragShieldActive(false);
        OnPointerExited(sender, e);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
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
        // region coordinates shift by sub-pixel amouits → visible text shake on every
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
            // partial repaiits expose DirectWrite sub-pixel glyph-position
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

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas is null) return;

        // Snapshot BEFORE releasing capture: ReleasePointerCapture can dispatch
        // PointerCaptureLost synchronously, so read and clear the pointer session first.
        bool wasLeft = _pointerSession.IsActive &&
            _pointerSession.PointerId == e.Pointer.PointerId &&
            _pointerSession.IsPrimary;
        _pointerSession = default;
        _selectionAnchor = null;
        SetSelectionDragShieldActive(false);
        _canvas.ReleasePointerCapture(e.Pointer);
        if (!wasLeft) return;

        if (!_selection.Range.Normalized().IsEmpty)
        {
            UpdateSelectionAdornerViewport();
            _selectionAdorner?.Invalidate();
        }

        // Click handling for links: if no real selection occurred, raise LinkClick
        // when the click lands on a LinkRun.
        if (_snapshot is null) return;
        if (_clickMode != ClickMode.Single) return; // double/triple-click: selection intent, not link-click
        if (!_selection.Range.Normalized().IsEmpty) return; // text was dragged — not a click
        var pt = e.GetCurrentPoint(_canvas).Position;
        if (_snapshot.HitTest(pt, out var pos))
        {
            var link = FindLinkTargetAt(pos, pt);
            if (link is not null)
            {
                // Intercept internal fragment anchors (e.g. footnote back/forward
                // links) and scroll without surfacing them to external subscribers.
                if (!link.Value.Url.StartsWith("#", StringComparison.Ordinal) || !HandleInternalAnchor(link.Value.Url))
                {
                    LinkClick?.Invoke(this, new MarkdownLinkClickEventArgs(link.Value.Url, link.Value.Title));
                }
            }
        }
    }

    /// <summary>
    /// Scrolls the document so the block with <paramref name="blockIndex"/>
    /// is near the top of the viewport.
    /// </summary>
    public void ScrollToBlock(int blockIndex)
    {
        if (_scroll is null || _snapshot is null) return;
        foreach (var b in _snapshot.Blocks)
        {
            double? y = FindBlockY(b, blockIndex);
            if (y is { } top)
            {
                double targetY = Math.Max(0, top - 24);
                _scroll.ChangeView(null, targetY, null, disableAnimation: false);
                return;
            }
        }
    }

    private static double? FindNearestScrollAnchor(LayoutSnapshot snapshot, int blockIndex, double offsetFromTop)
    {
        BlockBox? nearest = null;
        int bestDistance = int.MaxValue;
        foreach (var block in snapshot.Blocks)
        {
            int distance = Math.Abs(block.BlockIndex - blockIndex);
            if (distance >= bestDistance)
                continue;

            nearest = block;
            bestDistance = distance;
        }

        return nearest is null ? null : nearest.Bounds.Top - offsetFromTop;
    }

    private static double? FindBlockY(BlockBox box, int blockIndex)
    {
        if (box.BlockIndex == blockIndex) return box.Bounds.Top;
        if (box is Layout.Boxes.ListItemBox lib)
        {
            return FindBlockY(lib.Marker, blockIndex) ?? FindBlockY(lib.Content, blockIndex);
        }
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                if (FindBlockY(c, blockIndex) is { } y) return y;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                if (FindBlockY(cell, blockIndex) is { } y) return y;
            }
        }
        return null;
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
        if (_snapshot is null) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        switch (e.Key)
        {
            case VirtualKey.C when ctrl && _selection.IsActive:
                e.Handled = CopySelectionToClipboard();
                return;
            case VirtualKey.A when ctrl:
                _selection.SetAnchor(DocumentPosition.Zero);
                _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
                e.Handled = true;
                return;
            case VirtualKey.Tab:
                e.Handled = MoveFocus(reverse: shift);
                return;
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Up:
            case VirtualKey.Down:
                e.Handled = MoveFocusSpatial(e.Key);
                return;
            case VirtualKey.Enter:
            case VirtualKey.Space when _focusedItemIndex >= 0:
                e.Handled = ActivateFocusedItem();
                return;
            case VirtualKey.Escape:
                if (_focusedItemIndex >= 0)
                {
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

    /// <summary>
    /// Advances (or reverses) keyboard focus through the focusable items list.
    /// Returns true when focus is successfully moved (prevents Tab from leaving the control).
    /// </summary>
    private bool MoveFocus(bool reverse)
    {
        var items = _focusableItems;
        if (items is null || items.Count == 0) return false;

        int nextIndex = FocusNavigationHelper.MoveTab(items.Count, _focusedItemIndex, _focusResumeItemIndex, reverse);
        if (nextIndex < 0)
        {
            // Let Tab/Shift+Tab leave the control to the next focusable element.
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
        var items = _focusableItems;
        if (items is null || items.Count == 0) return false;

        if (_focusedItemIndex < 0)
        {
            if (_focusResumeItemIndex < 0) return false;
            _focusedItemIndex = Math.Clamp(_focusResumeItemIndex, 0, items.Count - 1);
            _focusResumeItemIndex = -1;
            return CommitFocusedItem(reverse: false);
        }

        var map = BuildFocusableRectMap();
        int currentLocalIndex = -1;
        for (int i = 0; i < map.Count; i++)
        {
            if (map[i].Index == _focusedItemIndex)
            {
                currentLocalIndex = i;
                break;
            }
        }

        if (currentLocalIndex < 0) return false;

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
        if (nextLocalIndex < 0) return false;

        _focusedItemIndex = map[nextLocalIndex].Index;
        _focusResumeItemIndex = -1;
        return CommitFocusedItem(reverse: false);
    }

    private bool CommitFocusedItem(bool reverse)
    {
        bool scrolled = ScrollFocusedItemIntoView();
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

    /// <summary>Fires LinkClick for the currently focused painted link.</summary>
    private bool ActivateFocusedItem()
    {
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        var item = items[_focusedItemIndex];
        if (!item.IsLink)
        {
            return TryFocusHostedElement(item, reverse: false);
        }

        // Find the LinkRun in the snapshot and fire LinkClick.
        if (_snapshot is null) return false;
        var pos = new DocumentPosition(item.BlockIndex, item.InlineIndex, 0);
        if (FindLinkAt(pos) is { } lr)
        {
            if (!lr.Url.StartsWith("#", StringComparison.Ordinal) || !HandleInternalAnchor(lr.Url))
                LinkClick?.Invoke(this, new MarkdownLinkClickEventArgs(lr.Url, lr.Title));
            return true;
        }
        return false;
    }

    private void FocusCurrentItem(bool reverse = false)
    {
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
        {
            UpdateFocusRing();
            return;
        }

        var item = items[_focusedItemIndex];
        if (!item.IsLink && TryFocusHostedElement(item, reverse))
        {
            if (_focusRing is not null) _focusRing.Visibility = Visibility.Collapsed;
            return;
        }

        Focus(FocusState.Keyboard);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
    }

    private void NotifyFocusedItemAutomation()
    {
        if (!TryGetFocusedLink(out var inline, out var link))
            return;

        void Raise()
        {
            var peer = FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(this) as MarkdownAutomationPeer;
            peer?.RaiseFocusForLink(inline, link);
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
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
        {
            HideFocusRing();
            return;
        }

        var item = items[_focusedItemIndex];
        if (!item.IsLink)
        {
            HideFocusRing();
            return;
        }

        var rect = GetFocusableItemRect(item);
        if (rect is not { Width: > 0, Height: > 0 } r)
        {
            HideFocusRing();
            return;
        }

        if (!EnsureFocusRing())
        {
            InvalidateInteractiveTextAdorner();
            return;
        }
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
        if (_focusRing is not null)
            _focusRing.Visibility = Visibility.Collapsed;

        InvalidateInteractiveTextAdorner();
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
        if (_snapshot is null) return null;
        foreach (var b in _snapshot.Blocks)
        {
            if (b is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == item.BlockIndex)
                return icb.GetRunRect(item.InlineIndex);
            var nested = GetFocusableItemRectFromBlock(b, item);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void RememberFocusResumePoint(Point documentPoint)
    {
        var map = BuildFocusableRectMap();
        if (map.Count == 0)
        {
            _focusResumeItemIndex = -1;
            return;
        }

        var rects = new List<Rect>(map.Count);
        foreach (var entry in map) rects.Add(entry.Rect);

        int nearest = FocusNavigationHelper.FindNearestIndex(rects, documentPoint);
        _focusResumeItemIndex = nearest >= 0 ? map[nearest].Index : -1;
    }

    private List<(int Index, Rect Rect)> BuildFocusableRectMap()
    {
        var result = new List<(int Index, Rect Rect)>();
        var items = _focusableItems;
        if (items is null) return result;

        for (int i = 0; i < items.Count; i++)
        {
            if (GetFocusableItemRect(items[i]) is { Width: > 0, Height: > 0 } rect)
                result.Add((i, rect));
        }

        return result;
    }

    private static Rect? GetFocusableItemRectFromBlock(BlockBox box, Layout.FocusableItem item)
    {
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
    private bool ScrollFocusedItemIntoView()
    {
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _scroll is null) return false;
        var item = items[_focusedItemIndex];
        if (GetFocusableItemRect(item) is not { } rect) return false;

        double top    = _scroll.VerticalOffset;
        double bottom = top + _scroll.ViewportHeight;
        const double margin = 24.0;

        if (rect.Top < top + margin)
            return _scroll.ChangeView(null, Math.Max(0, rect.Top - margin), null, disableAnimation: false);
        else if (rect.Bottom > bottom - margin)
            return _scroll.ChangeView(null, rect.Bottom - _scroll.ViewportHeight + margin, null, disableAnimation: false);

        return false;
    }

    private bool TryFocusHostedElement(Layout.FocusableItem item, bool reverse)
    {
        if (FindHostedElementForFocusable(item) is not { } element)
            return false;

        if (TryFocusHostedDescendant(element, reverse))
            return true;

        return element.Focus(FocusState.Keyboard);
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
        IReadOnlyList<Control> focusables)
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

    private static bool ContainsHostedFocusable(IReadOnlyList<Control> focusables, Control control)
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

    private static List<Control> CollectHostedFocusableControls(DependencyObject root)
    {
        var result = new List<Control>();
        CollectHostedFocusableControls(root, result);
        return result;
    }

    private static void CollectHostedFocusableControls(DependencyObject node, List<Control> result)
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

    private FrameworkElement? FindHostedElementForFocusable(Layout.FocusableItem item)
    {
        if (_snapshot is null) return null;
        foreach (var block in _snapshot.Blocks)
        {
            var found = FindHostedElementForFocusable(block, item);
            if (found is not null) return found;
        }

        return null;
    }

    private bool TrySetFocusedItemForHostedElement(FrameworkElement element)
    {
        var items = _focusableItems;
        if (items is null) return false;
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(FindHostedElementForFocusable(items[i]), element))
            {
                _focusedItemIndex = i;
                return true;
            }
        }

        return false;
    }

    private static FrameworkElement? FindHostedElementForFocusable(BlockBox box, Layout.FocusableItem item)
    {
        switch (box)
        {
            case Layout.Boxes.EmbedBox eb when item.IsBlockEmbed && eb.BlockIndex == item.BlockIndex:
                return eb.RealizedElement;
            case Layout.Boxes.CodeBlockBox codeBlock when codeBlock.BlockIndex == item.BlockIndex:
                if (item.IsCodeBlockCopy)
                    return codeBlock.RealizedCopyButton;
                return null;
            case Layout.Boxes.InlineContainerBox icb when item.IsInlineEmbed && icb.BlockIndex == item.BlockIndex:
                foreach (var run in icb.Runs)
                {
                    if (run.InlineIndex == item.InlineIndex && run is InlineEmbedRun embed)
                        return embed.RealizedElement;
                }
                return null;
            case Layout.Boxes.ListItemBox lib:
                return FindHostedElementForFocusable(lib.Marker, item)
                       ?? FindHostedElementForFocusable(lib.Content, item);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    var found = FindHostedElementForFocusable(cell, item);
                    if (found is not null) return found;
                }
                return null;
            case Layout.Boxes.StackBox sb:
                foreach (var child in sb.Children)
                {
                    var found = FindHostedElementForFocusable(child, item);
                    if (found is not null) return found;
                }
                return null;
            default:
                return null;
        }
    }

    private LinkRun? FindLinkAt(DocumentPosition pos)
    {
        if (_snapshot is null) return null;
        foreach (var b in _snapshot.Blocks)
        {
            if (FindLinkInBlock(b, pos) is { } found) return found;
        }
        return null;
    }

    private LinkTarget? FindLinkTargetAt(DocumentPosition pos, Point? point = null)
    {
        if (_snapshot is null) return null;
        foreach (var b in _snapshot.Blocks)
        {
            if (FindLinkTargetInBlock(b, pos, point) is { } found) return found;
        }
        return null;
    }

    private bool TryGetFocusedLink(out Layout.Boxes.InlineContainerBox inline, out LinkRun link)
    {
        inline = null!;
        link = null!;

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        return TryGetLinkForFocusable(items[_focusedItemIndex], out inline, out link);
    }

    private bool TryGetFocusableIndexForLink(LinkRun run, out int index)
    {
        index = -1;
        var items = _focusableItems;
        if (items is null) return false;

        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].IsLink)
                continue;

            if (TryGetLinkForFocusable(items[i], out _, out var link) &&
                ReferenceEquals(link, run))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private bool TryGetLinkForFocusable(Layout.FocusableItem item, out Layout.Boxes.InlineContainerBox inline, out LinkRun link)
    {
        inline = null!;
        link = null!;
        if (!item.IsLink || _snapshot is null)
            return false;

        foreach (var b in _snapshot.Blocks)
        {
            if (TryGetLinkForFocusable(b, item, out inline, out link))
                return true;
        }

        return false;
    }

    private static bool TryGetLinkForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out LinkRun link)
    {
        inline = null!;
        link = null!;

        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == item.BlockIndex:
                foreach (var r in icb.Runs)
                {
                    if (r.InlineIndex == item.InlineIndex && r is LinkRun lr)
                    {
                        inline = icb;
                        link = lr;
                        return true;
                    }
                }
                return false;
            case Layout.Boxes.ListItemBox lib:
                return TryGetLinkForFocusable(lib.Marker, item, out inline, out link)
                       || TryGetLinkForFocusable(lib.Content, item, out inline, out link);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (TryGetLinkForFocusable(cell, item, out inline, out link))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (TryGetLinkForFocusable(c, item, out inline, out link))
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
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToWord(LayoutSnapshot snapshot, DocumentPosition pos)
    {
        var icb = FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
        {
            // Block has no inline container (code block, embed row, etc.).
            // Set anchor at the click position so subsequent drag ExtendTo calls
            // always have a valid anchor; this makes the selection empty (start==end)
            // while allowing drag to extend from this point.
            _selection.SetAnchor(pos);
            // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
            return (pos, pos);
        }
        var (start, end) = icb.GetWordBoundaries(pos);
        _selection.SetAnchor(start);
        _selection.ExtendTo(end);
        // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
        return (start, end);
    }

    /// <summary>
    /// Expands selection to the entire inline container (paragraph/heading line)
    /// that contains <paramref name="pos"/>.
    /// Returns the (start, end) document positions of the selected block.
    /// </summary>
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToBlock(LayoutSnapshot snapshot, DocumentPosition pos)
    {
        var icb = FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
        {
            // Block has no inline container (code block, embed row, etc.).
            // Set anchor at the click position so subsequent drag ExtendTo calls
            // always have a valid anchor; this makes the selection empty (start==end)
            // while allowing drag to extend from this point.
            _selection.SetAnchor(pos);
            // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
            return (pos, pos);
        }
        var (start, end) = icb.GetBlockBoundaries();
        _selection.SetAnchor(start);
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
        if (_canvas is null) return;
        var pt = e.GetPosition(_canvas);
        var menu = new MenuFlyout();
        _contextMenuOpen = true;
        menu.Closed += (_, _) => _contextMenuOpen = false;

        var copyItem = new MenuFlyoutItem { Text = MarkdownLocalizedStrings.ContextMenuCopy };
        copyItem.IsEnabled = _selection.IsActive;
        copyItem.Click += (_, _) =>
        {
            CopySelectionToClipboard();
        };
        menu.Items.Add(copyItem);

        var selectAllItem = new MenuFlyoutItem { Text = MarkdownLocalizedStrings.ContextMenuSelectAll };
        selectAllItem.IsEnabled = IsSelectionEnabled;
        selectAllItem.Click += (_, _) =>
        {
            if (!IsSelectionEnabled) return;
            _selection.SetAnchor(DocumentPosition.Zero);
            _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
        };
        menu.Items.Add(selectAllItem);

        menu.ShowAt(_canvas, pt);
        e.Handled = true;
    }

    private static LinkRun? FindLinkInBlock(BlockBox box, DocumentPosition pos)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == pos.BlockIndex:
                foreach (var r in icb.Runs)
                {
                    if (r.InlineIndex == pos.InlineIndex && r is LinkRun lr) return lr;
                }
                return null;
            case Layout.Boxes.ListItemBox lib:
                if (FindLinkInBlock(lib.Marker, pos) is { } lm) return lm;
                return FindLinkInBlock(lib.Content, pos);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (FindLinkInBlock(cell, pos) is { } tf) return tf;
                }
                return null;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (FindLinkInBlock(c, pos) is { } f) return f;
                }
                return null;
        }
        return null;
    }

    private static LinkTarget? FindLinkTargetInBlock(BlockBox box, DocumentPosition pos, Point? point)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == pos.BlockIndex:
                foreach (var r in icb.Runs)
                {
                    if (r.InlineIndex != pos.InlineIndex)
                        continue;

                    if (point is { } p && !icb.IsPointInsideRunBounds(r, p))
                        return null;

                    if (r is LinkRun lr)
                        return new LinkTarget(lr.Url, lr.Title);

                    if (r is InlineImageRun { IsLinked: true } image)
                        return new LinkTarget(image.LinkUrl!, image.LinkTitle);
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
        => run is LinkRun or InlineImageRun { IsLinked: true };

    private readonly record struct LinkTarget(string Url, string? Title);
}

/// <summary>Event data for markdown link activation.</summary>
public sealed class MarkdownLinkClickEventArgs : EventArgs
{
    /// <summary>Initializes link activation event data.</summary>
    public MarkdownLinkClickEventArgs(string url, string? title)
    {
        Url = url;
        Title = title;
    }
    /// <summary>Gets the link URL.</summary>
    public string Url { get; }

    /// <summary>Gets the optional link title.</summary>
    public string? Title { get; }
}

