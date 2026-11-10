using System.Text;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

internal sealed class ContainerResolver(bool holdPending = false, bool holdImages = false) : IMarkdownImageResolver
{
    private readonly byte[] _png = DisplaySizedRasterDecodeTests.CreatePng(96, 192);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly string _cachePrefix = "root-local-container-relayout:" + Guid.NewGuid().ToString("N") + ":";

    internal string FirstUrl => $"https://images.example.test/{_suffix}-one.png";
    internal string SecondUrl => $"https://images.example.test/{_suffix}-two.png";
    internal string ThirdUrl => $"https://images.example.test/{_suffix}-three.png";
    internal string SvgUrl => $"https://images.example.test/{_suffix}-vector.svg";
    internal string PendingUrl => $"https://images.example.test/{_suffix}-pending.png";
    internal string FarUrl => $"https://images.example.test/{_suffix}-far.png";
    private readonly TaskCompletionSource _pendingStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _imagesReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int[] CaptureResolveCounts() =>
        [ResolveCountFor(FirstUrl), ResolveCountFor(SecondUrl), ResolveCountFor(ThirdUrl), ResolveCountFor(SvgUrl)];

    internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;

    internal Task WaitForPendingResolveAsync() => _pendingStarted.Task;
    internal void ReleaseImages() => _imagesReleased.TrySetResult();

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _counts.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
        if (source == PendingUrl && holdPending)
        {
            _pendingStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        if (holdImages)
            await _imagesReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (source == SvgUrl)
        {
            string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"32\"><rect width=\"64\" height=\"32\" fill=\"#2367a8\"/></svg>";
            return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                Encoding.UTF8.GetBytes(svg), "image/svg+xml", new Uri(source), _cachePrefix + source));
        }
        if (source != FirstUrl && source != SecondUrl && source != ThirdUrl &&
            source != PendingUrl && source != FarUrl)
            throw new InvalidOperationException("The container test resolver received an unexpected URL.");
        return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
            _png,
            "image/png",
            CacheKey: _cachePrefix + source));
    }
}
