using System;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Process-wide cache for immutable CanvasTextFormat descriptors. Formats are
/// not device resources, and callers hold leases so LRU eviction cannot dispose
/// a descriptor while a native text-layout constructor is consuming it.
/// </summary>
internal static class SharedCanvasTextFormatCache
{
    internal const long DefaultBudgetBytes = 512L * 1024;
    internal const string NativeCleanupFailureDataKey = "MarkdownRenderer.NativeCleanupFailure";
    private static readonly SharedCanvasTextFormatStore SharedStore = new(DefaultBudgetBytes, NativeResourceRetirement.Shared);

    internal static Lease Acquire(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment,
        FlowDirection flowDirection,
        string? localeName = null,
        bool useContentAwareLineSpacing = false) =>
        SharedStore.Acquire(style, wordWrapping, horizontalAlignment, flowDirection, localeName,
            useContentAwareLineSpacing, CanvasVerticalAlignment.Top);

    /// <summary>
    /// Acquires a format with an explicit vertical alignment. The original
    /// six-parameter overload remains the default-Top ABI for existing callers.
    /// </summary>
    internal static Lease Acquire(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment,
        FlowDirection flowDirection,
        string? localeName,
        bool useContentAwareLineSpacing,
        CanvasVerticalAlignment verticalAlignment) =>
        SharedStore.Acquire(style, wordWrapping, horizontalAlignment, flowDirection, localeName,
            useContentAwareLineSpacing, verticalAlignment);

    internal static void Clear() => SharedStore.Clear();

    internal static (int Count, long RetainedBytes) Statistics => SharedStore.Statistics;

    /// <summary>
    /// Converts a WinUI packaged-font reference to the form accepted by Win2D.
    /// XAML needs the fragment to select a family from the font file, whereas
    /// <see cref="CanvasTextFormat"/> accepts the app-package font URI itself.
    /// </summary>
    internal static string NormalizeFontFamilyForCanvas(string fontFamily)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        string candidate = fontFamily.Trim();
        int fragmentIndex = candidate.LastIndexOf('#');
        if (fragmentIndex <= 0 ||
            !Uri.TryCreate(candidate[..fragmentIndex], UriKind.Absolute, out Uri? packageFontUri) ||
            !string.Equals(packageFontUri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase))
            return candidate;

        return candidate[..fragmentIndex];
    }

    internal sealed class Lease : IDisposable
    {
        private SharedCanvasTextFormatEntry? _entry;

        internal Lease(SharedCanvasTextFormatEntry entry) => _entry = entry;

        internal CanvasTextFormat Format =>
            Volatile.Read(ref _entry)?.Format ?? throw new ObjectDisposedException(nameof(Lease));

        /// <summary>
        /// The shared entry this lease still references, or null once released.
        /// Narrow internal witness for native-lifetime diagnostics; never
        /// exposed in the public renderer API.
        /// </summary>
        internal SharedCanvasTextFormatEntry? SharedEntry => Volatile.Read(ref _entry);

        internal Exception? ReleaseForFailure() => Interlocked.Exchange(ref _entry, null)?.Release();

        public void Dispose() => _ = ReleaseForFailure();
    }
}
