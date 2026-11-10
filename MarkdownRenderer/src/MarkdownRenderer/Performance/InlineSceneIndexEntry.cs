using MarkdownRenderer.Layout.Internal.Inlines;

namespace MarkdownRenderer.Performance;

/// <summary>One retained inline scene fragment and its scalar extent.</summary>
internal readonly struct InlineSceneIndexEntry(
    InlineSceneGeometry Geometry,
    double Top,
    double Bottom,
    double Left,
    double Right)
{
    public InlineSceneGeometry Geometry { get; } = Geometry;
    public double Top { get; } = Top;
    public double Bottom { get; } = Bottom;
    public double Left { get; } = Left;
    public double Right { get; } = Right;
}
