using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

/// <summary>
/// An image resolver that counts its calls and resolves every source to a
/// small PNG asset keyed by the source.
/// </summary>
internal sealed class RecordingResolver : IMarkdownImageResolver
{
    private int _callCount;
    internal int CallCount => Volatile.Read(ref _callCount);

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return ValueTask.FromResult(MarkdownImageResolution.Resolved(
            new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source)));
    }
}
