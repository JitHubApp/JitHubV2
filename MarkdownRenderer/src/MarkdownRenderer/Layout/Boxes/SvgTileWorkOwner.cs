using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The plan, cancellation and demand of one in-flight SVG tile render.
/// </summary>
internal sealed class SvgTileWorkOwner(
    SvgTileRenderPlan plan,
    CancellationTokenSource cancellation,
    MarkdownSvgRenderDemand demand)
{
    internal readonly SvgTileRenderPlan Plan = plan;
    internal readonly CancellationTokenSource Cancellation = cancellation;
    internal readonly MarkdownSvgRenderDemand Demand = demand;
    // 0=active, 1=terminal failure claimed, 2=retired/reassigned,
    // 3=terminal failure map invalidation in progress.
    // The transition is coordinated while holding _svgTileLock.
    internal int State;
}
