using Windows.UI;
using MarkdownRenderer.Extensions.Vectors;

namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

internal readonly struct VectorScenePaintPalette
{
    internal VectorScenePaintPalette(Color Foreground, Color Surface, Color Link)
    {
        this.Foreground = Foreground;
        this.Surface = Surface;
        this.Link = Link;
    }

    public Color Foreground { get; init; }
    public Color Surface { get; init; }
    public Color Link { get; init; }

    public Color Resolve(MarkdownVectorPaintRole role) => role switch
    {
        MarkdownVectorPaintRole.Surface => Surface,
        MarkdownVectorPaintRole.Link => Link,
        _ => Foreground,
    };
}
