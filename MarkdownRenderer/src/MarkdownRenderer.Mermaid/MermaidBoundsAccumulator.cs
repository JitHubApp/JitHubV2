namespace MarkdownRenderer.Mermaid;

/// <summary>Accumulates the finite axis-aligned bounds of the points painted for one semantic item.</summary>
internal struct MermaidBoundsAccumulator
{
    internal bool HasValue;
    internal float Left;
    internal float Top;
    internal float Right;
    internal float Bottom;

    internal void Add(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return;
        if (!HasValue)
        {
            HasValue = true;
            Left = Right = x;
            Top = Bottom = y;
            return;
        }
        Left = Math.Min(Left, x);
        Top = Math.Min(Top, y);
        Right = Math.Max(Right, x);
        Bottom = Math.Max(Bottom, y);
    }

    internal void Add(MermaidBoundsAccumulator other)
    {
        if (!other.HasValue)
            return;
        Add(other.Left, other.Top);
        Add(other.Right, other.Bottom);
    }
}
