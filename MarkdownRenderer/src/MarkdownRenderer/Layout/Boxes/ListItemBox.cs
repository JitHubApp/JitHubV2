using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Lays out a list item as two side-by-side columns: a fixed-width marker gutter
/// on the left (bullet or number) and a variable-width content area on the right.
/// </summary>
internal sealed class ListItemBox : BlockBox
{
    public readonly InlineContainerBox Marker;
    public readonly StackBox Content;
    private readonly float _markerWidth;

    /// <summary>The marker (bullet or number) box on the left.</summary>
    /// <summary>The content (rest of list item) StackBox on the right.</summary>
    public float MarkerWidth => _markerWidth;

    /// <summary>Flow direction for this list item. When RightToLeft, the marker is placed on the right and content on the left.</summary>
    public FlowDirection FlowDirection { get; set; } = FlowDirection.LeftToRight;

    public ListItemBox(InlineContainerBox marker, StackBox content, float markerWidth)
    {
        Marker = marker;
        Content = content;
        _markerWidth = markerWidth;
    }

    public override float Measure(float availableWidth)
    {
        float contentWidth = Math.Max(1f, availableWidth - _markerWidth - (float)(Margin.Left + Margin.Right));

        Marker.Measure(_markerWidth);
        float contentHeight = Content.Measure(contentWidth);

        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        if (rtl)
        {
            Marker.Arrange(contentWidth, 0, _markerWidth);
            Content.Arrange(0, 0, contentWidth);
        }
        else
        {
            Marker.Arrange(0, 0, _markerWidth);
            Content.Arrange(_markerWidth, 0, contentWidth);
        }

        float height = Math.Max((float)Marker.Bounds.Height, contentHeight)
                       + (float)(Margin.Top + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, height);
        return height;
    }

    public override void Arrange(float x, float y, float width)
    {
        float contentWidth = Math.Max(1f, width - _markerWidth - (float)(Margin.Left + Margin.Right));
        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        float markerX = x + (rtl ? contentWidth : 0f) + (float)Margin.Left;
        float contentX = x + (rtl ? 0f : _markerWidth) + (float)Margin.Left;
        float contentY = y + (float)Margin.Top;
        Marker.Arrange(markerX, contentY, _markerWidth);
        Content.Arrange(
            contentX,
            contentY,
            contentWidth);
        Bounds = new Rect(x, y, width, Bounds.Height);
        IsDirty = false;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (Marker.Bounds.Bottom >= viewport.Top && Marker.Bounds.Top <= viewport.Bottom)
            Marker.Paint(ds, viewport);
        if (Content.Bounds.Bottom >= viewport.Top && Content.Bounds.Top <= viewport.Bottom)
            Content.Paint(ds, viewport);
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Windows.UI.Color color,
        Rect viewport)
    {
        if (Marker.Bounds.Bottom >= viewport.Top && Marker.Bounds.Top <= viewport.Bottom)
            Marker.PaintSelectionForeground(ds, range, color, viewport);
        if (Content.Bounds.Bottom >= viewport.Top && Content.Bounds.Top <= viewport.Bottom)
            Content.PaintSelectionForeground(ds, range, color, viewport);
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        if (Marker.HitTest(point, out position)) return true;
        if (Content.HitTest(point, out position)) return true;
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    internal override bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        if (Marker.HitTestSelectionEndpoint(point, out position)) return true;
        if (Content.HitTestSelectionEndpoint(point, out position)) return true;
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    public override void Dispose()
    {
        var cleanup = new BlockBoxCleanupScope();
        cleanup.TryDispose(Marker);
        cleanup.TryDispose(Content);
        cleanup.ThrowIfAny("disposing a list item");
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        var cleanup = new BlockBoxCleanupScope();
        cleanup.TryCollectRetirementTasks(Marker, pendingTasks);
        cleanup.TryCollectRetirementTasks(Content, pendingTasks);
        cleanup.ThrowIfAny("collecting list-item retirement tasks");
    }

    /// <summary>Validates the exact measured list-column geometry without allocation.</summary>
    internal bool HasCoherentMeasuredPlacement()
    {
        Rect bounds = Bounds;
        float width = (float)bounds.Width;
        Thickness margin = Margin;
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width <= 0 ||
            !double.IsFinite(bounds.Height) || bounds.Height < 0 ||
            !float.IsFinite(width) || width <= 0 ||
            !float.IsFinite(_markerWidth) || _markerWidth < 0 ||
            !double.IsFinite(margin.Left) || !double.IsFinite(margin.Top) ||
            !double.IsFinite(margin.Right) || !double.IsFinite(margin.Bottom) ||
            FlowDirection is not (FlowDirection.LeftToRight or FlowDirection.RightToLeft))
        {
            return false;
        }

        float contentWidth = Math.Max(1f, width - _markerWidth - (float)(margin.Left + margin.Right));
        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        float markerX = (float)bounds.X + (rtl ? contentWidth : 0f) + (float)margin.Left;
        float contentX = (float)bounds.X + (rtl ? 0f : _markerWidth) + (float)margin.Left;
        float contentY = (float)bounds.Y + (float)margin.Top;
        Rect markerBounds = Marker.Bounds;
        Rect contentBounds = Content.Bounds;
        float markerHeight = (float)markerBounds.Height;
        float contentHeight = (float)contentBounds.Height;
        if (!float.IsFinite(contentWidth) || contentWidth <= 0 ||
            !float.IsFinite(markerX) || !float.IsFinite(contentX) || !float.IsFinite(contentY) ||
            !double.IsFinite(markerBounds.X) || markerBounds.X != markerX ||
            !double.IsFinite(markerBounds.Y) || markerBounds.Y != contentY ||
            !double.IsFinite(markerBounds.Width) || markerBounds.Width != _markerWidth ||
            !double.IsFinite(markerBounds.Height) || markerBounds.Height < 0 ||
            !float.IsFinite(markerHeight) || (double)markerHeight != markerBounds.Height ||
            !double.IsFinite(contentBounds.X) || contentBounds.X != contentX ||
            !double.IsFinite(contentBounds.Y) || contentBounds.Y != contentY ||
            !double.IsFinite(contentBounds.Width) || contentBounds.Width != contentWidth ||
            !double.IsFinite(contentBounds.Height) || contentBounds.Height < 0 ||
            !float.IsFinite(contentHeight) || (double)contentHeight != contentBounds.Height)
        {
            return false;
        }

        float expectedHeight = Math.Max(markerHeight, contentHeight) +
            (float)(margin.Top + margin.Bottom);
        return float.IsFinite(expectedHeight) && bounds.Height == expectedHeight &&
               Content.HasCoherentMeasuredPlacement();
    }

    internal void SwapMeasuredPlacementNoThrow(ListItemBox prepared)
    {
        (Bounds, prepared.Bounds) = (prepared.Bounds, Bounds);
        (IsDirty, prepared.IsDirty) = (prepared.IsDirty, IsDirty);
    }
}
