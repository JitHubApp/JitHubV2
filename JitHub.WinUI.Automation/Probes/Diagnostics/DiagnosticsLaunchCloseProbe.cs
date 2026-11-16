using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.AutomationDataRoot;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.Diagnostics;

/// <summary>
/// Drives the diagnostics-launch-close probe: launches and closes the app and checks the runtime diagnostics evidence it leaves behind.
/// </summary>
internal static class DiagnosticsLaunchCloseProbe
{
    // This method stays whole because it checks one launch and close cycle end to end: close the window, confirm a zero
    // native exit code, then parse the persisted diagnostics file to verify app.started, the ordered 64-event burst and
    // the final marker, and write the evidence manifest.
    internal static void RunDiagnosticsLaunchCloseProbe(CaptureOptions options)
    {
        const int expectedBurstCount = 64;
        const string burstName = "diagnostics.close.probe.burst";
        const string markerName = "diagnostics.close.probe.marker";
        KillExistingApplicationInstances(options.AppPath);
        ResetRuntimeEvidence();
        using var app = LaunchApplication(
            options.AppPath,
            "--page=login",
            "--theme=dark",
            "--scenario=diagnostics-close-probe");
        IntPtr processExitHandle = NativeMethods.OpenProcessExitHandle(app.ProcessId);
        using var automation = new UIA3Automation();
        bool closedCleanly = false;
        try
        {
            Window window = GetReadyWindow(app, automation, "diagnostics launch/close");
            AssertProbe(
                window.FindFirstDescendant(cf => cf.ByAutomationId("JitHubMainWindowRoot")) is not null,
                "The launched app did not expose the JitHub root element.");
            WaitForElement(
                "login root",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginRoot")),
                TimeSpan.FromSeconds(10));

            window.Close();
            bool exited = Retry.WhileFalse(
                () => app.HasExited,
                timeout: TimeSpan.FromSeconds(12),
                interval: TimeSpan.FromMilliseconds(100)).Result;
            AssertProbe(exited, "The app did not exit after its main window closed.");
            uint nativeExitCode = NativeMethods.GetProcessExitCode(processExitHandle);
            AssertProbe(nativeExitCode == 0, $"The app exited with native process code {nativeExitCode}.");

            string diagnosticsPath = Path.Combine(
                GetAutomationDataRoot(),
                "Local",
                "Diagnostics",
                "v1",
                "diagnostics.ndjson");
            AssertProbe(File.Exists(diagnosticsPath), "The diagnostics file was not persisted during launch/close.");
            string[] diagnostics = File.ReadAllLines(diagnosticsPath);
            DiagnosticProbeEvent[] parsed = diagnostics
                .Select((line, index) => ParseDiagnosticProbeEvent(line, index))
                .Where(static item => item is not null)
                .Select(static item => item!)
                .ToArray();
            int appStartedIndex = Array.FindIndex(
                parsed,
                static item => string.Equals(item.Name, "app.started", StringComparison.Ordinal));
            AssertProbe(appStartedIndex >= 0, "The accepted app.started diagnostic was not drained before process exit.");

            DiagnosticProbeEvent[] burst = parsed
                .Where(item => string.Equals(item.Name, burstName, StringComparison.Ordinal))
                .ToArray();
            AssertProbe(
                burst.Length == expectedBurstCount,
                $"Expected {expectedBurstCount} close-adjacent diagnostics but found {burst.Length}.");
            for (int sequence = 0; sequence < expectedBurstCount; sequence++)
            {
                AssertProbe(
                    burst[sequence].Properties.TryGetValue("sequence", out string? value) &&
                    int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int actual) &&
                    actual == sequence,
                    $"Close-adjacent diagnostic sequence {sequence} was missing or out of order.");
                if (sequence > 0)
                {
                    AssertProbe(
                        burst[sequence].LineIndex == burst[sequence - 1].LineIndex + 1,
                        "Close-adjacent burst diagnostics were not persisted contiguously in enqueue order.");
                }
            }

            DiagnosticProbeEvent marker = parsed.SingleOrDefault(
                    item => string.Equals(item.Name, markerName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The terminal close-adjacent diagnostics marker was not persisted.");
            AssertProbe(
                marker.LineIndex == burst[^1].LineIndex + 1,
                "The terminal diagnostics marker was not persisted immediately after the ordered burst.");
            AssertProbe(
                marker.Properties.TryGetValue("accepted", out string? accepted) &&
                string.Equals(accepted, expectedBurstCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "The close path did not accept the complete diagnostics burst.");
            AssertProbe(
                parsed[appStartedIndex].LineIndex < burst[0].LineIndex,
                "The close-adjacent burst was persisted before app.started.");
            AssertProbe(
                marker.LineIndex == diagnostics.Length - 1,
                "The terminal close marker was not the final persisted diagnostic before exit.");

            string evidencePath = Path.Combine(options.OutputDirectory, "diagnostics-launch-close.ndjson");
            File.Copy(diagnosticsPath, evidencePath, overwrite: true);
            string manifestPath = Path.Combine(options.OutputDirectory, "diagnostics-launch-close-manifest.json");
            byte[] evidenceBytes = File.ReadAllBytes(evidencePath);
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "diagnostics-launch-close",
                        passed = true,
                        processId = app.ProcessId,
                        nativeExitCode,
                        appStartedLine = parsed[appStartedIndex].LineIndex,
                        burstCount = burst.Length,
                        burstFirstLine = burst[0].LineIndex,
                        burstLastLine = burst[^1].LineIndex,
                        markerLine = marker.LineIndex,
                        markerWasLast = marker.LineIndex == diagnostics.Length - 1,
                        diagnosticsSha256 = Convert.ToHexString(SHA256.HashData(evidenceBytes)),
                        evidenceFile = Path.GetFileName(evidencePath),
                        capturedAtUtc = DateTimeOffset.UtcNow
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            AssertNoRuntimeFailureLogs("diagnostics launch/close");
            closedCleanly = true;
            Console.WriteLine(
                $"diagnostics launch/close: ordered close burst, drain, persistence, exit, and logs verified; manifest={manifestPath}");
        }
        finally
        {
            NativeMethods.CloseProcessExitHandle(processExitHandle);
            if (!closedCleanly && !app.HasExited)
            {
                try { app.Kill(); } catch { }
            }
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static DiagnosticProbeEvent? ParseDiagnosticProbeEvent(string line, int lineIndex)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? name = null;
            Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    name = property.Value.GetString();
                }
                else if (property.Name.Equals("properties", StringComparison.OrdinalIgnoreCase) &&
                         property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty value in property.Value.EnumerateObject())
                    {
                        properties[value.Name] = value.Value.ValueKind == JsonValueKind.String
                            ? value.Value.GetString() ?? string.Empty
                            : value.Value.ToString();
                    }
                }
            }

            return string.IsNullOrWhiteSpace(name)
                ? null
                : new DiagnosticProbeEvent(lineIndex, name, properties);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ResetRuntimeEvidence()
    {
        string localRoot = Path.Combine(GetAutomationDataRoot(), "Local");
        string diagnosticsPath = Path.Combine(localRoot, "Diagnostics", "v1", "diagnostics.ndjson");
        if (File.Exists(diagnosticsPath))
        {
            File.Delete(diagnosticsPath);
        }

        string logDirectory = Path.Combine(localRoot, "logs");
        if (!Directory.Exists(logDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(logDirectory, "*.log"))
        {
            File.Delete(path);
        }
    }

    private static void AssertNoRuntimeFailureLogs(string context)
    {
        string logDirectory = Path.Combine(GetAutomationDataRoot(), "Local", "logs");
        if (!Directory.Exists(logDirectory))
        {
            return;
        }

        string[] failureLogs = Directory.EnumerateFiles(logDirectory, "*.log")
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return new FileInfo(path).Length > 0 &&
                     (name.EndsWith("-unhandled.log", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("activation-error.log", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("diagnostics-shutdown.log", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("diagnostics-close-probe.log", StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
        if (failureLogs.Length == 0)
        {
            return;
        }

        string details = string.Join(
            Environment.NewLine,
            failureLogs.Select(path => $"{Path.GetFileName(path)}:{Environment.NewLine}{File.ReadAllText(path)}"));
        throw new InvalidOperationException($"{context}: runtime failure logs were written.{Environment.NewLine}{details}");
    }
}
