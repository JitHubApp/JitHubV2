using System;

namespace MarkdownRenderer.Controls.Internal.Rendering.Tiles;

/// <summary>
/// The fixed grid of full-width document tiles at one width and
/// rasterization scale. Tile heights are whole device pixels, so every tile
/// starts on a device pixel row and adjacent tiles meet without a seam.
/// </summary>
internal readonly record struct DocumentTileGrid
{
    /// <summary>Device pixel rows in one tile.</summary>
    internal const int DefaultTilePixelHeight = 512;

    /// <summary>Widest surface Direct3D 11 guarantees, in device pixels.</summary>
    internal const int MaximumPixelWidth = 16384;

    /// <summary>
    /// Rounding step, in device pixels, of the single tile of a document
    /// shorter than one default tile, so it is not re-created on every
    /// pixel of growth.
    /// </summary>
    internal const int ShortDocumentPixelStep = 128;

    private DocumentTileGrid(double width, double scale, int pixelWidth, int pixelHeight)
    {
        Width = width;
        Scale = scale;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        TileHeight = pixelHeight / scale;
    }

    /// <summary>Document width in DIPs.</summary>
    internal double Width { get; }

    /// <summary>Device pixels per DIP.</summary>
    internal double Scale { get; }

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    /// <summary>Tile height in DIPs.</summary>
    internal double TileHeight { get; }

    /// <summary>Bytes one tile surface holds.</summary>
    internal long TileBytes => (long)PixelWidth * PixelHeight * 4;

    /// <summary>
    /// The grid for a document <paramref name="width"/> DIPs wide at
    /// <paramref name="scale"/>, or false when either is unusable.
    /// </summary>
    internal static bool TryCreate(
        double width,
        double scale,
        out DocumentTileGrid grid,
        int tilePixelHeight = DefaultTilePixelHeight)
    {
        grid = default;
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(scale) || scale <= 0 || tilePixelHeight <= 0)
            return false;

        int pixelWidth = (int)Math.Min(MaximumPixelWidth, Math.Ceiling(width * scale));
        grid = new DocumentTileGrid(width, scale, Math.Max(1, pixelWidth), tilePixelHeight);
        return true;
    }

    /// <summary>
    /// The tile height, in device pixels, for a document
    /// <paramref name="documentHeight"/> DIPs tall at <paramref name="scale"/>:
    /// the default, or for a shorter document one tile just tall enough,
    /// rounded up to <see cref="ShortDocumentPixelStep"/>. Many short
    /// documents, such as a list of comments, then hold no unused rows.
    /// </summary>
    internal static int PixelHeightFor(double documentHeight, double scale)
    {
        if (!double.IsFinite(documentHeight) || !double.IsFinite(scale) || documentHeight <= 0 || scale <= 0)
            return DefaultTilePixelHeight;

        double needed = Math.Ceiling(documentHeight * scale);
        if (needed >= DefaultTilePixelHeight)
            return DefaultTilePixelHeight;

        int steps = Math.Max(1, (int)Math.Ceiling(needed / ShortDocumentPixelStep));
        return Math.Min(DefaultTilePixelHeight, steps * ShortDocumentPixelStep);
    }

    /// <summary>The tile containing document Y <paramref name="y"/>, clamped at zero.</summary>
    internal int IndexAt(double y)
        => double.IsFinite(y) && y > 0 ? (int)Math.Min(int.MaxValue - 1, Math.Floor(y / TileHeight)) : 0;

    /// <summary>The document Y of the top of tile <paramref name="index"/>.</summary>
    internal double TopOf(int index) => index * (double)PixelHeight / Scale;

    /// <summary>Tiles needed to cover <paramref name="documentHeight"/> DIPs.</summary>
    internal int CountFor(double documentHeight)
        => double.IsFinite(documentHeight) && documentHeight > 0
            ? (int)Math.Min(int.MaxValue - 1, Math.Ceiling(documentHeight / TileHeight))
            : 0;

    /// <summary>True when both grids produce identical tile pixels.</summary>
    internal bool HasSameRaster(DocumentTileGrid other)
        => PixelWidth == other.PixelWidth && PixelHeight == other.PixelHeight &&
            Math.Abs(Scale - other.Scale) < 1e-9 && Math.Abs(Width - other.Width) < 1e-6;
}
