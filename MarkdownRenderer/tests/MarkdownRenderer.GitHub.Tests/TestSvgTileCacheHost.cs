using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A box-free <see cref="ISvgTileCacheHost"/>: the accepted and parking plans
/// are plain settable state, UI publication runs inline, and cleanup failures
/// are collected. Tests drive the <see cref="SvgTileCache"/> it hosts only
/// through the cache's own operations.
/// </summary>
internal sealed class TestSvgTileCacheHost : ISvgTileCacheHost
{
    internal TestSvgTileCacheHost()
    {
        Cache = new SvgTileCache(this);
    }

    internal SvgTileCache Cache { get; }

    public SvgTileRenderPlan? AcceptedSvgTilePlan { get; set; }

    public SvgTileRenderPlan? SvgParkingPlan { get; set; }

    public bool IsSvgPressureParked { get; set; }

    public long SvgHostVisualDemandEpoch { get; set; }

    /// <summary>False models a disposed, suspended or untiled box.</summary>
    internal bool IsLive { get; set; } = true;

    internal int GapFillCount { get; private set; }

    internal List<Exception> RetirementFailures { get; } = [];

    public long AdvanceSvgHostVisualDemandEpoch() => ++SvgHostVisualDemandEpoch;

    public bool IsAcceptedSvgTilePlan(SvgTileRenderPlan plan) =>
        IsLive && ReferenceEquals(plan, AcceptedSvgTilePlan);

    public void PublishOnUiThread(Action publish, Action? onDropped) => publish();

    public void TryImageCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            RetirementFailures.Add(exception);
        }
    }

    public void RecordImageRetirementFailure(Exception exception) => RetirementFailures.Add(exception);

    public void FillSvgTileGapWithoutTiles(CanvasDrawingSession ds, Rect destination) => GapFillCount++;

    /// <summary>Accepts <paramref name="plan"/> and swaps the cache to its grid, as a publication commit does.</summary>
    internal void Accept(SvgTileRenderPlan? plan) =>
        SvgTileCacheTestOperations.Adopt(Cache, plan, accepted => AcceptedSvgTilePlan = accepted);

    /// <summary>Publishes <paramref name="lease"/> for <paramref name="key"/> through admitted tile work.</summary>
    internal void PublishTile(
        SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key,
        SharedCanvasBitmapCache.Lease lease) =>
        SvgTileCacheTestOperations.PublishTile(Cache, plan, key, lease);

    /// <summary>Detaches and releases every tile the cache still owns.</summary>
    internal void ReleaseAll()
    {
        (SharedCanvasBitmapCache.Lease[] tiles, RetirableCancellationTokenSource[] work) = Cache.DetachAll();
        Cache.RetireDetached(tiles, work);
    }

    internal static SvgTileRenderPlan Plan(
        string identity,
        int width,
        int height,
        int generation = 1,
        IMarkdownSvgDocument? document = null,
        MarkdownSvgRenderDemand? demand = null) =>
        new(document ?? new NullSvgDocument(), identity, width, height, generation, demand);

    private sealed class NullSvgDocument : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = new(1, 1, 1);

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The tile-cache host never renders.");

        public void Dispose()
        {
        }
    }
}
