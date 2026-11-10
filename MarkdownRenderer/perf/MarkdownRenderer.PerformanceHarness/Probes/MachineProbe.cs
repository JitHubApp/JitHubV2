using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using Microsoft.Graphics.Canvas;
using Microsoft.Win32;

namespace MarkdownRenderer.PerformanceHarness.Probes;

/// <summary>
/// Captures the machine, runtime and process metadata recorded in a
/// performance report. Display, graphics-adapter and power evidence come
/// from <see cref="DisplayProbe"/>, <see cref="GraphicsAdapterProbe"/> and
/// <see cref="PowerProbe"/>; this type composes them in a fixed order.
/// </summary>
internal static class MachineProbe
{
    internal static MachineMetadata Capture(
        IntPtr windowHandle,
        CanvasDevice graphicsDevice,
        double dpiScale,
        double viewportWidthDips,
        double viewportHeightDips,
        bool configureProcessPowerThrottling)
    {
        string cpu = ReadRegistryString(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString");
        string displayDeviceName = DisplayProbe.GetWindowDisplayDeviceName(windowHandle);
        GraphicsAdapterMetadata graphics = GraphicsAdapterProbe.CaptureRenderingAdapter(graphicsDevice);
        GraphicsAdapterMetadata displayGraphics = GraphicsAdapterProbe.CaptureDisplayAdapter(displayDeviceName);
        PowerSourceMetadata power = PowerProbe.CapturePowerSource();
        string processPriorityClass = CaptureProcessPriorityClass();
        string processPowerThrottlingMode = PowerProbe.CaptureProcessPowerThrottling(
            configureProcessPowerThrottling);

        return new MachineMetadata
        {
            MachineInstanceSha256 = GetMachineInstanceSha256(cpu),
            Cpu = cpu.Trim(),
            LogicalProcessorCount = Environment.ProcessorCount,
            OsDescription = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.VersionString,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            DpiScale = dpiScale,
            ViewportWidthDips = viewportWidthDips,
            ViewportHeightDips = viewportHeightDips,
            DisplayDeviceName = displayDeviceName,
            ConfiguredRefreshRateHz = DisplayProbe.GetConfiguredRefreshRate(displayDeviceName),
            GpuAdapterIdentitySha256 = graphics.AdapterIdentitySha256,
            GpuAdapterLuid = graphics.AdapterLuid,
            GpuDriverVersion = graphics.DriverVersion,
            DisplayAdapterIdentitySha256 = displayGraphics.AdapterIdentitySha256,
            DisplayAdapterLuid = displayGraphics.AdapterLuid,
            DisplayAdapterDriverVersion = displayGraphics.DriverVersion,
            ActivePowerSchemeGuid = PowerProbe.GetActivePowerSchemeGuid(),
            UserConfiguredAcPowerModeGuid = PowerProbe.GetUserConfiguredPowerMode(acPower: true),
            UserConfiguredDcPowerModeGuid = PowerProbe.GetUserConfiguredPowerMode(acPower: false),
            EffectivePowerMode = PowerProbe.GetEffectivePowerMode(),
            PowerSource = power.Source,
            EnergySaverState = power.EnergySaverState,
            ProcessPriorityClass = processPriorityClass,
            ProcessPowerThrottlingMode = processPowerThrottlingMode,
            GcServer = GCSettings.IsServerGC,
            GcLatencyMode = GCSettings.LatencyMode.ToString(),
        };
    }

    internal static string GetBuildIdentity()
        => PerformanceDeploymentIdentity.CaptureForExecutable(
            Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The performance executable path is unavailable."));

    private static string GetMachineInstanceSha256(string cpu)
    {
        string machineGuid = ReadRegistryString(
            @"SOFTWARE\Microsoft\Cryptography",
            "MachineGuid");
        if (string.IsNullOrWhiteSpace(machineGuid))
            return string.Empty;

        string canonicalDescription = string.Join(
            '\n',
            "MarkdownRenderer.Performance.Machine/v1",
            $"machineGuid={machineGuid.Trim().ToUpperInvariant()}",
            $"cpu={cpu.Trim()}",
            $"logicalProcessorCount={Environment.ProcessorCount}",
            $"osArchitecture={RuntimeInformation.OSArchitecture}");
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDescription)));
    }

    private static string ReadRegistryString(string subKeyPath, string valueName)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey localMachine = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    view);
                using RegistryKey? key = localMachine.OpenSubKey(subKeyPath, writable: false);
                if (key?.GetValue(valueName) is string value &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // Leave the value explicitly empty; release validation will
                // reject incomplete environment evidence.
            }
        }

        return string.Empty;
    }

    private static string CaptureProcessPriorityClass()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.PriorityClass.ToString();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }
}
