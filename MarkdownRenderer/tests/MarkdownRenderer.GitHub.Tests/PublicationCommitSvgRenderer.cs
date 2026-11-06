using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>Legacy SVG provider for the publication commit tests; it never opens documents.</summary>
internal sealed class PublicationCommitSvgRenderer : IMarkdownSvgRenderer
{
    public long CacheGeneration => 0;

    public MarkdownSvgSourcePreparation PrepareSource(
        byte[] source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return MarkdownSvgSourcePreparation.Admit(source);
    }

    public ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Publication commit tests do not open provider documents.");
}
