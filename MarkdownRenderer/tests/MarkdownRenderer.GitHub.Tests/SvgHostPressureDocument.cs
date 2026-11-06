using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A 2048x2048 SVG document for owner-slot pressure tests. It must never be
/// rendered; its <see cref="Disposed"/> signal shows when parking released it.
/// </summary>
internal sealed class SvgHostPressureDocument : IMarkdownSvgDocument
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MarkdownSvgDocumentInfo Info { get; } = new(2048, 2048, 1);
    internal TaskCompletionSource Disposed => _disposed;

    public ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Pressure-park tests must not render.");

    public void Dispose() => _disposed.TrySetResult();
}
