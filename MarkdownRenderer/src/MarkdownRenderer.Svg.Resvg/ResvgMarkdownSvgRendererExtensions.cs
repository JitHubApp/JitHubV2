using MarkdownRenderer.Controls;

namespace MarkdownRenderer.Svg.Resvg;

/// <summary>Explicit registration helpers for the optional resvg provider.</summary>
public static class ResvgMarkdownSvgRendererExtensions
{
    /// <summary>Registers a caller-owned shared renderer with the control builder.</summary>
    public static MarkdownRendererControlBuilder WithResvgSvgRenderer(
        this MarkdownRendererControlBuilder builder,
        ResvgMarkdownSvgRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(renderer);
        return builder.WithSvgRenderer(renderer);
    }
}
