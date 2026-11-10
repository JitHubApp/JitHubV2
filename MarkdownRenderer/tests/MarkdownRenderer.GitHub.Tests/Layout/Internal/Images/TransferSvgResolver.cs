using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images;

internal sealed class TransferSvgResolver(byte[] bytes) : IMarkdownImageResolver
{
    private readonly string _cacheKey = "placement-transfer-svg:" + Guid.NewGuid().ToString("N");
    internal string Url { get; } = "https://images.example/transfer-placement-" + Guid.NewGuid().ToString("N") + ".svg";

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(source, Url, StringComparison.Ordinal))
            throw new InvalidOperationException("The transfer test resolver received an unexpected URL.");

        return ValueTask.FromResult(MarkdownImageResolution.Resolved(
            new MarkdownImageAsset(bytes, "image/svg+xml", CacheKey: _cacheKey)));
    }
}
