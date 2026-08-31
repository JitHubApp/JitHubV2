using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class AppSvgViewport : UserControl
{
    private const long MaximumCacheBytes = 64L * 1024 * 1024;
    private const double CanvasInset = 16;
    private const double TileSeamOverlapPixels = 1;
    private static readonly TimeSpan ZoomSettleDelay = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherQueueTimer _settleTimer;
    private readonly SvgTileCache _cache = new(MaximumCacheBytes);
    private readonly SettledZoomTracker _zoomTracker = new();
    private IRepositorySvgRasterizer _rasterizer = new RepositorySvgRasterizer();
    private RepositorySvgDocument? _document;
    private ScrollViewer? _scrollHost;
    private CancellationTokenSource? _renderCancellation;
    private long _renderGeneration;
    private bool _eventsAttached;
    private string _renderStatus = "empty";
    private double _lastReportedZoomPercent = 100;

    internal event EventHandler<AppSvgRenderFailedEventArgs>? RenderFailed;
    internal event EventHandler? ZoomSettled;

    public AppSvgViewport()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, L("RepoCode/Svg/AutomationName", "SVG preview"));
        SetRenderStatus("empty");
        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = ZoomSettleDelay;
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += SettleTimer_Tick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void AttachScrollHost(ScrollViewer scrollHost)
    {
        ArgumentNullException.ThrowIfNull(scrollHost);
        if (ReferenceEquals(_scrollHost, scrollHost))
        {
            AttachEvents();
            return;
        }

        DetachEvents();
        _scrollHost = scrollHost;
        _zoomTracker.Reset(scrollHost.ZoomFactor);
        _lastReportedZoomPercent = scrollHost.ZoomFactor * 100;
        AttachEvents();
        UpdateSurfaceSize();
    }

    internal void SetDocument(
        RepositorySvgDocument document,
        IRepositorySvgRasterizer rasterizer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rasterizer);

        RepositorySvgDocument? previous = _document;
        CancelRender();
        _document = document;
        _rasterizer = rasterizer;
        _zoomTracker.Reset(_scrollHost?.ZoomFactor ?? 1);
        _cache.Clear();
        TileCanvas.Children.Clear();
        DisposeDocument(previous);
        SetRenderStatus("rendering");
        ScheduleRender(immediate: true);
    }

    internal void Clear()
    {
        RepositorySvgDocument? previous = _document;
        _document = null;
        _zoomTracker.ClearPending();
        CancelRender();
        _cache.Clear();
        TileCanvas.Children.Clear();
        DisposeDocument(previous);
        SetRenderStatus("empty");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachEvents();
        UpdateSurfaceSize();
        ScheduleRender(immediate: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachEvents();
        _settleTimer.Stop();
        CancelRender();
    }

    private void AttachEvents()
    {
        if (_eventsAttached || !IsLoaded || _scrollHost is null)
        {
            return;
        }

        _scrollHost.ViewChanged += ScrollHost_ViewChanged;
        _scrollHost.SizeChanged += ScrollHost_SizeChanged;
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += XamlRoot_Changed;
        }

        _eventsAttached = true;
    }

    private void DetachEvents()
    {
        if (!_eventsAttached)
        {
            return;
        }

        if (_scrollHost is not null)
        {
            _scrollHost.ViewChanged -= ScrollHost_ViewChanged;
            _scrollHost.SizeChanged -= ScrollHost_SizeChanged;
        }

        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= XamlRoot_Changed;
        }

        _eventsAttached = false;
    }

    private void ScrollHost_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scrollHost is not null)
        {
            _zoomTracker.Observe(_scrollHost.ZoomFactor);
            double zoomPercent = _scrollHost.ZoomFactor * 100;
            if (Math.Abs(zoomPercent - _lastReportedZoomPercent) > 0.001)
            {
                double previous = _lastReportedZoomPercent;
                _lastReportedZoomPercent = zoomPercent;
                if (FrameworkElementAutomationPeer.FromElement(this) is AppSvgViewportAutomationPeer peer)
                {
                    peer.RaiseZoomLevelChanged(previous, zoomPercent);
                }
            }
        }

        ScheduleRender(immediate: false);
    }

    private void ScrollHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSurfaceSize();
        ScheduleRender(immediate: false);
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) =>
        ScheduleRender(immediate: false);

    private void TileCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleRender(immediate: false);

    private void UpdateSurfaceSize()
    {
        if (_scrollHost is null)
        {
            return;
        }

        double width = _scrollHost.ViewportWidth > 0
            ? _scrollHost.ViewportWidth
            : _scrollHost.ActualWidth;
        double height = _scrollHost.ViewportHeight > 0
            ? _scrollHost.ViewportHeight
            : _scrollHost.ActualHeight;
        if (width > 0 && height > 0)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
        }
    }

    private void ScheduleRender(bool immediate)
    {
        if (!IsLoaded || _document is null)
        {
            return;
        }

        _settleTimer.Stop();
        if (immediate)
        {
            DispatcherQueue.TryEnqueue(RenderNow);
        }
        else
        {
            _settleTimer.Start();
        }
    }

    private void SettleTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        RenderNow();

        if (!_zoomTracker.TrySettle(out _))
        {
            return;
        }

        ZoomSettled?.Invoke(this, EventArgs.Empty);
    }

    private void RenderNow()
    {
        RepositorySvgDocument? document = _document;
        SvgRenderPlan? plan = document is null ? null : CreateRenderPlan(document);
        if (document is null || plan is null || plan.Tiles.Count == 0)
        {
            return;
        }

        CancelRender();
        CancellationTokenSource cancellation = new();
        _renderCancellation = cancellation;
        long generation = ++_renderGeneration;
        UiTaskGuard.Observe(RenderAndPublishAsync(document, plan, generation, cancellation.Token), "ui-app-svg-viewport");
    }

    private async Task RenderAndPublishAsync(
        RepositorySvgDocument document,
        SvgRenderPlan plan,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            List<SvgTilePresentation> missing = [];
            Dictionary<SvgTileKey, RepositorySvgTile> tiles = new();
            foreach (SvgTilePresentation presentation in plan.Tiles)
            {
                if (_cache.TryGet(presentation.Key, out RepositorySvgTile? cached))
                {
                    tiles.Add(presentation.Key, cached!);
                }
                else
                {
                    missing.Add(presentation);
                }
            }

            if (missing.Count > 0)
            {
                RepositorySvgTile[] rendered = await Task.Run(() =>
                {
                    RepositorySvgTile[] output = new RepositorySvgTile[missing.Count];
                    for (int index = 0; index < missing.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output[index] = _rasterizer.RasterizeTile(
                            document,
                            missing[index].Request,
                            cancellationToken);
                    }

                    return output;
                }, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                for (int index = 0; index < rendered.Length; index++)
                {
                    SvgTileKey key = missing[index].Key;
                    _cache.Add(key, rendered[index]);
                    tiles[key] = rendered[index];
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _renderGeneration || !ReferenceEquals(document, _document) || !IsLoaded)
            {
                return;
            }

            PublishTiles(plan, tiles);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation == _renderGeneration && ReferenceEquals(document, _document) && IsLoaded)
            {
                SetRenderStatus("failed");
                RenderFailed?.Invoke(this, new AppSvgRenderFailedEventArgs(exception));
            }
        }
    }

    private void PublishTiles(
        SvgRenderPlan plan,
        IReadOnlyDictionary<SvgTileKey, RepositorySvgTile> tiles)
    {
        List<Image> images = new(plan.Tiles.Count);
        bool hasAccessibleImage = false;
        foreach (SvgTilePresentation presentation in plan.Tiles)
        {
            if (!tiles.TryGetValue(presentation.Key, out RepositorySvgTile? tile))
            {
                continue;
            }

            WriteableBitmap bitmap = new(tile.PixelWidth, tile.PixelHeight);
            using (Stream stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(tile.BgraPixels, 0, tile.BgraPixels.Length);
            }

            bitmap.Invalidate();
            Image image = new()
            {
                Source = bitmap,
                Width = presentation.LogicalWidth,
                Height = presentation.LogicalHeight,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };
            if (!hasAccessibleImage)
            {
                AutomationProperties.SetAutomationId(image, "SvgPreviewRenderedImage");
                AutomationProperties.SetName(image, L("RepoCode/Svg/AutomationName", "Rendered SVG"));
                AutomationProperties.SetAccessibilityView(image, AccessibilityView.Content);
                hasAccessibleImage = true;
            }
            else
            {
                AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
            }

            Canvas.SetLeft(image, presentation.LogicalX);
            Canvas.SetTop(image, presentation.LogicalY);
            images.Add(image);
        }

        TileCanvas.Children.Clear();
        foreach (Image image in images)
        {
            TileCanvas.Children.Add(image);
        }

        SetRenderStatus($"rendered:tiles:{images.Count}");
    }

    internal string RenderStatus => _renderStatus;

    internal bool CanZoom => _scrollHost is not null;

    internal double MinimumZoomPercent => (_scrollHost?.MinZoomFactor ?? 0.1f) * 100;

    internal double MaximumZoomPercent => (_scrollHost?.MaxZoomFactor ?? 8f) * 100;

    internal double ZoomPercent => (_scrollHost?.ZoomFactor ?? 1f) * 100;

    internal void ZoomToPercent(double zoomPercent)
    {
        if (_scrollHost is null || !double.IsFinite(zoomPercent))
        {
            return;
        }

        double clamped = Math.Clamp(zoomPercent, MinimumZoomPercent, MaximumZoomPercent);
        double targetZoomFactor = clamped / 100;
        ViewportZoomTarget target = ViewportZoomAnchor.PreserveCenter(
            _scrollHost.HorizontalOffset,
            _scrollHost.VerticalOffset,
            _scrollHost.ViewportWidth,
            _scrollHost.ViewportHeight,
            _scrollHost.ZoomFactor,
            targetZoomFactor);
        _scrollHost.ChangeView(
            horizontalOffset: target.HorizontalOffset,
            verticalOffset: target.VerticalOffset,
            zoomFactor: checked((float)targetZoomFactor),
            disableAnimation: false);
    }

    internal void ZoomByUnit(ZoomUnit zoomUnit)
    {
        double target = zoomUnit switch
        {
            ZoomUnit.LargeDecrement => ZoomPercent / 1.5,
            ZoomUnit.SmallDecrement => ZoomPercent / 1.1,
            ZoomUnit.LargeIncrement => ZoomPercent * 1.5,
            ZoomUnit.SmallIncrement => ZoomPercent * 1.1,
            _ => ZoomPercent,
        };
        ZoomToPercent(target);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new AppSvgViewportAutomationPeer(this);

    private void SetRenderStatus(string status)
    {
        _renderStatus = status;
        AutomationProperties.SetItemStatus(this, status);
    }

    private SvgRenderPlan? CreateRenderPlan(RepositorySvgDocument document)
    {
        double canvasWidth = TileCanvas.ActualWidth;
        double canvasHeight = TileCanvas.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0 || document.Width <= 0 || document.Height <= 0)
        {
            return null;
        }

        double fitScale = Math.Min(canvasWidth / document.Width, canvasHeight / document.Height);
        if (!double.IsFinite(fitScale) || fitScale <= 0)
        {
            return null;
        }

        double imageWidth = document.Width * fitScale;
        double imageHeight = document.Height * fitScale;
        double imageLeft = (canvasWidth - imageWidth) / 2;
        double imageTop = (canvasHeight - imageHeight) / 2;
        double zoom = Math.Clamp(_scrollHost?.ZoomFactor ?? 1, 0.1, 8);
        double dpiScale = Math.Max(0.5, XamlRoot?.RasterizationScale ?? 1);
        double pixelsPerLogicalUnit = zoom * dpiScale;
        float pixelsPerSourceUnit = checked((float)(fitScale * pixelsPerLogicalUnit));
        int outputWidth = Math.Max(1, checked((int)Math.Ceiling(imageWidth * pixelsPerLogicalUnit)));
        int outputHeight = Math.Max(1, checked((int)Math.Ceiling(imageHeight * pixelsPerLogicalUnit)));

        double visibleLeft = ((_scrollHost?.HorizontalOffset ?? 0) / zoom) - CanvasInset;
        double visibleTop = ((_scrollHost?.VerticalOffset ?? 0) / zoom) - CanvasInset;
        double visibleWidth = (_scrollHost?.ViewportWidth ?? canvasWidth) / zoom;
        double visibleHeight = (_scrollHost?.ViewportHeight ?? canvasHeight) / zoom;
        int visiblePixelLeft = Math.Clamp(
            (int)Math.Floor((visibleLeft - imageLeft) * pixelsPerLogicalUnit),
            0,
            outputWidth - 1);
        int visiblePixelTop = Math.Clamp(
            (int)Math.Floor((visibleTop - imageTop) * pixelsPerLogicalUnit),
            0,
            outputHeight - 1);
        int visiblePixelRight = Math.Clamp(
            (int)Math.Ceiling((visibleLeft + visibleWidth - imageLeft) * pixelsPerLogicalUnit),
            1,
            outputWidth);
        int visiblePixelBottom = Math.Clamp(
            (int)Math.Ceiling((visibleTop + visibleHeight - imageTop) * pixelsPerLogicalUnit),
            1,
            outputHeight);

        const int tileEdge = RepositorySvgTileRequest.MaximumTileEdge;
        int firstTileX = Math.Max(0, (visiblePixelLeft / tileEdge) - 1);
        int firstTileY = Math.Max(0, (visiblePixelTop / tileEdge) - 1);
        int lastTileX = Math.Min((outputWidth - 1) / tileEdge, (visiblePixelRight / tileEdge) + 1);
        int lastTileY = Math.Min((outputHeight - 1) / tileEdge, (visiblePixelBottom / tileEdge) + 1);
        int scaleBits = BitConverter.SingleToInt32Bits(pixelsPerSourceUnit);
        List<SvgTilePresentation> presentations = [];

        for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
            {
                int pixelX = tileX * tileEdge;
                int pixelY = tileY * tileEdge;
                int pixelWidth = Math.Min(tileEdge, outputWidth - pixelX);
                int pixelHeight = Math.Min(tileEdge, outputHeight - pixelY);
                double logicalWidth = pixelWidth / pixelsPerLogicalUnit;
                double logicalHeight = pixelHeight / pixelsPerLogicalUnit;
                if (pixelX + pixelWidth < outputWidth)
                {
                    logicalWidth += TileSeamOverlapPixels / pixelsPerLogicalUnit;
                }

                if (pixelY + pixelHeight < outputHeight)
                {
                    logicalHeight += TileSeamOverlapPixels / pixelsPerLogicalUnit;
                }

                RepositorySvgTileRequest request = new(
                    pixelX,
                    pixelY,
                    pixelWidth,
                    pixelHeight,
                    pixelsPerSourceUnit);
                SvgTileKey key = new(outputWidth, outputHeight, scaleBits, pixelX, pixelY);
                presentations.Add(new SvgTilePresentation(
                    key,
                    request,
                    imageLeft + (pixelX / pixelsPerLogicalUnit),
                    imageTop + (pixelY / pixelsPerLogicalUnit),
                    logicalWidth,
                    logicalHeight));
            }
        }

        return new SvgRenderPlan(presentations);
    }

    private void CancelRender()
    {
        _renderGeneration++;
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _renderCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static void DisposeDocument(RepositorySvgDocument? document)
    {
        if (document is not null)
        {
            UiTaskGuard.Observe(
                Task.Run(document.Dispose),
                "ui-app-svg-viewport");
        }
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private readonly record struct SvgTileKey(
        int OutputWidth,
        int OutputHeight,
        int ScaleBits,
        int PixelX,
        int PixelY);

    private sealed record SvgTilePresentation(
        SvgTileKey Key,
        RepositorySvgTileRequest Request,
        double LogicalX,
        double LogicalY,
        double LogicalWidth,
        double LogicalHeight);

    private sealed record SvgRenderPlan(IReadOnlyList<SvgTilePresentation> Tiles);

    private sealed class SvgTileCache(long maximumBytes)
    {
        private readonly Dictionary<SvgTileKey, LinkedListNode<Entry>> _entries = [];
        private readonly LinkedList<Entry> _lru = [];
        private long _bytes;

        public bool TryGet(SvgTileKey key, out RepositorySvgTile? tile)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                tile = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddLast(node);
            tile = node.Value.Tile;
            return true;
        }

        public void Add(SvgTileKey key, RepositorySvgTile tile)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                _bytes -= existing.Value.Tile.ByteCount;
                _lru.Remove(existing);
                _entries.Remove(key);
            }

            LinkedListNode<Entry> node = _lru.AddLast(new Entry(key, tile));
            _entries.Add(key, node);
            _bytes += tile.ByteCount;
            while (_bytes > maximumBytes && _lru.First is { } oldest)
            {
                _lru.RemoveFirst();
                _entries.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Tile.ByteCount;
            }
        }

        public void Clear()
        {
            _entries.Clear();
            _lru.Clear();
            _bytes = 0;
        }

        private sealed record Entry(SvgTileKey Key, RepositorySvgTile Tile);
    }
}

internal sealed class AppSvgRenderFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
