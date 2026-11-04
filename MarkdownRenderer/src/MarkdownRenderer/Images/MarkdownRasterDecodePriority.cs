namespace MarkdownRenderer.Images;

/// <summary>Scheduling priority for static raster preparation.</summary>
public enum MarkdownRasterDecodePriority
{
    /// <summary>Speculative preparation, behind visible requests.</summary>
    Overscan,
    /// <summary>Content needed in the current viewport.</summary>
    Visible,
}
