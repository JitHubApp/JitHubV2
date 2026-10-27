using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Managed, immutable script baseline offsets shared by paint, hit testing,
/// selection, and accessibility geometry. Ranges use half-open UTF-16 indexes
/// into the containing inline layout.
/// </summary>
internal sealed class ScriptBaselineGeometry
{
    internal readonly record struct Span(int Start, int Length, float OffsetY)
    {
        internal int End => Start + Length;
    }

    private readonly Span[] _spans;

    internal static ScriptBaselineGeometry Empty { get; } = new(Array.Empty<Span>());

    private ScriptBaselineGeometry(Span[] spans)
    {
        _spans = spans;
        foreach (Span span in spans)
        {
            if (span.OffsetY < 0)
                TopInset = Math.Max(TopInset, -span.OffsetY);
            else
                BottomInset = Math.Max(BottomInset, span.OffsetY);
        }
    }

    internal int Count => _spans.Length;
    internal float TopInset { get; }
    internal float BottomInset { get; }
    internal Span GetSpan(int index) => _spans[index];

    internal float GetOffsetAtCharacter(int index)
    {
        int spanIndex = FindSpanAtOrBefore(index);
        if (spanIndex >= 0)
        {
            Span span = _spans[spanIndex];
            if (index < span.End)
                return span.OffsetY;
        }

        return 0f;
    }

    /// <summary>
    /// Returns the end of the maximal same-offset segment beginning at
    /// <paramref name="start"/> and not beyond <paramref name="rangeEnd"/>.
    /// </summary>
    internal int GetSegmentEnd(int start, int rangeEnd, out float offsetY)
    {
        offsetY = 0f;
        if (start >= rangeEnd)
            return rangeEnd;

        int spanIndex = FindSpanAtOrBefore(start);
        if (spanIndex >= 0 && start < _spans[spanIndex].End)
        {
            Span span = _spans[spanIndex];
            offsetY = span.OffsetY;
            return Math.Min(rangeEnd, span.End);
        }

        int next = spanIndex + 1;
        return next < _spans.Length
            ? Math.Min(rangeEnd, _spans[next].Start)
            : rangeEnd;
    }

    internal float GetOffsetAtInsertionPoint(int offset)
    {
        if (_spans.Length == 0)
            return 0f;

        int character = offset > 0 ? offset - 1 : offset;
        return GetOffsetAtCharacter(character);
    }

    internal bool HasSameGeometry(ScriptBaselineGeometry? other)
    {
        if (other is null || _spans.Length != other._spans.Length ||
            TopInset != other.TopInset || BottomInset != other.BottomInset)
        {
            return false;
        }

        for (int index = 0; index < _spans.Length; index++)
        {
            if (_spans[index] != other._spans[index])
                return false;
        }

        return true;
    }

    private int FindSpanAtOrBefore(int characterIndex)
    {
        int low = 0;
        int high = _spans.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (_spans[middle].Start <= characterIndex)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    internal sealed class Builder
    {
        private List<Span>? _spans;

        internal void Add(int start, int length, float offsetY)
        {
            if (length <= 0 || !float.IsFinite(offsetY) || offsetY == 0f)
                return;

            List<Span> spans = _spans ??= new List<Span>();
            if (spans.Count > 0)
            {
                Span previous = spans[^1];
                if (start < previous.End)
                    throw new InvalidOperationException("Script baseline ranges must be ordered and non-overlapping.");
                if (start == previous.End && offsetY == previous.OffsetY)
                {
                    spans[^1] = previous with { Length = checked(previous.Length + length) };
                    return;
                }
            }

            spans.Add(new Span(start, length, offsetY));
        }

        internal ScriptBaselineGeometry Build()
            => _spans is { Count: > 0 }
                ? new ScriptBaselineGeometry(_spans.ToArray())
                : Empty;
    }
}
