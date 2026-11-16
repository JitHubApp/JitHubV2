using System.Collections.Concurrent;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Tables;

/// <summary>
/// Raster image resolver for the root-local table vector tests. It serves a
/// wide and a tall PNG, holds every resolve until <see cref="ReleaseImages"/>,
/// and counts completed resolves per source.
/// </summary>
internal sealed class RootLocalTableVectorResolver : IMarkdownImageResolver
{
    private readonly ConcurrentDictionary<string, MarkdownImageAsset> _assets =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly string _identity = Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _imagesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RootLocalTableVectorResolver()
    {
        AddAsset(WideUrl, 480, 96);
        AddAsset(TallUrl, 96, 240);
    }

    internal string WideUrl { get; } =
        "https://images.example.test/table-vector-wide-" + Guid.NewGuid().ToString("N") + ".png";
    internal string TallUrl { get; } =
        "https://images.example.test/table-vector-tall-" + Guid.NewGuid().ToString("N") + ".png";
    internal string[] Sources => _assets.Keys.Order(StringComparer.Ordinal).ToArray();
    internal int ResolveCountFor(string source) => _counts.TryGetValue(source, out int count) ? count : 0;

    /// <summary>The resolve count of every source, in <see cref="Sources"/> order.</summary>
    internal int[] CaptureResolveCounts() => Sources.Select(ResolveCountFor).ToArray();

    internal void ReleaseImages() => _imagesReady.TrySetResult();

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _imagesReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        _counts.AddOrUpdate(source, 1, static (_, count) => count + 1);
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
            "root-local-table-vector:" + _identity + ":" + Guid.NewGuid().ToString("N"));
    }
}
