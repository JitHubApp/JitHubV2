using System;
using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Resolves pointer hit tests and selection rectangles for a code block
/// against its arranged chunks and horizontal scroll offset.
/// </summary>
internal sealed class CodeBlockHitTester
{
    private readonly CodeBlockBox _host;

    internal CodeBlockHitTester(CodeBlockBox host) => _host = host;

    internal bool HitTest(Point point, out DocumentPosition position)
    {
        var outer = _host.OuterRect();
        if (!outer.Contains(point))
        {
            position = CodeStartPosition();
            return false;
        }

        if (TryHitTestChunks(point, out position))
            return true;

        var codeViewport = _host.CodeViewportRect(outer, _host.GetCodeStyle());
        if (point.Y < codeViewport.Top)
        {
            position = CodeStartPosition();
            return true;
        }

        if (point.Y > codeViewport.Bottom)
        {
            position = CodeEndPosition();
            return true;
        }

        if (_host.Chunks.Count > 0)
        {
            var first = _host.Chunks[0];
            var last = _host.Chunks[_host.Chunks.Count - 1];
            if (point.Y < first.Bounds.Top)
            {
                position = CodeStartPosition();
                return true;
            }

            if (point.Y > last.Bounds.Bottom)
            {
                position = CodeEndPosition();
                return true;
            }

            double clampedX = Clamp(point.X, codeViewport.Left, codeViewport.Right);
            Point clippedPoint = new(clampedX, point.Y);
            bool clippedHorizontalHit = codeViewport.Contains(clippedPoint) &&
                _host.MeasuredContentWidth > _host.BodyViewportWidth + 0.5f;
            int firstVisible = _host.FindFirstVisibleChunk(point.Y);
            for (int index = firstVisible; index < _host.Chunks.Count; index++)
            {
                InlineContainerBox chunk = _host.Chunks[index];
                if (chunk.Bounds.Top > point.Y)
                    break;
                if (point.Y < chunk.Bounds.Top || point.Y > chunk.Bounds.Bottom)
                    continue;

                if (chunk.HitTest(clippedPoint, out position))
                    return true;

                // A no-wrap chunk may be translated beyond its logical bounds
                // by RTL horizontal scrolling while its text is still visible
                // through CodeViewportRect. Only this parent-owned clipped
                // viewport may bypass the child container's X bounds check.
                if (clippedHorizontalHit &&
                    chunk.HitTestWithinHorizontalViewport(clippedPoint, out position))
                {
                    return true;
                }
            }
        }

        position = CodeStartPosition();
        return true;
    }

    internal IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        var clip = _host.CodeViewportRect(_host.OuterRect(), _host.GetCodeStyle());
        foreach (var chunk in _host.Chunks)
        {
            foreach (var rect in chunk.GetSelectionRects(range))
            {
                var clipped = CodeBlockBox.Intersect(rect, clip);
                if (clipped.Width > 0 && clipped.Height > 0)
                    yield return clipped;
            }
        }
    }

    private bool TryHitTestChunks(Point point, out DocumentPosition position)
    {
        int first = _host.FindFirstVisibleChunk(point.Y);
        for (int index = first; index < _host.Chunks.Count; index++)
        {
            InlineContainerBox chunk = _host.Chunks[index];
            if (chunk.Bounds.Top > point.Y)
                break;
            if (chunk.HitTest(point, out position))
                return true;
        }

        position = CodeStartPosition();
        return false;
    }

    private DocumentPosition CodeStartPosition()
    {
        if (_host.Chunks.Count == 0)
            return new DocumentPosition(_host.BlockIndex, 0, 0);

        var first = _host.Chunks[0];
        if (first.Runs.Count == 0)
            return new DocumentPosition(first.BlockIndex, 0, 0);

        return new DocumentPosition(first.BlockIndex, first.Runs[0].InlineIndex, 0);
    }

    private DocumentPosition CodeEndPosition()
    {
        if (_host.Chunks.Count == 0)
            return new DocumentPosition(_host.BlockIndex, 0, 0);

        var last = _host.Chunks[_host.Chunks.Count - 1];
        if (last.Runs.Count == 0)
            return new DocumentPosition(last.BlockIndex, 0, 0);

        var run = last.Runs[last.Runs.Count - 1];
        return new DocumentPosition(last.BlockIndex, run.InlineIndex, run.Text.Length);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max <= min)
            return min;

        return Math.Min(Math.Max(value, min), max - 0.5);
    }
}
