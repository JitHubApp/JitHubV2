using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;

using static MarkdownRenderer.PerformanceHarness.EvidenceNumbers;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The harness's self-check of its own lifecycle-plateau result before a
/// release report is written: the cycle and reset counts, the checkpoints,
/// and the growth recomputed from them.
/// </summary>
internal static class LifecyclePlateauResultCheck
{
    internal static bool IsCompleteAndPassing(PerformanceReport report)
    {
        LifecyclePlateauResult lifecycle = report.LifecyclePlateau;
        double managedGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100ManagedBytes,
            lifecycle.Checkpoint80ManagedBytes);
        double privateGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100PrivateBytes,
            lifecycle.Checkpoint80PrivateBytes);
        return lifecycle.RequiredCycles == report.SampleRequirements.LifecycleCyclesRequired &&
               lifecycle.CompletedCycles == lifecycle.RequiredCycles &&
               lifecycle.SyntheticDeviceResets == 4 &&
               lifecycle.SyntheticDeviceResets == lifecycle.CompletedCycles / 25 &&
               !string.IsNullOrWhiteSpace(lifecycle.DeviceResetMode) &&
               lifecycle.ActualDeviceLossEvents >= 0 &&
               lifecycle.Checkpoint80ManagedBytes > 0 &&
               lifecycle.Checkpoint100ManagedBytes > 0 &&
               lifecycle.Checkpoint80PrivateBytes > 0 &&
               lifecycle.Checkpoint100PrivateBytes > 0 &&
               IsFiniteNonNegative(lifecycle.ManagedGrowthPercent) &&
               IsFiniteNonNegative(lifecycle.PrivateGrowthPercent) &&
               IsFiniteNonNegative(lifecycle.GrowthPercent) &&
               NearlyEqual(lifecycle.ManagedGrowthPercent, managedGrowth) &&
               NearlyEqual(lifecycle.PrivateGrowthPercent, privateGrowth) &&
               NearlyEqual(lifecycle.GrowthPercent, Math.Max(managedGrowth, privateGrowth)) &&
               lifecycle.GrowthBudgetPercent == 5 &&
               lifecycle.GrowthPercent <= lifecycle.GrowthBudgetPercent &&
               lifecycle.RenderFailures == 0 &&
               lifecycle.Passed;
    }
}
