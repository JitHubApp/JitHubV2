using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Owns a renderer's XAML visual tree: an optional scroll viewer around a
/// root grid that stacks the Win2D canvas under the overlay canvas for hosted
/// embeds and selection chrome.
/// </summary>
/// <remarks>
/// The scroll viewer, root and overlay live until <see cref="Detach"/>. The
/// Win2D canvas is created on load and removed on unload, because Win2D
/// requires <see cref="CanvasVirtualControl.RemoveFromVisualTree"/> at the
/// containing control's Unloaded boundary.
/// </remarks>
internal sealed class MarkdownViewSurface
{
    public MarkdownViewSurface(bool ownsScrollViewport)
    {
        OwnsScrollViewport = ownsScrollViewport;
        if (ownsScrollViewport)
        {
            Scroll = new ScrollViewer
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                ZoomMode = ZoomMode.Disabled,
            };
        }
        Root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Overlay = new Canvas
        {
            // Background null so the empty overlay doesn't capture pointer
            // events. Hosted embeds are individually hit-testable.
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Root.Children.Add(Overlay);
        // Selection, hover and drag chrome is created only when used. A new
        // document does not need an Image, Border and two Button handles in
        // its XAML tree merely to paint its first viewport.
        if (Scroll is not null)
            Scroll.Content = Root;
    }

    /// <summary>
    /// True when the renderer scrolls itself; false when it follows an
    /// ancestor's effective viewport.
    /// </summary>
    public bool OwnsScrollViewport { get; }

    public ScrollViewer? Scroll { get; private set; }

    public Grid? Root { get; private set; }

    public Canvas? Overlay { get; private set; }

    public CanvasVirtualControl? Canvas { get; private set; }

    /// <summary>The element the renderer hosts as its content.</summary>
    public UIElement? ContentElement => Scroll is not null ? Scroll : Root;

    /// <summary>
    /// Creates the Win2D canvas beneath the overlay. Returns the new canvas,
    /// or null when one already exists or the surface is detached.
    /// </summary>
    public CanvasVirtualControl? TryCreateCanvas()
    {
        if (Canvas is not null || Root is null)
            return null;
        Canvas = new CanvasVirtualControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDoubleTapEnabled = true,
            IsHoldingEnabled = true,
            IsTapEnabled = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Root.Children.Insert(0, Canvas);
        return Canvas;
    }

    /// <summary>
    /// Removes the Win2D canvas from the visual tree. Call from the renderer's
    /// Unloaded boundary.
    /// </summary>
    public void RemoveCanvas()
    {
        Canvas?.RemoveFromVisualTree();
        Canvas = null;
    }

    /// <summary>Drops every element reference during disposal.</summary>
    public void Detach()
    {
        Canvas = null;
        Overlay = null;
        Scroll = null;
        Root = null;
    }
}
