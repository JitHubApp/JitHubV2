using System;

namespace MarkdownRenderer.Diagnostics;

internal static class GraphicsDeviceErrors
{
    public const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    public const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    public const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);
    public const int D2DErrorRecreateTarget = unchecked((int)0x8899000C);
    public const int D3DErrorDeviceLost = unchecked((int)0x88760868);
    public const int D3DErrorDeviceNotReset = unchecked((int)0x88760869);

    public static bool IsDeviceLost(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsDeviceLostHResult(current.HResult))
                return true;
        }

        return false;
    }

    public static bool IsDeviceLostHResult(int hresult)
        => hresult is DxgiErrorDeviceRemoved
            or DxgiErrorDeviceHung
            or DxgiErrorDeviceReset
            or DxgiErrorDriverInternalError
            or D2DErrorRecreateTarget
            or D3DErrorDeviceLost
            or D3DErrorDeviceNotReset;

    public static string FormatHResult(int hresult)
        => $"0x{unchecked((uint)hresult):X8}";
}
