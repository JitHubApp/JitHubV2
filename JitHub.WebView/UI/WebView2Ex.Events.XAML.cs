// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.Graphics.Display;

namespace WebView2Ex.UI;

partial class WebView2Ex
{

    double rasterizationScale = 1;
    bool isHostVisible;

    void OnLoaded(object sender, RoutedEventArgs args)
    {
        // OnLoaded and OnUnloaded are fired from XAML asynchronously, and unfortunately they could
        // be out of order.  If the element is removed from tree A and added to tree B, we could get
        // a Loaded event for tree B *before* we see the Unloaded event for tree A.  To handle this:
        //  * When we get a Loaded/Unloaded event, check the IsLoaded property. If it doesn't match
        //      the event we're in, nothing needs to be done since the other handler took care of it.
        //  * When we see a Loaded event when we have been or are already loaded, remove the
        //      XamlRootChanged event handler for the old tree and bind to the new one.

        // If we're not loaded, there's nothing for us to do since Unloaded took care of everything
        if (!IsLoaded) return;

        TryCompleteInitialization();

        if (VisualTreeHelper.GetChildrenCount(this) > 0)
        {
            var contentPresenter = VisualTreeHelper.GetChild(this, 0) as ContentPresenter;
            if (contentPresenter is not null)
            {
                contentPresenter.Background = Background;
                contentPresenter.HorizontalAlignment = HorizontalAlignment.Stretch;
                contentPresenter.VerticalAlignment = VerticalAlignment.Stretch;
                contentPresenter.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                contentPresenter.VerticalContentAlignment = VerticalAlignment.Stretch;
            }
        }
    }

    void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (IsLoaded) return;

        UpdateRenderedSubscriptionAndVisibility();

        var xamlRoot = XamlRoot;
        if (xamlRoot != null)
        {
            xamlRoot.Changed -= XamlRootChangedHanlder;
        }
        Window.Current.VisibilityChanged -= VisiblityChangedHandler;

        DisconnectFromRootVisualTarget();
    }


    void HandleXamlRootChanged()
    {
        XamlRootChangedHelper(false);
    }

    void XamlRootChangedHelper(bool forceUpdate)
    {
        var (scale, hostVisibility) = new Func<(double, bool)>(delegate
        {
            var xamlRoot = XamlRoot;
            if (xamlRoot != null)
            {
                var scale = (float)xamlRoot.RasterizationScale;
                bool hostVisibility = xamlRoot.IsHostVisible;

                return (scale, hostVisibility);
            }

            double rawPixelsPerViewPixel = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;

            return (rawPixelsPerViewPixel, Window.Current.Visible);
        })();


        if (forceUpdate || (scale != rasterizationScale))
        {
            rasterizationScale = scale;
            isHostVisible = hostVisibility; // If we did forceUpdate we'll want to update host visibility here too
            var Controller = this.Controller;
            if (Controller is not null)
            {
                Controller.RasterizationScale = scale;
            }
            SetCoreWebViewAndVisualSize((float)ActualWidth, (float)ActualHeight);
            CheckAndUpdateWebViewPosition();
            UpdateRenderedSubscriptionAndVisibility();
        }
        else if (hostVisibility != isHostVisible)
        {
            isHostVisible = hostVisibility;
            CheckAndUpdateVisibility();
        }
    }

    void VisiblityChangedHandler(object sender, VisibilityChangedEventArgs e)
    {
        HandleXamlRootChanged();
    }
    void XamlRootChangedHanlder(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        HandleXamlRootChanged();
    }
}
