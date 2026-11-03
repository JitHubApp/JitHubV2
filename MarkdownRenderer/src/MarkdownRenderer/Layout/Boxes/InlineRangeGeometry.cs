using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Maps document ranges, caret positions and run indexes of an inline
/// container to rectangles in root-local or logical coordinates, applying
/// the script baseline offsets and clipping full-line selection rectangles
/// to the container's content box.
/// </summary>
internal static class InlineRangeGeometry
{
    internal static IEnumerable<Rect> GetRangeRects(InlineContainerBox box, DocumentRange range)
        => EnumerateRangeRects(box, range, 0, 0, fullLineRects: false, logicalCoordinates: false);
    internal static IEnumerable<Rect> GetLogicalRangeRects(InlineContainerBox box, DocumentRange range)
        => EnumerateRangeRects(box, range, 0, 0, fullLineRects: false, logicalCoordinates: true);
    internal static bool TryGetCaretPoint(
        InlineContainerBox box,
        DocumentPosition position,
        bool rangeStart,
        out Point point)
        => TryGetCaretPoint(box, position, rangeStart, logicalCoordinates: false, out point);
    internal static bool TryGetLogicalCaretPoint(
        InlineContainerBox box,
        DocumentPosition position,
        bool rangeStart,
        out Point point)
        => TryGetCaretPoint(box, position, rangeStart, logicalCoordinates: true, out point);

    private static bool TryGetCaretPoint(
        InlineContainerBox box,
        DocumentPosition position,
        bool rangeStart,
        bool logicalCoordinates,
        out Point point)
    {
        point = default;
        if (box.TextLayout is null || position.BlockIndex != box.BlockIndex)
            return false;

        box.EnsureBuffer();
        if (box.LayoutBuffer.Length == 0)
            return false;

        int offset = Math.Clamp(ToBufferIndex(box, position), 0, box.LayoutBuffer.Length);
        TouchSelectionCaretQuery query = TouchSelectionCaretPolicy.Resolve(
            box.LayoutBuffer.Length,
            offset,
            rangeStart);
        var caret = box.TextLayout.GetCaretPosition(
            query.CharacterIndex,
            query.TrailingSideOfCharacter,
            out CanvasTextLayoutRegion region);
        var style = box.GetContainerStyle();
        var (rootLocalX, rootLocalY) = box.GetSnappedOrigin(
            style,
            out float logicalOriginX,
            out float logicalOriginY);
        float baseX = logicalCoordinates ? logicalOriginX : rootLocalX;
        float baseY = logicalCoordinates ? logicalOriginY : rootLocalY;
        // Win2D reports caret X in the paragraph's logical coordinate space.
        // Character regions and drawing are already mirrored for an RTL
        // paragraph, so mirror this one coordinate before translating it into
        // document space. DirectWrite still resolves the caret within each bidi
        // run; this transform only accounts for the paragraph base direction.
        double visualX = TouchSelectionCaretPolicy.ResolveVisualX(
            caret.X,
            box.TextLayout.RequestedSize.Width,
            box.Context.FlowDirection == FlowDirection.RightToLeft);
        double x = baseX + visualX;
        double y = baseY + region.LayoutBounds.Bottom +
            box.ScriptBaselineGeometry.GetOffsetAtCharacter(region.CharacterIndex);
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return false;

        point = new Point(x, y);
        return true;
    }
    internal static IEnumerable<Rect> GetBufferRangeRects(InlineContainerBox box, int from, int length)
        => EnumerateRangeRects(box, documentRange: null, from, length, fullLineRects: false, logicalCoordinates: false);
    internal static IEnumerable<Rect> GetBufferRangeLineRects(InlineContainerBox box, int from, int length)
        => EnumerateRangeRects(box, documentRange: null, from, length, fullLineRects: true, logicalCoordinates: false);

    private static IEnumerable<Rect> EnumerateRangeRects(
        InlineContainerBox box,
        DocumentRange? documentRange,
        int from,
        int length,
        bool fullLineRects,
        bool logicalCoordinates)
    {
        if (box.TextLayout is null)
            yield break;

        box.EnsureBuffer();
        if (!TryResolveBufferRange(box, documentRange, ref from, ref length))
            yield break;

        var style = box.GetContainerStyle();
        var (rootLocalX, rootLocalY) = box.GetSnappedOrigin(
            style,
            out float logicalOriginX,
            out float logicalOriginY);
        float baseX = logicalCoordinates ? logicalOriginX : rootLocalX;
        float baseY = logicalCoordinates ? logicalOriginY : rootLocalY;
        var lineMetrics = fullLineRects ? box.TryGetLineMetrics() : null;
        if (!fullLineRects || lineMetrics is null)
        {
            var regions = GetAdjustedCharacterRegions(box, box.TextLayout, from, length);
            if (regions is null)
                yield break;

            // DirectWrite lets the whitespace at a wrap hang past the layout
            // width and reports it inside the line's character regions. Clip
            // each region to the content width, or to its line's
            // non-whitespace glyphs where a long word genuinely overflows, so
            // range and selection rectangles stay inside the text's box.
            CanvasLineMetrics[]? clipLines = box.TryGetLineMetrics();
            double contentWidth = box.TextLayout.RequestedSize.Width;
            // The text origin is snapped to device pixels, so the content box
            // starts up to a pixel away from layout x = 0. Clip against the
            // box itself, not the snapped origin.
            Thickness clipMargin = box.Margin;
            Thickness clipPadding = box.GetEffectivePadding(style);
            double contentLeft = box.Bounds.X + clipMargin.Left + clipPadding.Left - rootLocalX;
            Dictionary<int, (double Left, double Right)>? lineInk = null;
            foreach (var region in regions)
            {
                var bounds = region.LayoutBounds;
                if (!TryClipRegionToContent(
                        box.TextLayout,
                        clipLines,
                        region,
                        contentLeft,
                        contentWidth,
                        ref lineInk,
                        out double left,
                        out double right))
                {
                    continue;
                }

                yield return new Rect(
                    baseX + left,
                    baseY + bounds.Y,
                    right - left,
                    bounds.Height);
            }
            yield break;
        }

        int rangeEnd = from + length;
        int lineStart = 0;
        double y = 0;
        foreach (var metric in lineMetrics)
        {
            int charCount = Math.Max(0, metric.CharacterCount);
            int lineEnd = lineStart + charCount;
            bool intersects = lineEnd > from && lineStart < rangeEnd;
            if (intersects)
            {
                yield return new Rect(
                    baseX,
                    baseY + y,
                    Math.Max(1, box.TextLayout.LayoutBounds.Width),
                    Math.Max(1, metric.Height));
            }

            y += metric.Height;
            lineStart = lineEnd;
        }
    }

    // A document range maps to its buffer offsets; a raw buffer range is
    // clamped, and an empty one widens to the character at its start.
    private static bool TryResolveBufferRange(
        InlineContainerBox box,
        DocumentRange? documentRange,
        ref int from,
        ref int length)
    {
        if (documentRange is { } range)
        {
            from = ToBufferIndex(box, range.Start);
            length = ToBufferIndex(box, range.End) - from;
            return length > 0;
        }

        if (box.LayoutBuffer.Length == 0)
            return false;

        from = Math.Clamp(from, 0, box.LayoutBuffer.Length);
        length = Math.Clamp(length, 0, box.LayoutBuffer.Length - from);
        if (length <= 0)
            length = from < box.LayoutBuffer.Length ? 1 : 0;
        return length > 0;
    }

    // Clips a region's horizontal extent to the content box, widened to its
    // line's non-whitespace ink. Returns false for a region made only of
    // whitespace hanging outside the box.
    private static bool TryClipRegionToContent(
        CanvasTextLayout layout,
        CanvasLineMetrics[]? clipLines,
        CanvasTextLayoutRegion region,
        double contentLeft,
        double contentWidth,
        ref Dictionary<int, (double Left, double Right)>? lineInk,
        out double left,
        out double right)
    {
        var bounds = region.LayoutBounds;
        left = bounds.X;
        right = bounds.X + bounds.Width;
        if (clipLines is not null && double.IsFinite(contentWidth) && contentWidth > 0 &&
            TryGetLineIndex(clipLines, region.CharacterIndex, out int lineIndex, out int clipLineStart))
        {
            lineInk ??= new Dictionary<int, (double Left, double Right)>();
            if (!lineInk.TryGetValue(lineIndex, out (double Left, double Right) ink))
            {
                ink = MeasureLineInk(layout, clipLines[lineIndex], clipLineStart);
                lineInk[lineIndex] = ink;
            }

            double allowedLeft = Math.Min(contentLeft, ink.Left);
            double allowedRight = Math.Max(contentLeft + contentWidth, ink.Right);
            if (double.IsFinite(contentLeft) && bounds.Width > 0)
            {
                // Only whitespace hanging outside the box is removed;
                // a region made of nothing else has no rectangle,
                // like any other zero-area selection fragment.
                // Zero-width regions such as line breaks are kept.
                if (left >= allowedRight || right <= allowedLeft)
                    return false;
                left = Math.Max(left, allowedLeft);
                right = Math.Min(right, allowedRight);
            }
        }

        return true;
    }

    private static bool TryGetLineIndex(
        CanvasLineMetrics[] lines,
        int characterIndex,
        out int lineIndex,
        out int lineStart)
    {
        int start = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            int count = Math.Max(0, lines[i].CharacterCount);
            if (characterIndex < start + count)
            {
                lineIndex = i;
                lineStart = start;
                return true;
            }
            start += count;
        }

        lineIndex = -1;
        lineStart = 0;
        return false;
    }

    // Horizontal extent of a line's characters excluding its trailing
    // whitespace, in layout coordinates. An all-whitespace line has no ink;
    // the empty extent then leaves only the content width as the limit.
    private static (double Left, double Right) MeasureLineInk(
        CanvasTextLayout layout,
        CanvasLineMetrics line,
        int lineStart)
    {
        int inkLength = Math.Max(0, line.CharacterCount - Math.Max(0, line.TrailingWhitespaceCount));
        if (inkLength == 0)
            return (double.PositiveInfinity, double.NegativeInfinity);

        double left = double.PositiveInfinity;
        double right = double.NegativeInfinity;
        CanvasTextLayoutRegion[]? regions = layout.GetCharacterRegions(lineStart, inkLength);
        if (regions is null)
            return (left, right);
        foreach (CanvasTextLayoutRegion region in regions)
        {
            left = Math.Min(left, region.LayoutBounds.X);
            right = Math.Max(right, region.LayoutBounds.X + region.LayoutBounds.Width);
        }
        return (left, right);
    }

    internal static CanvasTextLayoutRegion[]? GetAdjustedCharacterRegions(
        InlineContainerBox box,
        CanvasTextLayout layout,
        int start,
        int length)
    {
        if (start < 0 || length <= 0 || start > int.MaxValue - length)
            return Array.Empty<CanvasTextLayoutRegion>();

        int end = start + length;
        if (box.ScriptBaselineGeometry.Count == 0)
            return layout.GetCharacterRegions(start, length);

        int firstSegmentEnd = box.ScriptBaselineGeometry.GetSegmentEnd(start, end, out float firstOffset);
        if (firstSegmentEnd == end)
        {
            CanvasTextLayoutRegion[]? single = layout.GetCharacterRegions(start, length);
            if (single is not null && firstOffset != 0f)
                OffsetRegions(single, firstOffset);
            return single;
        }

        List<CanvasTextLayoutRegion>? combined = null;
        int cursor = start;
        while (cursor < end)
        {
            int segmentEnd = box.ScriptBaselineGeometry.GetSegmentEnd(cursor, end, out float offsetY);
            if (segmentEnd <= cursor)
                return null;

            CanvasTextLayoutRegion[]? segment = layout.GetCharacterRegions(cursor, segmentEnd - cursor);
            if (segment is null)
                return null;

            combined ??= new List<CanvasTextLayoutRegion>(segment.Length + 2);
            if (offsetY != 0f)
                OffsetRegions(segment, offsetY);
            combined.AddRange(segment);
            cursor = segmentEnd;
        }

        return combined?.ToArray() ?? Array.Empty<CanvasTextLayoutRegion>();
    }

    private static void OffsetRegions(CanvasTextLayoutRegion[] regions, float offsetY)
    {
        for (int index = 0; index < regions.Length; index++)
        {
            CanvasTextLayoutRegion region = regions[index];
            Rect bounds = region.LayoutBounds;
            bounds.Y += offsetY;
            region.LayoutBounds = bounds;
            regions[index] = region;
        }
    }
    internal static Rect GetRunRect(InlineContainerBox box, int inlineIndex)
    {
        if (box.TextLayout is null) return default;
        var style = box.GetContainerStyle();
        var (baseX, baseY) = box.GetSnappedOrigin(style);
        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (run.InlineIndex == inlineIndex && len > 0)
            {
                var regions = GetAdjustedCharacterRegions(box, box.TextLayout, cumulative, len);
                if (regions is not null && regions.Length > 0)
                {
                    // Union all regions for multi-line runs.
                    double x1 = double.MaxValue, y1 = double.MaxValue;
                    double x2 = double.MinValue, y2 = double.MinValue;
                    foreach (var r in regions)
                    {
                        var lb = r.LayoutBounds;
                        if (lb.X < x1) x1 = lb.X;
                        if (lb.Y < y1) y1 = lb.Y;
                        if (lb.X + lb.Width  > x2) x2 = lb.X + lb.Width;
                        if (lb.Y + lb.Height > y2) y2 = lb.Y + lb.Height;
                    }
                    return new Rect(baseX + x1, baseY + y1, x2 - x1, y2 - y1);
                }
                return default;
            }
            cumulative += len;
        }
        return default;
    }

    internal static int ToBufferIndex(InlineContainerBox box, DocumentPosition pos)
    {
        if (pos.BlockIndex < box.BlockIndex) return 0;
        if (pos.BlockIndex > box.BlockIndex) return box.LayoutBuffer.Length;
        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            if (run.InlineIndex == pos.InlineIndex)
                return cumulative + Math.Clamp(pos.CharacterOffset, 0, run.TextLength);
            cumulative += run.TextLength;
        }
        return box.LayoutBuffer.Length;
    }
}
