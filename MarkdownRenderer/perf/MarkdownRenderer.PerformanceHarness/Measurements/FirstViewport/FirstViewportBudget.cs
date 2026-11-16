using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;

namespace MarkdownRenderer.PerformanceHarness.Measurements.FirstViewport;

/// <summary>
/// The frozen first-viewport p95 budget for each measured source size and
/// parse-cache mode.
/// </summary>
internal static class FirstViewportBudget
{
    internal static double GetMilliseconds(int sourceBytes, string mode)
        => sourceBytes switch
        {
            100 * 1024 when mode == PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
            100 * 1024 => 125,
            1024 * 1024 => 250,
            10 * 1024 * 1024 => 750,
            _ => throw new UnreachableException(),
        };
}
