using System;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Process-wide cache for immutable CanvasTextFormat descriptors. Formats are
/// not device resources, and callers hold leases so LRU eviction cannot dispose
/// a descriptor while a native text-layout constructor is consuming it.
/// </summary>
internal static class SharedCanvasTextFormatCache
{
    internal const long DefaultBudgetBytes = 512L * 1024;

    private static readonly object CoordinationGate = new();
    private static readonly WeightedLruCache<TextFormatKey, Entry> Cache = new(
        DefaultBudgetBytes,
        static entry => entry.WeightBytes,
        static entry => entry.ReleaseCacheReference());

    internal static Lease Acquire(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment,
        FlowDirection flowDirection,
        string? localeName = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        var key = new TextFormatKey(
            NormalizeFontFamilyForCanvas(style.FontFamily),
            style.FontSize,
            style.FontWeight,
            style.FontStyle,
            wordWrapping,
            horizontalAlignment,
            flowDirection == FlowDirection.RightToLeft
                ? CanvasTextDirection.RightToLeftThenTopToBottom
                : CanvasTextDirection.LeftToRightThenTopToBottom,
            CanvasLineSpacingMode.Default,
            string.IsNullOrWhiteSpace(localeName) ? string.Empty : localeName.Trim());

        lock (CoordinationGate)
        {
            if (Cache.TryGetValue(key, out Entry? cached) && cached.TryAcquire())
                return new Lease(cached);

            var format = new CanvasTextFormat
            {
                FontFamily = key.FontFamily,
                FontSize = key.FontSize,
                FontWeight = key.FontWeight,
                FontStyle = key.FontStyle,
                WordWrapping = key.WordWrapping,
                LineSpacingMode = key.LineSpacingMode,
                Direction = key.Direction,
                HorizontalAlignment = key.HorizontalAlignment,
            };
            if (key.LocaleName.Length > 0)
                format.LocaleName = key.LocaleName;
            var created = new Entry(
                format,
                256L + ((long)(key.FontFamily.Length + key.LocaleName.Length) * sizeof(char)));
            created.AddCacheReference();
            Cache.Set(key, created);
            return new Lease(created);
        }
    }

    internal static void Clear()
    {
        lock (CoordinationGate)
            Cache.Clear();
    }

    internal static (int Count, long RetainedBytes) Statistics =>
        (Cache.Count, Cache.RetainedBytes);

    /// <summary>
    /// Converts a WinUI packaged-font reference to the form accepted by Win2D.
    /// XAML needs the fragment to select a family from the font file, whereas
    /// <see cref="CanvasTextFormat"/> accepts the app-package font URI itself.
    /// Keeping the fragment causes CanvasTextLayout creation to fail with
    /// E_INVALIDARG when a Markdown layout is built off the UI thread.
    /// </summary>
    internal static string NormalizeFontFamilyForCanvas(string fontFamily)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        string candidate = fontFamily.Trim();
        int fragmentIndex = candidate.LastIndexOf('#');
        if (fragmentIndex <= 0 ||
            !Uri.TryCreate(candidate[..fragmentIndex], UriKind.Absolute, out Uri? packageFontUri) ||
            !string.Equals(packageFontUri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return candidate[..fragmentIndex];
    }

    private readonly record struct TextFormatKey(
        string FontFamily,
        float FontSize,
        FontWeight FontWeight,
        FontStyle FontStyle,
        CanvasWordWrapping WordWrapping,
        CanvasHorizontalAlignment HorizontalAlignment,
        CanvasTextDirection Direction,
        CanvasLineSpacingMode LineSpacingMode,
        string LocaleName);

    internal sealed class Entry
    {
        private int _referenceCount = 1;

        internal Entry(CanvasTextFormat format, long weightBytes)
        {
            Format = format;
            WeightBytes = Math.Max(1, weightBytes);
        }

        internal CanvasTextFormat Format { get; }

        internal long WeightBytes { get; }

        internal void AddCacheReference() => Interlocked.Increment(ref _referenceCount);

        internal bool TryAcquire()
        {
            int observed = Volatile.Read(ref _referenceCount);
            while (observed > 0)
            {
                int updated = Interlocked.CompareExchange(
                    ref _referenceCount,
                    observed + 1,
                    observed);
                if (updated == observed)
                    return true;
                observed = updated;
            }

            return false;
        }

        internal void ReleaseCacheReference() => Release();

        internal void Release()
        {
            if (Interlocked.Decrement(ref _referenceCount) != 0)
                return;

            try { Format.Dispose(); }
            catch { /* Native shutdown and apartment teardown are best effort. */ }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;

        internal Lease(Entry entry) => _entry = entry;

        internal CanvasTextFormat Format =>
            Volatile.Read(ref _entry)?.Format ??
            throw new ObjectDisposedException(nameof(Lease));

        public void Dispose() =>
            Interlocked.Exchange(ref _entry, null)?.Release();
    }
}
