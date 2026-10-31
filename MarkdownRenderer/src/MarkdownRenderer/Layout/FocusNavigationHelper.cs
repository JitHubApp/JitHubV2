using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

internal enum FocusNavigationDirection
{
    Left,
    Right,
    Up,
    Down,
}

internal static class FocusNavigationHelper
{
    public static int MoveTab(int itemCount, int currentIndex, int resumeIndex, bool reverse)
    {
        if (itemCount <= 0) return -1;

        if (currentIndex < 0)
        {
            if (resumeIndex >= 0)
            {
                return reverse
                    ? Math.Clamp(resumeIndex - 1, 0, itemCount - 1)
                    : Math.Clamp(resumeIndex, 0, itemCount - 1);
            }

            return reverse ? itemCount - 1 : 0;
        }

        bool atEnd = !reverse && currentIndex == itemCount - 1;
        bool atStart = reverse && currentIndex == 0;
        if (atEnd || atStart) return -1;

        return reverse ? currentIndex - 1 : currentIndex + 1;
    }

    public static int FindNearestIndex(IReadOnlyList<Rect> rects, Point point)
    {
        if (rects.Count == 0) return -1;

        int best = -1;
        double bestScore = double.PositiveInfinity;
        for (int i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            double x = Math.Clamp(point.X, rect.Left, rect.Right);
            double y = Math.Clamp(point.Y, rect.Top, rect.Bottom);
            double dx = point.X - x;
            double dy = point.Y - y;
            double score = dx * dx + dy * dy;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    public static int MoveSpatial(IReadOnlyList<Rect> rects, int currentIndex, FocusNavigationDirection direction)
    {
        if (currentIndex < 0 || currentIndex >= rects.Count) return -1;
        var current = rects[currentIndex];
        if (current.Width <= 0 || current.Height <= 0) return -1;

        int best = -1;
        double bestScore = double.PositiveInfinity;
        for (int i = 0; i < rects.Count; i++)
        {
            if (i == currentIndex) continue;
            var candidate = rects[i];
            if (candidate.Width <= 0 || candidate.Height <= 0) continue;

            if (!TryGetDirectionalScore(current, candidate, direction, out double score)) continue;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    private static bool TryGetDirectionalScore(
        Rect current,
        Rect candidate,
        FocusNavigationDirection direction,
        out double score)
    {
        double cx = CenterX(current);
        double cy = CenterY(current);
        double tx = CenterX(candidate);
        double ty = CenterY(candidate);

        double primary;
        double secondary;
        double overlapPenalty;
        switch (direction)
        {
            case FocusNavigationDirection.Left:
                primary = cx - tx;
                secondary = Math.Abs(cy - ty);
                overlapPenalty = RangesOverlap(current.Top, current.Bottom, candidate.Top, candidate.Bottom) ? 0 : 1_000_000;
                break;
            case FocusNavigationDirection.Right:
                primary = tx - cx;
                secondary = Math.Abs(cy - ty);
                overlapPenalty = RangesOverlap(current.Top, current.Bottom, candidate.Top, candidate.Bottom) ? 0 : 1_000_000;
                break;
            case FocusNavigationDirection.Up:
                primary = cy - ty;
                secondary = Math.Abs(cx - tx);
                overlapPenalty = RangesOverlap(current.Left, current.Right, candidate.Left, candidate.Right) ? 0 : 1_000_000;
                break;
            case FocusNavigationDirection.Down:
                primary = ty - cy;
                secondary = Math.Abs(cx - tx);
                overlapPenalty = RangesOverlap(current.Left, current.Right, candidate.Left, candidate.Right) ? 0 : 1_000_000;
                break;
            default:
                score = double.PositiveInfinity;
                return false;
        }

        if (primary <= 0.5)
        {
            score = double.PositiveInfinity;
            return false;
        }

        score = overlapPenalty + primary * 1_000 + secondary;
        return true;
    }

    private static double CenterX(Rect rect) => rect.X + rect.Width / 2.0;
    private static double CenterY(Rect rect) => rect.Y + rect.Height / 2.0;

    private static bool RangesOverlap(double aStart, double aEnd, double bStart, double bEnd) =>
        aStart <= bEnd && bStart <= aEnd;
}
