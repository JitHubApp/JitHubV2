// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using Microsoft.Web.WebView2.Core;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using SysPoint = System.Drawing.Point;
using Windows.Win32.UI.WindowsAndMessaging;
using System.Diagnostics;
using Windows.Globalization;
using Windows.UI.ViewManagement;
using Size = Windows.Foundation.Size;
using Windows.UI.Xaml.Documents;
using Point = Windows.Foundation.Point;
using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Xaml.Hosting;
using Windows.Graphics.Display;
using WebView2Ex.Natives;
using static WebView2Ex.Natives.User32;
namespace WebView2Ex.UI;

partial class WebView2Ex
{
    HWND xamlHostHwnd;
    Point hostWindowPosition;
    Point webViewScaledPosition, webViewScaledSize;
    HWND m_tempHostHwnd, m_inputWindowHwnd;


    unsafe HWND EnsureTemporaryHostHwnd()
    {
        // If we don't know the parent yet, either use the CoreWindow as the parent,
        // or if we don't have one, create a dummy hwnd to be the temporary parent.
        // Using a dummy parent all the time won't work, since we can't reparent the
        // browser from a Non-ShellManaged Hwnd (dummy) to a ShellManaged one (CoreWindow).
        CoreWindow coreWindow = CoreWindow.GetForCurrentThread();
        if (coreWindow is not null)
        {
            var coreWindowInterop = (ICoreWindowInterop)((dynamic)coreWindow);
            m_tempHostHwnd = new(coreWindowInterop.WindowHandle);
        }
        else
        {
            // Register the window class.
            string CLASS_NAME = "WEBVIEW2_TEMP_PARENT";
            HINSTANCE hInstance = PInvoke.GetModuleHandle(default(PCWSTR));
            fixed (char* classNameAsChars = CLASS_NAME)
            {
                WNDCLASSW wc = new()
                {
                    lpfnWndProc = DefWindowProc,
                    hInstance = hInstance,
                    lpszClassName = new(classNameAsChars)
                };

                RegisterClass(in wc);

                m_tempHostHwnd = new(CreateWindowEx(
                    0,
                    CLASS_NAME,                                // Window class
                    "Webview2 Temporary Parent",               // Window text
                    (uint)WINDOW_STYLE.WS_OVERLAPPED,          // Window style
                    0, 0, 0, 0,
                    IntPtr.Zero,                               // Parent window
                    IntPtr.Zero,                               // Menu
                    hInstance,                                 // Instance handle
                    IntPtr.Zero                                // Additional application data
                ));
            }
        }
        return m_tempHostHwnd;
    }

    HWND GetHostHwnd()
    {
        if (xamlHostHwnd == default)
        {
            CoreWindow coreWindow = CoreWindow.GetForCurrentThread();
            if (coreWindow is not null)
            {
                var coreWindowInterop = (ICoreWindowInterop)((dynamic)coreWindow);
                xamlHostHwnd = new(coreWindowInterop.WindowHandle);
            }
        }

        return xamlHostHwnd;
    }
    HWND GetActiveInputWindowHwnd()
    {
        if (m_inputWindowHwnd == default)
        {
            var inputWindowHwnd = GetFocus();
            if (inputWindowHwnd == default)
            {
                throw new COMException("A COM error has occured", Marshal.GetLastWin32Error());
            }
            Debug.Assert(inputWindowHwnd != xamlHostHwnd); // Focused XAML host window cannot be set as input hwnd
            m_inputWindowHwnd = inputWindowHwnd;
        }
        return m_inputWindowHwnd;
    }

    void CheckAndUpdateWebViewPosition()
    {
        var CompositionController = this.Controller;
        if (CompositionController is null) return;

        // Skip this work if WebView2 has just been removed from the tree - otherwise the CWV2.Bounds update could cause a flicker.
        //
        // After WebView2 is removed from the tree, this handler gets run one more time during the frame's render pass 
        // (WebView2::HandleRendered()). The removed element's ActualWidth or ActualHeight could now evaluate to zero 
        // (if Width or Height weren't explicitly set), causing 0-sized Bounds to get applied below and clear the web content, 
        // producing a flicker that last until DComp Commit for this frame is processed by the compositor.
        if (!IsLoaded) return;

        // Check if the position of the WebView2 within the window has changed
        bool changed = false;
        var transform = TransformToVisual(null);
        var topLeft = transform.TransformPoint(new Point(0, 0));

        var scaledTopLeftX = Math.Ceiling(topLeft.X * rasterizationScale);
        var scaledTopLeftY = Math.Ceiling(topLeft.Y * rasterizationScale);

        if (scaledTopLeftX != webViewScaledPosition.X || scaledTopLeftY != webViewScaledPosition.Y)
        {
            webViewScaledPosition.X = scaledTopLeftX;
            webViewScaledPosition.Y = scaledTopLeftY;
            changed = true;
        }

        var scaledSizeX = Math.Ceiling(ActualWidth * rasterizationScale);
        var scaledSizeY = Math.Ceiling(ActualHeight * rasterizationScale);
        if (scaledSizeX != webViewScaledSize.X || scaledSizeY != webViewScaledSize.Y)
        {
            webViewScaledSize.X = scaledSizeX;
            webViewScaledSize.Y = scaledSizeY;
            changed = true;
        }

        if (changed)
        {
            // We create the Bounds using X, Y, width, and height
            CompositionController.Bounds = new Rect(
                (webViewScaledPosition.X),
                (webViewScaledPosition.Y),
                (webViewScaledSize.X),
                (webViewScaledSize.Y)
            );
        }
    }

    Rect GetBoundingRectangle()
    {
        return new Rect(
            (webViewScaledPosition.X),
        (webViewScaledPosition.Y),
        (webViewScaledSize.X),
        (webViewScaledSize.Y));
    }

    void CheckAndUpdateWindowPosition()
    {
        var hostWindow = GetHostHwnd();
        if (hostWindow == null)
        {
            return;
        }

        SysPoint windowPosition = new(0, 0);
        ClientToScreen(hostWindow, ref windowPosition);
        if (hostWindowPosition.X != windowPosition.X || hostWindowPosition.Y != windowPosition.Y)
        {
            hostWindowPosition.X = windowPosition.X;
            hostWindowPosition.Y = windowPosition.Y;
            var Controller = this.Controller;
            Controller?.NotifyParentWindowPositionChanged();
        }
    }

    void UpdateParentWindow(HWND newParentWindow)
    {
        var Controller = this.Controller;
        if (m_tempHostHwnd != default && Controller != null)
        {
            var windowRef = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)newParentWindow.Value);

            // Reparent webview host
            Controller.ParentWindow = windowRef;

            DestroyWindow(m_tempHostHwnd);
            m_tempHostHwnd = default;
        }
    }
}
