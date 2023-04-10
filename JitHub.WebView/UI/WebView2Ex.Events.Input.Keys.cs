// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using Windows.UI.Core;
using Windows.UI.Xaml.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using System.Diagnostics;
using static WebView2Ex.Natives.Macros;
using static WebView2Ex.Natives.User32;
using Windows.System;
using Windows.Win32.UI.Input.KeyboardAndMouse;
namespace WebView2Ex.UI;

partial class WebView2Ex
{
    void RegisterXamlKeyEventHandlers()
    {
        KeyDown += HandleKeyDown;

        var coreWindow = CoreWindow.GetForCurrentThread();
        // TODO: We do not have direct analogue for AcceleratorKeyActivated with DispatcherQueue in Islands/ win32. Please refer Task# 30013704 for  more details.
        if (coreWindow != null)
        {
            Dispatcher.AcceleratorKeyActivated += HandleAcceleratorKeyActivated;
        }
    }
    void UnregisterXamlKeyEventHandlers()
    {
        KeyDown -= HandleKeyDown;

        var coreWindow = CoreWindow.GetForCurrentThread();
        if (coreWindow != null)
        {
            Dispatcher.AcceleratorKeyActivated -= HandleAcceleratorKeyActivated;
        }
    }
    // Since WebView takes HWND focus (via OnGotFocus -> MoveFocus) Xaml assumes
    // focus was lost for an external reason. When the next unhandled TAB KeyDown
    // reaches the XamlRoot element, Xaml's FocusManager will try to move focus to the next
    // Xaml control and force HWND focus back to itself, popping Xaml focus out of the
    // WebView2 control. We mark TAB handled in our KeyDown handler so that it is ignored
    // by XamlRoot's tab processing.
    // If the WebView2 has been closed, then we should let Xaml's tab processing handle it.
    void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab && !isClosed)
        {
            e.Handled = true;
        }
    }

    // When Win32 HWND focus is switched to InputWindow, VK_TAB's processed by Xaml's CoreWindow
    // hosting accelerator key handling do not get dispatched to the child InputWindow.
    // Send CoreWebView2 the missing Tab/KeyDown so that tab handling occurs in Anaheim.
    void HandleAcceleratorKeyActivated(CoreDispatcher coreDispatcher, AcceleratorKeyEventArgs args)
    {
        if (args.VirtualKey == VirtualKey.Tab &&
            args.EventType == CoreAcceleratorKeyEventType.KeyDown &&
            m_webHasFocus &&
            args.Handled)
        {
            uint message = PInvoke.WM_KEYDOWN;
            WPARAM wparam = new((nuint)VIRTUAL_KEY.VK_TAB);
            LPARAM lparam = MakeLParam(0x0001, 0x000f);  // flags copied from matching WM_KEYDOWN

            LRESULT result = new(SendMessage(GetActiveInputWindowHwnd(), message, wparam, lparam));
            if (result == 0)
            {
                Debugger.Break();
                //winrt::check_hresult(HRESULT_FROM_WIN32(::GetLastError()));
            }
        }
    }
}
