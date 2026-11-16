namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

/// <summary>
/// The running totals and metadata that one preflight pass over an SVG
/// accumulates. It is a mutable value passed by reference between the scan
/// steps, and into CSS and embedded-reference inspection, so every nested
/// resource charges the same totals and no step allocates or captures it.
/// </summary>
internal struct SvgPreflightScan
{
    /// <summary>The weighted structural cost charged so far.</summary>
    internal int StructuralCost;

    /// <summary>The current depth of open SVG-namespace <c>&lt;svg&gt;</c> elements.</summary>
    internal int SvgNesting;

    /// <summary>The number of filter primitives seen.</summary>
    internal int FilterPrimitives;

    /// <summary>Whether the document root has been seen and validated.</summary>
    internal bool SawRoot;

    /// <summary>Whether the document or a nested resource contains text.</summary>
    internal bool HasText;

    /// <summary>Whether the document uses <c>currentColor</c>.</summary>
    internal bool UsesCurrentColor;

    /// <summary>Whether the document uses a <c>prefers-color-scheme</c> condition.</summary>
    internal bool UsesColorScheme;

    /// <summary>The encoding named by the XML declaration, if any.</summary>
    internal string? DeclaredEncoding;

    /// <summary>The root element's CSS width, if parseable.</summary>
    internal double? Width;

    /// <summary>The root element's CSS height, if parseable.</summary>
    internal double? Height;

    /// <summary>The first top-level description, trimmed and bounded.</summary>
    internal string? Description;

    /// <summary>The decoded bytes of embedded resources charged so far.</summary>
    internal long EmbeddedBytes;

    /// <summary>The decoded pixels of embedded rasters charged so far.</summary>
    internal long EmbeddedPixels;
}
