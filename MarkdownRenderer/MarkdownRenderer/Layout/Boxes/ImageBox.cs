using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Renders a markdown image. The bitmap is loaded asynchronously and the box
/// self-invalidates the canvas once decoding completes. Until then, the alt
/// text is shown as a placeholder. When alt text is non-empty it is also
/// rendered as a caption below the image.
///
/// SVG path: every SVG (data URI, remote, themed, gradient, filter, mask,
/// clipPath, &lt;use&gt;) is rasterized by <see cref="ThorVgRasterizer"/> to
/// a BGRA buffer that is wrapped in a <see cref="CanvasBitmap"/>. There is
/// exactly one render branch for both bitmaps and SVGs in <see cref="Paint"/>:
/// <c>_bitmap</c>. No Win2D <c>CanvasSvgDocument</c>, no Skia, no tier
/// classifier.
/// </summary>
public sealed class ImageBox : BlockBox
{
    private static readonly ConcurrentDictionary<string, CanvasBitmap?> _bitmapCache = new();

    // Hard caps on the static URL caches so a long-lived process viewing many
    // markdown docs with unique image URLs can't grow memory without bound.
    // The per-entry payload is small for raster bitmaps (a CanvasBitmap handle
    // bound to the GPU) but for SVGs each entry pins the rasterized BGRA buffer
    // (potentially several MB at high DPI), so a tighter cap is warranted there.
    private const int MaxBitmapCacheEntries = 256;
    private const int MaxSvgCacheEntries = 128;
    private const int MaxFailedUrlEntries = 512;

    private static void TrimCache<TValue>(ConcurrentDictionary<string, TValue> cache, int maxEntries)
    {
        // NOTE: We intentionally do NOT dispose evicted values here, even when
        // TValue is IDisposable. CanvasBitmap entries in _bitmapCache are
        // shared by reference with live ImageBox instances (cache-hit boxes
        // alias the cached handle into _bitmap). Disposing under eviction
        // would yank the GPU resource out from under any box still painting
        // that URL. Releasing the dictionary slot is enough — once no live
        // ImageBox holds the reference, the GC + finalizer reclaim the
        // underlying handle. SVG entries are records with no native
        // resources so this is a non-issue for _svgCache either way.
        while (cache.Count > maxEntries)
        {
            var victim = cache.Keys.FirstOrDefault();
            if (victim is null) break;
            cache.TryRemove(victim, out _);
        }
    }

    /// <summary>
    /// Cached SVG state for an URL. <see cref="RawBytes"/> is the
    /// pre-theme-injection payload, kept so theme/DPI changes can
    /// re-rasterize without re-fetching. <see cref="CachedBitmapBgra"/>
    /// is the rasterized output for the <see cref="ThemeColorArgb"/> +
    /// <see cref="DevicePixelScale"/> tuple — when those match the live
    /// values, the constructor creates a <see cref="CanvasBitmap"/>
    /// synchronously, eliminating the placeholder-flash ("blink") that
    /// occurs when a fresh layout box waits on an async re-rasterize.
    /// </summary>
    private sealed record SvgCacheEntry(
        byte[] RawBytes,
        Size Intrinsic,
        string? Title,
        string? Desc,
        byte[]? CachedBitmapBgra,
        int CachedBitmapWidthPx,
        int CachedBitmapHeightPx,
        uint ThemeColorArgb,
        float DevicePixelScale);

    private static readonly ConcurrentDictionary<string, SvgCacheEntry> _svgCache = new();

    // URLs that have permanently failed to load/parse. New ImageBox instances
    // for the same URL start in _loadFailed=true so the fatal state survives
    // the layout rebuild triggered by the original failure.
    private static readonly ConcurrentDictionary<string, byte> _failedUrls = new();

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
    private byte[]? _svgRawBytes; // cached pre-injection bytes, used to re-rasterize on theme/DPI change
    private Size _svgIntrinsicSize;
    private CanvasTextLayout? _placeholder;
    private CanvasTextLayout? _caption;
    private bool _loadStarted;
    private bool _loadFailed;
    private volatile bool _disposed;
    private float _availableWidth;
    private float _imageWidth;
    private float _imageHeight;
    private float _captionHeight;
    // Accessibility metadata extracted from the SVG root, set after a successful
    // load. Null when the SVG is missing <title>/<desc>, the asset is a bitmap,
    // or extraction failed.
    private string? _svgTitle;
    private string? _svgDesc;

    /// <summary>Raised when the asset finishes loading and a repaint is required.
    /// The event arg's <see cref="LoadCompletedEventArgs.LayoutInvalidated"/>
    /// indicates whether the host must re-run layout (intrinsic size may have
    /// changed) or merely repaint. Always raised on the UI thread.</summary>
    public event EventHandler<LoadCompletedEventArgs>? LoadCompleted;

    public ImageBox(MarkdownLayoutContext context, string url, string alt)
    {
        _context = context;
        _url = url ?? string.Empty;
        _alt = alt ?? string.Empty;
        _isSvg = SvgIntrinsics.LooksLikeSvg(_url);
        Margin = new Thickness(0, 6, 0, 6);
        if (string.IsNullOrEmpty(_url)) return;

        if (_failedUrls.ContainsKey(_url))
        {
            // Preserve fatal failure latch across rebuilds.
            _loadFailed = true;
            _loadStarted = true;
            return;
        }

        if (!_isSvg)
        {
            if (_bitmapCache.TryGetValue(_url, out var cached) && cached is not null)
            {
                _bitmap = cached;
                _loadStarted = true;
            }
            return;
        }

        // SVG cache hit path. If the cached rasterized bitmap was produced
        // with the current theme color + device pixel scale, materialize it
        // synchronously so the very first paint shows the image — no async
        // pass through ProcessCachedSvgAsync, no placeholder flash.
        if (_svgCache.TryGetValue(_url, out var entry))
        {
            _svgRawBytes = entry.RawBytes;
            _svgIntrinsicSize = entry.Intrinsic;
            _svgTitle = entry.Title;
            _svgDesc = entry.Desc;

            if (entry.CachedBitmapBgra is { } bgra
                && entry.ThemeColorArgb == GetCurrentThemeColorArgb()
                && Math.Abs(entry.DevicePixelScale - (float)_context.RasterizationScale) < 0.001f)
            {
                try
                {
                    _bitmap = CanvasBitmap.CreateFromBytes(
                        _context.ResourceCreator, bgra,
                        entry.CachedBitmapWidthPx, entry.CachedBitmapHeightPx,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    _loadStarted = true;
                }
                catch (Exception ex)
                {
                    // Device-lost or similar — fall through to async re-rasterize.
                    System.Diagnostics.Debug.WriteLine(
                        $"[ImageBox] cache-hit CanvasBitmap.CreateFromBytes failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>The alt text supplied for this image (empty if none).</summary>
    public string Alt => _alt;

    /// <summary>SVG &lt;title&gt; element value, or null. Used by the automation
    /// peer as the accessible name when richer than alt.</summary>
    public string? SvgTitle => _svgTitle;

    /// <summary>SVG &lt;desc&gt; element value, or null. Used by the automation
    /// peer as <c>HelpText</c> for screen-reader description.</summary>
    public string? SvgDesc => _svgDesc;

    /// <summary>True if the URL has an .svg extension or contains image/svg+xml.</summary>
    public bool IsSvg => _isSvg;

    /// <summary>Test-only: returns the cached bitmap, if any.</summary>
    public CanvasBitmap? Bitmap => _bitmap;

    /// <summary>Test-only: height of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageHeight => _imageHeight;

    /// <summary>Test-only: width of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageWidth => _imageWidth;

    /// <summary>Test-only: height of the caption area at last measure (excludes margins).</summary>
    public float MeasuredCaptionHeight => _captionHeight;

    /// <summary>
    /// Triggers the network/disk load for this image if it has not already
    /// started. Called by the renderer when the box enters the viewport.
    /// </summary>
    public void EnsureLoading()
    {
        if (!_loadStarted) StartLoad();
    }

    public override float Measure(float availableWidth)
    {
        _availableWidth = availableWidth;
        float maxW = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
        float w, h;

        if (_bitmap is { } bmp)
        {
            // Intrinsic-first sizing: render at the bitmap's natural size and
            // only downscale (preserving aspect) when the intrinsic width
            // exceeds the available column. For SVGs that have a known
            // intrinsic size from cache, prefer that over the rasterized
            // pixel dimensions (which include the DPI multiplier).
            float bw, bh;
            if (_isSvg && _svgIntrinsicSize.Width > 0 && _svgIntrinsicSize.Height > 0)
            {
                bw = (float)_svgIntrinsicSize.Width;
                bh = (float)_svgIntrinsicSize.Height;
            }
            else
            {
                bw = (float)bmp.Size.Width;
                bh = (float)bmp.Size.Height;
            }
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            w = bw * scale;
            h = bh * scale;
        }
        else if (_isSvg && (_svgRawBytes is not null || _loadStarted))
        {
            // SVG load is in flight. Reserve space using the intrinsic size
            // we recovered from the cache; fall back to a 16:9-ish band
            // when we have no intrinsic at all yet.
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
            // Use cached image dimensions from Measure() so paint matches the
            // layout exactly. Single render branch for both bitmaps and SVGs.
            var dest = new Rect(x, y, _imageWidth, _imageHeight);
            ds.DrawImage(bmp, dest);
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
    }

    private void StartLoad()
    {
        _loadStarted = true;
        if (string.IsNullOrEmpty(_url)) { _loadFailed = true; return; }

        // SVG cache hit but bitmap wasn't created in constructor (theme/DPI
        // mismatch) — re-rasterize from cached raw bytes at the new params.
        if (_isSvg && _svgRawBytes is not null)
        {
            _ = RasterizeAndPublishAsync(_svgRawBytes, intrinsicHint: _svgIntrinsicSize, isFreshLoad: false);
            return;
        }

        // SVG data URIs (data:image/svg+xml,...) may contain raw < > characters
        // that are illegal in RFC 3986, which causes Uri.TryCreate to return
        // false. Parse them directly from the raw URL string.
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
            if (_disposed) { try { bmp?.Dispose(); } catch { } return; }
            if (failed)
            {
                _loadFailed = true;
                if (!string.IsNullOrEmpty(_url)) { _failedUrls.TryAdd(_url, 0); TrimCache(_failedUrls, MaxFailedUrlEntries); }
            }
            else if (bmp is not null)
            {
                _bitmap = bmp;
                _bitmapCache[_url] = bmp;
                TrimCache(_bitmapCache, MaxBitmapCacheEntries);
            }
            LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
        },
        onDropped: () => { try { bmp?.Dispose(); } catch { } });
    }

    private async Task LoadSvgAsync(Uri uri)
    {
        byte[]? rawBytes = null;
        bool failed = false;
        try
        {
            if (uri.Scheme == "data")
            {
                string raw = uri.ToString();
                int comma = raw.IndexOf(',');
                if (comma < 0) { failed = true; }
                else
                {
                    const int MaxSvgBytes = 4 * 1024 * 1024;
                    string payload = raw.Substring(comma + 1);
                    rawBytes = raw.IndexOf(";base64", 0, comma, StringComparison.OrdinalIgnoreCase) >= 0
                        ? Convert.FromBase64String(payload)
                        : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                    if (rawBytes.Length > MaxSvgBytes)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[ImageBox] SVG data URI exceeds {MaxSvgBytes} bytes; skipping.");
                        rawBytes = null;
                        failed = true;
                    }
                }
            }
            else
            {
                // Guard against huge responses before allocating. 4 MB is well
                // above any reasonable SVG icon/illustration in a markdown doc
                // and prevents a malicious host from OOM-ing the process.
                const int MaxSvgBytes = 4 * 1024 * 1024;
                using var response = await _http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxSvgBytes)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ImageBox] SVG at {uri} Content-Length={response.Content.Headers.ContentLength} exceeds {MaxSvgBytes} bytes; skipping.");
                    failed = true;
                }
                else
                {
                    using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var buf = new byte[MaxSvgBytes + 1];
                    int read = 0, chunk;
                    while (read <= MaxSvgBytes && (chunk = await stream.ReadAsync(buf, read, buf.Length - read).ConfigureAwait(false)) > 0)
                        read += chunk;
                    if (read > MaxSvgBytes)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[ImageBox] SVG at {uri} exceeded {MaxSvgBytes} bytes mid-stream; skipping.");
                        failed = true;
                    }
                    else
                    {
                        rawBytes = buf[..read];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] svg fetch failed for {uri}: {ex.Message}");
            failed = true;
        }

        if (failed || rawBytes is null)
        {
            PublishOnUiThread(() =>
            {
                if (_disposed) return;
                _loadFailed = true;
                if (!string.IsNullOrEmpty(_url)) { _failedUrls.TryAdd(_url, 0); TrimCache(_failedUrls, MaxFailedUrlEntries); }
                LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
            });
            return;
        }

        await RasterizeAndPublishAsync(rawBytes, intrinsicHint: default, isFreshLoad: true);
    }

    private async Task LoadSvgDataUriAsync(string rawDataUri)
    {
        byte[]? rawBytes = null;
        bool failed = false;
        try
        {
            int comma = rawDataUri.IndexOf(',');
            if (comma < 0) { failed = true; }
            else
            {
                string payload = rawDataUri.Substring(comma + 1);
                rawBytes = rawDataUri.IndexOf(";base64", 0, comma, StringComparison.OrdinalIgnoreCase) >= 0
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] svg data uri decode failed: {ex.Message}");
            failed = true;
        }

        if (failed || rawBytes is null)
        {
            PublishOnUiThread(() =>
            {
                if (_disposed) return;
                _loadFailed = true;
                if (!string.IsNullOrEmpty(_url)) { _failedUrls.TryAdd(_url, 0); TrimCache(_failedUrls, MaxFailedUrlEntries); }
                LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
            });
            return;
        }

        await RasterizeAndPublishAsync(rawBytes, intrinsicHint: default, isFreshLoad: true);
    }

    /// <summary>
    /// Off-thread: extract title/desc metadata, inject the live theme color
    /// for <c>currentColor</c> resolution, rasterize via ThorVG, and publish
    /// the resulting <see cref="CanvasBitmap"/> on the UI thread. Updates
    /// the shared SVG cache with both the raw bytes (for future
    /// theme/DPI mismatches) and the rasterized BGRA (for blink-free
    /// cache hits in subsequent layout rebuilds).
    /// </summary>
    private async Task RasterizeAndPublishAsync(byte[] rawBytes, Size intrinsicHint, bool isFreshLoad)
    {
        uint themeColor = GetCurrentThemeColorArgb();
        float scale = (float)_context.RasterizationScale;
        if (scale <= 0) scale = 1f;

        // Capture rasterizer inputs off-thread so we don't touch _context
        // state from the work item beyond the immutable snapshot above.
        var work = await Task.Run(() =>
        {
            string? title = null, desc = null;
            try
            {
                var meta = SvgTitleExtractor.Extract(rawBytes);
                title = meta.Title;
                desc = meta.Desc;
            }
            catch { }

            byte[] themed;
            try
            {
                themed = SvgThemeInjector.Inject(
                    rawBytes,
                    (byte)((themeColor >> 16) & 0xFF),
                    (byte)((themeColor >> 8) & 0xFF),
                    (byte)(themeColor & 0xFF));
            }
            catch { themed = rawBytes; }

            Size intrinsic = intrinsicHint;
            if (intrinsic.Width <= 0 || intrinsic.Height <= 0)
            {
                var (iw, ih) = SvgIntrinsics.TryExtractIntrinsicSize(themed);
                if (iw > 0 && ih > 0) intrinsic = new Size(iw, ih);
            }

            var (tw, th) = PickRasterDimensions(intrinsic, scale);
            var raster = ThorVgRasterizer.Rasterize(themed, tw, th);
            return (title, desc, intrinsic, raster);
        }).ConfigureAwait(false);

        PublishOnUiThread(() =>
        {
            if (_disposed) return;

            _svgTitle = work.title;
            _svgDesc = work.desc;
            _svgIntrinsicSize = work.intrinsic;
            _svgRawBytes = rawBytes;

            if (work.raster is { } r)
            {
                try
                {
                    var bmp = CanvasBitmap.CreateFromBytes(
                        _context.ResourceCreator, r.Bgra, r.WidthPx, r.HeightPx,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    _bitmap = bmp;

                    if (!string.IsNullOrEmpty(_url))
                    {
                        _svgCache[_url] = new SvgCacheEntry(
                            rawBytes, work.intrinsic, work.title, work.desc,
                            r.Bgra, r.WidthPx, r.HeightPx, themeColor, scale);
                        TrimCache(_svgCache, MaxSvgCacheEntries);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ImageBox] CanvasBitmap.CreateFromBytes failed: {ex.Message}");
                    _loadFailed = true;
                    if (!string.IsNullOrEmpty(_url)) { _failedUrls.TryAdd(_url, 0); TrimCache(_failedUrls, MaxFailedUrlEntries); }
                }
            }
            else if (isFreshLoad)
            {
                // ThorVG couldn't parse the SVG. Only latch fatal on the
                // initial load — a theme-swap re-rasterize that fails should
                // not invalidate the cached bitmap (we'll keep showing the
                // last good render).
                _loadFailed = true;
                if (!string.IsNullOrEmpty(_url)) { _failedUrls.TryAdd(_url, 0); TrimCache(_failedUrls, MaxFailedUrlEntries); }
            }

            LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
        });
    }

    private uint GetCurrentThemeColorArgb()
    {
        var fg = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).Foreground;
        return ((uint)fg.A << 24) | ((uint)fg.R << 16) | ((uint)fg.G << 8) | fg.B;
    }

    /// <summary>
    /// Chooses a sensible rasterization target size given the SVG's intrinsic
    /// dimensions and the host's device pixel scale. Caps at
    /// <see cref="MaxRasterDimension"/> so peak bitmap memory is bounded;
    /// the rasterized bitmap is later scaled by <c>ds.DrawImage</c> to the
    /// layout-computed display rect, so a slightly smaller raster than
    /// display size is acceptable. Defaults to 256×256 when no intrinsic
    /// is available.
    /// </summary>
    private const int MaxRasterDimension = 2048;
    private static (int W, int H) PickRasterDimensions(Size intrinsic, float scale)
    {
        // Cap effective DPI scale at 4x.
        if (scale > 4f) scale = 4f;
        int w = intrinsic.Width > 0 ? (int)Math.Round(intrinsic.Width * scale) : (int)Math.Round(256 * scale);
        int h = intrinsic.Height > 0 ? (int)Math.Round(intrinsic.Height * scale) : (int)Math.Round(256 * scale);
        if (w > MaxRasterDimension || h > MaxRasterDimension)
        {
            double s = Math.Min((double)MaxRasterDimension / w, (double)MaxRasterDimension / h);
            w = Math.Max(1, (int)Math.Round(w * s));
            h = Math.Max(1, (int)Math.Round(h * s));
        }
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Runs <paramref name="publish"/> on the UI dispatcher when one is
    /// configured and we are off-thread; otherwise inline. Matches the dispatch
    /// contract so all field writes + LoadCompleted invocations happen on the
    /// UI thread under happens-before with Dispose().</summary>
    private void PublishOnUiThread(Action publish, Action? onDropped = null)
    {
        var dispatcher = _context.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            if (!dispatcher.TryEnqueue(() => publish()))
                onDropped?.Invoke();
        }
        else
        {
            publish();
        }
    }

    /// <summary>Test hook: clears the static failed-URL latch and SVG cache
    /// so tests don't pollute each other.</summary>
    internal static void ResetFailureLatchForTests()
    {
        _failedUrls.Clear();
        _svgCache.Clear();
    }
}
