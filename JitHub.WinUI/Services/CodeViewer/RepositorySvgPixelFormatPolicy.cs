using MarkdownRenderer.Images;

namespace JitHub.Services.CodeViewer;

internal static class RepositorySvgPixelFormatPolicy
{
    internal static MarkdownSvgPixelFormat Select(
        bool forceSoftwareRenderer,
        bool rgbaSupported) =>
        !forceSoftwareRenderer && rgbaSupported
            ? MarkdownSvgPixelFormat.Rgba8Premultiplied
            : MarkdownSvgPixelFormat.Bgra8Premultiplied;
}
