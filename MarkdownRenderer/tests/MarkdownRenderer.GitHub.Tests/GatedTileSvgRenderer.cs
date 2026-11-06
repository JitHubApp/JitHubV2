using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// An SVG renderer double whose documents hold the second visible tile
/// until the test releases it, and can paint the whole bitmap opaque.
/// </summary>
internal sealed class GatedTileSvgRenderer(
    MarkdownSvgDocumentInfo info, bool opaqueWholeBitmap = false) : IMarkdownSvgRenderer
{
    internal bool OpaqueWholeBitmap { get; } = opaqueWholeBitmap;
    internal TaskCompletionSource SecondVisibleTileStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource ReleaseSecondVisibleTile { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public long CacheGeneration => 0;

    public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return MarkdownSvgSourcePreparation.Admit(source);
    }

    public ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IMarkdownSvgDocument>(new GatedTileSvgDocument(this, info));
    }
}
