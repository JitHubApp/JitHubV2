namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class MachineMetadata
{
    public string MachineInstanceSha256 { get; init; } = string.Empty;
    public string Cpu { get; init; } = string.Empty;
    public int LogicalProcessorCount { get; init; }
    public string OsDescription { get; init; } = string.Empty;
    public string OsVersion { get; init; } = string.Empty;
    public string OsArchitecture { get; init; } = string.Empty;
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string FrameworkDescription { get; init; } = string.Empty;
    public double DpiScale { get; init; }
    public double ViewportWidthDips { get; init; }
    public double ViewportHeightDips { get; init; }
    public string DisplayDeviceName { get; init; } = string.Empty;
    public double ConfiguredRefreshRateHz { get; init; }
    public double ObservedRefreshRateHz { get; set; }
    public bool IsAtLeast120Hz { get; set; }
    public string GpuAdapterIdentitySha256 { get; init; } = string.Empty;
    public string GpuAdapterLuid { get; init; } = string.Empty;
    public string GpuDriverVersion { get; init; } = string.Empty;
    public string DisplayAdapterIdentitySha256 { get; init; } = string.Empty;
    public string DisplayAdapterLuid { get; init; } = string.Empty;
    public string DisplayAdapterDriverVersion { get; init; } = string.Empty;
    public string ActivePowerSchemeGuid { get; init; } = string.Empty;
    public string UserConfiguredAcPowerModeGuid { get; init; } = string.Empty;
    public string UserConfiguredDcPowerModeGuid { get; init; } = string.Empty;
    public string EffectivePowerMode { get; init; } = string.Empty;
    public string PowerSource { get; init; } = string.Empty;
    public string EnergySaverState { get; init; } = string.Empty;
    public string ProcessPriorityClass { get; init; } = string.Empty;
    public string ProcessPowerThrottlingMode { get; init; } = string.Empty;
    public bool GcServer { get; init; }
    public string GcLatencyMode { get; init; } = string.Empty;
}
