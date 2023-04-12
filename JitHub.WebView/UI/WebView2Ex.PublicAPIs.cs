// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
using WebView2Ex.Natives;
using Windows.UI.Core;
using Windows.UI.WindowManagement;
using Windows.Win32.Foundation;
namespace WebView2Ex.UI;

partial class WebView2Ex
{
    public partial void Dispose();
    public partial void Close();
    public void SetWindow(HWND window)
    {
        ParentWindow = window;
        UpdateWindow();
    }
    public void SetWindow(AppWindow appWindow)
    {
        SetWindow((HWND)(nint)((IApplicationWindow_HwndInterop)(dynamic)appWindow).WindowHandle.Value);
    }
    public void SetWindow(CoreWindow coreWindow)
    {
        SetWindow((HWND)((ICoreWindowInterop)(dynamic)coreWindow).WindowHandle);
    }
    
}
