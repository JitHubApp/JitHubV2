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
    public const int RpcErrorDisconnected = unchecked((int)0x80010108);
    public const int RpcErrorServerDied = unchecked((int)0x80010007);
    public const int RpcErrorServerDiedDne = unchecked((int)0x80010012);
    public const int RoErrorClosed = unchecked((int)0x80000013);
    public const int Win32ErrorInvalidHandle = unchecked((int)0x80070006);
    public const int Win32ErrorInvalidWindowHandle = unchecked((int)0x80070578);

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

    public static bool IsShutdownOrDisposed(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ObjectDisposedException || IsShutdownHResult(current.HResult))
                return true;
        }

        return false;
    }

    public static bool IsShutdownHResult(int hresult)
        => hresult is RpcErrorDisconnected
            or RpcErrorServerDied
            or RpcErrorServerDiedDne
            or RoErrorClosed
            or Win32ErrorInvalidHandle
            or Win32ErrorInvalidWindowHandle;

    public static string FormatHResult(int hresult)
        => $"0x{unchecked((uint)hresult):X8}";
}
