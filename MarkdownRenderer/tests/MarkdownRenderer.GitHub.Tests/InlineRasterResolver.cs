using System.Collections.Concurrent;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Offline raster resolver for the root-local inline image relayout tests. It
/// counts resolves per source and holds the pending source unresolved until
/// cancellation, so a sibling image keeps live work across a relayout.
/// </summary>
internal sealed class InlineRasterResolver : IMarkdownImageResolver
{
    private readonly byte[] _png;
    private readonly ConcurrentDictionary<string, int> _resolveCounts = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _pendingResolve =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _cachePrefix = "root-local-inline:" + Guid.NewGuid().ToString("N") + ":";

    internal InlineRasterResolver(byte[] png)
    {
        _png = png;
        string suffix = Guid.NewGuid().ToString("N");
        InlineFirstUrl = $"https://images.example.test/inline-first-{suffix}.png";
        InlineSecondUrl = $"https://images.example.test/inline-second-{suffix}.png";
        StandaloneUrl = $"https://images.example.test/standalone-{suffix}.png";
        PendingUrl = $"https://images.example.test/pending-{suffix}.png";
        FarUrl = $"https://images.example.test/far-{suffix}.png";
    }

    internal string InlineFirstUrl { get; }
    internal string InlineSecondUrl { get; }
    internal string StandaloneUrl { get; }
    internal string PendingUrl { get; }
    internal string FarUrl { get; }
    internal string[] Sources => [InlineFirstUrl, InlineSecondUrl, StandaloneUrl, PendingUrl, FarUrl];

    internal int ResolveCountFor(string source) =>
        _resolveCounts.TryGetValue(source, out int count) ? count : 0;

    internal Task WaitForPendingResolveAsync() => _pendingResolve.Task;

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resolveCounts.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
        if (string.Equals(source, PendingUrl, StringComparison.Ordinal))
            return WaitForPendingAsync(cancellationToken);

        if (!Sources.Take(3).Contains(source, StringComparer.Ordinal) &&
            !string.Equals(source, FarUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test resolver received an unexpected image source.");
        }

        return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
            _png,
            "image/png",
            new Uri(source),
            _cachePrefix + source)));
    }

    private async ValueTask<MarkdownImageResolution> WaitForPendingAsync(CancellationToken cancellationToken)
    {
        _pendingResolve.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new OperationCanceledException(cancellationToken);
    }
}
