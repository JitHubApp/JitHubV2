namespace MarkdownRenderer.Images;

/// <summary>Metadata and dependency hints discovered while opening an SVG.</summary>
/// <param name="IntrinsicWidthDips">Optional intrinsic width in 96-DPI CSS pixels/DIPs.</param>
/// <param name="IntrinsicHeightDips">Optional intrinsic height in 96-DPI CSS pixels/DIPs.</param>
/// <param name="IntrinsicAspectRatio">Optional intrinsic width-to-height ratio.</param>
/// <param name="Description">Optional accessible description from the root <c>desc</c> element.</param>
/// <param name="HasText">True when rendering depends on the worker's font generation.</param>
/// <param name="UsesCurrentColor">True when rendering depends on the semantic color.</param>
/// <param name="UsesColorScheme">True when rendering depends on the selected color scheme.</param>
public sealed record MarkdownSvgDocumentInfo(
    double? IntrinsicWidthDips = null,
    double? IntrinsicHeightDips = null,
    double? IntrinsicAspectRatio = null,
    string? Description = null,
    bool HasText = false,
    bool UsesCurrentColor = false,
    bool UsesColorScheme = false);
