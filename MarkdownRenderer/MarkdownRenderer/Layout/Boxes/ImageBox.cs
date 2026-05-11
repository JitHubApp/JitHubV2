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
    private static readonly ConcurrentDictionary<string, (byte[] Bytes, Size Intrinsic)> _svgBytesCache = new();
    // URLs that have permanently failed to load/parse. New ImageBox instances
    // for the same URL start in _loadFailed=true so the fatal state survives
    // the layout rebuild triggered by the original failure.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _failedUrls = new();
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
    private byte[]? _svgBytes; // raw bytes — re-parsed against the drawing session's device to avoid cross-device issues
    private Size _svgIntrinsicSize;
    private CanvasTextLayout? _placeholder;
    private CanvasTextLayout? _caption;
    private bool _loadStarted;
    private bool _loadFailed;
    private bool _svgReparseStarted;
    private int _svgReparseFailures;
    private bool _disposed;
    private const int MaxSvgReparseAttempts = 2;
    private float _availableWidth;
    private float _imageHeight;
    private float _captionHeight;

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
            else if (_isSvg && _svgBytesCache.TryGetValue(_url, out var entry) && entry.Bytes is not null)
            {
                _svgBytes = entry.Bytes;
                _svgIntrinsicSize = entry.Intrinsic;
                _loadStarted = true;
            }
        }
    }

    /// <summary>The alt text supplied for this image (empty if none).</summary>
    public string Alt => _alt;

    /// <summary>True if the URL has an .svg extension or contains image/svg+xml.</summary>
    public bool IsSvg => _isSvg;

    /// <summary>Test-only: returns the cached bitmap, if any.</summary>
    public CanvasBitmap? Bitmap => _bitmap;

    /// <summary>Test-only: width of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageHeight => _imageHeight;

    /// <summary>Test-only: height of the caption area at last measure (excludes margins).</summary>
    public float MeasuredCaptionHeight => _captionHeight;

    private static bool LooksLikeSvg(string url) => SvgIntrinsics.LooksLikeSvg(url);

    public override float Measure(float availableWidth)
    {
        _availableWidth = availableWidth;
        if (!_loadStarted) StartLoad();

        float maxW = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
        float h;
        if (_bitmap is { } bmp)
        {
            float bw = (float)bmp.Size.Width;
            float bh = (float)bmp.Size.Height;
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            h = bh * scale;
        }
        else if (_svgBytes is not null)
        {
            // Use intrinsic SVG size if we have it, else default to a band.
            float bw = _svgIntrinsicSize.Width > 0 ? (float)_svgIntrinsicSize.Width : maxW;
            float bh = _svgIntrinsicSize.Height > 0 ? (float)_svgIntrinsicSize.Height : 200f;
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            h = bh * scale;
        }
        else
        {
            // Placeholder height = 32px alt-text band.
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
        float w = (float)(Bounds.Width - Margin.Left - Margin.Right);

        if (_bitmap is { } bmp)
        {
            float bw = (float)bmp.Size.Width;
            float bh = (float)bmp.Size.Height;
            float scale = bw > 0 ? Math.Min(1f, w / bw) : 1f;
            var dest = new Rect(x, y, bw * scale, bh * scale);
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
                        dispatcher.TryEnqueue(Publish);
                    else
                        Publish();
                });
            }
            if (_svg is not null)
            {
                float bw = _svgIntrinsicSize.Width > 0 ? (float)_svgIntrinsicSize.Width : w;
                float bh = _svgIntrinsicSize.Height > 0 ? (float)_svgIntrinsicSize.Height : _imageHeight;
                float scale = bw > 0 ? Math.Min(1f, w / bw) : 1f;
                try
                {
                    ds.DrawSvg(_svg, new Size(bw * scale, bh * scale), x, y);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ImageBox] DrawSvg failed: {ex.Message}");
                    // Likely device-lost or codec failure. Drop the parsed doc.
                    // Count this as a reparse failure so we don't loop forever
                    // re-parsing on a broken device — if we've already failed
                    // once, latch fatal here.
                    try { _svg?.Dispose(); } catch { }
                    _svg = null;
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
        // If we were disposed while the load was in flight, drop the freshly
        // loaded resource and bail without raising LoadCompleted — the host
        // already moved on to a new snapshot and the event would race against
        // a freshly-built box.
        if (_disposed)
        {
            try { bmp?.Dispose(); } catch { }
            return;
        }
        if (failed)
        {
            _loadFailed = true;
            if (!string.IsNullOrEmpty(_url)) _failedUrls.TryAdd(_url, 0);
        }
        else if (bmp is not null)
        {
            _bitmap = bmp;
            _bitmapCache[_url] = bmp;
        }
        LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
    }

    private async Task LoadSvgAsync(Uri uri)
    {
        byte[]? bytes = null;
        CanvasSvgDocument? doc = null;
        Size intrinsic = default;
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
                // per-call CTS needed.
                bytes = await _http.Value.GetByteArrayAsync(uri).ConfigureAwait(false);
            }

            if (bytes is not null)
            {
                intrinsic = ExtractSvgIntrinsicSize(bytes);

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
                    // bytes retained — Paint will re-parse against ds.Device as fallback.
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] svg load failed for {uri}: {ex.Message}");
            failed = true;
        }

        // Disposal race: if the snapshot was replaced before the awaits
        // completed, drop the freshly-parsed document so we don't leak native
        // resources, and don't raise LoadCompleted on an obsolete box.
        if (_disposed)
        {
            try { doc?.Dispose(); } catch { }
            return;
        }
        if (failed)
        {
            _loadFailed = true;
            if (!string.IsNullOrEmpty(_url)) _failedUrls.TryAdd(_url, 0);
        }
        else if (bytes is not null)
        {
            _svgIntrinsicSize = intrinsic;
            _svgBytes = bytes;
            _svgBytesCache[_url] = (bytes, intrinsic);
            if (doc is not null) _svg = doc;
        }
        LoadCompleted?.Invoke(this, new LoadCompletedEventArgs(layoutInvalidated: true));
    }

    internal static Size ExtractSvgIntrinsicSize(byte[] svgBytes)
    {
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(svgBytes);
        return (w > 0 && h > 0) ? new Size(w, h) : default;
    }
}
