using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnThemeChanged()));

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
            typeof(MarkdownRendererControl), new PropertyMetadata(null));

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

    public MarkdownRendererControl()
    {
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += (_, _) => OnLoadedInternal();
        Unloaded += (_, _) => OnUnloaded();
        ActualThemeChanged += (_, _) => OnThemeChanged();
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
        RequestRebuild();
    }

    private void OnUnloaded()
    {
        if (_sizeChangedHandler is not null)
        {
            SizeChanged -= _sizeChangedHandler;
            _sizeChangedHandler = null;
        }
        _pipelineCts?.Cancel();
    }

    private void OnThemeChanged()
    {
        if (Theme is { } t) t.Invalidate();
        RequestRebuild();
    }

    /// <summary>Kicks off (or re-kicks) the parse + layout pipeline.</summary>
    public void RequestRebuild()
    {
        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = new CancellationTokenSource();
        _ = RebuildAsync(_pipelineCts.Token);
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        if (_canvas is null) return;
        var width = (float)Math.Max(50, ActualWidth);
        if (width <= 0) return;
        _lastWidth = width;

        var registry = ExtensionRegistry ?? new MarkdownExtensionRegistry();
        var pipeline = registry.BuildPipeline();
        var parser = new MarkdigParser(pipeline);
        var source = Markdown ?? string.Empty;

        ParsedMarkdown parsed;
        try { parsed = await parser.ParseAsync(source, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        var sourceMap = new MarkdownSourceMap(parsed.SourceText);
        var theme = Theme ?? _defaultTheme;
        var themeSnapshot = new ThemeResolver(this, theme).CreateSnapshot();
        // Use the shared CanvasDevice (always available, no visual-tree required).
        // CanvasVirtualControl only has a device after CreateResources fires, so
        // passing _canvas directly would crash if layout runs before first draw.
        var device = CanvasDevice.GetSharedDevice();
        var ctx = new MarkdownLayoutContext(device, themeSnapshot, sourceMap, registry, FlowDirection);
        var builder = new LayoutBuilder(ctx, EmbedFactory);

        LayoutSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => builder.Build(parsed.Document, width), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        _snapshot = snapshot;
        _canvas.Width = width;
        _canvas.Height = Math.Max(1, snapshot.Size.Height);
        _root!.Width = width;
        _root.Height = _canvas.Height;
        _overlay!.Width = width;
        _overlay.Height = _canvas.Height;

        // UI thread: realize hosted FrameworkElements + image LoadCompleted hooks.
        _overlay.Children.Clear();
        foreach (var b in snapshot.Blocks) PlaceEmbeds(b);

        _canvas.Invalidate();
    }

    private void PlaceEmbeds(Layout.BlockBox box)
    {
        switch (box)
        {
            case Layout.Boxes.EmbedBox eb:
            {
                try
                {
                    var fe = eb.Factory.CreateBlock(eb.SourceBlock);
                    eb.RealizedElement = fe;
                    fe.Width = eb.Bounds.Width - eb.Margin.Left - eb.Margin.Right;
                    fe.Height = eb.Bounds.Height - eb.Margin.Top - eb.Margin.Bottom;
                    Canvas.SetLeft(fe, eb.Bounds.X + eb.Margin.Left);
                    Canvas.SetTop(fe, eb.Bounds.Y + eb.Margin.Top);
                    _overlay!.Children.Add(fe);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MarkdownRendererControl] EmbedBox factory threw: {ex.Message}");
                }
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
                    try
                    {
                        var fe = run.ElementFactory();
                        run.RealizedElement = fe;
                        fe.Width = rect.Width;
                        fe.Height = rect.Height;
                        Canvas.SetLeft(fe, rect.X);
                        Canvas.SetTop(fe, rect.Y);
                        _overlay!.Children.Add(fe);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MarkdownRendererControl] inline embed factory threw: {ex.Message}");
                    }
                }
                break;
            }
            case Layout.Boxes.ListItemBox lib:
                PlaceEmbeds(lib.Marker);
                PlaceEmbeds(lib.Content);
                break;
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) PlaceEmbeds(cell);
                break;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children) PlaceEmbeds(c);
                break;
        }
    }

    private void OnImageLoadCompleted(object? sender, EventArgs e)
    {
        // Image dimensions just became known; re-run layout so blocks below
        // shift. Run on UI thread; cheap because parsed AST + bitmap are cached.
        if (DispatcherQueue is { } dq)
        {
            dq.TryEnqueue(() => RequestRebuild());
        }
        else
        {
            RequestRebuild();
        }
    }

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (_snapshot is null) return;
        var theme = Theme ?? _defaultTheme;
        var hl = theme.AccentColor ?? Color.FromArgb(0x66, 0x00, 0x67, 0xC0);
        hl = Color.FromArgb(0x55, hl.R, hl.G, hl.B);
        foreach (var region in args.InvalidatedRegions)
        {
            using var ds = sender.CreateDrawingSession(region);
            // Selection beneath text.
            _selection.PaintHighlight(ds, _snapshot, hl);
            _snapshot.Paint(ds, region);
        }
    }

    // ---- Input ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSelectionEnabled || _snapshot is null || _canvas is null) return;
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(_canvas).Position;
        if (_snapshot.HitTest(pt, out var pos))
        {
            _selectionAnchor = pos;
            _selection.SetAnchor(pos);
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

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_snapshot is null || _canvas is null) return;
        var pt = e.GetCurrentPoint(_canvas).Position;

        // Drag-select.
        if (_selectionAnchor is not null)
        {
            if (_snapshot.HitTest(pt, out var pos))
            {
                _selection.ExtendTo(pos);
                _canvas.Invalidate();
            }
        }

        // Hover effect for links + cursor change.
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

        bool changed = !ReferenceEquals(hovered, _lastHoveredRun);
        if (changed)
        {
            // Clear previous hover state on every InlineContainerBox.
            foreach (var b in _snapshot.Blocks) ClearHover(b);
            if (hoveredBox is not null && hovered is LinkRun)
                hoveredBox.HoveredRun = hovered;
            _lastHoveredRun = hovered;
            _canvas.Invalidate();
        }

        try
        {
            ProtectedCursor = hovered is LinkRun
                ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand)
                : Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.IBeam);
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
            _canvas?.Invalidate();
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

