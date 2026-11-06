using System;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// The UIA reserved Mixed and NotSupported attribute sentinels from
/// UIAutomationCore, with private stand-ins when the native call is unavailable.
/// </summary>
internal static class UiaReservedAttributeValues
{
    public static object Mixed => GetReservedValue(UiaGetReservedMixedAttributeValue, FallbackMixedAttributeValue.Instance);
    public static object NotSupported => GetReservedValue(UiaGetReservedNotSupportedValue, FallbackNotSupportedValue.Instance);

    private delegate int ReservedValueFactory(out IntPtr value);

    [DllImport("UIAutomationCore.dll", PreserveSig = true)]
    private static extern int UiaGetReservedMixedAttributeValue(out IntPtr value);

    [DllImport("UIAutomationCore.dll", PreserveSig = true)]
    private static extern int UiaGetReservedNotSupportedValue(out IntPtr value);

    private static object GetReservedValue(ReservedValueFactory factory, object fallback)
    {
        IntPtr value = IntPtr.Zero;
        try
        {
            int hr = factory(out value);
            if (hr >= 0 && value != IntPtr.Zero)
                return Marshal.GetObjectForIUnknown(value);
        }
        catch
        {
        }
        finally
        {
            if (value != IntPtr.Zero)
                Marshal.Release(value);
        }

        return fallback;
    }

    private sealed class FallbackMixedAttributeValue
    {
        public static readonly FallbackMixedAttributeValue Instance = new();
        private FallbackMixedAttributeValue() { }
    }

    private sealed class FallbackNotSupportedValue
    {
        public static readonly FallbackNotSupportedValue Instance = new();
        private FallbackNotSupportedValue() { }
    }
}
