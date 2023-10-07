// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace WebView2Ex.UI;

partial class WebView2Ex
{
    (CoreWebView2MoveFocusReason m_storedMoveFocusReason, bool m_isPending) m_xamlFocusChangeInfo = (default, default);
    bool m_webHasFocus;
    void HandleGotFocus(object sender, RoutedEventArgs e)
    {
        var CoreWebView2 = this.CoreWebView2;
        if (CoreWebView2 != null && m_xamlFocusChangeInfo.m_isPending)
        {
            var CompositionController = this.Controller;
            if (CompositionController is null) return;
            try
            {
                CompositionController!.MoveFocus(m_xamlFocusChangeInfo.m_storedMoveFocusReason);
                m_webHasFocus = true;
            }
            catch (COMException)
            {
                // Occasionally, a request to restore the minimized window does not complete. This triggers
                // FocusManager to set Xaml Focus to WV2 and consequently into CWV2 MoveFocus() call above, 
                // which in turn will attempt ::SetFocus() on InputHWND, and that will fail with E_INVALIDARG
                // since that HWND remains minimized. Work around by ignoring this error here. Since the app
                // is minimized, focus state is not relevant - the next (successful) attempt to restrore the app
                // will set focus into WV2/CWV2 correctly.
                Debugger.Break();
                //if (e.ErrorCode != 0x80070057) // E_INVALIDARG
                //{
                //    throw;
                //}
            }
            m_xamlFocusChangeInfo.m_isPending = false;
        }
    }
    void HandleGettingFocus(object sender, GettingFocusEventArgs args)
    {
        var CoreWebView2 = this.CoreWebView2;
        if (CoreWebView2 is not null)
        {
            CoreWebView2MoveFocusReason moveFocusReason = CoreWebView2MoveFocusReason.Programmatic;

            if (args.InputDevice == FocusInputDeviceKind.Keyboard)
            {
                if (args.Direction == FocusNavigationDirection.Next)
                {
                    moveFocusReason = CoreWebView2MoveFocusReason.Next;
                }
                else if (args.Direction == FocusNavigationDirection.Previous)
                {
                    moveFocusReason = CoreWebView2MoveFocusReason.Previous;
                }
            }

            m_xamlFocusChangeInfo.m_storedMoveFocusReason = moveFocusReason;
            m_xamlFocusChangeInfo.m_isPending = true;
        }
    }
}
