// Native methods wrapper
// Some code are from https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using System;
using System.Runtime.InteropServices;

namespace WebView2Ex.Natives;

[ComImport, Guid("45D64A29-A63E-4CB6-B498-5781D298CB4F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICoreWindowInterop
{
    IntPtr WindowHandle { get; }
    bool MessageHandled { set; }
}