namespace MarkdownRenderer.Images;

/// <summary>Exact physical-pixel viewport dimensions for a fractional SVG raster.</summary>
/// <param name="Width">Positive finite viewport width in physical pixels.</param>
/// <param name="Height">Positive finite viewport height in physical pixels.</param>
/// <remarks>
/// The viewport is separate from its integer raster allocation. For example,
/// a viewport of 1112.5 by 197.5 pixels is stored in an allocation of 1113 by
/// 198 pixels. The renderer validates that each viewport dimension rounds up
/// to the corresponding allocation dimension before dispatching native work.
/// </remarks>
public readonly record struct MarkdownSvgPhysicalViewport(
    double Width,
    double Height);
