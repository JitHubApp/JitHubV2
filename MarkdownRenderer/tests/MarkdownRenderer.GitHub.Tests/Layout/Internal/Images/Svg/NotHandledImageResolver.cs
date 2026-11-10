using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg;

internal sealed class NotHandledImageResolver : IMarkdownImageResolver
{
    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MarkdownImageResolution.NotHandled);
    }
}
