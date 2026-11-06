using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

internal sealed class PrivatePlacementRasterResolver(byte[] png) : IMarkdownImageResolver
{
    private readonly string _cacheKey = "private-placement-raster:" + Guid.NewGuid().ToString("N");
    private int _resolveCount;
    internal int ResolveCount => Volatile.Read(ref _resolveCount);

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _resolveCount);
        return ValueTask.FromResult(MarkdownImageResolution.Resolved(
            new MarkdownImageAsset(png, "image/png", CacheKey: _cacheKey)));
    }
}
