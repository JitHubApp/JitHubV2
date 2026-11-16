using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

/// <summary>
/// Offline raster resolver for the mixed inline vector and image relayout
/// tests. It serves one PNG for its single URL and counts resolves, so a
/// test can prove a relayout did not ask the provider again.
/// </summary>
internal sealed class InlineVectorRasterResolver : IMarkdownImageResolver
{
    private readonly byte[] _png;
    private readonly string _cacheKey = "mixed-inline-vector:" + Guid.NewGuid().ToString("N");
    private int _resolveCount;

    internal InlineVectorRasterResolver(byte[] png)
    {
        _png = png;
        Url = $"https://images.example.test/mixed-vector-{Guid.NewGuid():N}.png";
    }

    internal string Url { get; }
    internal int ResolveCount => Volatile.Read(ref _resolveCount);

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(source, Url, StringComparison.Ordinal))
            throw new InvalidOperationException("The test resolver received an unexpected image URL.");
        Interlocked.Increment(ref _resolveCount);
        return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
            _png,
            "image/png",
            new Uri(source),
            _cacheKey)));
    }
}
