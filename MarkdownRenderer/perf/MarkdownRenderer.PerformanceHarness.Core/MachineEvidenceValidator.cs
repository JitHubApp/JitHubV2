using System.Diagnostics;
using System.Runtime;
using static MarkdownRenderer.PerformanceHarness.EvidenceValueRules;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Validates the machine, display, power and process environment a
/// performance report records, and decides whether two reports ran on a
/// comparable machine. Release evidence, the regression evaluator and the
/// harness self-check share these rules.
/// </summary>
public static class MachineEvidenceValidator
{
    public static bool IsMachineComparable(
        MachineMetadata candidate,
        MachineMetadata reference)
        => IsMachineMetadataComplete(candidate) &&
           IsMachineMetadataComplete(reference) &&
           string.Equals(
               candidate.MachineInstanceSha256,
               reference.MachineInstanceSha256,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(candidate.Cpu, reference.Cpu, StringComparison.Ordinal) &&
           candidate.LogicalProcessorCount == reference.LogicalProcessorCount &&
           string.Equals(candidate.OsDescription, reference.OsDescription, StringComparison.Ordinal) &&
           string.Equals(candidate.OsVersion, reference.OsVersion, StringComparison.Ordinal) &&
           string.Equals(candidate.OsArchitecture, reference.OsArchitecture, StringComparison.Ordinal) &&
           string.Equals(candidate.ProcessArchitecture, reference.ProcessArchitecture, StringComparison.Ordinal) &&
           string.Equals(candidate.FrameworkDescription, reference.FrameworkDescription, StringComparison.Ordinal) &&
           Math.Abs(candidate.DpiScale - reference.DpiScale) < 0.01 &&
            Math.Abs(candidate.ViewportWidthDips - reference.ViewportWidthDips) < 1 &&
            Math.Abs(candidate.ViewportHeightDips - reference.ViewportHeightDips) < 1 &&
            string.Equals(
                candidate.DisplayDeviceName,
                reference.DisplayDeviceName,
                StringComparison.Ordinal) &&
            Math.Abs(candidate.ConfiguredRefreshRateHz - reference.ConfiguredRefreshRateHz) < 0.5 &&
           HasQualifiedRefresh(candidate) &&
           HasQualifiedRefresh(reference) &&
           string.Equals(
               candidate.GpuAdapterIdentitySha256,
               reference.GpuAdapterIdentitySha256,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(candidate.GpuDriverVersion, reference.GpuDriverVersion, StringComparison.Ordinal) &&
            string.Equals(
                candidate.DisplayAdapterIdentitySha256,
                reference.DisplayAdapterIdentitySha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                candidate.DisplayAdapterDriverVersion,
                reference.DisplayAdapterDriverVersion,
                StringComparison.Ordinal) &&
            string.Equals(candidate.ActivePowerSchemeGuid, reference.ActivePowerSchemeGuid, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(candidate.UserConfiguredAcPowerModeGuid, reference.UserConfiguredAcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.UserConfiguredDcPowerModeGuid, reference.UserConfiguredDcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.EffectivePowerMode, reference.EffectivePowerMode, StringComparison.Ordinal) &&
           string.Equals(candidate.PowerSource, reference.PowerSource, StringComparison.Ordinal) &&
           string.Equals(candidate.EnergySaverState, reference.EnergySaverState, StringComparison.Ordinal) &&
           string.Equals(candidate.ProcessPriorityClass, reference.ProcessPriorityClass, StringComparison.Ordinal) &&
           string.Equals(
               candidate.ProcessPowerThrottlingMode,
               reference.ProcessPowerThrottlingMode,
               StringComparison.Ordinal) &&
           candidate.GcServer == reference.GcServer &&
           string.Equals(candidate.GcLatencyMode, reference.GcLatencyMode, StringComparison.Ordinal);

    public static bool IsSameRunEnvironment(
        MachineMetadata start,
        MachineMetadata completion)
        => IsMachineMetadataComplete(start) &&
           IsEnvironmentSnapshotComplete(completion) &&
           string.Equals(start.MachineInstanceSha256, completion.MachineInstanceSha256, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.Cpu, completion.Cpu, StringComparison.Ordinal) &&
           start.LogicalProcessorCount == completion.LogicalProcessorCount &&
           string.Equals(start.OsDescription, completion.OsDescription, StringComparison.Ordinal) &&
           string.Equals(start.OsVersion, completion.OsVersion, StringComparison.Ordinal) &&
           string.Equals(start.OsArchitecture, completion.OsArchitecture, StringComparison.Ordinal) &&
           string.Equals(start.ProcessArchitecture, completion.ProcessArchitecture, StringComparison.Ordinal) &&
           string.Equals(start.FrameworkDescription, completion.FrameworkDescription, StringComparison.Ordinal) &&
           Math.Abs(start.DpiScale - completion.DpiScale) < 0.01 &&
           Math.Abs(start.ViewportWidthDips - completion.ViewportWidthDips) < 1 &&
           Math.Abs(start.ViewportHeightDips - completion.ViewportHeightDips) < 1 &&
           string.Equals(start.DisplayDeviceName, completion.DisplayDeviceName, StringComparison.Ordinal) &&
           Math.Abs(start.ConfiguredRefreshRateHz - completion.ConfiguredRefreshRateHz) < 0.5 &&
           string.Equals(start.GpuAdapterIdentitySha256, completion.GpuAdapterIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.GpuAdapterLuid, completion.GpuAdapterLuid, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.GpuDriverVersion, completion.GpuDriverVersion, StringComparison.Ordinal) &&
            string.Equals(start.DisplayAdapterIdentitySha256, completion.DisplayAdapterIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.DisplayAdapterLuid, completion.DisplayAdapterLuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.DisplayAdapterDriverVersion, completion.DisplayAdapterDriverVersion, StringComparison.Ordinal) &&
           string.Equals(start.ActivePowerSchemeGuid, completion.ActivePowerSchemeGuid, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.UserConfiguredAcPowerModeGuid, completion.UserConfiguredAcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.UserConfiguredDcPowerModeGuid, completion.UserConfiguredDcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.EffectivePowerMode, completion.EffectivePowerMode, StringComparison.Ordinal) &&
           string.Equals(start.PowerSource, completion.PowerSource, StringComparison.Ordinal) &&
           string.Equals(start.EnergySaverState, completion.EnergySaverState, StringComparison.Ordinal) &&
           string.Equals(start.ProcessPriorityClass, completion.ProcessPriorityClass, StringComparison.Ordinal) &&
           string.Equals(start.ProcessPowerThrottlingMode, completion.ProcessPowerThrottlingMode, StringComparison.Ordinal) &&
           start.GcServer == completion.GcServer &&
           string.Equals(start.GcLatencyMode, completion.GcLatencyMode, StringComparison.Ordinal);

    public static bool IsMachineMetadataComplete(MachineMetadata machine)
        => IsEnvironmentSnapshotComplete(machine) &&
           IsFinitePositive(machine.ObservedRefreshRateHz) &&
           machine.IsAtLeast120Hz ==
               (machine.ConfiguredRefreshRateHz >= 119 &&
                machine.ObservedRefreshRateHz >= Math.Max(
                    115,
                    machine.ConfiguredRefreshRateHz * 0.95));

    public static bool IsEnvironmentSnapshotComplete(MachineMetadata machine)
        => IsSha256(machine.MachineInstanceSha256) &&
           !string.IsNullOrWhiteSpace(machine.Cpu) &&
           machine.LogicalProcessorCount > 0 &&
           !string.IsNullOrWhiteSpace(machine.OsDescription) &&
           !string.IsNullOrWhiteSpace(machine.OsVersion) &&
           !string.IsNullOrWhiteSpace(machine.OsArchitecture) &&
           !string.IsNullOrWhiteSpace(machine.ProcessArchitecture) &&
           !string.IsNullOrWhiteSpace(machine.FrameworkDescription) &&
           IsFinitePositive(machine.DpiScale) &&
           IsFinitePositive(machine.ViewportWidthDips) &&
           IsFinitePositive(machine.ViewportHeightDips) &&
           !string.IsNullOrWhiteSpace(machine.DisplayDeviceName) &&
           IsFinitePositive(machine.ConfiguredRefreshRateHz) &&
           IsSha256(machine.GpuAdapterIdentitySha256) &&
           IsGpuAdapterLuid(machine.GpuAdapterLuid) &&
           !string.IsNullOrWhiteSpace(machine.GpuDriverVersion) &&
           IsSha256(machine.DisplayAdapterIdentitySha256) &&
           IsGpuAdapterLuid(machine.DisplayAdapterLuid) &&
           !string.IsNullOrWhiteSpace(machine.DisplayAdapterDriverVersion) &&
           Guid.TryParseExact(machine.ActivePowerSchemeGuid, "D", out _) &&
           HasConsistentConfiguredPowerModeSupport(machine) &&
           IsEffectivePowerMode(machine.EffectivePowerMode) &&
           machine.PowerSource is "AC" or "Battery" &&
           machine.EnergySaverState is "Off" or "On" &&
           Enum.TryParse(machine.ProcessPriorityClass, ignoreCase: false, out ProcessPriorityClass priority) &&
           Enum.IsDefined(priority) &&
           Enum.GetName(priority) == machine.ProcessPriorityClass &&
           machine.ProcessPowerThrottlingMode ==
               PerformanceMeasurementContract.ProcessPowerThrottlingMode &&
           Enum.TryParse(machine.GcLatencyMode, ignoreCase: false, out GCLatencyMode latencyMode) &&
           Enum.IsDefined(latencyMode) &&
           Enum.GetName(latencyMode) == machine.GcLatencyMode;

    public static bool IsReleasePowerEnvironment(MachineMetadata machine)
        => machine.PowerSource == "AC" && machine.EnergySaverState == "Off";

    public static bool IsConfiguredRefreshRateQualified(double configuredRefreshRateHz)
        => double.IsFinite(configuredRefreshRateHz) &&
           configuredRefreshRateHz >= PerformanceMeasurementContract.MinimumConfiguredRefreshRateHz;

    public static bool HasQualifiedRefresh(MachineMetadata machine)
        => machine.IsAtLeast120Hz &&
           IsConfiguredRefreshRateQualified(machine.ConfiguredRefreshRateHz) &&
           double.IsFinite(machine.ObservedRefreshRateHz) &&
           machine.ObservedRefreshRateHz >= Math.Max(
               115,
               machine.ConfiguredRefreshRateHz * 0.95);

    public static double CalculateObservedRefreshRateHz(ScrollResult scroll)
    {
        if (scroll.StopwatchFrequency <= 0)
            return 0;

        long[] elapsedTicks = scroll.Trials
            .SelectMany(static trial => trial.FrameIntervalElapsedTicks)
            .ToArray();
        if (elapsedTicks.Length == 0 || elapsedTicks.Any(static ticks => ticks <= 0))
            return 0;

        double medianTicks = PerformanceStatistics.Percentile(elapsedTicks, 0.50);
        return medianTicks > 0
            ? scroll.StopwatchFrequency / medianTicks
            : 0;
    }

    internal static bool HasObservedRefreshEvidence(
        MachineMetadata machine,
        ScrollResult scroll)
        => NearlyEqual(
            machine.ObservedRefreshRateHz,
            CalculateObservedRefreshRateHz(scroll));

    private static bool IsPowerMode(string value)
        => value.Equals("unsupported", StringComparison.Ordinal) ||
           value.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("961cc777-2547-4f9d-8174-7d86181b8a7a", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("ded574b5-45a0-4f42-8737-46345c09c238", StringComparison.OrdinalIgnoreCase);

    private static bool HasConsistentConfiguredPowerModeSupport(MachineMetadata machine)
        => IsPowerMode(machine.UserConfiguredAcPowerModeGuid) &&
           IsPowerMode(machine.UserConfiguredDcPowerModeGuid) &&
           (machine.UserConfiguredAcPowerModeGuid == "unsupported") ==
               (machine.UserConfiguredDcPowerModeGuid == "unsupported");

    private static bool IsEffectivePowerMode(string value)
        => value is "unsupported" or
           "BatterySaver" or
           "BetterBattery" or
           "Balanced" or
           "HighPerformance" or
           "MaxPerformance" or
           "GameMode" or
           "MixedReality";
}
