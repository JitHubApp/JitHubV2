using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class AppSvgViewport : UserControl
{
    private const double TileSeamOverlapPixels = 1;
    private static readonly TimeSpan ZoomSettleDelay = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherQueueTimer _settleTimer;
    private readonly SettledZoomTracker _zoomTracker = new();
    private IRepositorySvgRasterizer? _rasterizer;
    private RepositorySvgDocument? _document;
    private ScrollViewer? _scrollHost;
    private CancellationTokenSource? _renderCancellation;
    private SvgPublishedPlan? _publishedPlan;
    private CanvasDevice? _canvasDevice;
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
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Content);
        AutomationProperties.SetAccessibilityView(TileCanvas, AccessibilityView.Raw);
        TileCanvas.Draw += TileCanvas_Draw;
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
        IRepositorySvgRasterizer rasterizer,
        bool retainCurrentBitmap = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rasterizer);

        RepositorySvgDocument? previous = _document;
        CancelRender();
        _document = document;
        _rasterizer = rasterizer;
        _zoomTracker.Reset(_scrollHost?.ZoomFactor ?? 1);
        if (!retainCurrentBitmap)
        {
            ReplacePublishedPlan(null);
        }
        DisposeDocument(previous);
        AutomationProperties.SetHelpText(this, document.Info.Description ?? string.Empty);
        SetRenderStatus("rendering");
        ScheduleRender(immediate: true);
    }

    internal void Clear()
    {
        RepositorySvgDocument? previous = _document;
        _document = null;
        _rasterizer = null;
        _zoomTracker.ClearPending();
        CancelRender();
        ReplacePublishedPlan(null);
        DisposeDocument(previous);
        AutomationProperties.SetHelpText(this, string.Empty);
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
        IRepositorySvgRasterizer? rasterizer = _rasterizer;
        CanvasDevice? device = _canvasDevice;
        SvgRenderPlan? plan = document is null || device is null
            ? null
            : CreateRenderPlan(document, SelectPixelFormat(device));
        if (document is null ||
            rasterizer is null ||
            device is null ||
            plan is null ||
            plan.Tiles.Count == 0)
        {
            return;
        }

        CancelRender();
        if (!SvgRenderWork.TryCreate(plan, device, out SvgRenderWork? work))
        {
            // A retained transition bitmap is allowed only while it fits in the
            // same aggregate budget as the replacement. Release it and retry
            // before reporting a typed resource-limit failure.
            ReplacePublishedPlan(null);
            if (!SvgRenderWork.TryCreate(plan, device, out work))
            {
                SetRenderStatus("failed");
                RenderFailed?.Invoke(
                    this,
                    new AppSvgRenderFailedEventArgs(
                        new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded)));
                return;
            }
        }

        CancellationTokenSource cancellation = new();
        _renderCancellation = cancellation;
        long generation = ++_renderGeneration;
        UiTaskGuard.Observe(
            RenderAndPublishAsync(
                document,
                rasterizer,
                device,
                plan,
                work!,
                generation,
                cancellation.Token),
            "ui-app-svg-viewport");
    }

    private async Task RenderAndPublishAsync(
        RepositorySvgDocument document,
        IRepositorySvgRasterizer rasterizer,
        CanvasDevice device,
        SvgRenderPlan plan,
        SvgRenderWork work,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (SvgTilePresentation presentation in work.MissingTiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using RepositorySvgTile rendered = await rasterizer.RasterizeTileAsync(
                    document,
                    presentation.Request,
                    plan.PixelFormat,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                CanvasBitmap? bitmap = await Task.Run(
                    () => UploadTile(device, rendered),
                    cancellationToken).ConfigureAwait(false);
                RepositorySvgGpuCache.Lease? lease = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lease = RepositorySvgGpuCache.StoreAndAcquire(
                        device,
                        presentation.Key.CacheIdentity,
                        bitmap,
                        rendered.ByteCount,
                        work.Reservation);
                    bitmap = null;
                    work.Add(presentation.Key, lease);
                    lease = null;
                }
                finally
                {
                    lease?.Dispose();
                    bitmap?.Dispose();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await RunOnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _renderGeneration || !ReferenceEquals(document, _document) || !IsLoaded)
                {
                    return;
                }

                SvgPublishedPlan published = work.DetachPublishedPlan(plan);
                ReplacePublishedPlan(published);
                SetRenderStatus($"rendered:tiles:{published.TileCount}");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (MarkdownSvgException exception) when (
            exception.Reason == MarkdownSvgFailureReason.Canceled &&
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                if (generation == _renderGeneration && ReferenceEquals(document, _document) && IsLoaded)
                {
                    SetRenderStatus("failed");
                    RenderFailed?.Invoke(this, new AppSvgRenderFailedEventArgs(exception));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            work.Dispose();
            if (!ReferenceEquals(device, Volatile.Read(ref _canvasDevice)))
            {
                RepositorySvgGpuCache.ReleaseDevice(device);
            }
        }
    }

    private static CanvasBitmap UploadTile(CanvasDevice device, RepositorySvgTile tile)
    {
        int rowBytes = checked(tile.PixelWidth * 4);
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(rowBytes * tile.PixelHeight));
        ReadOnlySpan<byte> source = tile.BgraPixels.Span;
        if (tile.StrideBytes == rowBytes)
        {
            source[..pixels.Length].CopyTo(pixels);
        }
        else
        {
            Span<byte> destination = pixels;
            for (int row = 0; row < tile.PixelHeight; row++)
            {
                source.Slice(checked(row * tile.StrideBytes), rowBytes)
                    .CopyTo(destination.Slice(checked(row * rowBytes), rowBytes));
            }
        }

        CanvasBitmap bitmap = CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            tile.PixelWidth,
            tile.PixelHeight,
            tile.PixelFormat == MarkdownSvgPixelFormat.Rgba8Premultiplied
                ? DirectXPixelFormat.R8G8B8A8UIntNormalized
                : DirectXPixelFormat.B8G8R8A8UIntNormalized);
        return bitmap;
    }

    private static MarkdownSvgPixelFormat SelectPixelFormat(CanvasDevice device) =>
        RepositorySvgPixelFormatPolicy.Select(
            device.ForceSoftwareRenderer,
            device.IsPixelFormatSupported(DirectXPixelFormat.R8G8B8A8UIntNormalized));

    private void TileCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        SvgPublishedPlan? published = _publishedPlan;
        if (published is null)
        {
            return;
        }

        foreach (SvgPublishedTile tile in published.Tiles)
        {
            args.DrawingSession.DrawImage(
                tile.Bitmap.Bitmap,
                new Rect(
                    tile.Presentation.LogicalX,
                    tile.Presentation.LogicalY,
                    tile.Presentation.LogicalWidth,
                    tile.Presentation.LogicalHeight));
        }
    }

    private void TileCanvas_CreateResources(
        CanvasControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        CanvasDevice? previousDevice = _canvasDevice;
        _canvasDevice = sender.Device;
        if (args.Reason != CanvasCreateResourcesReason.NewDevice)
        {
            if (_document is not null)
            {
                ScheduleRender(immediate: true);
            }

            return;
        }

        CancelRender();
        if (previousDevice is not null)
        {
            RepositorySvgGpuCache.ReleaseDevice(previousDevice);
        }

        ReplacePublishedPlan(null);
        if (_document is not null)
        {
            SetRenderStatus("rendering");
            ScheduleRender(immediate: true);
        }
    }

    private void ReplacePublishedPlan(SvgPublishedPlan? replacement)
    {
        SvgPublishedPlan? previous = _publishedPlan;
        _publishedPlan = replacement;
        TileCanvas.Invalidate();
        previous?.Dispose();
    }

    private Task RunOnUiAsync(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("The SVG viewport dispatcher is unavailable."));
        }

        return completion.Task;
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
        AutomationProperties.SetAutomationId(
            this,
            status.StartsWith("rendered:", StringComparison.Ordinal)
                ? "SvgPreviewRenderedImage"
                : "SvgPreviewViewport");
        AutomationProperties.SetItemStatus(this, status);
    }

    private SvgRenderPlan? CreateRenderPlan(
        RepositorySvgDocument document,
        MarkdownSvgPixelFormat pixelFormat)
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

        double visibleLeft = ((_scrollHost?.HorizontalOffset ?? 0) / zoom) - TileCanvas.Margin.Left;
        double visibleTop = ((_scrollHost?.VerticalOffset ?? 0) / zoom) - TileCanvas.Margin.Top;
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
                    outputWidth,
                    outputHeight,
                    pixelX,
                    pixelY,
                    pixelWidth,
                    pixelHeight);
                string tileIdentity = string.Concat(
                    document.CacheIdentity,
                    "|raster:",
                    outputWidth.ToString(CultureInfo.InvariantCulture),
                    "x",
                    outputHeight.ToString(CultureInfo.InvariantCulture),
                    "|scale:",
                    scaleBits.ToString(CultureInfo.InvariantCulture),
                    "|tile:",
                    pixelX.ToString(CultureInfo.InvariantCulture),
                    ",",
                    pixelY.ToString(CultureInfo.InvariantCulture),
                    ",",
                    pixelWidth.ToString(CultureInfo.InvariantCulture),
                    "x",
                    pixelHeight.ToString(CultureInfo.InvariantCulture));
                tileIdentity = string.Concat(tileIdentity, "|format:", pixelFormat.ToString());
                SvgTileKey key = new(tileIdentity);
                presentations.Add(new SvgTilePresentation(
                    key,
                    request,
                    imageLeft + (pixelX / pixelsPerLogicalUnit),
                    imageTop + (pixelY / pixelsPerLogicalUnit),
                    logicalWidth,
                    logicalHeight));
            }
        }

        return new SvgRenderPlan(presentations, pixelFormat);
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
                "ui-app-svg-document-dispose");
        }
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private readonly record struct SvgTileKey(string CacheIdentity);

    private sealed record SvgTilePresentation(
        SvgTileKey Key,
        RepositorySvgTileRequest Request,
        double LogicalX,
        double LogicalY,
        double LogicalWidth,
        double LogicalHeight);

    private sealed record SvgRenderPlan(
        IReadOnlyList<SvgTilePresentation> Tiles,
        MarkdownSvgPixelFormat PixelFormat);

    private sealed record SvgPublishedTile(
        SvgTilePresentation Presentation,
        RepositorySvgGpuCache.Lease Bitmap);

    private sealed partial class SvgRenderWork : IDisposable
    {
        private Dictionary<SvgTileKey, RepositorySvgGpuCache.Lease> _tiles = [];
        private RepositorySvgGpuCache.Reservation? _reservation;

        private SvgRenderWork(
            IReadOnlyList<SvgTilePresentation> missingTiles,
            RepositorySvgGpuCache.Reservation reservation)
        {
            MissingTiles = missingTiles;
            _reservation = reservation;
        }

        internal IReadOnlyList<SvgTilePresentation> MissingTiles { get; }

        internal RepositorySvgGpuCache.Reservation Reservation =>
            _reservation ?? throw new ObjectDisposedException(nameof(SvgRenderWork));

        internal static bool TryCreate(
            SvgRenderPlan plan,
            CanvasDevice device,
            out SvgRenderWork? work)
        {
            Dictionary<SvgTileKey, RepositorySvgGpuCache.Lease> acquired = [];
            List<SvgTilePresentation> missing = [];
            long missingBytes = 0;
            try
            {
                foreach (SvgTilePresentation presentation in plan.Tiles)
                {
                    if (RepositorySvgGpuCache.TryAcquire(
                        device,
                        presentation.Key.CacheIdentity,
                        out RepositorySvgGpuCache.Lease? lease))
                    {
                        acquired.Add(presentation.Key, lease!);
                    }
                    else
                    {
                        missing.Add(presentation);
                        missingBytes = checked(
                            missingBytes +
                            ((long)presentation.Request.PixelWidth *
                             presentation.Request.PixelHeight *
                             4));
                    }
                }

                if (!RepositorySvgGpuCache.TryReserve(
                    missingBytes,
                    out RepositorySvgGpuCache.Reservation? reservation))
                {
                    work = null;
                    return false;
                }

                work = new SvgRenderWork(missing, reservation!) { _tiles = acquired };
                acquired = [];
                return true;
            }
            finally
            {
                foreach (RepositorySvgGpuCache.Lease lease in acquired.Values)
                {
                    lease.Dispose();
                }
            }
        }

        internal void Add(SvgTileKey key, RepositorySvgGpuCache.Lease lease) =>
            _tiles.Add(key, lease);

        internal SvgPublishedPlan DetachPublishedPlan(SvgRenderPlan plan)
        {
            if (_tiles.Count != plan.Tiles.Count)
            {
                throw new InvalidOperationException("The complete visible SVG tile set is unavailable.");
            }

            _reservation?.Dispose();
            _reservation = null;
            Dictionary<SvgTileKey, RepositorySvgGpuCache.Lease> tiles = _tiles;
            _tiles = [];
            return new SvgPublishedPlan(plan, tiles);
        }

        public void Dispose()
        {
            _reservation?.Dispose();
            _reservation = null;
            foreach (RepositorySvgGpuCache.Lease tile in _tiles.Values)
            {
                tile.Dispose();
            }

            _tiles.Clear();
        }
    }

    private sealed partial class SvgPublishedPlan : IDisposable
    {
        private readonly List<SvgPublishedTile> _tiles;
        private readonly Dictionary<SvgTileKey, RepositorySvgGpuCache.Lease> _bitmaps;

        public SvgPublishedPlan(
            SvgRenderPlan plan,
            Dictionary<SvgTileKey, RepositorySvgGpuCache.Lease> bitmaps)
        {
            _bitmaps = bitmaps;
            _tiles = new List<SvgPublishedTile>(plan.Tiles.Count);
            try
            {
                foreach (SvgTilePresentation presentation in plan.Tiles)
                {
                    if (bitmaps.TryGetValue(
                        presentation.Key,
                        out RepositorySvgGpuCache.Lease? bitmap))
                    {
                        _tiles.Add(new SvgPublishedTile(presentation, bitmap));
                    }
                }
            }
            catch
            {
                foreach (RepositorySvgGpuCache.Lease bitmap in _bitmaps.Values)
                {
                    bitmap.Dispose();
                }

                _bitmaps.Clear();
                _tiles.Clear();
                throw;
            }
        }

        public IReadOnlyList<SvgPublishedTile> Tiles => _tiles;

        public int TileCount => _tiles.Count;

        public void Dispose()
        {
            foreach (RepositorySvgGpuCache.Lease bitmap in _bitmaps.Values)
            {
                bitmap.Dispose();
            }

            _bitmaps.Clear();
            _tiles.Clear();
        }
    }
}

internal sealed class AppSvgRenderFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
