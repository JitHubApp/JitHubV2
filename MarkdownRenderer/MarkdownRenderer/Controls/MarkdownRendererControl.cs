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
    private readonly List<Rectangle> _selectionOverlayRects = new();

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

    public event EventHandler<MarkdownLinkClickEventArgs>? LinkClick;

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
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        // Release native resources & hosted embeds so re-attaching the
        // control to a new visual parent doesn't leak DirectWrite layouts
        // or keep stale FrameworkElements alive.
        _overlay?.Children.Clear();
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
        // Use the shared CanvasDevice (always available, no visual-tree required).
        // CanvasVirtualControl only has a device after CreateResources fires, so
        // passing _canvas directly would crash if layout runs before first draw.
        var device = CanvasDevice.GetSharedDevice();
        var ctx = new MarkdownLayoutContext(device, themeSnapshot, sourceMap, registry, FlowDirection);
        var builder = new LayoutBuilder(ctx, EmbedFactory);

        ct.ThrowIfCancellationRequested();
        var snapshot = await Task.Run(() => builder.Build(parsed.Document, width), ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

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

        // UI thread: collect embed plans (don't realise yet), hook image
        // LoadCompleted, then realise only embeds that fall in the current
        // viewport. Hooking _scroll.ViewChanged drives subsequent realisation
        // as the user scrolls.
        DerealizeAllEmbeds();
        _overlay.Children.Clear();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _embedPlans.Clear();
        _selectionOverlayRects.Clear(); // overlay was just cleared above
        _selection.Clear();             // stale selection no longer valid after re-layout
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
        if (_scroll is null || _embedPlans.Count == 0) return;
        double top = _scroll.VerticalOffset;
        double bottom = top + _scroll.ViewportHeight;
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

        // Surface the realised count to UI automation so external test
        // harnesses can verify virtualisation directly instead of relying on
        // descendant-button heuristics, which are sensitive to UIA peer
        // caching behaviour.
        try
        {
            int realised = 0;
            foreach (var pl in _embedPlans) if (pl.Realized is not null) realised++;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, $"realized:{realised}");
        }
        catch { /* AutomationProperties not yet attached — ignore. */ }
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

    private void OnImageLoadCompleted(object? sender, EventArgs e)
    {
        // CanvasBitmap.LoadAsync continues on a thread-pool thread.  Always
        // marshal to the UI thread — RequestRebuild manipulates the canvas
        // and CTS state, both of which require thread-affinity.  Drop the
        // event silently if we have no dispatcher (control already unloaded).
        var dq = DispatcherQueue;
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            // Coalesce image-load storms by debouncing through the existing
            // RequestRebuild path: subsequent calls cancel the prior CTS.
            RequestRebuild();
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
            return;
        }

        Focus(FocusState.Pointer);
        if (_snapshot.HitTest(pt, out var pos))
        {
            _selectionAnchor = pos;
            _selection.SetAnchor(pos);
            // Invalidate to repaint link-hover state (hover suppressed during drag).
            _canvas.Invalidate();
            _canvas.CapturePointer(e.Pointer);
        }
        else
        {
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
    /// </summary>
    private void SetCursorShape(Microsoft.UI.Input.InputSystemCursorShape? shape)
    {
        if (shape == _currentCursorShape) return;
        try
        {
            ProtectedCursor = shape is { } s
                ? Microsoft.UI.Input.InputSystemCursor.Create(s)
                : null;
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
                int charOffset = rightHalf ? run.Text.Length : 0;
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
    /// </summary>
    private void UpdateSelectionOverlay()
    {
        if (_overlay is null) return;

        // Remove previous selection rectangles.
        foreach (var r in _selectionOverlayRects)
            _overlay.Children.Remove(r);
        _selectionOverlayRects.Clear();

        var snapshot = _snapshot;
        if (snapshot is null || !_selection.IsActive) return;

        var theme = Theme ?? _defaultTheme;
        var accentBase = theme.AccentColor ?? Color.FromArgb(0x66, 0x00, 0x67, 0xC0);
        var hl = Color.FromArgb(0x55, accentBase.R, accentBase.G, accentBase.B);
        var brush = new SolidColorBrush(hl);

        foreach (var rect in _selection.GetHighlightRects(snapshot))
        {
            // Integer-pixel snap (same as SelectionController.PaintHighlight).
            double x = Math.Floor(rect.X);
            double y = Math.Floor(rect.Y);
            double w = Math.Ceiling(rect.X + rect.Width) - x;
            double h = Math.Ceiling(rect.Y + rect.Height) - y;

            var r = new Rectangle
            {
                Fill = brush,
                Width = w,
                Height = h,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            // Insert at index 0 so selection is behind embedded controls.
            _overlay.Children.Insert(0, r);
            _selectionOverlayRects.Add(r);
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
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
        _canvas.ReleasePointerCapture(e.Pointer);
        _selectionAnchor = null;

        // Click handling for links: if no real selection occurred, raise LinkClick
        // when the click lands on a LinkRun.
        if (_snapshot is null) return;
        if (!_selection.Range.Normalized().IsEmpty
            && !_selection.Range.Start.Equals(_selection.Range.End))
        {
            return;
        }
        var pt = e.GetCurrentPoint(_canvas).Position;
        if (_snapshot.HitTest(pt, out var pos))
        {
            var link = FindLinkAt(pos);
            if (link is not null) LinkClick?.Invoke(this, new MarkdownLinkClickEventArgs(link.Url, link.Title));
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_snapshot is null) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (ctrl && e.Key == VirtualKey.C && _selection.IsActive)
        {
            MarkdownClipboardWriter.Copy(_snapshot.SourceMap, _selection.Range);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.A)
        {
            _selection.SetAnchor(DocumentPosition.Zero);
            _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
            // No _canvas.Invalidate() — overlay handles selection display.
            e.Handled = true;
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

