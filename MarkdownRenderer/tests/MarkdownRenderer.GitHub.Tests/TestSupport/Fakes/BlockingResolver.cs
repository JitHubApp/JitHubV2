using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

/// <summary>
/// An image resolver that holds the "blocked.png" source until the test
/// releases it and resolves every other source at once, counting both
/// kinds of call.
/// </summary>
internal sealed class BlockingResolver : IMarkdownImageResolver
{
    private int _visibleCalls;
    private int _blockedCalls;
    internal int VisibleCalls => Volatile.Read(ref _visibleCalls);
    internal int BlockedCalls => Volatile.Read(ref _blockedCalls);
    internal TaskCompletionSource<bool> BlockedStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> ReleaseBlocked { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        if (source == "blocked.png")
        {
            Interlocked.Increment(ref _blockedCalls);
            BlockedStarted.TrySetResult(true);
            await ReleaseBlocked.Task.WaitAsync(cancellationToken);
        }
        else
        {
            Interlocked.Increment(ref _visibleCalls);
        }

        return MarkdownImageResolution.Resolved(
            new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
    }
}
