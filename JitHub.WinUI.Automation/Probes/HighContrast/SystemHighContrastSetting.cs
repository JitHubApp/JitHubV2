using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastNativeMethods;

namespace JitHub.WinUI.Automation.Probes.HighContrast;

/// <summary>
/// Reads, writes, restores and waits for the system high contrast setting.
/// </summary>
internal static class SystemHighContrastSetting
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint SpiSetHighContrast = 0x0043;
    internal const uint HcfHighContrastOn = 0x00000001;
    private const uint HcfOptionNoThemeChange = 0x00001000;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    internal static NativeHighContrastSnapshot ReadHighContrast()
    {
        NativeHighContrast native = new()
        {
            Size = (uint)Marshal.SizeOf<NativeHighContrast>()
        };
        if (!SystemParametersInfoW(SpiGetHighContrast, native.Size, ref native, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETHIGHCONTRAST failed.");
        }

        string? scheme = native.DefaultScheme == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUni(native.DefaultScheme);
        return new NativeHighContrastSnapshot(native.Flags, scheme);
    }

    internal static void WriteHighContrast(NativeHighContrastSnapshot target)
    {
        NativeHighContrastSnapshot current = ReadHighContrast();
        uint targetFlags = target.Flags;
        if (current.IsEnabled != target.IsEnabled)
        {
            targetFlags &= ~HcfOptionNoThemeChange;
        }

        IntPtr scheme = target.DefaultScheme is null
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(target.DefaultScheme);
        try
        {
            NativeHighContrast native = new()
            {
                Size = (uint)Marshal.SizeOf<NativeHighContrast>(),
                Flags = targetFlags,
                DefaultScheme = scheme
            };
            if (!SystemParametersInfoW(
                    SpiSetHighContrast,
                    native.Size,
                    ref native,
                    SpifUpdateIniFile | SpifSendChange))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETHIGHCONTRAST failed.");
            }
        }
        finally
        {
            if (scheme != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(scheme);
            }
        }
    }

    internal static void RestoreHighContrast(NativeHighContrastSnapshot prior)
    {
        NativeHighContrastSnapshot current = ReadHighContrast();
        if (current != prior)
        {
            WriteHighContrast(prior);
        }

        WaitForHighContrast(state => state == prior, "the exact prior High Contrast state to be restored");
    }

    internal static void WaitForHighContrast(
        Func<NativeHighContrastSnapshot, bool> predicate,
        string description)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            if (predicate(ReadHighContrast()))
            {
                return;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15));

        throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    internal readonly record struct NativeHighContrastSnapshot(uint Flags, string? DefaultScheme)
    {
        public bool IsEnabled => (Flags & HcfHighContrastOn) != 0;
    }
}
