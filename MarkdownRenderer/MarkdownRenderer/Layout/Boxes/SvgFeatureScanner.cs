using System;
using System.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Conservative classifier that routes an SVG document to one of two render
/// tiers based on a fast string scan over its bytes. The scan is deliberately
/// permissive: any feature Win2D's <c>CanvasSvgDocument</c> is known to handle
/// poorly (filters, masks, clip paths, patterns, foreign objects, animations,
/// CSS &lt;style&gt; blocks) routes to the Skia rasterization fallback.
/// </summary>
/// <remarks>
/// False positives (Win2D-capable SVGs routed to Skia) are acceptable — Skia
/// renders them correctly, just allocates a bitmap. False negatives (Skia-only
/// SVGs routed to Win2D) are caught by the runtime exception safety net in
/// <see cref="ImageBox"/>, which switches the URL to Skia on the next paint.
/// Public for unit-test access (mirrors <see cref="SvgIntrinsics"/>).
/// </remarks>
public enum SvgRenderTier
{
    /// <summary>Use Win2D's vector <c>CanvasSvgDocument</c> path (zero rasterization cost).</summary>
    Win2D,
    /// <summary>Use the Svg.Skia rasterization fallback.</summary>
    Skia,
}

/// <summary>Conservative SVG feature classifier — see <see cref="SvgRenderTier"/>.</summary>
public static class SvgFeatureScanner
{
    // Elements/attributes whose presence forces Skia. Stored as UTF-8 byte
    // patterns to avoid decoding the entire SVG to a string for the scan.
    private static readonly string[] _skiaSentinels = new[]
    {
        "<filter",
        "filter=",          // filter="url(#…)" applied to a shape
        "<mask",
        "mask=",
        "<clipPath",
        "clip-path=",
        "<pattern",
        "<foreignObject",
        "<animate",         // covers <animate>, <animateTransform>, <animateMotion>
        "<style",           // CSS in <style>
        "<feGaussianBlur",  // direct filter primitives even outside <filter>
        "<feDropShadow",
        "<feColorMatrix",
        "<feBlend",
        "<feComposite",
        "<feMerge",
        "<feMorphology",
        "<feTurbulence",
    };

    /// <summary>
    /// Classifies the SVG. Returns <see cref="SvgRenderTier.Skia"/> if any
    /// known-incompatible feature is detected; otherwise <see cref="SvgRenderTier.Win2D"/>.
    /// Safe to call from any thread; performs no I/O.
    /// </summary>
    public static SvgRenderTier Classify(ReadOnlySpan<byte> svgBytes)
    {
        if (svgBytes.IsEmpty) return SvgRenderTier.Win2D;

        // Decode lazily into a stack-friendly UTF-8 string. SVG payloads in
        // markdown rarely exceed a few hundred KB; allocating a string here
        // is comparable to the parse cost itself.
        string text;
        try
        {
            text = Encoding.UTF8.GetString(svgBytes);
        }
        catch
        {
            // Malformed UTF-8 — let the parser handle it; the safety net will
            // fall back to Skia if Win2D rejects it.
            return SvgRenderTier.Win2D;
        }

        foreach (var sentinel in _skiaSentinels)
        {
            if (text.IndexOf(sentinel, StringComparison.OrdinalIgnoreCase) >= 0)
                return SvgRenderTier.Skia;
        }
        return SvgRenderTier.Win2D;
    }
}
