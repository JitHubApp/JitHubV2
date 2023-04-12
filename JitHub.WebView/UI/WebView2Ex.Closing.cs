// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
using System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using static WebView2Ex.Natives.User32;
namespace WebView2Ex.UI;

partial class WebView2Ex : IDisposable
{
    bool isClosed;

    // MODIFIED: Original Does not implement IDisposable, but I do
    /// <summary>
    /// Allias of <see cref="Close"/>
    /// </summary>
    public partial void Dispose() => Close();
    // END MODIFIED

    public partial void Close() => CloseInternal(false);

    ~WebView2Ex()
    {
        CloseInternal(true);
    }
    // Close Implementation (notice this does not implement IClosable). Also called implicitly as part of destruction.
    void CloseInternal(bool inShutdownPath)
    {
        DisconnectFromRootVisualTarget();

        UnregisterEtcEvents();
        m_renderedRegistered = true;

        if (m_tempHostHwnd != default && CoreWindow.GetForCurrentThread() is null)
        {
            DestroyWindow(m_tempHostHwnd);
            m_tempHostHwnd = default;
        }

        UnregisterInitEvent();

        m_inputWindowHwnd = default;

        WebView2Runtime = null;

        isClosed = true;
    }
}
