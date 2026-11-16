using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates.Mutations;

/// <summary>
/// Gate fixture mutations of the completion-time machine snapshot: a missing
/// or malformed snapshot, or one environment field that changed while the run
/// was measured.
/// </summary>
internal static class PerformanceGateCompletionMachineMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["completion-missing-object"] = static report => report.Remove("completionMachine"),
            ["completion-null-object"] = static report => report["completionMachine"] = null,
            ["completion-missing-field"] = static report =>
                Completion(report).Remove("displayAdapterDriverVersion"),
            ["completion-null-field"] = Set("displayAdapterDriverVersion", null),
            ["completion-unknown-field"] = Set("unexpected", 1),
            ["completion-machine-instance"] = Set("machineInstanceSha256", new string('B', 64)),
            ["completion-cpu"] = Set("cpu", "other-cpu"),
            ["completion-logical-processors"] = Set("logicalProcessorCount", 9),
            ["completion-os-description"] = Set("osDescription", "other-os"),
            ["completion-os-version"] = Set("osVersion", "10.0.99999"),
            ["completion-os-architecture"] = Set("osArchitecture", "Arm64"),
            ["completion-process-architecture"] = Set("processArchitecture", "X86"),
            ["completion-framework"] = Set("frameworkDescription", ".NET other"),
            ["completion-dpi"] = Set("dpiScale", 2),
            ["completion-viewport-width"] = Set("viewportWidthDips", 1101),
            ["completion-viewport-height"] = Set("viewportHeightDips", 761),
            ["completion-display-device"] = Set("displayDeviceName", @"\\.\DISPLAY2"),
            ["completion-configured-refresh"] = Set("configuredRefreshRateHz", 121),
            ["completion-gpu-identity"] = Set("gpuAdapterIdentitySha256", new string('E', 64)),
            ["completion-gpu-luid"] = Set("gpuAdapterLuid", "00000009:0000000A"),
            ["completion-gpu-driver"] = Set("gpuDriverVersion", "31.0.15.9999"),
            ["completion-display-adapter-identity"] = Set("displayAdapterIdentitySha256", new string('F', 64)),
            ["completion-display-adapter-luid"] = Set("displayAdapterLuid", "0000000B:0000000C"),
            ["completion-display-adapter-driver"] = Set("displayAdapterDriverVersion", "31.0.15.9999"),
            ["completion-power-scheme"] = Set("activePowerSchemeGuid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            ["completion-ac-power-mode"] = Set(
                "userConfiguredAcPowerModeGuid",
                "961cc777-2547-4f9d-8174-7d86181b8a7a"),
            ["completion-dc-power-mode"] = Set(
                "userConfiguredDcPowerModeGuid",
                "ded574b5-45a0-4f42-8737-46345c09c238"),
            ["completion-power-source"] = Set("powerSource", "Battery"),
            ["completion-energy-saver"] = Set("energySaverState", "On"),
            ["completion-effective-power-mode"] = Set("effectivePowerMode", "Balanced"),
            ["completion-priority"] = Set("processPriorityClass", "Normal"),
            ["completion-throttling"] = Set(
                "processPowerThrottlingMode",
                "execution-speed=on;ignore-timer-resolution=off"),
            ["completion-gc-server"] = Set("gcServer", true),
            ["completion-gc-latency"] = Set("gcLatencyMode", "LowLatency"),
        },
    };

    private static JsonObject Completion(JsonObject report)
        => report["completionMachine"]!.AsObject();

    private static Action<JsonObject> Set(string property, string? value)
        => report => Completion(report)[property] = value;

    private static Action<JsonObject> Set(string property, int value)
        => report => Completion(report)[property] = value;

    private static Action<JsonObject> Set(string property, bool value)
        => report => Completion(report)[property] = value;
}
