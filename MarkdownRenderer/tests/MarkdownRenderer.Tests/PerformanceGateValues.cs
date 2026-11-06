namespace MarkdownRenderer.Tests;

/// <summary>
/// The committed per-trial measurements (performance-gate-schema10-values.json)
/// that place the baseline and candidate fixtures exactly on the gate's
/// inclusive edges.
/// </summary>
internal sealed class PerformanceGateValues
{
    public double[][] FirstViewportReferenceTrialP95Milliseconds { get; init; } = [];
    public double[][] FirstViewportCandidateTrialP95Milliseconds { get; init; } = [];
    public double[] ScrollReferenceUiP95Milliseconds { get; init; } = [];
    public double[] ScrollCandidateUiP95Milliseconds { get; init; } = [];
    public double[] ScrollReferenceUiP99Milliseconds { get; init; } = [];
    public double[] ScrollCandidateUiP99Milliseconds { get; init; } = [];
    public double[] ScrollReferenceFrameP95Milliseconds { get; init; } = [];
    public double[] ScrollCandidateFrameP95Milliseconds { get; init; } = [];
    public double[] ScrollReferenceAllocationP95Bytes { get; init; } = [];
    public double[] ScrollCandidateAllocationP95Bytes { get; init; } = [];
    public long[] SourceLookupReferenceTrialBatchTicks { get; init; } = [];
    public long[] SourceLookupCandidateTrialBatchTicks { get; init; } = [];
    public double[] CancellationReferenceTrialP95Milliseconds { get; init; } = [];
    public double[] CancellationCandidateTrialP95Milliseconds { get; init; } = [];
    public double[] CancellationReferenceWarmupMilliseconds { get; init; } = [];
    public double[] CancellationCandidateWarmupMilliseconds { get; init; } = [];
}
