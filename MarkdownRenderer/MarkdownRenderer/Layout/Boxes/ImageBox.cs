using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Storage.Streams;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Renders a markdown image. The bitmap (or SVG document) is loaded asynchronously and
/// the box self-invalidates the canvas once decoding completes. Until then, the
/// alt text is shown as a placeholder. When alt text is non-empty, it is also
/// rendered as a caption below the image.
/// </summary>
public sealed class ImageBox : BlockBox
{
    private static readonly ConcurrentDictionary<string, CanvasBitmap?> _bitmapCache = new();
    /// <summary>
    /// Cached raw SVG bytes plus extracted metadata. The bytes are stored
    /// pre-theme-injection so a theme switch can re-tint without
    /// re-fetching, and <see cref="Title"/>/<see cref="Desc"/> are restored
    /// to new <see cref="ImageBox"/> instances on cache hit so accessibility
    /// metadata survives layout rebuilds (which create a fresh box per URL).
    /// </summary>
    private readonly record struct SvgCacheEntry(byte[] RawBytes, Size Intrinsic, string? Title, string? Desc);
    private static readonly ConcurrentDictionary<string, SvgCacheEntry> _svgBytesCache = new();
    // URLs that have permanently failed to load/parse. New ImageBox instances
    // for the same URL start in _loadFailed=true so the fatal state survives
    // the layout rebuild triggered by the original failure.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _failedUrls = new();
    // URLs that have been promoted to the Skia rasterization tier — either by
    // the feature scanner up front or by the runtime safety net after Win2D
    // failed at parse / draw time. Survives layout rebuilds so the same URL
    // doesn't re-enter the Win2D path only to fail again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _skiaTierUrls = new();
    private static readonly Lazy<HttpClient> _http = new(() =>
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MarkdownRenderer/1.0");
        return c;
    });

    private readonly MarkdownLayoutContext _context;
    private readonly string _url;
    private readonly string _alt;
    private readonly bool _isSvg;
    private CanvasBitmap? _bitmap;
    private CanvasSvgDocument? _svg;
    private byte[]? _svgBytes; // theme-injected bytes — re-parsed against the drawing session's device to avoid cross-device issues
    private byte[]? _svgRawBytes; // pre-injection bytes; cached so theme switches re-tint without refetching
    private Size _svgIntrinsicSize;
    private CanvasTextLayout? _placeholder;
    private CanvasTextLayout? _caption;
    private bool _loadStarted;
    private bool _loadFailed;
    private bool _svgReparseStarted;
    private int _svgReparseFailures;
    private volatile bool _disposed;
    private const int MaxSvgReparseAttempts = 2;
    private float _availableWidth;
    private float _imageWidth;
    private float _imageHeight;
    private float _captionHeight;
    // Accessibility metadata extracted from the SVG root, set on the UI
    // thread after a successful load. Null when the SVG is missing
    // <title>/<desc>, the asset is a bitmap, or extraction failed.
    private string? _svgTitle;
    private string? _svgDesc;

    /// <summary>Raised when the asset finishes loading and a repaint is required.
    /// The event arg's <see cref="LoadCompletedEventArgs.LayoutInvalidated"/>
    /// indicates whether the host must re-run layout (intrinsic size may have
    /// changed) or merely repaint (e.g. a device-specific SVG re-parse). Always
    /// raised on the UI thread.</summary>
    public event EventHandler<LoadCompletedEventArgs>? LoadCompleted;

    public ImageBox(MarkdownLayoutContext context, string url, string alt)
    {
        _context = context;
        _url = url ?? string.Empty;
        _alt = alt ?? string.Empty;
        _isSvg = SvgIntrinsics.LooksLikeSvg(_url);
        Margin = new Thickness(0, 6, 0, 6);
        if (!string.IsNullOrEmpty(_url))
        {
            if (_failedUrls.ContainsKey(_url))
            {
                // Preserve fatal failure latch across rebuilds: skip loading,
                // render the alt-text placeholder.
                _loadFailed = true;
                _loadStarted = true;
            }
            else if (!_isSvg && _bitmapCache.TryGetValue(_url, out var cached) && cached is not null)
            {
                _bitmap = cached;
                _loadStarted = true;
            }
            else if (_isSvg && _svgBytesCache.TryGetValue(_url, out var entry) && entry.RawBytes is not null)
            {
                // Restore raw bytes + intrinsic + a11y metadata. Leave
                // _loadStarted=false so EnsureLoading triggers the cached
                // re-process path (theme inject + tier classify) which
                // populates _bitmap or _svgBytes for paint.
                _svgRawBytes = entry.RawBytes;
                _svgIntrinsicSize = entry.Intrinsic;
                _svgTitle = entry.Title;
                _svgDesc = entry.Desc;
            }
        }
    }

    /// <summary>The alt text supplied for this image (empty if none).</summary>
    public string Alt => _alt;

    /// <summary>SVG &lt;title&gt; element value, or null. Used by the
    /// automation peer as the accessible name when richer than alt.</summary>
    public string? SvgTitle => _svgTitle;

    /// <summary>SVG &lt;desc&gt; element value, or null. Used by the
    /// automation peer as <c>HelpText</c> for screen-reader description.</summary>
    public string? SvgDesc => _svgDesc;

    /// <summary>True if the URL has an .svg extension or contains image/svg+xml.</summary>
    public bool IsSvg => _isSvg;

    /// <summary>Test-only: returns the cached bitmap, if any.</summary>
    public CanvasBitmap? Bitmap => _bitmap;

    /// <summary>Test-only: height of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageHeight => _imageHeight;

    /// <summary>Test-only: width of the image content area at last measure (excludes margins).
    /// May be smaller than the available column width when the image's intrinsic size fits.</summary>
    public float MeasuredImageWidth => _imageWidth;

    /// <summary>Test-only: height of the caption area at last measure (excludes margins).</summary>
    public float MeasuredCaptionHeight => _captionHeight;

    private static bool LooksLikeSvg(string url) => SvgIntrinsics.LooksLikeSvg(url);

    /// <summary>
    /// Triggers the network/disk load for this image if it has not already started.
    /// For images already in the in-memory cache the load is effectively a no-op
    /// (the constructor set <c>_loadStarted = true</c>).  For un-cached images the
    /// first call kicks the HTTP / SVG pipeline; subsequent calls are no-ops.
    ///
    /// Called by <see cref="MarkdownRenderer.Controls.MarkdownRendererControl"/>
    /// from <c>RealizeVisibleEmbeds</c> when the image's bounds enter the
    /// viewport + overscan band, implementing lazy / prioritised loading.
    /// </summary>
    public void EnsureLoading()
    {
        if (!_loadStarted) StartLoad();
    }

    public override float Measure(float availableWidth)
    {
        _availableWidth = availableWidth;
        // Do NOT auto-start the load here.  Loads are deferred until
        // EnsureLoading() is called by MarkdownRendererControl once the image
        // enters the viewport + overscan band (lazy loading).  Images already
        // in the in-memory cache set _loadStarted = true in the constructor so
        // they are unaffected — their bitmaps are available immediately.

        float maxW = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
        float w, h;
        if (_bitmap is { } bmp)
        {
            // Intrinsic-first sizing: render at the bitmap's natural size and
            // only downscale (preserving aspect) when the intrinsic width
            // exceeds the available column.
            float bw = (float)bmp.Size.Width;
            float bh = (float)bmp.Size.Height;
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            w = bw * scale;
            h = bh * scale;
        }
        else if (_svgBytes is not null)
        {
            // Same intrinsic-first rule for SVGs. When the SVG has no intrinsic
            // dimensions (e.g. only a viewBox without width/height), fall back
            // to filling the column width with a 16:9-ish band rather than
            // claiming a fixed 200px height — matches what most HTML engines do.
            float bw = _svgIntrinsicSize.Width > 0 ? (float)_svgIntrinsicSize.Width : maxW;
            float bh = _svgIntrinsicSize.Height > 0 ? (float)_svgIntrinsicSize.Height : 200f;
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            w = bw * scale;
            h = bh * scale;
        }
        else
        {
            // Placeholder height = 32px alt-text band, stretched to column.
            w = maxW;
            h = 32f;
            _placeholder?.Dispose();
            using var fmt = new CanvasTextFormat
            {
                FontFamily = "Segoe UI Variable",
                FontSize = 13,
                WordWrapping = CanvasWordWrapping.Wrap,
            };
            string placeholderText = _loadFailed
                ? $"⚠ image failed: {(string.IsNullOrEmpty(_alt) ? _url : _alt)}"
                : $"⌛ loading {(string.IsNullOrEmpty(_alt) ? _url : _alt)}…";
            _placeholder = new CanvasTextLayout(_context.ResourceCreator,
                placeholderText, fmt, maxW, float.MaxValue);
        }

        _imageWidth = w;
        _imageHeight = h;

        // Caption layout — only when alt text is non-empty.
        _caption?.Dispose();
        _caption = null;
        _captionHeight = 0f;
        if (!string.IsNullOrEmpty(_alt))
        {
            var cs = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            using var cfmt = new CanvasTextFormat
            {
                FontFamily = cs.FontFamily,
                FontSize = cs.FontSize,
                FontStyle = cs.FontStyle,
                FontWeight = cs.FontWeight,
                WordWrapping = CanvasWordWrapping.Wrap,
                Direction = _context.FlowDirection == FlowDirection.RightToLeft
                    ? CanvasTextDirection.RightToLeftThenTopToBottom
                    : CanvasTextDirection.LeftToRightThenTopToBottom,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
            };
            _caption = new CanvasTextLayout(_context.ResourceCreator, _alt, cfmt, maxW, float.MaxValue)
            {
                Options = CanvasDrawTextOptions.EnableColorFont,
            };
            _captionHeight = (float)_caption.LayoutBounds.Height
                             + (float)(cs.Margin.Top + cs.Margin.Bottom);
        }

        float total = h + _captionHeight + (float)(Margin.Top + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, total);
        return total;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        float x = (float)(Bounds.X + Margin.Left);
        float y = (float)(Bounds.Y + Margin.Top);

        if (_bitmap is { } bmp)
        {
            // Use the cached image dimensions from Measure() so paint matches
            // the layout that was rendered. This guarantees we don't accidentally
            // re-stretch the image to the full column width during repaint.
            var dest = new Rect(x, y, _imageWidth, _imageHeight);
            ds.DrawImage(bmp, dest);
        }
        else if (_svgBytes is not null)
        {
            // Pre-parse should have populated _svg off the UI thread. If it
            // didn't (e.g. shared-device parse failed and we deferred to a
            // device-specific reparse), kick a one-shot async re-parse and
            // skip drawing this frame. LoadCompleted will trigger a repaint
            // once the document is ready. Never block the paint thread on
            // CanvasSvgDocument.LoadAsync.
            if (_svg is null && !_svgReparseStarted && !_loadFailed)
            {
                _svgReparseStarted = true;
                var bytes = _svgBytes;
                var device = ds.Device;
                var dispatcher = _context.Dispatcher;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    CanvasSvgDocument? doc = null;
                    bool failed = false;
                    try
                    {
                        using var ms = new InMemoryRandomAccessStream();
                        using (var writer = new DataWriter(ms))
                        {
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            writer.DetachStream();
                        }
                        ms.Seek(0);
                        doc = await CanvasSvgDocument.LoadAsync(device, ms);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBox] SVG reparse failed: {ex.Message}");
                        failed = true;
                    }
                    // Marshal back to UI thread so field writes happen-before any
                    // subsequent Paint, and LoadCompleted handlers run on the
                    // dispatcher (consistent threading contract for subscribers).
                    void Publish()
                    {
                        if (_disposed)
                        {
                            // Box was torn down while the reparse was in flight.
                            // Dispose the freshly-parsed document and bail — do
                            // not raise LoadCompleted, do not resurrect state.
                            try { doc?.Dispose(); } catch { }
                            return;
                        }
                        if (failed)
                        {
                            _svgReparseFailures++;
                            if (_svgReparseFailures >= MaxSvgReparseAttempts)
                            {
                                // Latch fatal: stop retrying and fall back to
                                // the alt-text placeholder. Record the URL in
                                // the failed set so subsequent ImageBox
                                // instances (e.g. after layout rebuild) start
                                // already failed instead of restarting the
                                // retry cycle from the bytes cache.
                                _loadFailed = true;
                                _svgBytes = null;
                                if (!string.IsNullOrEmpty(_url)) _failedUrls.TryAdd(_url, 0);
                            }
                            else
                            {
                                // Allow a single bounded retry (e.g. transient
                                // device contention). Guard is reset; next
                                // paint may try again.
                                _svgReparseStarted = false;
                            }
                        }
                        else
                        {
                            _svgReparseFailures = 0;
                            _svg = doc;
                        }
                        // Repaint-only — intrinsic size already known, no relayout.
                        LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: failed && _loadFailed));
                    }
                    if (dispatcher is not null && !dispatcher.HasThreadAccess)
                    {
                        if (!dispatcher.TryEnqueue(Publish))
                        {
                            // Dispatcher shutting down; release the freshly-
                            // parsed document so it doesn't leak.
                            try { doc?.Dispose(); } catch { }
                        }
                    }
                    else
                        Publish();
                });
            }
            if (_svg is not null)
            {
                // Use cached image dimensions from Measure() so paint matches
                // the layout exactly. Clip to the image rect so SVG filter
                // effects (e.g. Gaussian blur in decorative SVGs) don't bleed
                // below the image bounds and overlap the caption text.
                float renderW = _imageWidth;
                float renderH = _imageHeight;
                try
                {
                    var clipRect = new Rect(x, y, renderW, renderH);
                    using (ds.CreateLayer(1.0f, clipRect))
                    {
                        ds.DrawSvg(_svg, new Size(renderW, renderH), x, y);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBox] DrawSvg failed: {ex.Message}");
                    // Likely a Win2D codec gap (filter / mask / etc.) or a
                    // device-lost. Drop the parsed doc and promote this URL
                    // to the Skia tier so the next load uses the rasterizer.
                    try { _svg?.Dispose(); } catch { }
                    _svg = null;
                    if (!string.IsNullOrEmpty(_url)) _skiaTierUrls.TryAdd(_url, 0);

                    // Attempt an inline Skia rasterization with the bytes we
                    // already have so the user sees the image on the very
                    // next frame rather than waiting for a reload pass.
                    var raster = TryRasterize(_svgBytes!, _svgIntrinsicSize, _context.RasterizationScale);
                    if (raster is { } r)
                    {
                        _bitmap = CanvasBitmap.CreateFromBytes(
                            _context.ResourceCreator, r.Bgra, r.WidthPx, r.HeightPx,
                            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                        _svgBytes = null;
                    }
                    else
                    {
                        _svgReparseFailures++;
                        if (_svgReparseFailures >= MaxSvgReparseAttempts)
                        {
                            _loadFailed = true;
                            _svgBytes = null;
                            if (!string.IsNullOrEmpty(_url)) _failedUrls.TryAdd(_url, 0);
                        }
                        else
                        {
                            _svgReparseStarted = false;
                        }
                    }
                }
            }
        }
        else if (_placeholder is not null)
        {
            var fg = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).Foreground;
            ds.DrawTextLayout(_placeholder, x, y, fg);
        }

        if (_caption is not null)
        {
            var cs = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            float cy = y + _imageHeight + (float)cs.Margin.Top;
            ds.DrawTextLayout(_caption, x, cy, cs.Foreground);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    public override System.Collections.Generic.IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        var n = range.Normalized();
        if (BlockIndex >= n.Start.BlockIndex && BlockIndex <= n.End.BlockIndex
            && !(n.Start.BlockIndex == n.End.BlockIndex
                 && n.Start.InlineIndex == n.End.InlineIndex
                 && n.Start.CharacterOffset == n.End.CharacterOffset))
        {
            yield return Bounds;
        }
    }

    public override void Dispose()
    {
        _disposed = true;
        _placeholder?.Dispose();
        _placeholder = null;
        _caption?.Dispose();
        _caption = null;
        _svg?.Dispose();
        _svg = null;
    }

    private void StartLoad()
    {
        _loadStarted = true;
        if (string.IsNullOrEmpty(_url)) { _loadFailed = true; return; }

        // Cache hit from a prior layout's load: skip the network/data-URI
        // fetch and re-process the cached raw bytes against the live theme.
        // This restores _bitmap / _svgBytes for the new box instance and
        // ensures Skia-tier SVGs don't re-rasterize until paint.
        if (_isSvg && _svgRawBytes is not null)
        {
            _ = ProcessCachedSvgAsync(_svgRawBytes);
            return;
        }

        // SVG data URIs (data:image/svg+xml,...) may contain raw < > characters
        // that are illegal in RFC 3986, which causes Uri.TryCreate to return false.
        // Parse them directly from the raw URL string to avoid that failure.
        if (_isSvg && _url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadSvgDataUriAsync(_url);
            return;
        }

        if (!Uri.TryCreate(_url, UriKind.RelativeOrAbsolute, out var uri)) { _loadFailed = true; return; }

        if (_isSvg)
            _ = LoadSvgAsync(uri);
        else
            _ = LoadBitmapAsync(uri);
    }

    /// <summary>
    /// Re-materializes a Skia bitmap or Win2D <see cref="CanvasSvgDocument"/>
    /// from already-cached raw SVG bytes. Theme color is re-injected so
    /// <c>currentColor</c> picks up the live theme, then the same tier
    /// classifier as the fresh-load path decides between Skia rasterization
    /// and Win2D parse.
    /// </summary>
    private async Task ProcessCachedSvgAsync(byte[] rawBytes)
    {
        byte[]? bytes = null;
        CanvasSvgDocument? doc = null;
        Size intrinsic = _svgIntrinsicSize;
        SvgSkiaRasterizer.Raster? rasterized = null;
        bool failed = false;
        try
        {
            bytes = await Task.Run(() => InjectThemeColor(rawBytes)).ConfigureAwait(false);
            if (intrinsic.Width <= 0 || intrinsic.Height <= 0)
                intrinsic = ExtractSvgIntrinsicSize(bytes);

            bool useSkia = _skiaTierUrls.ContainsKey(_url)
                           || SvgFeatureScanner.Classify(bytes) == SvgRenderTier.Skia;
            if (useSkia)
            {
                rasterized = TryRasterize(bytes, intrinsic, _context.RasterizationScale);
                if (rasterized is null) useSkia = false;
            }

            if (!useSkia)
            {
                try
                {
                    using var ms = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(ms))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        writer.DetachStream();
                    }
                    ms.Seek(0);
                    doc = await CanvasSvgDocument.LoadAsync(
                        Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), ms);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBox] cached SVG re-parse failed: {ex.Message}");
                    _skiaTierUrls.TryAdd(_url, 0);
                    rasterized = TryRasterize(bytes, intrinsic, _context.RasterizationScale);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] cached SVG processing failed: {ex.Message}");
            failed = true;
        }

        PublishOnUiThread(() =>
        {
            if (_disposed) { try { doc?.Dispose(); } catch { } return; }
            if (failed)
            {
                _loadFailed = true;
            }
            else if (rasterized is { } r)
            {
                var bmp = CanvasBitmap.CreateFromBytes(
                    _context.ResourceCreator, r.Bgra, r.WidthPx, r.HeightPx,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                _bitmap = bmp;
                _svgIntrinsicSize = intrinsic;
            }
            else if (bytes is not null)
            {
                _svgIntrinsicSize = intrinsic;
                _svgBytes = bytes;
                if (doc is not null) _svg = doc;
            }
            LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
        },
        onDropped: () => { try { doc?.Dispose(); } catch { } });
    }

    private async Task LoadBitmapAsync(Uri uri)
    {
        CanvasBitmap? bmp = null;
        bool failed = false;
        try
        {
            bmp = await CanvasBitmap.LoadAsync(_context.ResourceCreator, uri);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] bitmap load failed for {uri}: {ex.Message}");
            failed = true;
        }
        PublishOnUiThread(() =>
        {
            // _disposed re-checked on the UI thread under happens-before with
            // Dispose(); if we lost the race, drop the freshly-loaded native
            // resource and bail without raising LoadCompleted.
            if (_disposed) { try { bmp?.Dispose(); } catch { } return; }
            if (failed)
            {
                _loadFailed = true;
                // Register the URL so a layout rebuild triggered by the
                // failure LoadCompleted doesn't restart the network fetch in
                // a fresh ImageBox — avoids retry storm on persistent
                // network/codec failures. Tests can call ResetFailureLatch().
                if (!string.IsNullOrEmpty(_url)) _failedUrls.TryAdd(_url, 0);
            }
            else if (bmp is not null)
            {
                _bitmap = bmp;
                _bitmapCache[_url] = bmp;
            }
            LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
        },
        onDropped: () => { try { bmp?.Dispose(); } catch { } });
    }

    private async Task LoadSvgAsync(Uri uri)
    {
        byte[]? bytes = null;
        byte[]? rawBytes = null;
        CanvasSvgDocument? doc = null;
        Size intrinsic = default;
        SvgSkiaRasterizer.Raster? rasterized = null;
        bool failed = false;
        try
        {
            if (uri.Scheme == "data")
            {
                // data:image/svg+xml;base64,XXXX  or  data:image/svg+xml,<svg…>
                string raw = uri.ToString();
                int comma = raw.IndexOf(',');
                if (comma < 0) { failed = true; }
                else
                {
                    string payload = raw.Substring(comma + 1);
                    bytes = raw.IndexOf(";base64", 0, comma, StringComparison.OrdinalIgnoreCase) >= 0
                        ? Convert.FromBase64String(payload)
                        : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                }
            }
            else
            {
                // HttpClient.Timeout (30s) already bounds this call; no
                // per-call CTS needed. ConfigureAwait(false) puts the
                // continuation on the thread pool — we marshal back to the
                // UI dispatcher below before touching instance state or
                // raising LoadCompleted.
                bytes = await _http.Value.GetByteArrayAsync(uri).ConfigureAwait(false);
            }

            if (bytes is not null)
            {
                rawBytes = bytes;
                ExtractMetadata(rawBytes);
                bytes = InjectThemeColor(rawBytes);
                intrinsic = ExtractSvgIntrinsicSize(bytes);

                // Tier classifier — features Win2D's CanvasSvgDocument doesn't
                // implement (filters, masks, clip paths, foreign objects, CSS,
                // animations) route to the Skia rasterization fallback. URLs
                // already promoted to tier B by the runtime safety net also
                // skip the Win2D parse to avoid the known-failing path.
                bool useSkia = _skiaTierUrls.ContainsKey(_url)
                               || SvgFeatureScanner.Classify(bytes) == SvgRenderTier.Skia;
                if (useSkia)
                {
                    rasterized = TryRasterize(bytes, intrinsic, _context.RasterizationScale);
                    if (rasterized is null)
                    {
                        // Skia couldn't parse it either. Fall through to the
                        // Win2D path as a last resort; if that also fails the
                        // alt-text placeholder takes over.
                        useSkia = false;
                    }
                }

                if (!useSkia)
                {
                    // Pre-parse the SVG document off the UI thread so the first Paint
                    // doesn't sync-block on LoadAsync. Tied to the shared device; the
                    // paint path falls back to re-parsing on device loss.
                    try
                    {
                        using var ms = new InMemoryRandomAccessStream();
                        using (var writer = new DataWriter(ms))
                        {
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            writer.DetachStream();
                        }
                        ms.Seek(0);
                        doc = await CanvasSvgDocument.LoadAsync(
                            Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), ms);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBox] SVG pre-parse failed for {uri}: {ex.Message}");
                        // Promote to Skia tier and rasterize as a safety net.
                        // If the Skia rasterize also fails the bytes are
                        // retained and Paint will retry the Win2D parse once
                        // more before latching the failure.
                        _skiaTierUrls.TryAdd(_url, 0);
                        rasterized = TryRasterize(bytes, intrinsic, _context.RasterizationScale);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] svg load failed for {uri}: {ex.Message}");
            failed = true;
        }

        PublishOnUiThread(() =>
        {
            // _disposed re-checked here on the UI thread (serialized with
            // Dispose()), eliminating the TOCTOU window that existed when the
            // check ran on the threadpool continuation. If lost, drop the
            // freshly-parsed CanvasSvgDocument.
            if (_disposed) { try { doc?.Dispose(); } catch { } return; }
            if (failed)
            {
                _loadFailed = true;
                if (!string.IsNullOrEmpty(_url)) _failedUrls.TryAdd(_url, 0);
            }
            else if (rasterized is { } r)
            {
                // Skia path produced a BGRA buffer — wrap it as a CanvasBitmap
                // and join the existing bitmap render flow. _bitmap takes
                // precedence over _svg in Paint, so no Win2D draw will occur
                // for this URL even if _svg is also set elsewhere.
                var bmp = CanvasBitmap.CreateFromBytes(
                    _context.ResourceCreator, r.Bgra, r.WidthPx, r.HeightPx,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                _bitmap = bmp;
                _svgIntrinsicSize = intrinsic;
                if (rawBytes is not null && !string.IsNullOrEmpty(_url))
                    _svgBytesCache[_url] = new SvgCacheEntry(rawBytes, intrinsic, _svgTitle, _svgDesc);
            }
            else if (bytes is not null)
            {
                _svgIntrinsicSize = intrinsic;
                _svgBytes = bytes;
                _svgRawBytes = rawBytes;
                if (rawBytes is not null && !string.IsNullOrEmpty(_url))
                    _svgBytesCache[_url] = new SvgCacheEntry(rawBytes, intrinsic, _svgTitle, _svgDesc);
                if (doc is not null) _svg = doc;
            }
            LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
        },
        onDropped: () => { try { doc?.Dispose(); } catch { } });
    }

    /// <summary>
    /// Handles <c>data:image/svg+xml</c> URIs directly from the raw URL string.
    /// <see cref="Uri"/> rejects SVG data URIs that contain unescaped
    /// angle-bracket characters (<c>&lt;</c>, <c>&gt;</c>) which are invalid per
    /// RFC 3986 but commonly appear in hand-authored SVG data URIs.
    /// </summary>
    private async Task LoadSvgDataUriAsync(string rawDataUri)
    {
        byte[]? bytes = null;
        byte[]? rawBytes = null;
        CanvasSvgDocument? doc = null;
        Size intrinsic = default;
        SvgSkiaRasterizer.Raster? rasterized = null;
        bool failed = false;
        try
        {
            int comma = rawDataUri.IndexOf(',');
            if (comma < 0)
            {
                failed = true;
            }
            else
            {
                string payload = rawDataUri.Substring(comma + 1);
                bytes = rawDataUri.IndexOf(";base64", 0, comma, StringComparison.OrdinalIgnoreCase) >= 0
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }

            if (bytes is not null)
            {
                rawBytes = bytes;
                ExtractMetadata(rawBytes);
                bytes = InjectThemeColor(rawBytes);
                intrinsic = ExtractSvgIntrinsicSize(bytes);

                bool useSkia = _skiaTierUrls.ContainsKey(_url)
                               || SvgFeatureScanner.Classify(bytes) == SvgRenderTier.Skia;
                if (useSkia)
                {
                    rasterized = TryRasterize(bytes, intrinsic, _context.RasterizationScale);
                    if (rasterized is null) useSkia = false;
                }

                if (!useSkia)
                {
                    try
                    {
                        using var ms = new InMemoryRandomAccessStream();
                        using (var writer = new DataWriter(ms))
                        {
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            writer.DetachStream();
                        }
                        ms.Seek(0);
                        doc = await CanvasSvgDocument.LoadAsync(
                            Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), ms);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ImageBox] SVG data URI pre-parse failed: {ex.Message}");
                        _skiaTierUrls.TryAdd(_url, 0);
                        rasterized = TryRasterize(bytes, intrinsic, _context.RasterizationScale);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] SVG data URI load failed: {ex.Message}");
            failed = true;
        }

        PublishOnUiThread(() =>
        {
            if (_disposed) { try { doc?.Dispose(); } catch { } return; }
            if (failed)
            {
                _loadFailed = true;
                if (!string.IsNullOrEmpty(_url)) _failedUrls.TryAdd(_url, 0);
            }
            else if (rasterized is { } r)
            {
                var bmp = CanvasBitmap.CreateFromBytes(
                    _context.ResourceCreator, r.Bgra, r.WidthPx, r.HeightPx,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                _bitmap = bmp;
                _svgIntrinsicSize = intrinsic;
                if (rawBytes is not null && !string.IsNullOrEmpty(_url))
                    _svgBytesCache[_url] = new SvgCacheEntry(rawBytes, intrinsic, _svgTitle, _svgDesc);
            }
            else if (bytes is not null)
            {
                _svgIntrinsicSize = intrinsic;
                _svgBytes = bytes;
                _svgRawBytes = rawBytes;
                if (rawBytes is not null && !string.IsNullOrEmpty(_url))
                    _svgBytesCache[_url] = new SvgCacheEntry(rawBytes, intrinsic, _svgTitle, _svgDesc);
                if (doc is not null) _svg = doc;
            }
            LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
        },
        onDropped: () => { try { doc?.Dispose(); } catch { } });
    }

    // Runs `publish` on the UI dispatcher when one is configured and we are
    // off-thread; otherwise inline. Matches the dispatch contract used by
    // the paint-thread reparse Publish() path so that all field writes +
    // LoadCompleted invocations happen on the UI thread under
    // happens-before with Dispose().
    private void PublishOnUiThread(Action publish, Action? onDropped = null)
    {
        var dispatcher = _context.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            // TryEnqueue returns false during dispatcher shutdown. Invoke
            // onDropped so the caller can dispose any freshly-allocated
            // native resources captured by the publish closure.
            if (!dispatcher.TryEnqueue(() => publish()))
                onDropped?.Invoke();
        }
        else
        {
            publish();
        }
    }

    /// <summary>Test hook: clears the static failed-URL latch so tests don't
    /// pollute each other. Not part of the supported public surface.</summary>
    internal static void ResetFailureLatchForTests()
    {
        _failedUrls.Clear();
        _skiaTierUrls.Clear();
    }

    /// <summary>Test hook: returns whether <paramref name="url"/> has been
    /// promoted to the Skia rasterization tier. Used by tier-routing tests.</summary>
    internal static bool IsSkiaTierForTests(string url) => _skiaTierUrls.ContainsKey(url);

    /// <summary>Test hook: forces <paramref name="url"/> into the Skia tier
    /// without going through the runtime safety net. Used by tier-routing tests.</summary>
    internal static void ForceSkiaTierForTests(string url) => _skiaTierUrls.TryAdd(url, 0);

    internal static Size ExtractSvgIntrinsicSize(byte[] svgBytes)
    {
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(svgBytes);
        return (w > 0 && h > 0) ? new Size(w, h) : default;
    }

    /// <summary>
    /// Chooses a sensible rasterization target size and invokes
    /// <see cref="SvgSkiaRasterizer.Rasterize"/>. Caps the dimensions at
    /// <c>MaxRasterDimension</c> to bound peak bitmap memory; the result is
    /// later down-/up-scaled by <c>ds.DrawImage</c> to the layout-computed
    /// display rect, so a slightly smaller raster than display size is OK.
    /// Defaults to 256×256 when the SVG has no intrinsic dimensions.
    /// The <paramref name="rasterScale"/> multiplier (typically the host
    /// XamlRoot's RasterizationScale, e.g. 1.5 / 2.0) over-samples the
    /// raster so the image stays crisp on high-DPI displays.
    /// </summary>
    private const int MaxRasterDimension = 2048;
    private static SvgSkiaRasterizer.Raster? TryRasterize(byte[] bytes, Size intrinsic, double rasterScale = 1.0)
    {
        double scale = rasterScale > 0 ? rasterScale : 1.0;
        // Cap effective DPI scale at 4x — beyond that the cost outweighs
        // perceptible sharpness, and very small SVGs rasterized at 8x can
        // blow past MaxRasterDimension which then forces a downscale that
        // throws away the over-sampling we just paid for.
        if (scale > 4.0) scale = 4.0;

        int w = intrinsic.Width > 0 ? (int)Math.Round(intrinsic.Width * scale) : (int)Math.Round(256 * scale);
        int h = intrinsic.Height > 0 ? (int)Math.Round(intrinsic.Height * scale) : (int)Math.Round(256 * scale);
        if (w > MaxRasterDimension || h > MaxRasterDimension)
        {
            double s = Math.Min((double)MaxRasterDimension / w, (double)MaxRasterDimension / h);
            w = Math.Max(1, (int)Math.Round(w * s));
            h = Math.Max(1, (int)Math.Round(h * s));
        }
        return SvgSkiaRasterizer.Rasterize(bytes, w, h);
    }

    /// <summary>
    /// Pre-processes raw SVG bytes prior to backend dispatch:
    /// <list type="bullet">
    ///   <item>Injects a <c>color="#RRGGBB"</c> attribute on the root
    ///         &lt;svg&gt; element using the current theme's body
    ///         foreground so <c>currentColor</c> tokens (Octicons /
    ///         status-badge style) resolve to the theme color.</item>
    ///   <item>Extracts <c>&lt;title&gt;</c> and <c>&lt;desc&gt;</c>
    ///         text and stores them on the box for the automation peer.</item>
    /// </list>
    /// Both transforms are best-effort and never throw; failure leaves
    /// the original bytes / null metadata in place.
    /// </summary>
    /// <summary>
    /// Best-effort SVG pre-processing: extracts <c>&lt;title&gt;</c>/
    /// <c>&lt;desc&gt;</c> for accessibility, then injects the theme's
    /// foreground color onto the root so <c>currentColor</c> resolves to
    /// the live theme. Failure on either pass is non-fatal.
    /// </summary>
    private byte[] PreprocessSvgBytes(byte[] bytes)
    {
        ExtractMetadata(bytes);
        return InjectThemeColor(bytes);
    }

    /// <summary>Pulls SVG <c>&lt;title&gt;</c>/<c>&lt;desc&gt;</c> into the
    /// box's accessibility fields. Idempotent and safe to re-run.</summary>
    private void ExtractMetadata(byte[] bytes)
    {
        try
        {
            var meta = SvgTitleExtractor.Extract(bytes);
            _svgTitle = meta.Title;
            _svgDesc = meta.Desc;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] title/desc extract failed: {ex.Message}");
        }
    }

    /// <summary>Returns the SVG bytes with the active theme's foreground
    /// color injected as the root's <c>color</c> attribute, so
    /// <c>currentColor</c> in inner shapes resolves to the live theme.
    /// Returns the input untouched on failure.</summary>
    private byte[] InjectThemeColor(byte[] bytes)
    {
        try
        {
            var bodyStyle = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body);
            var fg = bodyStyle.Foreground;
            return SvgThemeInjector.Inject(bytes, fg.R, fg.G, fg.B);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] currentColor inject failed: {ex.Message}");
            return bytes;
        }
    }
}
