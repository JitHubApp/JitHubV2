namespace MarkdownRenderer.Images;

/// <summary>Supported premultiplied sRGB byte orders.</summary>
public enum MarkdownRasterPixelFormat
{
    /// <summary>Red, green, blue, alpha.</summary>
    Rgba8Premultiplied,
    /// <summary>Blue, green, red, alpha.</summary>
    Bgra8Premultiplied,
}
