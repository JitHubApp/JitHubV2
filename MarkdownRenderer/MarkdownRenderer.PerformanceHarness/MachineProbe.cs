using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Win32;
using WinRT;

namespace MarkdownRenderer.PerformanceHarness;

internal static class MachineProbe
{
    private const int EnumCurrentSettings = -1;
    private const uint DisplayFrequencyField = 0x00400000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint EffectivePowerModeVersion1 = 1;
    private const uint EffectivePowerModeVersion2 = 2;
    private const int InvalidArgumentHResult = unchecked((int)0x80070057);
    private const string UnsupportedPowerMode = "unsupported";
    private static readonly Guid Direct3DDxgiInterfaceAccessId =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid DxgiDeviceId =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

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
        string displayDeviceName = GetWindowDisplayDeviceName(windowHandle);
        GraphicsMetadata graphics = CaptureRenderingGraphicsMetadata(graphicsDevice);
        GraphicsMetadata displayGraphics = CaptureDisplayGraphicsMetadata(displayDeviceName);
        PowerMetadata power = CapturePowerMetadata();
        string processPriorityClass = CaptureProcessPriorityClass();
        string processPowerThrottlingMode = CaptureProcessPowerThrottling(
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
            ConfiguredRefreshRateHz = GetConfiguredRefreshRate(displayDeviceName),
            GpuAdapterIdentitySha256 = graphics.AdapterIdentitySha256,
            GpuAdapterLuid = graphics.AdapterLuid,
            GpuDriverVersion = graphics.DriverVersion,
            DisplayAdapterIdentitySha256 = displayGraphics.AdapterIdentitySha256,
            DisplayAdapterLuid = displayGraphics.AdapterLuid,
            DisplayAdapterDriverVersion = displayGraphics.DriverVersion,
            ActivePowerSchemeGuid = GetActivePowerSchemeGuid(),
            UserConfiguredAcPowerModeGuid = GetUserConfiguredPowerMode(acPower: true),
            UserConfiguredDcPowerModeGuid = GetUserConfiguredPowerMode(acPower: false),
            EffectivePowerMode = GetEffectivePowerMode(),
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

    private static double GetConfiguredRefreshRate(string displayDeviceName)
    {
        if (string.IsNullOrWhiteSpace(displayDeviceName))
            return 0;

        var mode = new DevMode
        {
            DeviceName = string.Empty,
            FormName = string.Empty,
            Size = (ushort)Marshal.SizeOf<DevMode>(),
        };

        return EnumDisplaySettings(displayDeviceName, EnumCurrentSettings, ref mode) &&
               (mode.Fields & DisplayFrequencyField) != 0 &&
               mode.DisplayFrequency > 1
            ? mode.DisplayFrequency
            : 0;
    }

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

    private static string GetWindowDisplayDeviceName(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            return string.Empty;

        IntPtr monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return string.Empty;

        var monitorInfo = new MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };
        return GetMonitorInfo(monitor, ref monitorInfo)
            ? monitorInfo.DeviceName.TrimEnd('\0')
            : string.Empty;
    }

    private static GraphicsMetadata CaptureRenderingGraphicsMetadata(CanvasDevice graphicsDevice)
    {
        if (!TryGetCanvasDeviceAdapterLuid(graphicsDevice, out LocallyUniqueIdentifier adapterLuid))
            return new GraphicsMetadata(string.Empty, string.Empty, string.Empty);

        var open = new D3dKmtOpenAdapterFromLuidData
        {
            AdapterLuid = adapterLuid,
        };
        if (D3dKmtOpenAdapterFromLuid(ref open) != 0)
            return new GraphicsMetadata(string.Empty, string.Empty, string.Empty);

        try
        {
            return CaptureOpenAdapterGraphicsMetadata(open.Adapter, adapterLuid);
        }
        finally
        {
            CloseAdapter(open.Adapter);
        }
    }

    private static bool TryGetCanvasDeviceAdapterLuid(
        CanvasDevice graphicsDevice,
        out LocallyUniqueIdentifier adapterLuid)
    {
        adapterLuid = default;
        IntPtr canvasAbi = IntPtr.Zero;
        IntPtr interfaceAccess = IntPtr.Zero;
        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        try
        {
            canvasAbi = MarshalInspectable<CanvasDevice>.FromManaged(graphicsDevice);
            Guid interfaceAccessId = Direct3DDxgiInterfaceAccessId;
            if (Marshal.QueryInterface(canvasAbi, in interfaceAccessId, out interfaceAccess) < 0)
                return false;

            GetDxgiInterface getInterface = GetVtableMethod<GetDxgiInterface>(interfaceAccess, 3);
            Guid dxgiDeviceId = DxgiDeviceId;
            if (getInterface(interfaceAccess, ref dxgiDeviceId, out dxgiDevice) < 0)
                return false;

            GetDxgiAdapter getAdapter = GetVtableMethod<GetDxgiAdapter>(dxgiDevice, 7);
            if (getAdapter(dxgiDevice, out adapter) < 0)
                return false;

            GetDxgiAdapterDescription getDescription =
                GetVtableMethod<GetDxgiAdapterDescription>(adapter, 8);
            if (getDescription(adapter, out DxgiAdapterDescription description) < 0)
                return false;

            adapterLuid = description.AdapterLuid;
            return true;
        }
        finally
        {
            ReleaseComPointer(adapter);
            ReleaseComPointer(dxgiDevice);
            ReleaseComPointer(interfaceAccess);
            if (canvasAbi != IntPtr.Zero)
                MarshalInspectable<CanvasDevice>.DisposeAbi(canvasAbi);
        }
    }

    private static TDelegate GetVtableMethod<TDelegate>(IntPtr instance, int slot)
        where TDelegate : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr method = Marshal.ReadIntPtr(vtable, checked(slot * IntPtr.Size));
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(method);
    }

    private static void ReleaseComPointer(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
            _ = Marshal.Release(pointer);
    }

    private static GraphicsMetadata CaptureDisplayGraphicsMetadata(string displayDeviceName)
    {
        if (string.IsNullOrWhiteSpace(displayDeviceName))
            return new GraphicsMetadata(string.Empty, string.Empty, string.Empty);

        IntPtr deviceContext = CreateDC(null, displayDeviceName, null, IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
            return new GraphicsMetadata(string.Empty, string.Empty, string.Empty);

        try
        {
            var open = new D3dKmtOpenAdapterFromHdcData
            {
                DeviceContext = deviceContext,
            };
            if (D3dKmtOpenAdapterFromHdc(ref open) != 0)
                return new GraphicsMetadata(string.Empty, string.Empty, string.Empty);

            try
            {
                return CaptureOpenAdapterGraphicsMetadata(open.Adapter, open.AdapterLuid);
            }
            finally
            {
                CloseAdapter(open.Adapter);
            }
        }
        finally
        {
            _ = DeleteDC(deviceContext);
        }
    }

    private static GraphicsMetadata CaptureOpenAdapterGraphicsMetadata(
        uint adapter,
        LocallyUniqueIdentifier adapterLuid)
    {
        string luid = $"{unchecked((uint)adapterLuid.HighPart):X8}:" +
            $"{adapterLuid.LowPart:X8}";
        return new GraphicsMetadata(
            GetGpuAdapterIdentitySha256(adapter),
            luid,
            GetUmdDriverVersion(adapter));
    }

    private static void CloseAdapter(uint adapter)
    {
        var close = new D3dKmtCloseAdapterData
        {
            Adapter = adapter,
        };
        _ = D3dKmtCloseAdapter(ref close);
    }

    private static string GetGpuAdapterIdentitySha256(uint adapter)
    {
        var physicalAdapterCount = new D3dKmtPhysicalAdapterCount();
        var adapterAddress = new D3dKmtAdapterAddress();
        if (!TryQueryAdapterInfo(
                adapter,
                KmtQueryAdapterInfoType.PhysicalAdapterCount,
                ref physicalAdapterCount) ||
            !TryQueryAdapterInfo(
                adapter,
                KmtQueryAdapterInfoType.AdapterAddress,
                ref adapterAddress) ||
            physicalAdapterCount.Count == 0 ||
            physicalAdapterCount.Count > 64)
            return string.Empty;

        var canonicalDescription = new StringBuilder();
        canonicalDescription.AppendLine("MarkdownRenderer.Performance.GpuAdapter/v2");
        canonicalDescription.Append("physicalAdapterCount=")
            .Append(physicalAdapterCount.Count)
            .Append('\n');
        canonicalDescription.Append("adapterAddress=")
            .Append(adapterAddress.BusNumber.ToString("X8"))
            .Append(':').Append(adapterAddress.DeviceNumber.ToString("X8"))
            .Append(':').Append(adapterAddress.FunctionNumber.ToString("X8"))
            .Append('\n');
        for (uint index = 0; index < physicalAdapterCount.Count; index++)
        {
            var query = new D3dKmtQueryDeviceIds
            {
                PhysicalAdapterIndex = index,
            };
            if (!TryQueryAdapterInfo(
                    adapter,
                    KmtQueryAdapterInfoType.PhysicalAdapterDeviceIds,
                    ref query))
                return string.Empty;

            canonicalDescription
                .Append("adapter[").Append(index).Append("]=")
                .Append(query.DeviceIds.VendorId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.DeviceId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.SubVendorId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.SubSystemId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.RevisionId.ToString("X8"))
                .Append(':').Append(query.DeviceIds.BusType.ToString("X8"))
                .Append('\n');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDescription.ToString())));
    }

    private static string GetUmdDriverVersion(uint adapter)
    {
        var version = new D3dKmtUmdDriverVersion();
        if (!TryQueryAdapterInfo(
                adapter,
                KmtQueryAdapterInfoType.UmdDriverVersion,
                ref version) ||
            version.DriverVersion == 0)
            return string.Empty;

        ulong bits = unchecked((ulong)version.DriverVersion);
        return string.Join(
            '.',
            (bits >> 48) & 0xFFFF,
            (bits >> 32) & 0xFFFF,
            (bits >> 16) & 0xFFFF,
            bits & 0xFFFF);
    }

    private static bool TryQueryAdapterInfo<T>(
        uint adapter,
        KmtQueryAdapterInfoType type,
        ref T data)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, buffer, fDeleteOld: false);
            var query = new D3dKmtQueryAdapterInfoData
            {
                Adapter = adapter,
                Type = type,
                PrivateDriverData = buffer,
                PrivateDriverDataSize = checked((uint)size),
            };
            if (D3dKmtQueryAdapterInfo(ref query) != 0)
                return false;

            data = Marshal.PtrToStructure<T>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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

    private static string GetActivePowerSchemeGuid()
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

    private static string GetUserConfiguredPowerMode(bool acPower)
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

    private static string GetEffectivePowerMode()
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

    private static PowerMetadata CapturePowerMetadata()
    {
        if (!GetSystemPowerStatus(out SystemPowerStatus status))
            return new PowerMetadata("Unknown", "Unknown");

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
        return new PowerMetadata(source, energySaverState);
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

    private static string CaptureProcessPowerThrottling(bool configure)
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

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string? deviceName,
        int modeNumber,
        ref DevMode deviceMode);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr windowHandle,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfoEx monitorInfo);

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(
        string? driver,
        string device,
        string? output,
        IntPtr initializationData);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromHdc")]
    private static extern int D3dKmtOpenAdapterFromHdc(
        ref D3dKmtOpenAdapterFromHdcData data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromLuid")]
    private static extern int D3dKmtOpenAdapterFromLuid(
        ref D3dKmtOpenAdapterFromLuidData data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTCloseAdapter")]
    private static extern int D3dKmtCloseAdapter(ref D3dKmtCloseAdapterData data);

    [DllImport("gdi32.dll", EntryPoint = "D3DKMTQueryAdapterInfo")]
    private static extern int D3dKmtQueryAdapterInfo(
        ref D3dKmtQueryAdapterInfoData data);

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

    private sealed record GraphicsMetadata(
        string AdapterIdentitySha256,
        string AdapterLuid,
        string DriverVersion);

    private sealed record PowerMetadata(string Source, string EnergySaverState);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EffectivePowerModeCallback(
        EffectivePowerMode mode,
        IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiInterface(
        IntPtr instance,
        ref Guid interfaceId,
        out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiAdapter(IntPtr instance, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiAdapterDescription(
        IntPtr instance,
        out DxgiAdapterDescription description);

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
    private struct LocallyUniqueIdentifier
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtOpenAdapterFromHdcData
    {
        public IntPtr DeviceContext;
        public uint Adapter;
        public LocallyUniqueIdentifier AdapterLuid;
        public uint VideoPresentSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtOpenAdapterFromLuidData
    {
        public LocallyUniqueIdentifier AdapterLuid;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtCloseAdapterData
    {
        public uint Adapter;
    }

    private enum KmtQueryAdapterInfoType
    {
        AdapterAddress = 6,
        UmdDriverVersion = 18,
        PhysicalAdapterCount = 30,
        PhysicalAdapterDeviceIds = 31,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtQueryAdapterInfoData
    {
        public uint Adapter;
        public KmtQueryAdapterInfoType Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtPhysicalAdapterCount
    {
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtAdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtUmdDriverVersion
    {
        public long DriverVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtDeviceIds
    {
        public uint VendorId;
        public uint DeviceId;
        public uint SubVendorId;
        public uint SubSystemId;
        public uint RevisionId;
        public uint BusType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtQueryDeviceIds
    {
        public uint PhysicalAdapterIndex;
        public D3dKmtDeviceIds DeviceIds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LocallyUniqueIdentifier AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}
