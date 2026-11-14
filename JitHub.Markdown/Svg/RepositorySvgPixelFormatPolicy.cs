using MarkdownRenderer.Images.Svg;

namespace JitHub.Markdown.Svg;

/// <summary>
/// Chooses the pixel format for repository SVG previews: premultiplied RGBA when the
/// hardware renderer is in use and supports it, otherwise premultiplied BGRA.
/// </summary>
public static class RepositorySvgPixelFormatPolicy
{
    public static MarkdownSvgPixelFormat Select(
        bool forceSoftwareRenderer,
        bool rgbaSupported) =>
        !forceSoftwareRenderer && rgbaSupported
            ? MarkdownSvgPixelFormat.Rgba8Premultiplied
            : MarkdownSvgPixelFormat.Bgra8Premultiplied;
}
