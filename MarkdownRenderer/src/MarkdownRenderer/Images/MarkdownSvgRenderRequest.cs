namespace MarkdownRenderer.Images;

/// <summary>Immutable physical-pixel inputs for one SVG rasterization.</summary>
/// <param name="TargetWidthPixels">Width of the complete intended output in physical pixels.</param>
/// <param name="TargetHeightPixels">Height of the complete intended output in physical pixels.</param>
/// <param name="TileRegion">
/// Optional subregion to rasterize in complete-output coordinates. A null value
/// requests the complete output.
/// </param>
/// <param name="PixelFormat">Requested premultiplied output byte order.</param>
/// <param name="Priority">Scheduling priority for the raster operation.</param>
public sealed record MarkdownSvgRenderRequest(
    int TargetWidthPixels,
    int TargetHeightPixels,
    MarkdownSvgTileRegion? TileRegion = null,
    MarkdownSvgPixelFormat PixelFormat = MarkdownSvgPixelFormat.Rgba8Premultiplied,
    MarkdownSvgRenderPriority Priority = MarkdownSvgRenderPriority.Visible)
{
    /// <summary>
    /// Gets the optional exact physical-pixel viewport for the complete output.
    /// Its ceiling must equal <see cref="TargetWidthPixels"/> and
    /// <see cref="TargetHeightPixels"/>. The integer target remains the raster
    /// allocation size; this value changes only the physical SVG scale and clip.
    /// </summary>
    public MarkdownSvgPhysicalViewport? PhysicalViewport { get; init; }
}
