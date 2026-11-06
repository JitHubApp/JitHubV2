using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Reads the power scheme, power mode, power source and energy-saver state,
/// and configures and reads the process power-throttling policy.
/// </summary>
internal static class PowerProbe
{
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint EffectivePowerModeVersion1 = 1;
    private const uint EffectivePowerModeVersion2 = 2;
    private const int InvalidArgumentHResult = unchecked((int)0x80070057);
    private const string UnsupportedPowerMode = "unsupported";

    internal static string GetActivePowerSchemeGuid()
    {
        IntPtr schemePointer = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out schemePointer) != 0 ||
                schemePointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            return Marshal.PtrToStructure<Guid>(schemePointer).ToString("D");
        }
        finally
        {
            if (schemePointer != IntPtr.Zero)
                _ = LocalFree(schemePointer);
        }
    }

    internal static string GetUserConfiguredPowerMode(bool acPower)
    {
        try
        {
            Guid mode;
            uint error = acPower
                ? PowerGetUserConfiguredAcPowerMode(out mode)
                : PowerGetUserConfiguredDcPowerMode(out mode);
            return error == 0 ? mode.ToString("D") : string.Empty;
        }
        catch (EntryPointNotFoundException)
        {
            // These documented APIs were introduced in Windows 11. Older
            // supported Windows versions have no power-mode overlay to record.
            return UnsupportedPowerMode;
        }
    }

    internal static string GetEffectivePowerMode()
    {
        try
        {
            if (TryGetEffectivePowerMode(
                    EffectivePowerModeVersion2,
                    out string mode,
                    out int registrationResult))
            {
                return mode;
            }

            return registrationResult == InvalidArgumentHResult &&
                   TryGetEffectivePowerMode(
                       EffectivePowerModeVersion1,
                       out mode,
                       out _)
                ? mode
                : string.Empty;
        }
        catch (EntryPointNotFoundException)
        {
            // The notification API was introduced in Windows 10 version 1809.
            // Preserve explicit availability rather than inventing a mode on
            // older Windows versions.
            return UnsupportedPowerMode;
        }
    }

    private static bool TryGetEffectivePowerMode(
        uint version,
        out string mode,
        out int registrationResult)
    {
        mode = string.Empty;
        int observedMode = int.MinValue;
        using var callbackObserved = new ManualResetEventSlim(initialState: false);
        EffectivePowerModeCallback callback = (effectiveMode, _) =>
        {
            // A power transition can deliver another notification before
            // unregistration completes. Retain the most recent snapshot.
            Interlocked.Exchange(ref observedMode, (int)effectiveMode);
            callbackObserved.Set();
        };

        registrationResult = PowerRegisterForEffectivePowerModeNotifications(
            version,
            callback,
            IntPtr.Zero,
            out IntPtr registrationHandle);
        if (registrationResult < 0 || registrationHandle == IntPtr.Zero)
        {
            GC.KeepAlive(callback);
            return false;
        }

        bool wasObserved;
        int unregisterResult;
        try
        {
            // Registration promises an immediate initial callback. Keep the
            // wait bounded so a broken platform implementation cannot hang a
            // release run.
            wasObserved = callbackObserved.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            // Unregistration waits for callbacks already in progress, so it is
            // safe to dispose the event and release the delegate afterwards.
            unregisterResult =
                PowerUnregisterFromEffectivePowerModeNotifications(registrationHandle);
            GC.KeepAlive(callback);
        }

        mode = wasObserved && unregisterResult >= 0
            ? FormatEffectivePowerMode(observedMode)
            : string.Empty;
        return mode.Length > 0;
    }

    private static string FormatEffectivePowerMode(int mode)
        => mode switch
        {
            (int)EffectivePowerMode.EffectivePowerModeBatterySaver =>
                "BatterySaver",
            (int)EffectivePowerMode.EffectivePowerModeBetterBattery =>
                "BetterBattery",
            (int)EffectivePowerMode.EffectivePowerModeBalanced =>
                "Balanced",
            (int)EffectivePowerMode.EffectivePowerModeHighPerformance =>
                "HighPerformance",
            (int)EffectivePowerMode.EffectivePowerModeMaxPerformance =>
                "MaxPerformance",
            (int)EffectivePowerMode.EffectivePowerModeGameMode =>
                "GameMode",
            (int)EffectivePowerMode.EffectivePowerModeMixedReality =>
                "MixedReality",
            _ => string.Empty,
        };

    internal static PowerSourceMetadata CapturePowerSource()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
            return new PowerSourceMetadata("Unknown", "Unknown");

        string source = status.AcLineStatus switch
        {
            0 => "Battery",
            1 => "AC",
            _ => "Unknown",
        };
        string energySaverState = status.SystemStatusFlag switch
        {
            0 => "Off",
            1 => "On",
            _ => "Unknown",
        };
        return new PowerSourceMetadata(source, energySaverState);
    }

    internal static string CaptureProcessPowerThrottling(bool configure)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = PerformanceMeasurementContract.ProcessPowerThrottlingControlMask,
            StateMask = 0,
        };
        using Process process = Process.GetCurrentProcess();
        uint size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
        if (configure &&
            !SetProcessInformation(
                process.Handle,
                ProcessInformationClass.ProcessPowerThrottling,
                ref state,
                size))
            return string.Empty;

        state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
        };
        return GetProcessInformation(
                   process.Handle,
                   ProcessInformationClass.ProcessPowerThrottling,
                   ref state,
                   size) &&
               (state.ControlMask & PerformanceMeasurementContract.ProcessPowerThrottlingControlMask) ==
                   PerformanceMeasurementContract.ProcessPowerThrottlingControlMask &&
               (state.StateMask & PerformanceMeasurementContract.ProcessPowerThrottlingControlMask) == 0
            ? PerformanceMeasurementContract.ProcessPowerThrottlingMode
            : string.Empty;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(
        IntPtr userRootPowerKey,
        out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerGetUserConfiguredACPowerMode")]
    private static extern uint PowerGetUserConfiguredAcPowerMode(out Guid powerModeGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerGetUserConfiguredDCPowerMode")]
    private static extern uint PowerGetUserConfiguredDcPowerMode(out Guid powerModeGuid);

    [DllImport("powrprof.dll")]
    private static extern int PowerRegisterForEffectivePowerModeNotifications(
        uint version,
        EffectivePowerModeCallback callback,
        IntPtr context,
        out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern int PowerUnregisterFromEffectivePowerModeNotifications(
        IntPtr registrationHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        ProcessInformationClass informationClass,
        ref ProcessPowerThrottlingState information,
        uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        IntPtr process,
        ProcessInformationClass informationClass,
        ref ProcessPowerThrottlingState information,
        uint informationSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EffectivePowerModeCallback(
        EffectivePowerMode mode,
        IntPtr context);

    private enum ProcessInformationClass
    {
        ProcessPowerThrottling = 4,
    }

    private enum EffectivePowerMode
    {
        EffectivePowerModeBatterySaver = 0,
        EffectivePowerModeEnergySaverHighSavings = EffectivePowerModeBatterySaver,
        EffectivePowerModeBetterBattery = 1,
        EffectivePowerModeEnergySaverStandard = EffectivePowerModeBetterBattery,
        EffectivePowerModeBalanced = 2,
        EffectivePowerModeHighPerformance = 3,
        EffectivePowerModeMaxPerformance = 4,
        EffectivePowerModeGameMode = 5,
        EffectivePowerModeMixedReality = 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

}
