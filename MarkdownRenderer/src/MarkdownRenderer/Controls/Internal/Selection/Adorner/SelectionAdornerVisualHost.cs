using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// Presents the adorner surface through a sprite visual hosted as the
/// child visual of an inert placeholder element in the overlay.
/// </summary>
internal sealed class SelectionAdornerVisualHost
    : ISelectionAdornerSurfaceHost<SelectionAdornerCompositionSurface>, IDisposable
{
    private readonly SpriteVisual _visual;
    private SelectionAdornerCompositionSurface? _source;
    private bool _disposed;

    internal SelectionAdornerVisualHost(Border placeholder)
    {
        Placeholder = placeholder;
        Compositor = ElementCompositionPreview.GetElementVisual(placeholder).Compositor;
        _visual = Compositor.CreateSpriteVisual();
        ElementCompositionPreview.SetElementChildVisual(placeholder, _visual);
    }

    internal Border Placeholder { get; }
    internal Compositor Compositor { get; }

    public SelectionAdornerCompositionSurface? Source
    {
        get => _source;
        set
        {
            if (_disposed)
                return;
            _visual.Brush = value?.Brush;
            _source = value;
        }
    }

    /// <summary>
    /// Sizes the visual to the whole allocation and clips it to the viewport.
    /// </summary>
    internal void SetViewport(SelectionAdornerSurfaceAllocation allocation, double width, double height)
    {
        if (_disposed)
            return;
        var size = new Vector2(allocation.Width, allocation.Height);
        if (_visual.Size != size)
            _visual.Size = size;
        float right = Math.Max(0f, allocation.Width - (float)width);
        float bottom = Math.Max(0f, allocation.Height - (float)height);
        if (_visual.Clip is InsetClip clip)
        {
            if (clip.RightInset != right)
                clip.RightInset = right;
            if (clip.BottomInset != bottom)
                clip.BottomInset = bottom;
        }
        else
        {
            _visual.Clip = Compositor.CreateInsetClip(0, 0, right, bottom);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source = null;
        _visual.Brush = null;
        ElementCompositionPreview.SetElementChildVisual(Placeholder, null);
        _visual.Clip?.Dispose();
        _visual.Dispose();
    }
}
