using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

internal sealed class ContainerVectorResolver(int imageCount) : IMarkdownImageResolver
{
    private readonly byte[] _png = DisplaySizedRasterDecodeTests.CreatePng(96, 192);
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly string _cacheKey = "container-vector-relayout:" + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _imagesReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _resolveCount;

    internal int ResolveCount => Volatile.Read(ref _resolveCount);
    internal string GetUrl(int index)
    {
        if ((uint)index >= (uint)imageCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return $"https://images.example.test/{_id}-{index}.png";
    }

    internal void ReleaseImages() => _imagesReleased.TrySetResult();

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enumerable.Range(0, imageCount).Any(index =>
                string.Equals(source, GetUrl(index), StringComparison.Ordinal)))
            throw new InvalidOperationException("Unexpected vector-container test image URL.");
        Interlocked.Increment(ref _resolveCount);
        await _imagesReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
            _png,
            "image/png",
            new Uri(source),
            _cacheKey + ":" + source));
    }
}
