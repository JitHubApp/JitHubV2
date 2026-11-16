namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

/// <summary>
/// Records whether the benchmark window stayed foreground, visible, and
/// unobstructed for the full measurement interval. A 250 ms poll is backed by
/// WinEvent hooks so brief foreground changes and window-covering events are
/// recorded immediately rather than inferred from the start/end snapshots.
/// </summary>
public sealed class MeasurementVisibilityEvidence
{
    public const string CurrentPolicy = "foreground-visible-unoccluded-v1";
    public const int PollIntervalMilliseconds = 250;
    public const int MaximumSampleGapMilliseconds = 1_000;

    public string Policy { get; init; } = CurrentPolicy;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public long StopwatchFrequency { get; init; }
    public long DurationTicks { get; init; }
    public int PollIntervalMs { get; init; } = PollIntervalMilliseconds;
    public int MaximumSampleGapMs { get; init; } = MaximumSampleGapMilliseconds;
    public bool ForegroundHookInstalled { get; init; }
    public bool DesktopSwitchHookInstalled { get; init; }
    public bool WindowEventHookInstalled { get; init; }
    public int ForegroundLossEvents { get; init; }
    public int DesktopSwitchEvents { get; init; }
    public int CaptureFailures { get; init; }
    public List<MeasurementVisibilitySample> Samples { get; init; } = [];
}
