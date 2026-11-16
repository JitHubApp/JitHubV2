using System.Collections.Immutable;

namespace MarkdownRenderer.PerformanceHarness.Measurements;

/// <summary>
/// The UTF-16 source sizes that the first-viewport and retained-memory gates
/// measure, in measurement order.
/// </summary>
internal static class MeasuredSourceSizes
{
    internal static readonly ImmutableArray<int> FirstViewport =
    [
        100 * 1024,
        1024 * 1024,
        10 * 1024 * 1024,
    ];
}
