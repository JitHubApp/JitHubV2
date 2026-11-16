using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg.HostAdmission;

internal sealed class RegistryDocument : IMarkdownSvgDocument
{
    public MarkdownSvgDocumentInfo Info { get; } = new(2048, 2048, 1);
    public ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Pure registry tests must not render.");
    public void Dispose() { }
}
