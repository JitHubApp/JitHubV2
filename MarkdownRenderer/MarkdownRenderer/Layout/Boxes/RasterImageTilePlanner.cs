using System;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Plans bounded output-pixel rectangles for a future tiled raster decode.
/// Coordinates are in the complete raster's final displayed pixel space,
/// after scaling and EXIF orientation have been applied.
/// </summary>
internal static class RasterImageTilePlanner
{
    /// <summary>Hard per-tile edge limit, independent of user performance options.</summary>
    internal const int TileEdgePixels = 1024;

    /// <summary>
    /// Maximum number of tiles possible under the raster decoder's immutable
    /// dimension ceiling. A caller can therefore allocate the full plan safely.
    /// </summary>
    internal const int MaxTileCount =
        ((RasterImageResourceBudget.MaxDimension + TileEdgePixels - 1) / TileEdgePixels) *
        ((RasterImageResourceBudget.MaxDimension + TileEdgePixels - 1) / TileEdgePixels);

    /// <summary>
    /// Creates a row-major, gap-free plan. Invalid or out-of-policy dimensions
    /// are rejected before any allocation or multiplication is performed.
    /// </summary>
    internal static bool TryCreate(int width, int height, out RasterImageTilePlan plan)
    {
        plan = default;
        if (width <= 0 || height <= 0 ||
            width > RasterImageResourceBudget.MaxDimension ||
            height > RasterImageResourceBudget.MaxDimension)
        {
            return false;
        }

        int columns = 1 + ((width - 1) / TileEdgePixels);
        int rows = 1 + ((height - 1) / TileEdgePixels);
        int tileCount;
        try
        {
            tileCount = checked(columns * rows);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (tileCount is < 1 or > MaxTileCount)
            return false;

        RasterImageTile[] tiles = new RasterImageTile[tileCount];
        int index = 0;
        for (int y = 0; y < height;)
        {
            int tileHeight = Math.Min(TileEdgePixels, height - y);
            for (int x = 0; x < width;)
            {
                int tileWidth = Math.Min(TileEdgePixels, width - x);
                tiles[index++] = new RasterImageTile(x, y, tileWidth, tileHeight);
                x = checked(x + tileWidth);
            }

            y = checked(y + tileHeight);
        }

        if (index != tileCount)
            return false;

        plan = new RasterImageTilePlan(width, height, tiles);
        return true;
    }
}

/// <summary>A tile rectangle in the final displayed raster-pixel coordinate space.</summary>
internal readonly record struct RasterImageTile(int X, int Y, int Width, int Height)
{
    internal int Right => checked(X + Width);
    internal int Bottom => checked(Y + Height);
}

/// <summary>Immutable dimensions and tile list produced by <see cref="RasterImageTilePlanner"/>.</summary>
internal readonly struct RasterImageTilePlan
{
    private readonly RasterImageTile[]? _tiles;

    internal RasterImageTilePlan(int width, int height, RasterImageTile[] tiles)
    {
        Width = width;
        Height = height;
        _tiles = tiles;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int Count => _tiles?.Length ?? 0;
    internal ReadOnlyMemory<RasterImageTile> Tiles => _tiles ?? Array.Empty<RasterImageTile>();
}
