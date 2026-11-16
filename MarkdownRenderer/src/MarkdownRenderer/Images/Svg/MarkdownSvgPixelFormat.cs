namespace MarkdownRenderer.Images.Svg;

/// <summary>Supported premultiplied 32-bit raster byte orders.</summary>
public enum MarkdownSvgPixelFormat
{
    /// <summary>Premultiplied red, green, blue, alpha bytes.</summary>
    Rgba8Premultiplied,

    /// <summary>Premultiplied blue, green, red, alpha bytes.</summary>
    Bgra8Premultiplied,
}
