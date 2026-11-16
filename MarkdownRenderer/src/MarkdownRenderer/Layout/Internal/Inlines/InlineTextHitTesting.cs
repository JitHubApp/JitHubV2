using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Text;

namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// Hit-tests an inline container's measured text layout: document positions
/// under a point (including the script-shifted glyph regions), the run
/// under a pointer, and whether a point lies inside a run's rendered
/// bounds.
/// </summary>
internal static class InlineTextHitTesting
{
    internal static bool HitTestNativeLayout(InlineContainerBox box, Point point, out DocumentPosition position)
    {
        if (box.TextLayout is null)
        {
            position = new DocumentPosition(box.BlockIndex, 0, 0);
            return true;
        }

        var style = box.GetContainerStyle();
        var (x, y) = box.GetSnappedOrigin(style);
        float layoutX = (float)point.X - x;
        float layoutY = (float)point.Y - y;
        CanvasTextLayoutRegion hit;
        bool trailingSide;
        if (!TryHitTestScriptGeometry(box, layoutX, layoutY, out hit, out trailingSide))
            box.TextLayout.HitTest(layoutX, layoutY, out hit, out trailingSide);
        int charIndex = (int)hit.CharacterIndex;
        // When the pointer is on the trailing (right for LTR) half of a glyph,
        // DirectWrite returns the glyph's character index + trailingSide=true.
        // For selection we want the CARET position, which is one past that index.
        // Without this adjustment, dragging past the last character of a run never
        // produces an offset that includes the final character — the selection
        // stops one char short and the last char is never visually highlighted.
        if (trailingSide) charIndex++;

        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            // Use strict '<' to match RunAt: character at index `cumulative+len`
            // belongs to the *next* run, not this one.  Prior `<=` pulled boundary
            // hits backward into the wrong run, leaving HitTest and RunAt
            // disagreeing about which run owns the boundary character.
            if (charIndex < cumulative + len)
            {
                position = new DocumentPosition(box.BlockIndex, run.InlineIndex, charIndex - cumulative);
                return true;
            }
            cumulative += len;
        }
        // Past last char: position at end of last run (or block end if empty).
        if (box.RunList.Count > 0)
        {
            var last = box.RunList[box.RunList.Count - 1];
            position = new DocumentPosition(box.BlockIndex, last.InlineIndex, last.TextLength);
        }
        else
        {
            position = new DocumentPosition(box.BlockIndex, 0, 0);
        }
        return true;
    }

    private static bool TryHitTestScriptGeometry(
        InlineContainerBox box,
        float x,
        float y,
        out CanvasTextLayoutRegion hit,
        out bool trailingSide)
    {
        hit = default;
        trailingSide = false;
        if (box.TextLayout is null || box.ScriptBaselineGeometry.Count == 0)
            return false;

        for (int spanIndex = 0; spanIndex < box.ScriptBaselineGeometry.Count; spanIndex++)
        {
            ScriptBaselineGeometry.Span span = box.ScriptBaselineGeometry.GetSpan(spanIndex);
            CanvasTextLayoutRegion[]? regions = box.TextLayout.GetCharacterRegions(span.Start, span.Length);
            if (regions is null)
                continue;

            foreach (CanvasTextLayoutRegion region in regions)
            {
                Rect bounds = region.LayoutBounds;
                bounds.Y += span.OffsetY;
                if (!bounds.Contains(new Point(x, y)))
                    continue;

                box.TextLayout.HitTest(x, y - span.OffsetY, out CanvasTextLayoutRegion candidate, out bool candidateTrailing);
                int candidateIndex = candidate.CharacterIndex;
                if (candidateIndex < span.Start || candidateIndex >= span.End)
                    continue;

                hit = candidate;
                trailingSide = candidateTrailing;
                return true;
            }
        }

        return false;
    }
    internal static InlineRun? RunAt(InlineContainerBox box, Point point)
    {
        if (box.TextLayout is null) return null;
        if (!box.Bounds.Contains(point)) return null;
        var style = box.GetContainerStyle();
        var (x, y) = box.GetSnappedOrigin(style);
        float layoutX = (float)point.X - x;
        float layoutY = (float)point.Y - y;
        CanvasTextLayoutRegion hitRegion;
        bool trailingSide;
        if (!TryHitTestScriptGeometry(box, layoutX, layoutY, out hitRegion, out trailingSide))
            box.TextLayout.HitTest(layoutX, layoutY, out hitRegion, out trailingSide);
        int charIndex = (int)hitRegion.CharacterIndex;
        if (trailingSide) charIndex++;
        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (charIndex < cumulative + len) return run;
            cumulative += len;
        }
        // charIndex >= total buffer length (trailing-edge of last run): return last run
        // so hover-cursor and highlight match the hit-test behaviour that maps this
        // position to the last run's DocumentPosition.
        if (box.RunList.Count > 0) return box.RunList[box.RunList.Count - 1];
        return null;
    }
    internal static InlineRun? RunAtVisualPoint(InlineContainerBox box, Point point)
    {
        if (box.TextLayout is null || !box.Bounds.Contains(point))
            return null;

        box.EnsureBuffer();

        var style = box.GetContainerStyle();
        var (baseX, baseY) = box.GetSnappedOrigin(style);
        float layoutX = (float)point.X - baseX;
        float layoutY = (float)point.Y - baseY;
        CanvasTextLayoutRegion hitRegion;
        if (!TryHitTestScriptGeometry(box, layoutX, layoutY, out hitRegion, out _))
            box.TextLayout.HitTest(layoutX, layoutY, out hitRegion, out _);

        // CharacterIndex identifies the glyph under the pointer. Deliberately
        // ignore trailingSide here: applying it converts this geometric hit to
        // the next caret position and loses one-character atomic runs such as
        // inline images.
        int characterIndex = (int)hitRegion.CharacterIndex;
        int cumulative = 0;
        foreach (InlineRun run in box.RunList)
        {
            int length = run.TextLength;
            if (characterIndex < cumulative + length)
            {
                return IsPointInsideBufferRange(
                    box,
                    cumulative,
                    length,
                    point,
                    baseX,
                    baseY)
                    ? run
                    : null;
            }

            cumulative += length;
        }

        return null;
    }
    internal static bool TryGetRunBounds(InlineContainerBox box, InlineRun run, Point preferredPoint, out Rect bounds)
    {
        bounds = default;
        if (box.TextLayout is null)
            return false;

        box.EnsureBuffer();

        int cumulative = 0;
        foreach (var candidate in box.RunList)
        {
            int len = candidate.TextLength;
            if (!ReferenceEquals(candidate, run))
            {
                cumulative += len;
                continue;
            }

            if (len <= 0)
                return false;

            var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, cumulative, len);
            if (regions is null || regions.Length == 0)
                return false;

            var style = box.GetContainerStyle();
            var (baseX, baseY) = box.GetSnappedOrigin(style);
            Rect? first = null;
            Rect? closest = null;
            double closestDistance = double.MaxValue;

            foreach (var region in regions)
            {
                var lb = region.LayoutBounds;
                var rect = new Rect(
                    baseX + lb.X,
                    baseY + lb.Y,
                    lb.Width,
                    lb.Height);

                first ??= rect;
                if (rect.Contains(preferredPoint))
                {
                    bounds = rect;
                    return true;
                }

                double centerX = rect.X + rect.Width / 2.0;
                double centerY = rect.Y + rect.Height / 2.0;
                double dx = centerX - preferredPoint.X;
                double dy = centerY - preferredPoint.Y;
                double distance = dx * dx + dy * dy;
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = rect;
                }
            }

            bounds = closest ?? first.GetValueOrDefault();
            return bounds.Width > 0 && bounds.Height > 0;
        }

        return false;
    }
    internal static bool IsPointInsideRunBounds(InlineContainerBox box, InlineRun run, Point point)
    {
        if (box.TextLayout is null || !box.Bounds.Contains(point))
            return false;

        box.EnsureBuffer();

        int cumulative = 0;
        foreach (var candidate in box.RunList)
        {
            int len = candidate.TextLength;
            if (!ReferenceEquals(candidate, run))
            {
                cumulative += len;
                continue;
            }

            if (len <= 0)
                return false;

            var style = box.GetContainerStyle();
            var (baseX, baseY) = box.GetSnappedOrigin(style);
            return IsPointInsideBufferRange(
                box,
                cumulative,
                len,
                point,
                baseX,
                baseY);
        }

        return false;
    }

    private static bool IsPointInsideBufferRange(
        InlineContainerBox box,
        int start,
        int length,
        Point point,
        float baseX,
        float baseY)
    {
        if (box.TextLayout is null || length <= 0)
            return false;

        var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, start, length);
        if (regions is null || regions.Length == 0)
            return false;

        foreach (CanvasTextLayoutRegion region in regions)
        {
            Rect layoutBounds = region.LayoutBounds;
            var bounds = new Rect(
                baseX + layoutBounds.X,
                baseY + layoutBounds.Y,
                layoutBounds.Width,
                layoutBounds.Height);

            if (bounds.Contains(point))
                return true;
        }

        return false;
    }
}
