using System.Runtime.InteropServices;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Reads the monitor that hosts the benchmark window and its configured
/// refresh rate through the Win32 display APIs.
/// </summary>
internal static class DisplayProbe
{
    private const int EnumCurrentSettings = -1;
    private const uint DisplayFrequencyField = 0x00400000;
    private const uint MonitorDefaultToNearest = 0x00000002;

    internal static double GetConfiguredRefreshRate(string displayDeviceName)
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

    internal static string GetWindowDisplayDeviceName(IntPtr windowHandle)
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
