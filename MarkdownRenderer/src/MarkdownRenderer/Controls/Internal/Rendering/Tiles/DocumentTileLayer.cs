using System;
using System.Numerics;
using MarkdownRenderer.Diagnostics.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace MarkdownRenderer.Controls.Internal.Rendering.Tiles;

/// <summary>
/// The composition layer that presents the document: a clipped container
/// visual hosted on the document canvas element, holding one sprite per
/// painted tile, plus the graphics device that draws them. It owns the
/// tiles' lifetime and staleness; <see cref="CanvasTilePainter"/> decides
/// what to paint.
/// </summary>
/// <remarks>
/// The container is the canvas element's child visual, so the scroll viewer
/// moves every tile on the compositor without any UI-thread work. Tiles are
/// positioned in document coordinates and survive scrolling untouched.
/// </remarks>
internal sealed class DocumentTileLayer : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly DispatcherQueue? _dispatcher;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly InsetClip _clip;
    private readonly DocumentTileCache<DocumentTileSurface> _tiles = new();
    private CompositionGraphicsDevice? _graphicsDevice;
    private CanvasDevice? _canvasDevice;
    private DocumentTileGrid _grid;
    private bool _hasGrid;
    private bool _disposed;

    internal DocumentTileLayer(FrameworkElement host)
    {
        _host = host;
        _dispatcher = host.DispatcherQueue;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _root = _compositor.CreateContainerVisual();
        _clip = _compositor.CreateInsetClip();
        _root.Clip = _clip;
        ElementCompositionPreview.SetElementChildVisual(host, _root);
        // The document's extent changes with layout commits; a resized host
        // needs a new plan even when no tile went stale.
        host.SizeChanged += OnHostSizeChanged;
    }

    /// <summary>
    /// Raised on the UI thread when tiles went stale or the document changed
    /// shape, so the painter must plan another frame.
    /// </summary>
    internal event Action? PaintRequested;

    /// <summary>The Win2D device the tiles currently render with, if any.</summary>
    internal CanvasDevice? CanvasDevice => _canvasDevice;

    /// <summary>Marks every tile stale; each keeps its pixels until repainted.</summary>
    internal void MarkAllStale()
    {
        if (_disposed)
            return;
        _tiles.MarkAllStale();
        PaintRequested?.Invoke();
    }

    /// <summary>
    /// Marks stale every tile from document Y <paramref name="top"/> down.
    /// Positive infinity marks nothing but still requests a frame, because
    /// the document's extent may have changed.
    /// </summary>
    internal void MarkStaleFrom(double top)
    {
        if (_disposed)
            return;
        if (double.IsPositiveInfinity(top))
        {
            PaintRequested?.Invoke();
            return;
        }

        if (!_hasGrid || !double.IsFinite(top) || top <= 0)
        {
            MarkAllStale();
            return;
        }

        _tiles.MarkStale(_grid.IndexAt(top));
        PaintRequested?.Invoke();
    }

    /// <summary>Marks stale every tile intersecting document Y <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    internal void MarkStale(double top, double bottom)
    {
        if (_disposed)
            return;
        if (!_hasGrid || !double.IsFinite(top) || !double.IsFinite(bottom))
        {
            MarkAllStale();
            return;
        }

        if (bottom < top)
            return;
        _tiles.MarkStale(_grid.IndexAt(top), _grid.IndexAt(bottom));
        PaintRequested?.Invoke();
    }

    /// <summary>
    /// Sizes the clip to the document and switches to <paramref name="grid"/>,
    /// releasing every tile when it rasterizes differently from the current one.
    /// </summary>
    internal void Prepare(DocumentTileGrid grid, double documentHeight)
    {
        if (_disposed)
            return;

        if (!_hasGrid || !_grid.HasSameRaster(grid))
        {
            _tiles.Clear(Release);
            _grid = grid;
            _hasGrid = true;
        }

        // Win2D can replace its shared device after a loss. Switch the
        // graphics device before drawing so no tile paints with a dead one.
        if (_graphicsDevice is not null)
            _ = EnsureGraphicsDevice();

        var size = new Vector2((float)grid.Width, (float)Math.Max(0, documentHeight));
        if (_root.Size != size)
            _root.Size = size;
    }

    internal bool NeedsPaint(int index) => _tiles.NeedsPaint(index);

    /// <summary>
    /// Returns the tile for <paramref name="index"/>, reusing a spare or
    /// creating one, moved to its document position.
    /// </summary>
    internal DocumentTileSurface Acquire(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DocumentTileSurface? tile = _tiles.Find(index);
        if (tile is null)
        {
            tile = _tiles.TakeSpare() ?? CreateTile();
            _tiles.Assign(index, tile);
        }

        tile.MoveTo(_grid.TopOf(index));
        return tile;
    }

    /// <summary>Records a successful paint of tile <paramref name="index"/> and shows it.</summary>
    internal void MarkPainted(int index, DocumentTileSurface tile)
    {
        _tiles.MarkCurrent(index);
        tile.Show();
    }

    /// <summary>Releases every tile <paramref name="plan"/> no longer covers.</summary>
    internal void Evict(DocumentTilePlan plan) => _tiles.Evict(plan, Hide, Release);

    /// <summary>Releases the tiles kept for reuse once scrolling settles.</summary>
    internal void ReleaseSpares() => _tiles.ReleaseSpares(Release);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.SizeChanged -= OnHostSizeChanged;
        _tiles.Clear(Release);
        try
        {
            ElementCompositionPreview.SetElementChildVisual(_host, null);
        }
        catch (Exception exception)
        {
            // Teardown can race XAML's own release of the host; the visuals
            // below are still disposed deterministically.
            ReportReleaseFailure(exception);
        }

        _root.Clip = null;
        _clip.Dispose();
        _root.Dispose();
        ReleaseGraphicsDevice();
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_disposed)
            PaintRequested?.Invoke();
    }

    private DocumentTileSurface CreateTile()
    {
        var tile = new DocumentTileSurface(_compositor, EnsureGraphicsDevice(), _grid);
        _root.Children.InsertAtTop(tile.Visual);
        return tile;
    }

    private static void Hide(DocumentTileSurface tile) => tile.Hide();

    private void Release(DocumentTileSurface tile)
    {
        try
        {
            _root.Children.Remove(tile.Visual);
            tile.Dispose();
        }
        catch (Exception exception)
        {
            // A lost device can fail native release; the tile is unreachable
            // either way and its finalizer-free wrappers hold no managed state.
            ReportReleaseFailure(exception);
        }
    }

    /// <summary>
    /// Returns the composition graphics device, rendering with the current
    /// shared Win2D device. A replaced Win2D device is switched in place.
    /// </summary>
    private CompositionGraphicsDevice EnsureGraphicsDevice()
    {
        CanvasDevice canvasDevice = CanvasDevice.GetSharedDevice();
        if (_graphicsDevice is { } graphicsDevice)
        {
            if (!ReferenceEquals(_canvasDevice, canvasDevice))
            {
                CanvasComposition.SetCanvasDevice(graphicsDevice, canvasDevice);
                _canvasDevice = canvasDevice;
            }

            return graphicsDevice;
        }

        graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, canvasDevice);
        graphicsDevice.RenderingDeviceReplaced += OnRenderingDeviceReplaced;
        _graphicsDevice = graphicsDevice;
        _canvasDevice = canvasDevice;
        return graphicsDevice;
    }

    /// <summary>
    /// A replaced rendering device discards surface content without any
    /// paint failure, so mark every tile stale on the UI thread.
    /// </summary>
    private void OnRenderingDeviceReplaced(CompositionGraphicsDevice sender, RenderingDeviceReplacedEventArgs args)
        => _dispatcher?.TryEnqueue(() =>
        {
            if (!_disposed && ReferenceEquals(sender, _graphicsDevice))
                MarkAllStale();
        });

    private void ReleaseGraphicsDevice()
    {
        CompositionGraphicsDevice? graphicsDevice = _graphicsDevice;
        _graphicsDevice = null;
        _canvasDevice = null;
        if (graphicsDevice is null)
            return;
        try
        {
            graphicsDevice.RenderingDeviceReplaced -= OnRenderingDeviceReplaced;
            graphicsDevice.Dispose();
        }
        catch (Exception exception)
        {
            ReportReleaseFailure(exception);
        }
    }

    private static void ReportReleaseFailure(Exception failure)
        => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] document tile release failed: {failure.GetType().Name} " +
            $"{GraphicsDeviceErrors.FormatHResult(failure.HResult)}");
}
