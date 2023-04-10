using System;
using System.Runtime.InteropServices;

namespace WebView2Ex.Natives;


[ComImport, Guid("B74EA3BC-43C1-521F-9C75-E5C15054D78C"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
interface IApplicationWindow_HwndInterop
{
    Windows.UI.WindowId WindowHandle { get; }
}