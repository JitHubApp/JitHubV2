namespace MarkdownRenderer.Images;

/// <summary>A rectangular physical-pixel tile in complete-output coordinates.</summary>
/// <param name="X">Horizontal offset from the complete output's left edge.</param>
/// <param name="Y">Vertical offset from the complete output's top edge.</param>
/// <param name="Width">Tile width in physical pixels.</param>
/// <param name="Height">Tile height in physical pixels.</param>
public readonly record struct MarkdownSvgTileRegion(int X, int Y, int Width, int Height);
