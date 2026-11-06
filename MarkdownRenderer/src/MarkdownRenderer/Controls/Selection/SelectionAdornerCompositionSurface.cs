using System;
using Microsoft.UI.Composition;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// One Win2D-drawn composition surface and the brush that presents it.
/// It has no XAML peer, so its native release never races XAML teardown.
/// </summary>
internal sealed class SelectionAdornerCompositionSurface : IDisposable
{
    internal SelectionAdornerCompositionSurface(
        CompositionGraphicsDevice device,
        SelectionAdornerSurfaceAllocation allocation)
    {
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(allocation.Width * allocation.Dpi / 96.0));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(allocation.Height * allocation.Dpi / 96.0));
        Surface = device.CreateDrawingSurface(
            new Size(pixelWidth, pixelHeight),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        try
        {
            Brush = device.Compositor.CreateSurfaceBrush(Surface);
            Brush.Stretch = CompositionStretch.Fill;
        }
        catch
        {
            Surface.Dispose();
            throw;
        }

        PixelBounds = new Rect(0, 0, pixelWidth, pixelHeight);
        Dpi = allocation.Dpi;
    }

    internal CompositionDrawingSurface Surface { get; }
    internal CompositionSurfaceBrush Brush { get; }
    internal Rect PixelBounds { get; }
    internal float Dpi { get; }

    public void Dispose()
    {
        Brush.Dispose();
        Surface.Dispose();
    }
}
