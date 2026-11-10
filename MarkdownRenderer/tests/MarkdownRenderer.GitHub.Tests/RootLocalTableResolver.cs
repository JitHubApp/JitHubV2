using System.Collections.Concurrent;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Raster image resolver for the root-local table relayout tests. It serves
/// PNG assets with fixed aspect ratios, counts each resolve per source, and
/// can hold one "pending" source unresolved until cancellation.
/// </summary>
internal sealed class RootLocalTableResolver : IMarkdownImageResolver
{
    private readonly ConcurrentDictionary<string, MarkdownImageAsset> _assets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _pendingResolve =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _cachePrefix = "root-local-table:" + Guid.NewGuid().ToString("N") + ":";
    private readonly string? _pendingUrl;
    private readonly string? _farUrl;
    private readonly string? _standaloneUrl;

    private RootLocalTableResolver(int generatedImageCount, bool includePending, bool includeStandalone,
        bool narrowImages = false)
    {
        GeneratedImageCount = generatedImageCount;
        string suffix = Guid.NewGuid().ToString("N");
        WideUrl = $"https://images.example.test/table-wide-{suffix}.png";
        TallUrl = $"https://images.example.test/table-tall-{suffix}.png";
        SquareUrl = $"https://images.example.test/table-square-{suffix}.png";
        InlineUrl = $"https://images.example.test/table-inline-{suffix}.png";
        _pendingUrl = includePending ? $"https://images.example.test/table-pending-{suffix}.png" : null;
        _farUrl = includePending ? $"https://images.example.test/table-far-{suffix}.png" : null;
        _standaloneUrl = includeStandalone ? $"https://images.example.test/table-standalone-{suffix}.png" : null;
        if (generatedImageCount > 0)
            for (int index = 0; index < generatedImageCount; index++)
                AddAsset(GeneratedUrl(index), 96 + (index % 5) * 24, 48 + (index % 3) * 32);
        if (generatedImageCount == 0)
        {
            AddAsset(WideUrl, narrowImages ? 8 : 480, narrowImages ? 2 : 96);
            AddAsset(TallUrl, narrowImages ? 12 : 96, narrowImages ? 64 : 240);
            AddAsset(SquareUrl, narrowImages ? 16 : 168, narrowImages ? 16 : 168);
            AddAsset(InlineUrl, 128, 48);
        }
        if (_farUrl is not null)
            AddAsset(_farUrl, 96, 64);
        if (_standaloneUrl is not null)
            AddAsset(_standaloneUrl, 160, 96);
    }

    internal string WideUrl { get; }
    internal string TallUrl { get; }
    internal string SquareUrl { get; }
    internal string InlineUrl { get; }
    internal string PendingUrl => _pendingUrl ?? throw new InvalidOperationException("No pending URI was configured.");
    internal string FarUrl => _farUrl ?? throw new InvalidOperationException("No far-off URI was configured.");
    internal string StandaloneUrl => _standaloneUrl ?? throw new InvalidOperationException("No standalone URI was configured.");
    internal int GeneratedImageCount { get; }
    internal string[] Sources => _assets.Keys.Order(StringComparer.Ordinal).ToArray();
    internal string GeneratedUrl(int index) => $"https://images.example.test/generated-table-{index}-{_cachePrefix}.png";
    internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;

    /// <summary>The resolve count of every source, in <see cref="Sources"/> order.</summary>
    internal int[] CaptureResolveCounts() => Sources.Select(ResolveCountFor).ToArray();

    internal static RootLocalTableResolver CreateStandard(bool includePending = false, bool includeStandalone = false,
        bool narrowImages = false) =>
        new(generatedImageCount: 0, includePending, includeStandalone, narrowImages);

    internal static RootLocalTableResolver CreateGenerated(int imageCount, int columns, int bodyRows)
    {
        _ = columns;
        _ = bodyRows;
        return new(generatedImageCount: imageCount, includePending: false, includeStandalone: false);
    }

    internal Task WaitForPendingResolveAsync() => _pendingResolve.Task;

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        _counts.AddOrUpdate(source, 1, static (_, count) => count + 1);
        if (string.Equals(source, _pendingUrl, StringComparison.Ordinal))
        {
            _pendingResolve.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        return _assets.TryGetValue(source, out MarkdownImageAsset? asset)
            ? MarkdownImageResolution.Resolved(asset)
            : MarkdownImageResolution.Unavailable;
    }

    private void AddAsset(string source, int width, int height)
    {
        _assets[source] = new MarkdownImageAsset(
            DisplaySizedRasterDecodeTests.CreatePng(width, height),
            "image/png",
            new Uri(source),
            _cachePrefix + Guid.NewGuid().ToString("N"));
    }
}
