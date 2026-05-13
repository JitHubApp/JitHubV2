using System;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Block that reserves space for a hosted WinUI <see cref="FrameworkElement"/>.
/// The element itself is created on the UI thread by
/// <see cref="MarkdownRenderer.Controls.MarkdownRendererControl"/> after layout
/// completes; this box only carries the source AST node, the desired height,
/// and a reference to the factory that owns it.
/// </summary>
public sealed class EmbedBox : BlockBox
{
    public Block SourceBlock { get; }
    public IMarkdownEmbedFactory Factory { get; }

    /// <summary>Set by the control once the FrameworkElement is realized.</summary>
    public FrameworkElement? RealizedElement { get; set; }

    public EmbedBox(Block sourceBlock, IMarkdownEmbedFactory factory, Thickness margin = default)
    {
        SourceBlock = sourceBlock ?? throw new ArgumentNullException(nameof(sourceBlock));
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Margin = margin;
    }

    public override float Measure(float availableWidth)
    {
        float innerWidth = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
        float h = Factory.MeasureHeight(SourceBlock, innerWidth);
        if (h < 0) h = 0;
        float total = h + (float)(Margin.Top + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, total);
        return total;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        // The hosted element is drawn by WinUI on the overlay canvas — nothing
        // to paint on the Win2D surface.
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        // Pointer events go to the hosted XAML element directly via the
        // overlay; the canvas-level hit test should treat the embed area as
        // non-selectable text.
        position = new DocumentPosition(BlockIndex, 0, 0);
        return false;
    }
}
