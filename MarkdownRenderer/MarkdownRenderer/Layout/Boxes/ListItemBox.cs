using System;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Lays out a list item as two side-by-side columns: a fixed-width marker gutter
/// on the left (bullet or number) and a variable-width content area on the right.
/// </summary>
public sealed class ListItemBox : BlockBox
{
    private readonly InlineContainerBox _marker;
    private readonly StackBox _content;
    private readonly float _markerWidth;

    /// <summary>The marker (bullet or number) box on the left.</summary>
    public InlineContainerBox Marker => _marker;
    /// <summary>The content (rest of list item) StackBox on the right.</summary>
    public StackBox Content => _content;

    /// <summary>Flow direction for this list item. When RightToLeft, the marker is placed on the right and content on the left.</summary>
    public FlowDirection FlowDirection { get; set; } = FlowDirection.LeftToRight;

    public ListItemBox(InlineContainerBox marker, StackBox content, float markerWidth)
    {
        _marker = marker;
        _content = content;
        _markerWidth = markerWidth;
    }

    public override float Measure(float availableWidth)
    {
        float contentWidth = Math.Max(1f, availableWidth - _markerWidth - (float)(Margin.Left + Margin.Right));

        _marker.Measure(_markerWidth);
        float contentHeight = _content.Measure(contentWidth);

        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        if (rtl)
        {
            _marker.Arrange(contentWidth, 0, _markerWidth);
            _content.Arrange(0, 0, contentWidth);
        }
        else
        {
            _marker.Arrange(0, 0, _markerWidth);
            _content.Arrange(_markerWidth, 0, contentWidth);
        }

        float height = Math.Max((float)_marker.Bounds.Height, contentHeight)
                       + (float)(Margin.Top + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, height);
        return height;
    }

    public override void Arrange(float x, float y, float width)
    {
        float dx = x - (float)Bounds.X;
        float dy = y - (float)Bounds.Y;
        _marker.Arrange(
            (float)_marker.Bounds.X + dx + (float)Margin.Left,
            (float)_marker.Bounds.Y + dy + (float)Margin.Top,
            _markerWidth);
        float contentWidth = Math.Max(1f, width - _markerWidth - (float)(Margin.Left + Margin.Right));
        _content.Arrange(
            (float)_content.Bounds.X + dx + (float)Margin.Left,
            (float)_content.Bounds.Y + dy + (float)Margin.Top,
            contentWidth);
        Bounds = new Rect(x, y, width, Bounds.Height);
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (_marker.Bounds.Bottom >= viewport.Top && _marker.Bounds.Top <= viewport.Bottom)
            _marker.Paint(ds, viewport);
        if (_content.Bounds.Bottom >= viewport.Top && _content.Bounds.Top <= viewport.Bottom)
            _content.Paint(ds, viewport);
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Windows.UI.Color color,
        Rect viewport)
    {
        if (_marker.Bounds.Bottom >= viewport.Top && _marker.Bounds.Top <= viewport.Bottom)
            _marker.PaintSelectionForeground(ds, range, color, viewport);
        if (_content.Bounds.Bottom >= viewport.Top && _content.Bounds.Top <= viewport.Bottom)
            _content.PaintSelectionForeground(ds, range, color, viewport);
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        if (_marker.HitTest(point, out position)) return true;
        if (_content.HitTest(point, out position)) return true;
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    public override void Dispose()
    {
        _marker.Dispose();
        _content.Dispose();
    }
}
