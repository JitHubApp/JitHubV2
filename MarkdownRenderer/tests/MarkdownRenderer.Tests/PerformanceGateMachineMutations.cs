using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations of the measured machine. Each change is mirrored
/// into the completion-time machine snapshot so the report stays internally
/// consistent and only the cross-report comparison (or the value itself) is
/// wrong.
/// </summary>
internal static class PerformanceGateMachineMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["machine-fingerprint"] = Set("machineInstanceSha256", new string('B', 64)),
            ["machine-os-description"] = Set("osDescription", "other-fixture-os"),
            ["machine-gpu-luid"] = Set("gpuAdapterLuid", "invalid-luid"),
            ["machine-gpu-identity"] = Set("gpuAdapterIdentitySha256", new string('D', 64)),
            ["machine-gpu-driver"] = Set("gpuDriverVersion", "31.0.15.9999"),
            ["machine-display-device"] = Set("displayDeviceName", string.Empty),
            ["machine-display-adapter-identity"] = Set("displayAdapterIdentitySha256", new string('E', 64)),
            ["machine-display-adapter-luid"] = Set("displayAdapterLuid", "invalid-luid"),
            ["machine-display-adapter-driver"] = Set("displayAdapterDriverVersion", "31.0.15.9999"),
            ["machine-power-guid"] = Set("activePowerSchemeGuid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            ["machine-ac-power-mode"] = Set("userConfiguredAcPowerModeGuid", "not-a-power-mode"),
            ["machine-dc-power-mode"] = Set(
                "userConfiguredDcPowerModeGuid",
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            ["machine-power-source"] = Set("powerSource", "Battery"),
            ["machine-power-source-casing"] = Set("powerSource", "ac"),
            ["machine-energy-saver"] = Set("energySaverState", "On"),
            ["machine-energy-saver-casing"] = Set("energySaverState", "off"),
            ["machine-power-pair"] = Set("userConfiguredDcPowerModeGuid", "unsupported"),
            ["machine-power-mode-casing"] = static report =>
            {
                Set("userConfiguredAcPowerModeGuid", "Unsupported")(report);
                Set("userConfiguredDcPowerModeGuid", "Unsupported")(report);
            },
            ["machine-effective-power-mode"] = Set("effectivePowerMode", "Balanced"),
            ["machine-effective-power-mode-casing"] = Set("effectivePowerMode", "highperformance"),
            ["machine-priority"] = Set("processPriorityClass", "Normal"),
            ["machine-priority-casing"] = Set("processPriorityClass", "high"),
            ["machine-throttling"] = Set(
                "processPowerThrottlingMode",
                "execution-speed=on;ignore-timer-resolution=off"),
            ["machine-gc-server"] = Set("gcServer", true),
            ["machine-gc-latency"] = Set("gcLatencyMode", "LowLatency"),
            ["machine-gc-latency-casing"] = Set("gcLatencyMode", "interactive"),
        },
    };

    private static Action<JsonObject> Set(string property, string value)
        => report => SetMirrored(report, property, value);

    private static Action<JsonObject> Set(string property, bool value)
        => report => SetMirrored(report, property, value);

    private static void SetMirrored(JsonObject report, string property, JsonNode? value)
    {
        JsonObject machine = report["machine"]!.AsObject();
        machine[property] = value;
        report["completionMachine"]![property] = machine[property]!.DeepClone();
    }
}
