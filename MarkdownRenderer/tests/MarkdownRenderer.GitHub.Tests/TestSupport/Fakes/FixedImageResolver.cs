using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

/// <summary>
/// An image resolver that resolves every source to one fixed asset.
/// </summary>
internal sealed class FixedImageResolver(MarkdownImageAsset asset) : IMarkdownImageResolver
{
    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MarkdownImageResolution.Resolved(asset));
    }
}
