namespace MarkdownRenderer.PerformanceHarness;

public sealed class LifecyclePlateauResult
{
    public int CompletedCycles { get; init; }
    public int RequiredCycles { get; init; } = 100;
    public int SyntheticDeviceResets { get; init; }
    public string DeviceResetMode { get; init; } = "snapshot-release-and-canvas-device-trim";
    public int ActualDeviceLossEvents { get; init; }
    public long Checkpoint80ManagedBytes { get; init; }
    public long Checkpoint100ManagedBytes { get; init; }
    public long Checkpoint80PrivateBytes { get; init; }
    public long Checkpoint100PrivateBytes { get; init; }
    public double ManagedGrowthPercent { get; init; }
    public double PrivateGrowthPercent { get; init; }
    public double GrowthPercent { get; init; }
    public double GrowthBudgetPercent { get; init; } = 5;
    public int RenderFailures { get; init; }
    public bool Passed { get; init; }
}
