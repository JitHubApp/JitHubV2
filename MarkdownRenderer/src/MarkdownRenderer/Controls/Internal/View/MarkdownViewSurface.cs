using System;
using MarkdownRenderer.Controls.Internal.Rendering.Tiles;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.View;

/// <summary>
/// Owns a renderer's XAML visual tree: an optional scroll viewer around a
/// root grid that stacks the document canvas under the overlay canvas for
/// hosted embeds and selection chrome. The document canvas is a plain
/// hit-testable element whose child visual is the composition
/// <see cref="DocumentTileLayer"/> that presents the painted document.
/// </summary>
/// <remarks>
/// The scroll viewer, root and overlay live until <see cref="Detach"/>. The
/// document canvas and its tiles are created on load and removed on unload,
/// so an unloaded, retained view holds no GPU surfaces.
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

    /// <summary>The hit-testable document canvas element, sized to the document.</summary>
    public Border? Canvas { get; private set; }

    /// <summary>The composition tiles presenting the document on <see cref="Canvas"/>.</summary>
    public DocumentTileLayer? Tiles { get; private set; }

    /// <summary>The element the renderer hosts as its content.</summary>
    public UIElement? ContentElement => Scroll is not null ? Scroll : Root;

    /// <summary>
    /// Creates the document canvas and its tile layer beneath the overlay.
    /// Returns the new canvas, or null when one already exists or the surface
    /// is detached.
    /// </summary>
    public Border? TryCreateCanvas()
    {
        if (Canvas is not null || Root is null)
            return null;
        var canvas = new Border
        {
            // A brush, even transparent, makes the whole document hit-testable.
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDoubleTapEnabled = true,
            IsHoldingEnabled = true,
            IsTapEnabled = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Root.Children.Insert(0, canvas);
        Canvas = canvas;
        Tiles = new DocumentTileLayer(canvas);
        return canvas;
    }

    /// <summary>
    /// Releases the document tiles and removes the canvas from the visual
    /// tree. Call from the renderer's Unloaded boundary.
    /// </summary>
    public void RemoveCanvas()
    {
        Tiles?.Dispose();
        Tiles = null;
        if (Canvas is { } canvas && Root is not null)
            _ = Root.Children.Remove(canvas);
        Canvas = null;
    }

    /// <summary>
    /// Asks the ancestor viewport to bring a document rect into view when the
    /// renderer does not own its scroll viewer. Returns false when it owns
    /// one, is detached, or the request failed.
    /// </summary>
    public bool BringIntoExternalViewport(Rect rect, bool animate, double alignmentRatio = 0)
    {
        if (OwnsScrollViewport || Root is null)
            return false;

        try
        {
            Root.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = animate,
                TargetRect = rect,
                VerticalAlignmentRatio = Math.Clamp(alignmentRatio, 0, 1),
                VerticalOffset = 0,
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Drops every element reference during disposal.</summary>
    public void Detach()
    {
        Tiles?.Dispose();
        Tiles = null;
        Canvas = null;
        Overlay = null;
        Scroll = null;
        Root = null;
    }
}
