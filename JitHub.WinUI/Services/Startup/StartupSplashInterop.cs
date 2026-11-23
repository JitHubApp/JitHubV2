using System.Runtime.InteropServices;

namespace JitHub.WinUI.Services.Startup;

/// <summary>Win32 and DWM surface used by the native startup splash.</summary>
internal static unsafe partial class StartupSplashInterop
{
    internal const int CwUseDefault = unchecked((int)0x80000000);
    internal const int GwlpUserData = -21;
    internal const int HtClient = 1;
    internal const int MaNoActivate = 3;
    internal const int SwShowNoActivate = 4;
    internal const int SmCxSizeFrame = 32;
    internal const int SmCySizeFrame = 33;
    internal const int SmCxPaddedBorder = 92;
    internal const int ColorWindow = 5;
    internal const int DiNormal = 3;
    internal const int IdcAppStarting = 32650;
    internal const uint ImageIcon = 1;
    internal const uint LrLoadFromFile = 0x00000010;
    internal const uint MonitorDefaultToPrimary = 1;
    internal const uint SpiGetHighContrast = 0x0042;
    internal const uint HcfHighContrastOn = 0x00000001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint WsPopup = 0x80000000;
    internal const uint WsCaption = 0x00C00000;
    internal const uint WsThickFrame = 0x00040000;
    internal const uint WsExNoActivate = 0x08000000;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmPaint = 0x000F;
    internal const uint WmEraseBkgnd = 0x0014;
    internal const uint WmMouseActivate = 0x0021;
    internal const uint WmNcCreate = 0x0081;
    internal const uint WmNcCalcSize = 0x0083;
    internal const uint WmNcHitTest = 0x0084;
    internal const uint WmTimer = 0x0113;
    internal const uint WmApp = 0x8000;
    internal const uint DwmwaUseImmersiveDarkMode = 20;
    internal const uint DwmwaCloak = 13;
    internal const uint DwmwaWindowCornerPreference = 33;
    internal const int DwmwcpRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        public nint DeviceContext;
        public int Erase;
        public Rect PaintRect;
        public int Restore;
        public int IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateStruct
    {
        public nint CreateParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClass(WindowClass* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterClass(string className, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindow(
        uint extendedStyle,
        string className,
        string? title,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(Message* message, nint window, uint minimum, uint maximum);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(Message* message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(nint window, nuint id, uint elapseMilliseconds, nint callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint window, Rect* rect);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, MonitorInfo* info);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(uint action, uint parameter, void* value, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetSysColor(int index);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    internal static partial nint LoadCursor(nint instance, nint cursorName);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIconEx(
        nint deviceContext,
        int x,
        int y,
        nint icon,
        int width,
        int height,
        uint frame,
        nint flickerFreeBrush,
        uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint BeginPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint deviceContext, Rect* rect, nint brush);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint window, uint attribute, void* value, uint size);
}
