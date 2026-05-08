using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Renders a markdown image. The bitmap is loaded asynchronously (via
/// <see cref="CanvasBitmap.LoadAsync(ICanvasResourceCreator, Uri)"/>) and the
/// box self-invalidates the canvas once decoding completes. Until then, the
/// alt text is shown as a placeholder.
/// </summary>
public sealed class ImageBox : BlockBox
{
    private static readonly ConcurrentDictionary<string, CanvasBitmap?> _cache = new();

    private readonly MarkdownLayoutContext _context;
    private readonly string _url;
    private readonly string _alt;
    private CanvasBitmap? _bitmap;
    private CanvasTextLayout? _placeholder;
    private bool _loadStarted;
    private bool _loadFailed;
    private float _availableWidth;

    /// <summary>Raised when the bitmap finishes loading and a repaint is required.</summary>
    public event EventHandler? LoadCompleted;

    public ImageBox(MarkdownLayoutContext context, string url, string alt)
    {
        _context = context;
        _url = url ?? string.Empty;
        _alt = string.IsNullOrEmpty(alt) ? "image" : alt;
        Margin = new Thickness(0, 6, 0, 6);
        // Only honour positive cache entries.  A `null` cache entry would mean
        // a previous load failed; silently keeping it forever would prevent
        // any retry after transient network/auth failure.  We just re-attempt.
        if (!string.IsNullOrEmpty(_url) && _cache.TryGetValue(_url, out var cached) && cached is not null)
        {
            _bitmap = cached;
            _loadStarted = true;
        }
    }

    public override float Measure(float availableWidth)
    {
        _availableWidth = availableWidth;
        if (!_loadStarted) StartLoad();

        float h;
        if (_bitmap is { } bmp)
        {
            float bw = (float)bmp.Size.Width;
            float bh = (float)bmp.Size.Height;
            float maxW = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
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
            _placeholder = new CanvasTextLayout(_context.ResourceCreator,
                _loadFailed ? $"⚠ image failed: {_alt}" : $"⌛ loading {_alt}…",
                fmt, Math.Max(1f, availableWidth), float.MaxValue);
        }

        float total = h + (float)(Margin.Top + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, total);
        return total;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        float x = (float)(Bounds.X + Margin.Left);
        float y = (float)(Bounds.Y + Margin.Top);
        float w = (float)(Bounds.Width - Margin.Left - Margin.Right);
        float h = (float)(Bounds.Height - Margin.Top - Margin.Bottom);

        if (_bitmap is { } bmp)
        {
            float bw = (float)bmp.Size.Width;
            float bh = (float)bmp.Size.Height;
            float scale = bw > 0 ? Math.Min(1f, w / bw) : 1f;
            var dest = new Rect(x, y, bw * scale, bh * scale);
            ds.DrawImage(bmp, dest);
        }
        else if (_placeholder is not null)
        {
            var fg = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).Foreground;
            ds.DrawTextLayout(_placeholder, x, y, fg);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    private void StartLoad()
    {
        _loadStarted = true;
        if (string.IsNullOrEmpty(_url)) { _loadFailed = true; return; }
        if (!Uri.TryCreate(_url, UriKind.RelativeOrAbsolute, out var uri)) { _loadFailed = true; return; }

        _ = LoadAsync(uri);
    }

    private async Task LoadAsync(Uri uri)
    {
        try
        {
            var bmp = await CanvasBitmap.LoadAsync(_context.ResourceCreator, uri);
            _bitmap = bmp;
            _cache[_url] = bmp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImageBox] load failed for {uri}: {ex.Message}");
            _loadFailed = true;
            // Do NOT cache failures — let the next ImageBox for this URL retry
            // (e.g. after the user reconnects / re-authenticates).
        }
        finally
        {
            LoadCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}
