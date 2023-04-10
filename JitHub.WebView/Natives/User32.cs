// Native methods wrapper
// Some code are from https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using SysPoint = System.Drawing.Point;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WebView2Ex.Natives;

static class User32
{
    delegate bool ClientToScreenDelegate(HWND hWnd, ref SysPoint lpPoint);
    delegate int SendMessageDelegate(HWND hWnd, uint Msg, WPARAM wParam, LPARAM lParam);
    delegate IntPtr CreateWindowExDelegate(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam
    );
    delegate IntPtr DefWindowProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    delegate HWND GetFocusDelegate();
    delegate ushort RegisterClassDelegate(in WNDCLASSW lpWndClass);
    delegate BOOL DestroyWindowDelegate(HWND hWnd);

    static readonly ClientToScreenDelegate _ClientToScreen;
    public static bool ClientToScreen(HWND hWnd, ref SysPoint lpPoint)
        => _ClientToScreen.Invoke(hWnd, ref lpPoint);
    
    static readonly SendMessageDelegate _SendMessage;
    public static int SendMessage(HWND hWnd, uint Msg, WPARAM wParam, LPARAM lParam)
        => _SendMessage.Invoke(hWnd, Msg, wParam, lParam);
    
    static readonly CreateWindowExDelegate _CreateWindowEx;
    public static nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam
    ) => _CreateWindowEx.Invoke(dwExStyle, lpClassName, lpWindowName, dwStyle, x, y, nWidth, nHeight, hWndParent, hMenu, hInstance, lpParam);
    
    static readonly WNDPROC _DefWindowProc;
    public static LRESULT DefWindowProc(HWND param0, uint param1, WPARAM param2, LPARAM param3)
        => _DefWindowProc.Invoke(param0, param1, param2, param3);
    
    static readonly GetFocusDelegate _GetFocus;
    public static HWND GetFocus() => _GetFocus.Invoke();

    static readonly RegisterClassDelegate _RegisterClass;
    public static ushort RegisterClass(in WNDCLASSW lpWndClass)
        => _RegisterClass.Invoke(in lpWndClass);

    static readonly DestroyWindowDelegate _DestroyWindow;
    public static BOOL DestroyWindow(HWND hWnd)
        => _DestroyWindow.Invoke(hWnd);


    static User32()
    {
        var user32module = PInvoke.GetModuleHandle("user32.dll");
        if (user32module == default)
        {
            user32module = PInvoke.GetModuleHandle("ext-ms-win-rtcore-webview-l1-1-0.dll");
        }
        if (user32module == default) Environment.FailFast("Failed to obtain user32 apis");
        var address = PInvoke.GetProcAddress(user32module, "ClientToScreen");
        _ClientToScreen =
            Marshal.GetDelegateForFunctionPointer<ClientToScreenDelegate>(
                address
            );
        _SendMessage =
            Marshal.GetDelegateForFunctionPointer<SendMessageDelegate>(
                PInvoke.GetProcAddress(user32module, "SendMessageW")
            );
        _CreateWindowEx =
            Marshal.GetDelegateForFunctionPointer<CreateWindowExDelegate>(
            PInvoke.GetProcAddress(user32module, "CreateWindowExW")
            );
        _DefWindowProc =
            Marshal.GetDelegateForFunctionPointer<WNDPROC>(
                PInvoke.GetProcAddress(user32module, "DefWindowProcW")
            );
        _GetFocus =
            Marshal.GetDelegateForFunctionPointer<GetFocusDelegate>(
            PInvoke.GetProcAddress(user32module, "GetFocus")
        );
        _RegisterClass =
            Marshal.GetDelegateForFunctionPointer<RegisterClassDelegate>(
                PInvoke.GetProcAddress(user32module, "RegisterClassW")
            );
        _DestroyWindow = Marshal.GetDelegateForFunctionPointer<DestroyWindowDelegate>(
            PInvoke.GetProcAddress(user32module, "DestroyWindow")
        );
    }
}
