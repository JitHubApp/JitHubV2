using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
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
    private CancellationTokenSource? _pipelineCts;
    private float _lastWidth;
    private static readonly MarkdownTheme _defaultTheme = new();
    private SizeChangedEventHandler? _sizeChangedHandler;
    private readonly SelectionController _selection = new();
    private DocumentPosition? _selectionAnchor;

    // XAML Rectangle elements drawn on _overlay to show the selection highlight.
    // Keeping selection on the XAML layer means the DirectWrite canvas tiles are
    // never invalidated during a drag — eliminating tile-offset-driven glyph shake.
    // The list acts as a pool: Rectangles are never removed from _overlay.Children
    // during a drag update — only their Width/Height/Canvas.Left/Top/Visibility are
    // mutated.  Inserting/removing Canvas children triggers a XAML layout pass on
    // every pointer-move event and causes sub-pixel visual jitter of surrounding
    // XAML elements (embedded buttons, etc.).  With the pool we pay the one-time
    // Insert cost when a new stripe is first needed and zero tree-modification cost
    // on subsequent updates.  The pool is invalidated (Cleared) whenever
    // _overlay.Children.Clear() fires (on rebuild/unload).
    private readonly List<Rectangle> _selectionOverlayRects = new();
    // Brush cache: reuse the same SolidColorBrush across drag frames so we don't
    // allocate a new brush object on every pointer-move event.
    private SolidColorBrush? _selectionBrush;
    private Windows.UI.Color _selectionBrushColor;

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

    // ---- Keyboard navigation ----
    // Ordered list of focusable items (LinkRuns + InlineEmbedRuns) in the current
    // snapshot.  Rebuilt after each snapshot commit.  -1 means "nothing focused".
    private System.Collections.Generic.IReadOnlyList<Layout.FocusableItem>? _focusableItems;
    private int _focusedItemIndex = -1;
    // XAML Border element used to show a focus ring around the focused item.
    // Lives on _overlay at ZIndex 1 (above selection at -1, below embeds at 0).
    private Microsoft.UI.Xaml.Controls.Border? _focusRing;

    // ---- Multi-click tracking (double/triple click selection) ----
    private long _lastPressTickMs;
    private Point _lastPressPoint;
    private int _consecutiveClickCount;
    // Set when the pointer is captured for a left-button press; cleared in OnPointerReleased.
    // Guards the link-click path against right-button releases.
    private bool _leftPointerCaptured;
    // System double-click time; read from the Win32 API at first use.
    private static readonly int _doubleClickTimeMs = GetSystemDoubleClickTimeMs();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static int GetSystemDoubleClickTimeMs()
    {
        try { return (int)GetDoubleClickTime(); } catch { return 500; }
    }

    // Click mode for the current press; governs drag-extension behaviour.
    private enum ClickMode { Single, Word, Block }
    private ClickMode _clickMode;
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
    private readonly List<Rect> _blockEmbedRects = new();

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
                double w = Math.Round(Box.Bounds.Width  - Box.Margin.Left - Box.Margin.Right);
                double h = Math.Round(Box.Bounds.Height - Box.Margin.Top  - Box.Margin.Bottom);
                fe.Width  = w;
                fe.Height = h;
                double left = Math.Round(Box.Bounds.X + Box.Margin.Left);
                double top  = Math.Round(Box.Bounds.Y + Box.Margin.Top);
                Canvas.SetLeft(fe, left);
                Canvas.SetTop(fe, top);
                owner._overlay!.Children.Add(fe);
                // _blockEmbedRects rebuilt in RealizeVisibleEmbeds.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownRendererControl] EmbedBox factory threw: {ex.Message}");
            }
        }
        public override void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Box.Factory.RecycleBlock(Box.SourceBlock, fe); } catch { }
            Box.RealizedElement = null;
            Realized = null;
            // Refresh _blockEmbedRects defensively (rebuilt fully each Realize cycle from realised plans).
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
                double iLeft = Math.Round(Rect.X);
                double iTop  = Math.Round(Rect.Y);
                double iW    = Math.Round(Rect.X + Rect.Width)  - iLeft;
                double iH    = Math.Round(Rect.Y + Rect.Height) - iTop;
                fe.Width  = iW;
                fe.Height = iH;
                Canvas.SetLeft(fe, iLeft);
                Canvas.SetTop(fe, iTop);
                owner._overlay!.Children.Add(fe);
                // _embedRects rebuilt in RealizeVisibleEmbeds.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownRendererControl] inline embed factory threw: {ex.Message}");
            }
        }
        public override void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Run.Recycle?.Invoke(fe); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownRendererControl] inline embed Recycle threw: {ex.Message}");
            }
            Run.RealizedElement = null;
            Realized = null;
        }
    }
    private readonly List<EmbedPlan> _embedPlans = new();

    // Lazy-load queue: all ImageBox instances in the current snapshot.
    // EnsureLoading() is called for each when its bounds enter the
    // viewport + LazyImageOverscanPx band.  Images already in the
    // in-memory cache start loading (no-op) immediately after build.
    private readonly List<Layout.Boxes.ImageBox> _imagePlans = new();

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

    // ---- Dependency properties ----

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownRendererControl),
            new PropertyMetadata(string.Empty, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly DependencyProperty ThemeProperty =
        DependencyProperty.Register(nameof(Theme), typeof(MarkdownTheme), typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnThemeDpChanged(e)));

    public MarkdownTheme? Theme
    {
        get => (MarkdownTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    public static readonly DependencyProperty ExtensionRegistryProperty =
        DependencyProperty.Register(nameof(ExtensionRegistry), typeof(MarkdownExtensionRegistry),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    public MarkdownExtensionRegistry? ExtensionRegistry
    {
        get => (MarkdownExtensionRegistry?)GetValue(ExtensionRegistryProperty);
        set => SetValue(ExtensionRegistryProperty, value);
    }

    public static readonly DependencyProperty EmbedFactoryProperty =
        DependencyProperty.Register(nameof(EmbedFactory), typeof(IMarkdownEmbedFactory),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    public IMarkdownEmbedFactory? EmbedFactory
    {
        get => (IMarkdownEmbedFactory?)GetValue(EmbedFactoryProperty);
        set => SetValue(EmbedFactoryProperty, value);
    }

    public static readonly DependencyProperty IsSelectionEnabledProperty =
        DependencyProperty.Register(nameof(IsSelectionEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true));

    public bool IsSelectionEnabled
    {
        get => (bool)GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    internal MarkdownLinkPeer GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run)
    {
        // GetValue is atomic for concurrent UIA callers and avoids the
        // TryGetValue+Add race. The factory captures the *current* parent
        // peer; this is acceptable because the parent peer's bounding-rect
        // computation only reads through to the live owner control + box,
        // so a "stale" parent reference still resolves to the right rect.
        return _linkPeerCache.GetValue(run, r => new MarkdownLinkPeer(this, parent, r));
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

    protected override AutomationPeer OnCreateAutomationPeer() => new MarkdownAutomationPeer(this);

    /// <summary>
    /// Internal accessor for the current layout snapshot. Used by the
    /// automation peer to walk the document structure (headings, links, etc.).
    /// May be null before the first layout completes.
    /// </summary>
    internal LayoutSnapshot? CurrentSnapshot => _snapshot;

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
            foreach (var p in _embedPlans) if (p.Realized is not null) n++;
            return n;
        }
    }

    /// <summary>
    /// Map a document-local box rectangle into screen coordinates. Used by
    /// per-block automation peers to report accurate bounding rectangles so
    /// Narrator's "scan by element" gestures move focus to the correct spot
    /// on screen, including after scrolling.
    /// </summary>
    internal Windows.Foundation.Rect GetScreenRectForBox(Windows.Foundation.Rect docRect)
    {
        try
        {
            double yOffset = _scroll?.VerticalOffset ?? 0;
            var local = new Windows.Foundation.Point(docRect.X, docRect.Y - yOffset);
            var transform = TransformToVisual(null);
            var topLeft = transform.TransformPoint(local);
            return new Windows.Foundation.Rect(topLeft.X, topLeft.Y, docRect.Width, docRect.Height);
        }
        catch
        {
            // Element may not be in a tree yet (e.g. peer requested before
            // first layout pass). Fall back to a zero-rect; UIA will treat
            // it as "no bounding rect" and skip it.
            return default;
        }
    }

    /// <summary>
    /// The raw markdown source the renderer was last given. Useful for
    /// assistive technologies that want a flat textual representation when
    /// structural traversal isn't available.
    /// </summary>
    internal string CurrentMarkdownSource => Markdown ?? string.Empty;

    public MarkdownRendererControl()
    {
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += (_, _) => OnLoadedInternal();
        Unloaded += (_, _) => OnUnloaded();
        ActualThemeChanged += (_, _) => OnThemeChanged();
        // Selection changes update the XAML overlay (not the DirectWrite canvas),
        // so tiles are never invalidated during a drag.
        _selection.Changed += (_, _) => UpdateSelectionOverlay();
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
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
        };
        _root = new Grid();
        _canvas = new CanvasVirtualControl();
        _overlay = new Canvas
        {
            // Background null so the empty overlay doesn't capture pointer
            // events. Hosted embeds are individually hit-testable.
            Background = null,
            IsHitTestVisible = true,
        };
        _root.Children.Add(_canvas);
        _root.Children.Add(_overlay);
        _scroll.Content = _root;

        _canvas.RegionsInvalidated += OnRegionsInvalidated;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerExited += OnPointerExited;
        _canvas.PointerCanceled += OnPointerExited;
        _canvas.PointerCaptureLost += OnPointerExited;
        _canvas.RightTapped += OnRightTapped;
        KeyDown += OnKeyDown;

        Content = _scroll;
    }

    private void OnLoadedInternal()
    {
        _sizeChangedHandler = (_, e) =>
        {
            if (Math.Abs(_lastWidth - (float)e.NewSize.Width) > 0.5f) RequestRebuild();
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
        if (_sizeChangedHandler is not null)
        {
            SizeChanged -= _sizeChangedHandler;
            _sizeChangedHandler = null;
        }
        if (Theme is { } t)
        {
            t.Changed -= OnThemeRevisionChanged;
        }
        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = null;
        // Tear down embed plans before clearing the overlay so block embed
        // factories get RecycleBlock callbacks and inline embeds release
        // their Run.RealizedElement references — otherwise hosted controls
        // and their event handlers would leak past detach.
        DerealizeAllEmbeds();
        _embedPlans.Clear();
        _imagePlans.Clear();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
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
        // Clear the selection-rect pool: after the overlay is wiped the pooled
        // Rectangles are no longer in _overlay.Children, so the pool references
        // are stale.  CommitSnapshot does the same; mirror it here so a re-attach
        // cycle (Unload → Load) starts with a clean pool.
        _selectionOverlayRects.Clear();
        _selectionBrush = null;
        _focusRing = null; // evicted from overlay above; lazily re-created on re-attach
        var snap = _snapshot;
        _snapshot = null;
        snap?.Dispose();
    }

    private void OnThemeChanged()
    {
        if (Theme is { } t) t.Invalidate();
        RequestRebuild();
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
        RequestRebuild();
    }

    /// <summary>Kicks off (or re-kicks) the parse + layout pipeline.</summary>
    public void RequestRebuild()
    {
        // Cancel the in-flight build (if any) but do NOT dispose the CTS here:
        // the background task still holds a reference to the token and accessing
        // IsCancellationRequested / ThrowIfCancellationRequested on a disposed
        // token can throw ObjectDisposedException, which escapes our OCE catches
        // and becomes an unobserved task exception.  Let the GC collect the old CTS.
        _pipelineCts?.Cancel();
        _pipelineCts = new CancellationTokenSource();
        var cts = _pipelineCts;
        _ = RebuildAsync(cts.Token).ContinueWith(t =>
        {
            if (t.IsFaulted)
                System.Diagnostics.Debug.WriteLine($"[MarkdownRendererControl] Rebuild faulted: {t.Exception}");
            // Dispose the CTS only after the task it was attached to has finished.
            if (ReferenceEquals(_pipelineCts, cts)) { /* still current — let it live */ }
            else cts.Dispose();
        }, TaskScheduler.Default);
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        try
        {
            await RebuildInternalAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* expected – a new build was requested */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MarkdownRendererControl] Rebuild failed: {ex}");
        }
    }

    private async Task RebuildInternalAsync(CancellationToken ct)
    {
        if (_canvas is null) return;
        var width = (float)Math.Max(50, ActualWidth);
        if (width <= 0) return;
        _lastWidth = width;

        var registry = ExtensionRegistry ?? new MarkdownExtensionRegistry();
        var pipeline = registry.BuildPipeline();
        var parser = new MarkdigParser(pipeline);
        var source = Markdown ?? string.Empty;

        ct.ThrowIfCancellationRequested();
        var parsed = await parser.ParseAsync(source, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var sourceMap = new MarkdownSourceMap(parsed.SourceText);
        var theme = Theme ?? _defaultTheme;
        var themeSnapshot = new ThemeResolver(this, theme).CreateSnapshot();
        // Capture background color on the UI thread now — ActualTheme is correct
        // at this point (ActualThemeChanged fires before we call RequestRebuild).
        // Win11 surface colors: dark = #202020, light = white.
        _canvasBackground = ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        // Use the shared CanvasDevice (always available, no visual-tree required).
        // CanvasVirtualControl only has a device after CreateResources fires, so
        // passing _canvas directly would crash if layout runs before first draw.
        var device = CanvasDevice.GetSharedDevice();
        var ctx = new MarkdownLayoutContext(device, themeSnapshot, sourceMap, registry, FlowDirection, DispatcherQueue);
        _themeSnapshot = themeSnapshot;
        var builder = new LayoutBuilder(ctx, EmbedFactory);

        ct.ThrowIfCancellationRequested();
        var snapshot = await Task.Run(() => builder.Build(parsed.Document, width), ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        // Scroll anchoring: capture the current read position before the canvas
        // height changes.  If the user has scrolled down, we identify the first
        // visible block (its top edge closest to viewport top) and how far it is
        // from the viewport top.  After committing the new layout we restore the
        // same offset so content above the fold shifting (e.g. an image loading)
        // doesn't jump the reader's position.
        (int BlockIndex, double OffsetFromTop)? scrollAnchor = null;
        if (_scroll is { VerticalOffset: > 0 } scrollSnap && _snapshot is { } prevSnap)
        {
            double vTop = scrollSnap.VerticalOffset;
            foreach (var b in prevSnap.Blocks)
            {
                if (b.Bounds.Bottom < vTop) continue;
                scrollAnchor = (b.BlockIndex, b.Bounds.Top - vTop);
                break;
            }
        }

        // Atomically swap snapshots, then dispose the old one so its
        // CanvasTextLayout / placeholder handles are released.
        var old = _snapshot;
        _snapshot = snapshot;
        old?.Dispose();
        _canvas.Width = width;
        _canvas.Height = Math.Max(1, snapshot.Size.Height);
        _root!.Width = width;
        _root.Height = _canvas.Height;
        _overlay!.Width = width;
        _overlay.Height = _canvas.Height;

        // Restore scroll anchor: find the anchor block's new Y in the new layout
        // and adjust the scroll offset so the user's read position is unchanged.
        if (scrollAnchor is { } anchor && _scroll is { } scrollRestore)
        {
            double? newY = null;
            foreach (var b in snapshot.Blocks)
            {
                if (b.BlockIndex == anchor.BlockIndex)
                {
                    newY = b.Bounds.Top - anchor.OffsetFromTop;
                    break;
                }
            }
            if (newY is { } targetOffset && targetOffset >= 0)
            {
                scrollRestore.ChangeView(null, targetOffset, null, disableAnimation: true);
            }
        }

        // UI thread: collect embed plans (don't realise yet), hook image
        // LoadCompleted, then realise only embeds that fall in the current
        // viewport. Hooking _scroll.ViewChanged drives subsequent realisation
        // as the user scrolls.
        DerealizeAllEmbeds();
        _overlay.Children.Clear();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _embedPlans.Clear();
        _imagePlans.Clear();
        // Identities change across rebuild even when the count happens to
        // match — reset so the first post-rebuild realisation always fires.
        _lastFiredRealizedCount = -1;
        _selectionOverlayRects.Clear(); // overlay was just cleared above
        _selection.Clear();             // stale selection no longer valid after re-layout
        _focusedItemIndex = -1;         // selection/focus stale after re-layout
        _focusRing = null;              // evicted from overlay; will be lazily re-created on next Tab
        _focusableItems = snapshot.CollectFocusableItems();
        foreach (var b in snapshot.Blocks) CollectEmbedPlans(b);
        if (_scroll is not null)
        {
            _scroll.ViewChanged -= OnScrollViewChanged;
            _scroll.ViewChanged += OnScrollViewChanged;
        }
        RealizeVisibleEmbeds();

        _canvas.Invalidate();
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Run a final realisation pass after intermediate-view bursts settle
        // so we don't thrash during fling/inertia. ViewChanged with
        // IsIntermediate=false fires at the end of inertia; we also realise on
        // intermediate ticks to keep the visual current.
        RealizeVisibleEmbeds();
    }

    private void DerealizeAllEmbeds()
    {
        foreach (var plan in _embedPlans)
        {
            if (plan.Realized is not null) plan.Derealize(this);
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
        double realizeTop = top - EmbedVirtualizationOverscanPx;
        double realizeBottom = bottom + EmbedVirtualizationOverscanPx;
        double derealizeTop = top - EmbedVirtualizationDerealizeOverscanPx;
        double derealizeBottom = bottom + EmbedVirtualizationDerealizeOverscanPx;

        // Drop old realisation-side caches; they'll be repopulated as embeds realise.
        // We do NOT clear them when only some embeds change state mid-scroll —
        // because removing a single fe from _overlay.Children doesn't shift
        // others' indices. Tracking realised plans gives us authoritative cache rebuilds.
        _embedRects.Clear();
        _blockEmbedRects.Clear();

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
                    _blockEmbedRects.Add(new Rect(left, t, w, h));
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
                ib.LoadCompleted -= OnImageLoadCompleted;
                ib.LoadCompleted += OnImageLoadCompleted;
                _imagePlans.Add(ib);
                break;
            }
            case Layout.Boxes.InlineContainerBox icb:
            {
                foreach (var (run, rect) in icb.EnumerateEmbedRects())
                {
                    _embedPlans.Add(new InlineEmbedPlan { Icb = icb, Run = run, Rect = rect });
                }
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
        if (_snapshot is null) return;
        var frame = MarkdownRenderer.Diagnostics.ShakeLogger.NextFrame();
        int regionCount = 0;
        foreach (var region in args.InvalidatedRegions)
        {
            regionCount++;
            MarkdownRenderer.Diagnostics.ShakeLogger.LogPaint(
                "region", regionCount, region.X, region.Y, region.Width, region.Height);
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
            // Selection is rendered on the XAML overlay (Rectangle elements),
            // not here — canvas tiles are never dirtied during drag.
            _snapshot.Paint(ds, region);
        }
        MarkdownRenderer.Diagnostics.ShakeLogger.Log("frame-end",
            $"frame={frame} regions={regionCount} hovered={(_lastHoveredRun is null ? "null" : _lastHoveredRun.GetType().Name)} dragging={_selectionAnchor is not null}");
    }

    // ---- Input ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSelectionEnabled || _snapshot is null || _canvas is null) return;
        // Only process left (primary) button presses; right-clicks are handled by
        // OnRightTapped and must not affect the multi-click counter or selection anchor.
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        var pt = e.GetCurrentPoint(_canvas).Position;

        // Pressing *on* a hosted inline embed must NOT start a selection.
        // The embed is a real WinUI element layered above the canvas — its
        // own pointer-pressed handler must run (Button click, TextBox focus,
        // …).  Returning here without setting _selectionAnchor or capturing
        // the pointer lets XAML's normal pointer routing deliver the event
        // to the embedded element.
        if (IsPointOverEmbed(pt))
        {
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

        Focus(FocusState.Pointer);
        if (_snapshot.HitTest(pt, out var pos))
        {
            // Advance clock/position only on successful text hits so a miss in the
            // same spot doesn't corrupt the double/triple-click timing window.
            _lastPressTickMs = nowMs;
            _lastPressPoint  = pt;
            // Always arm the anchor first: this suppresses hover processing
            // in OnPointerMoved during any captured drag (single, double, or triple-click)
            // and prevents a stale anchor from an earlier interaction being reused.
            _selectionAnchor = pos;
            _leftPointerCaptured = true; // set only on HitTest success so release events don't misfire

            if (_consecutiveClickCount == 3)
            {
                // Triple-click: select the entire block (line).
                _clickMode = ClickMode.Block;
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToBlock(_snapshot, pos);
                _canvas.CapturePointer(e.Pointer);
                return;
            }
            if (_consecutiveClickCount == 2)
            {
                // Double-click: select the word under the cursor.
                _clickMode = ClickMode.Word;
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToWord(_snapshot, pos);
                _canvas.CapturePointer(e.Pointer);
                return;
            }
            _clickMode = ClickMode.Single;
            _selection.SetAnchor(pos);
            // Invalidate to repaint link-hover state (hover suppressed during drag).
            _canvas.Invalidate();
            _canvas.CapturePointer(e.Pointer);
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
            _canvas.Invalidate();
        }
    }

    private InlineRun? _lastHoveredRun;
    private Layout.Boxes.InlineContainerBox? _lastHoveredBox; // box that contains _lastHoveredRun; used for targeted canvas invalidation
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
            // Atomic embed inclusion: when the pointer is inside an inline
            // embed rect during a drag, snap the position to either the
            // start or end of the InlineEmbedRun (whichever side the pointer
            // is closer to).  This treats the embed as a single, indivisible
            // unit of selectable content — the user can never have a
            // selection that ends *halfway through* an embedded button or
            // textbox, which matches how browsers handle <input> /
            // <textarea> inside contenteditable text.
            if (TryHitTestEmbed(pt, out var embedPos))
            {
                MarkdownRenderer.Diagnostics.ShakeLogger.Log("ptr-move-drag-embed",
                    $"px={pt.X:F4} py={pt.Y:F4} pos=blk{embedPos.BlockIndex}/inl{embedPos.InlineIndex}/c{embedPos.CharacterOffset}");
                _selection.ExtendTo(embedPos);
                // No _canvas.Invalidate(): selection is on the XAML overlay.
            }
            else if (_snapshot.HitTest(pt, out var pos))
            {
                MarkdownRenderer.Diagnostics.ShakeLogger.Log("ptr-move-drag",
                    $"px={pt.X:F4} py={pt.Y:F4} pos=blk{pos.BlockIndex}/inl{pos.InlineIndex}/c{pos.CharacterOffset}");
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
            // Clear our own link-hover so when the pointer leaves the embed
            // the previous hovered link doesn't appear stuck-on.
            if (_lastHoveredRun is not null)
            {
                var boxToInvalidate = _lastHoveredBox;
                foreach (var b in _snapshot.Blocks) ClearHover(b);
                _lastHoveredRun = null;
                _lastHoveredBox = null;
                InvalidateLinkHoverRegion(boxToInvalidate, null);
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

        var hoveredLink = hovered as LinkRun;
        var lastLink = _lastHoveredRun as LinkRun;
        bool linkChanged = !ReferenceEquals(hoveredLink, lastLink);
        var wantedShape = hoveredLink is not null
            ? Microsoft.UI.Input.InputSystemCursorShape.Hand
            : Microsoft.UI.Input.InputSystemCursorShape.IBeam;

        if (linkChanged)
        {
            // Clear previous hover state on every InlineContainerBox.
            foreach (var b in _snapshot.Blocks) ClearHover(b);
            if (hoveredBox is not null && hoveredLink is not null)
                hoveredBox.HoveredRun = hoveredLink;
            // Use targeted invalidation: only repaint the boxes whose link-hover
            // color actually changed.  This limits the repainted tile region,
            // reducing tile-boundary sub-pixel variance that causes glyph shake.
            InvalidateLinkHoverRegion(_lastHoveredBox, hoveredBox);
        }
        _lastHoveredRun = hovered;
        _lastHoveredBox = hoveredBox;

        SetCursorShape(wantedShape);
    }

    /// <summary>
    /// Invalidates the canvas regions that cover <paramref name="prev"/> and/or
    /// <paramref name="next"/> (the boxes whose link-hover color just changed).
    /// Falls back to a full invalidate only when neither box is known.
    /// </summary>
    private void InvalidateLinkHoverRegion(
        Layout.Boxes.InlineContainerBox? prev,
        Layout.Boxes.InlineContainerBox? next)
    {
        if (_canvas is null) return;
        if (prev is null && next is null)
        {
            _canvas.Invalidate();
            return;
        }
        if (prev is not null) _canvas.Invalidate(prev.Bounds);
        if (next is not null && !ReferenceEquals(prev, next)) _canvas.Invalidate(next.Bounds);
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
            _currentCursorShape = shape;
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
        for (int i = 0; i < _embedRects.Count; i++)
        {
            var r = _embedRects[i].Rect;
            if (pt.X >= r.X && pt.X <= r.X + r.Width &&
                pt.Y >= r.Y && pt.Y <= r.Y + r.Height)
                return true;
        }
        for (int i = 0; i < _blockEmbedRects.Count; i++)
        {
            var r = _blockEmbedRects[i];
            if (pt.X >= r.X && pt.X <= r.X + r.Width &&
                pt.Y >= r.Y && pt.Y <= r.Y + r.Height)
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
            if (pt.X >= r.X && pt.X <= r.X + r.Width &&
                pt.Y >= r.Y && pt.Y <= r.Y + r.Height)
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
        position = default;
        return false;
    }

    /// <summary>
    /// Syncs the selection highlight to the XAML _overlay using Rectangle elements.
    /// Called whenever _selection.Changed fires (SetAnchor / ExtendTo / Clear).
    /// By keeping selection on the XAML layer the DirectWrite canvas tiles are
    /// never dirtied during a drag, which eliminates tile-offset glyph shake.
    ///
    /// Pool pattern: Rectangles are never removed from _overlay.Children during a
    /// drag update.  Only their geometry properties (Width/Height/Canvas.Left/Top)
    /// and Visibility are mutated.  This avoids the XAML visual-tree mutation
    /// (Children.Remove / Children.Insert) that was triggering a layout/render pass
    /// on every pointer-move event and causing sub-pixel vertical jitter of nearby
    /// XAML elements (embedded buttons, etc.).
    /// </summary>
    private void UpdateSelectionOverlay()
    {
        if (_overlay is null) return;

        var snapshot = _snapshot;
        if (snapshot is null || !_selection.IsActive)
        {
            // Hide all pooled rectangles — don't remove them (cheaper).
            foreach (var r in _selectionOverlayRects)
                r.Visibility = Visibility.Collapsed;
            return;
        }

        // Compute the desired selection highlight color.
        var theme = Theme ?? _defaultTheme;
        var accentBase = theme.AccentColor ?? Color.FromArgb(0x66, 0x00, 0x67, 0xC0);
        var hl = Color.FromArgb(0x55, accentBase.R, accentBase.G, accentBase.B);

        // Reuse the cached brush when the color hasn't changed (common case during
        // a drag: the highlight color is constant throughout the gesture).
        if (_selectionBrush is null || _selectionBrushColor != hl)
        {
            _selectionBrush = new SolidColorBrush(hl);
            _selectionBrushColor = hl;
        }

        int poolIdx = 0;
        foreach (var rect in _selection.GetHighlightRects(snapshot))
        {
            // Integer-pixel snap.
            double x = Math.Floor(rect.X);
            double y = Math.Floor(rect.Y);
            double w = Math.Ceiling(rect.X + rect.Width) - x;
            double h = Math.Ceiling(rect.Y + rect.Height) - y;

            Rectangle r;
            if (poolIdx < _selectionOverlayRects.Count)
            {
                // Reuse existing pooled rectangle — no Children mutation.
                r = _selectionOverlayRects[poolIdx];
            }
            else
            {
                // Pool exhausted: allocate a new rectangle and add it once.
                r = new Rectangle { IsHitTestVisible = false };
                // ZIndex -1 places it behind embedded controls (default ZIndex 0).
                Canvas.SetZIndex(r, -1);
                _overlay.Children.Add(r);
                _selectionOverlayRects.Add(r);
            }

            r.Fill = _selectionBrush;
            r.Width = w;
            r.Height = h;
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            r.Visibility = Visibility.Visible;
            poolIdx++;
        }

        // Hide any remaining pooled rectangles beyond the current stripe count.
        for (int i = poolIdx; i < _selectionOverlayRects.Count; i++)
            _selectionOverlayRects[i].Visibility = Visibility.Collapsed;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // PointerExited is also wired for PointerCanceled and PointerCaptureLost.
        // Clear drag state so a canceled/interrupted gesture doesn't leave a stale
        // anchor that causes ghost-selection on the next PointerMoved, or a stale
        // _leftPointerCaptured that causes the next PointerReleased to misidentify
        // the button.
        _leftPointerCaptured = false;
        _selectionAnchor = null;
        _clickMode = ClickMode.Single; // reset so no stale word/block mode on next press

        // Clear hover state when the pointer leaves the canvas (or capture is
        // lost).  Without this, a link's hover colour and the hand cursor
        // persist even when the pointer is no longer over the control.
        if (_snapshot is null || _canvas is null) return;
        bool hadHover = _lastHoveredRun is not null;
        if (hadHover)
        {
            var boxToInvalidate = _lastHoveredBox;
            foreach (var b in _snapshot.Blocks) ClearHover(b);
            _lastHoveredRun = null;
            _lastHoveredBox = null;
            // Use targeted invalidation so only the paragraph with the link is
            // repainted.  This fires whenever the pointer leaves the canvas into
            // an embed (button, checkbox) — a full Invalidate() here would
            // repaint all visible tiles and trigger tile-offset glyph shake.
            InvalidateLinkHoverRegion(boxToInvalidate, null);
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
        // PointerCaptureLost synchronously (which calls OnPointerExited and sets
        // _leftPointerCaptured = false), so we must read the flag first.
        bool wasLeft = _leftPointerCaptured;
        _leftPointerCaptured = false;
        _selectionAnchor = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        if (!wasLeft) return;

        // Click handling for links: if no real selection occurred, raise LinkClick
        // when the click lands on a LinkRun.
        if (_snapshot is null) return;
        if (!_selection.Range.Normalized().IsEmpty) return; // text was dragged — not a click
        var pt = e.GetCurrentPoint(_canvas).Position;
        if (_snapshot.HitTest(pt, out var pos))
        {
            var link = FindLinkAt(pos);
            if (link is not null)
            {
                // Intercept internal fragment anchors (e.g. footnote back/forward
                // links) and scroll without surfacing them to external subscribers.
                if (link.Url.StartsWith("#footnote-", StringComparison.OrdinalIgnoreCase))
                {
                    HandleInternalAnchor(link.Url);
                }
                else
                {
                    LinkClick?.Invoke(this, new MarkdownLinkClickEventArgs(link.Url, link.Title));
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
    private void HandleInternalAnchor(string url)
    {
        if (_snapshot is null) return;
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
        if (!isDef && !isRef) return;
        string orderStr = isDef ? url.Substring(defPrefix.Length) : url.Substring(refPrefix.Length);
        if (!int.TryParse(orderStr, out int order)) return;

        // Walk blocks looking for the tagged block index stored in the
        // footnote index dictionary on the snapshot.
        int? targetIndex = isDef
            ? _snapshot.FootnoteDefBlock(order)
            : _snapshot.FootnoteRefBlock(order);
        if (targetIndex is { } idx) ScrollToBlock(idx);
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
                MarkdownClipboardWriter.Copy(_snapshot.SourceMap, _selection.Range);
                e.Handled = true;
                return;
            case VirtualKey.A when ctrl:
                _selection.SetAnchor(DocumentPosition.Zero);
                _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
                e.Handled = true;
                return;
            case VirtualKey.Tab:
                e.Handled = MoveFocus(reverse: shift);
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

        if (_focusedItemIndex < 0)
        {
            // No item is focused yet — enter focus cycling at the boundary.
            _focusedItemIndex = reverse ? items.Count - 1 : 0;
        }
        else
        {
            // Already focused — check if we're at the exit boundary.
            bool atEnd   = !reverse && _focusedItemIndex == items.Count - 1;
            bool atStart = reverse  && _focusedItemIndex == 0;
            if (atEnd || atStart)
            {
                // Let Tab/Shift+Tab leave the control to the next focusable element.
                _focusedItemIndex = -1;
                UpdateFocusRing();
                return false;
            }
            _focusedItemIndex = reverse ? _focusedItemIndex - 1 : _focusedItemIndex + 1;
        }
        UpdateFocusRing();
        ScrollFocusedItemIntoView();
        return true;
    }

    /// <summary>Fires LinkClick or simulates click on an inline embed for the currently focused item.</summary>
    private bool ActivateFocusedItem()
    {
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        var item = items[_focusedItemIndex];
        if (!item.IsLink) return false;

        // Find the LinkRun in the snapshot and fire LinkClick.
        if (_snapshot is null) return false;
        var pos = new DocumentPosition(item.BlockIndex, item.InlineIndex, 0);
        if (FindLinkAt(pos) is { } lr)
        {
            if (lr.Url.StartsWith("#footnote-", StringComparison.Ordinal))
                HandleInternalAnchor(lr.Url);
            else
                LinkClick?.Invoke(this, new MarkdownLinkClickEventArgs(lr.Url, lr.Title));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Places or moves the focus-ring <see cref="Border"/> on the overlay to
    /// surround the currently focused item. Hides the ring if nothing is focused.
    /// </summary>
    private void UpdateFocusRing()
    {
        if (_overlay is null) return;

        // Lazily create the focus ring element.
        if (_focusRing is null)
        {
            _focusRing = new Microsoft.UI.Xaml.Controls.Border
            {
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
            };
            Canvas.SetZIndex(_focusRing, 1);
            _overlay.Children.Add(_focusRing);
        }

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
        {
            _focusRing.Visibility = Visibility.Collapsed;
            return;
        }

        var item = items[_focusedItemIndex];
        var rect = GetFocusableItemRect(item);
        if (rect is not { Width: > 0, Height: > 0 } r)
        {
            _focusRing.Visibility = Visibility.Collapsed;
            return;
        }

        // Style with accent color (use the link foreground, which is the accent).
        var accent = _themeSnapshot?.GetStyle(MarkdownElementKeys.Link).Foreground
                     ?? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        _focusRing.BorderBrush = new SolidColorBrush(accent);

        const double pad = 2.0;
        Canvas.SetLeft(_focusRing, r.X - pad);
        Canvas.SetTop(_focusRing, r.Y - pad);
        _focusRing.Width  = r.Width  + pad * 2;
        _focusRing.Height = r.Height + pad * 2;
        _focusRing.Visibility = Visibility.Visible;
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

    private static Rect? GetFocusableItemRectFromBlock(BlockBox box, Layout.FocusableItem item)
    {
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
    private void ScrollFocusedItemIntoView()
    {
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _scroll is null) return;
        var item = items[_focusedItemIndex];
        if (GetFocusableItemRect(item) is not { } rect) return;

        double top    = _scroll.VerticalOffset;
        double bottom = top + _scroll.ViewportHeight;
        const double margin = 24.0;

        if (rect.Top < top + margin)
            _scroll.ChangeView(null, Math.Max(0, rect.Top - margin), null, disableAnimation: false);
        else if (rect.Bottom > bottom - margin)
            _scroll.ChangeView(null, rect.Bottom - _scroll.ViewportHeight + margin, null, disableAnimation: false);
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
            // Clear any lingering selection to avoid stale visual.
            _selection.Clear();
            _canvas?.Invalidate();
            return (pos, pos);
        }
        var (start, end) = icb.GetWordBoundaries(pos);
        _selection.SetAnchor(start);
        _selection.ExtendTo(end);
        _canvas?.Invalidate();
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
            // Clear any lingering selection to avoid stale visual.
            _selection.Clear();
            _canvas?.Invalidate();
            return (pos, pos);
        }
        var (start, end) = icb.GetBlockBoundaries();
        _selection.SetAnchor(start);
        _selection.ExtendTo(end);
        _canvas?.Invalidate();
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

        var copyItem = new MenuFlyoutItem { Text = "Copy" };
        copyItem.IsEnabled = _selection.IsActive;
        copyItem.Click += (_, _) =>
        {
            if (_snapshot is not null && _selection.IsActive)
                MarkdownClipboardWriter.Copy(_snapshot.SourceMap, _selection.Range);
        };
        menu.Items.Add(copyItem);

        var selectAllItem = new MenuFlyoutItem { Text = "Select All" };
        selectAllItem.Click += (_, _) =>
        {
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
}

public sealed class MarkdownLinkClickEventArgs : EventArgs
{
    public MarkdownLinkClickEventArgs(string url, string? title)
    {
        Url = url;
        Title = title;
    }
    public string Url { get; }
    public string? Title { get; }
}

