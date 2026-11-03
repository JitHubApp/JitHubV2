namespace MarkdownRenderer.Images;

/// <summary>A static image container. The provider must validate the actual encoded bytes.</summary>
public enum MarkdownRasterFormat
{
    /// <summary>Portable Network Graphics.</summary>
    Png,
    /// <summary>JPEG.</summary>
    Jpeg,
    /// <summary>Windows bitmap.</summary>
    Bmp,
    /// <summary>WebP.</summary>
    WebP,
    /// <summary>Graphics Interchange Format.</summary>
    Gif,
    /// <summary>AV1 Image File Format.</summary>
    Avif,
    /// <summary>Windows icon.</summary>
    Ico,
}
