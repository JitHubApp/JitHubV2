// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Windows.UI;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.Win32.Foundation;

namespace WebView2Ex.UI;

[ObservableObject]
public partial class WebView2Ex : UserControl
{
    [ObservableProperty]
    WebView2Runtime? _WebView2Runtime;
    CoreWebView2? CoreWebView2 => WebView2Runtime?.CoreWebView2;
    CoreWebView2CompositionController? Controller => WebView2Runtime?.CompositionController;
    public WebView2Ex()
    {
        SetupSmoothScroll();
        ManipulationMode = ManipulationModes.None;
        
        RegisterEventsInit();
        IsTabStop = true;
        // Set the background for WebView2 to ensure it will be visible to hit-testing.
        Background = new SolidColorBrush(Colors.Transparent);
    }

    private HWND ParentWindow;

    partial void OnWebView2RuntimeChanging(WebView2Runtime? value)
    {
        var oldRuntime = WebView2Runtime;
        if (oldRuntime is null) return;
        if (oldRuntime.RootVisualTarget == visual)
        {
            oldRuntime.RootVisualTarget = null;
            oldRuntime.CompositionController.IsVisible = false;
        }
        // oldRuntime.SetWindow(HWND.Null);
        oldRuntime.Owner = null;
        oldRuntime.CompositionController.CursorChanged -= CoreWebView2CursorChanged;
    }
    partial void OnWebView2RuntimeChanged(WebView2Runtime? value)
    {
        var newRuntime = WebView2Runtime;
        if (newRuntime is null) return;
        if (newRuntime.Owner is not null && newRuntime.Owner.WebView2Runtime == newRuntime)
            newRuntime.Owner.WebView2Runtime = null;
        UpdateWindow();
        newRuntime.CompositionController.CursorChanged += CoreWebView2CursorChanged;
        newRuntime.CompositionController.IsVisible = m_isVisible;
        if (visual is not null) newRuntime.RootVisualTarget = visual;
    }
    void UpdateWindow()
    {
        WebView2Runtime?.SetWindow(ParentWindow);
    }
}