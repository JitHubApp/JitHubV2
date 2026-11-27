using System;
using System.Numerics;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Rendering.Tiles;

/// <summary>
/// One full-width document tile: a Win2D-drawn composition surface shown by
/// a sprite visual at the tile's document Y. It has no XAML peer, so its
/// native release never races XAML teardown. The sprite stays hidden until
/// the surface has been painted at least once for its current position.
/// </summary>
internal sealed class DocumentTileSurface : IDisposable
{
    private readonly CompositionSurfaceBrush _brush;
    private bool _disposed;

    internal DocumentTileSurface(Compositor compositor, CompositionGraphicsDevice device, DocumentTileGrid grid)
    {
        Grid = grid;
        Surface = device.CreateDrawingSurface(
            new Size(grid.PixelWidth, grid.PixelHeight),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);
        try
        {
            _brush = compositor.CreateSurfaceBrush(Surface);
            _brush.Stretch = CompositionStretch.Fill;
            // Tiles sit on whole device pixels at 1:1, so sampling must not blur.
            _brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            Visual = compositor.CreateSpriteVisual();
            Visual.Brush = _brush;
            Visual.Size = new Vector2((float)(grid.PixelWidth / grid.Scale), (float)grid.TileHeight);
            Visual.IsVisible = false;
        }
        catch
        {
            _brush?.Dispose();
            Surface.Dispose();
            throw;
        }
    }

    /// <summary>The grid this tile's surface was sized for.</summary>
    internal DocumentTileGrid Grid { get; }

    internal CompositionDrawingSurface Surface { get; }

    internal SpriteVisual Visual { get; }

    /// <summary>The device-pixel rectangle of the whole surface.</summary>
    internal Rect PixelBounds => new(0, 0, Grid.PixelWidth, Grid.PixelHeight);

    /// <summary>Moves the tile to document Y <paramref name="top"/>, hidden until shown.</summary>
    internal void MoveTo(double top)
    {
        var offset = new Vector3(0, (float)top, 0);
        if (Visual.Offset != offset)
        {
            Visual.IsVisible = false;
            Visual.Offset = offset;
        }
    }

    /// <summary>Shows the tile after its surface was painted for its position.</summary>
    internal void Show() => Visual.IsVisible = true;

    internal void Hide() => Visual.IsVisible = false;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Visual.Brush = null;
        _brush.Dispose();
        Surface.Dispose();
        Visual.Dispose();
    }
}
