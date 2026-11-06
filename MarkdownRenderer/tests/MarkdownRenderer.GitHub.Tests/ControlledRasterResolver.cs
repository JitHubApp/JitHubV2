using System.Collections.Concurrent;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

internal sealed class ControlledRasterResolver : IMarkdownImageResolver
{
    private readonly byte[] _png;
    private readonly ConcurrentDictionary<string, int> _resolveCounts = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _pendingResolve =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _cachePrefix = "root-local-relayout:" + Guid.NewGuid().ToString("N") + ":";

    internal ControlledRasterResolver(byte[] png)
    {
        _png = png;
        string suffix = Guid.NewGuid().ToString("N");
        TargetUrl = $"https://images.example.test/target-{suffix}.png";
        SecondTargetUrl = $"https://images.example.test/second-target-{suffix}.png";
        PendingUrl = $"https://images.example.test/pending-{suffix}.png";
        FarUrl = $"https://images.example.test/far-{suffix}.png";
    }

    internal string TargetUrl { get; }
    internal string SecondTargetUrl { get; }
    internal string PendingUrl { get; }
    internal string FarUrl { get; }
    internal int ResolveCountFor(string source) =>
        _resolveCounts.TryGetValue(source, out int count) ? count : 0;

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _resolveCounts.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
        if (string.Equals(source, PendingUrl, StringComparison.Ordinal))
        {
            _pendingResolve.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }

        if (!string.Equals(source, TargetUrl, StringComparison.Ordinal) &&
            !string.Equals(source, SecondTargetUrl, StringComparison.Ordinal) &&
            !string.Equals(source, FarUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test resolver received an unexpected image source.");
        }

        return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
            _png,
            "image/png",
            CacheKey: _cachePrefix + source));
    }

    internal Task WaitForPendingResolveAsync() => _pendingResolve.Task;
}
