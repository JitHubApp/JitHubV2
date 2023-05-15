// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using Microsoft.Web.WebView2.Core;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Composition;
using Windows.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace WebView2Ex.UI;

partial class WebView2Ex
{

    void TryCompleteInitialization()
    {
        XamlRootChangedHelper(true);
        var xamlRoot = XamlRoot;
        if (xamlRoot != null)
        {
            xamlRoot.Changed += XamlRootChangedHanlder;
        }
        else
        {
            Window.Current.VisibilityChanged += VisiblityChangedHandler;
        }

        // WebView2 in WinUI 2 is a ContentControl that either renders its web content to a SpriteVisual, or in the case that
        // the WebView2 Runtime is not installed, renders a message to that effect as its Content. In the case where the
        // WebView2 starts with Visibility.Collapsed, hit testing code has trouble seeing the WebView2 if it does not have
        // Content. To work around this, give the WebView2 a transparent Grid as Content that hit testing can find. The size
        // of this Grid must be kept in sync with the size of the WebView2 (see ResizeChildPanel()).
        Content = new Grid { Background = new SolidColorBrush(Colors.Transparent) };

        visual ??= Window.Current.Compositor.CreateSpriteVisual();

        SetCoreWebViewAndVisualSize((float)ActualWidth, (float)ActualHeight);

        ElementCompositionPreview.SetElementChildVisual(this, visual);
        var CompositionController = this.Controller;
        if (CompositionController is not null)
            CompositionController.RootVisualTarget = visual;
    }
    SpriteVisual? visual;
}
