using MarkdownRenderer.PerformanceHarness;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Builds the start and completion machine snapshots for regression evaluator
/// reports, with named mutations that change one machine property so tests
/// can check which differences make two runs non-comparable.
/// </summary>
internal static class PerformanceRegressionMachineFixture
{
    private const string UnsupportedPowerMode = "unsupported";
    private const string ZeroPowerModeGuid = "00000000-0000-0000-0000-000000000000";

    /// <remarks>
    /// This method stays whole because MachineMetadata is init-only, so each
    /// property and the mutation that replaces it must be set in one object
    /// initializer.
    /// </remarks>
    internal static MachineMetadata CreateMachine(string? mutation, double observedRefreshRateHz)
        => new()
        {
            MachineInstanceSha256 = mutation switch
            {
                "machine-fingerprint-missing" => string.Empty,
                "machine-fingerprint-different" => new string('B', 64),
                _ => new string('A', 64),
            },
            Cpu = "test-cpu",
            LogicalProcessorCount = 8,
            OsDescription = mutation == "os-description-different" ? "different-test-os" : "test-os",
            OsVersion = "10.0.26100",
            OsArchitecture = "X64",
            ProcessArchitecture = "X64",
            FrameworkDescription = ".NET test",
            DpiScale = 1,
            ViewportWidthDips = 1280,
            ViewportHeightDips = 720,
            DisplayDeviceName = mutation == "display-device-different"
                ? @"\\.\DISPLAY2"
                : @"\\.\DISPLAY1",
            ConfiguredRefreshRateHz = 120,
            ObservedRefreshRateHz = observedRefreshRateHz,
            IsAtLeast120Hz = observedRefreshRateHz >= 115,
            GpuAdapterIdentitySha256 = new string(mutation == "gpu-identity-different" ? 'D' : 'C', 64),
            GpuAdapterLuid = mutation == "gpu-luid-different"
                ? "00000000:00000002"
                : "00000000:00000001",
            GpuDriverVersion = mutation == "gpu-driver-different" ? "2.0.0.0" : "1.2.3.4",
            DisplayAdapterIdentitySha256 =
                new string(mutation == "display-gpu-identity-different" ? 'F' : 'E', 64),
            DisplayAdapterLuid = "00000000:00000003",
            DisplayAdapterDriverVersion = mutation switch
            {
                "display-gpu-driver-different" => "5.0.0.0",
                "display-driver-missing" => string.Empty,
                _ => "4.3.2.1",
            },
            ActivePowerSchemeGuid = mutation == "power-scheme-different"
                ? "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"
                : "381b4222-f694-41f0-9685-ff5bb260df2e",
            UserConfiguredAcPowerModeGuid = SelectAcPowerMode(mutation),
            UserConfiguredDcPowerModeGuid = SelectDcPowerMode(mutation),
            EffectivePowerMode = mutation switch
            {
                "effective-power-mode-different" => "HighPerformance",
                "effective-power-mode-unsupported" => UnsupportedPowerMode,
                "effective-power-mode-invalid" => "not-a-mode",
                _ => "Balanced",
            },
            PowerSource = mutation == "power-source-different" ? "Battery" : "AC",
            EnergySaverState = mutation == "energy-saver-different" ? "On" : "Off",
            ProcessPriorityClass = mutation == "priority-different" ? "High" : "Normal",
            ProcessPowerThrottlingMode = mutation == "power-throttling-uncontrolled"
                ? "system-managed"
                : PerformanceMeasurementContract.ProcessPowerThrottlingMode,
            GcServer = mutation == "gc-server-different",
            GcLatencyMode = mutation == "gc-latency-different" ? "SustainedLowLatency" : "Interactive",
        };

    /// <summary>
    /// Copies the start snapshot, applying the completion-* mutations that
    /// model a machine change during the run.
    /// </summary>
    internal static MachineMetadata CreateCompletionMachine(
        MachineMetadata start,
        string? mutation)
        => new()
        {
            MachineInstanceSha256 = start.MachineInstanceSha256,
            Cpu = start.Cpu,
            LogicalProcessorCount = start.LogicalProcessorCount,
            OsDescription = start.OsDescription,
            OsVersion = start.OsVersion,
            OsArchitecture = start.OsArchitecture,
            ProcessArchitecture = start.ProcessArchitecture,
            FrameworkDescription = start.FrameworkDescription,
            DpiScale = start.DpiScale,
            ViewportWidthDips = start.ViewportWidthDips,
            ViewportHeightDips = start.ViewportHeightDips,
            DisplayDeviceName = mutation == "completion-display-changed"
                ? @"\\.\DISPLAY2"
                : start.DisplayDeviceName,
            ConfiguredRefreshRateHz = start.ConfiguredRefreshRateHz,
            GpuAdapterIdentitySha256 = start.GpuAdapterIdentitySha256,
            GpuAdapterLuid = start.GpuAdapterLuid,
            GpuDriverVersion = start.GpuDriverVersion,
            DisplayAdapterIdentitySha256 = start.DisplayAdapterIdentitySha256,
            DisplayAdapterLuid = start.DisplayAdapterLuid,
            DisplayAdapterDriverVersion = mutation == "completion-display-driver-changed"
                ? "9.9.9.9"
                : start.DisplayAdapterDriverVersion,
            ActivePowerSchemeGuid = start.ActivePowerSchemeGuid,
            UserConfiguredAcPowerModeGuid =
                mutation == "completion-configured-power-mode-support-changed"
                    ? UnsupportedPowerMode
                    : start.UserConfiguredAcPowerModeGuid,
            UserConfiguredDcPowerModeGuid =
                mutation == "completion-configured-power-mode-support-changed"
                    ? UnsupportedPowerMode
                    : start.UserConfiguredDcPowerModeGuid,
            EffectivePowerMode = mutation switch
            {
                "completion-effective-power-mode-changed" => "HighPerformance",
                "completion-effective-power-mode-unsupported" => UnsupportedPowerMode,
                _ => start.EffectivePowerMode,
            },
            PowerSource = start.PowerSource,
            EnergySaverState = start.EnergySaverState,
            ProcessPriorityClass = start.ProcessPriorityClass,
            ProcessPowerThrottlingMode = start.ProcessPowerThrottlingMode,
            GcServer = start.GcServer,
            GcLatencyMode = start.GcLatencyMode,
        };

    private static string SelectAcPowerMode(string? mutation)
        => mutation switch
        {
            "ac-power-mode-different" => "961cc777-2547-4f9d-8174-7d86181b8a7a",
            "configured-power-modes-unsupported" or "configured-power-mode-support-mixed" =>
                UnsupportedPowerMode,
            _ => ZeroPowerModeGuid,
        };

    private static string SelectDcPowerMode(string? mutation)
        => mutation switch
        {
            "dc-power-mode-different" => "ded574b5-45a0-4f42-8737-46345c09c238",
            "configured-power-modes-unsupported" => UnsupportedPowerMode,
            _ => ZeroPowerModeGuid,
        };
}
