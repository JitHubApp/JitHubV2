using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Core;

namespace WebView2Ex.UI;

public partial class WebView2ExBasicMapping : WebView2Ex
{
    public CoreWebView2 CoreWebView2 => WebView2Runtime.CoreWebView2;
    public WebView2ExBasicMapping()
    {
        // Assuming we are on core window only
        SetWindow(CoreWindow.GetForCurrentThread());

        InitializeAsync();
    }

    // normal TaskCompletionSource does not exist in UWP
    readonly TaskCompletionSource<bool> WebView2RuntimeTCS = new();
    async void InitializeAsync()
    {
        // Assuming we create our own runtime
        WebView2Runtime = await WebView2Runtime.CreateAsync();
        WebView2Runtime.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
        WebView2RuntimeTCS.SetResult(true);
        CoreWebView2Initialized?.Invoke(this, new());
    }

    private void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        NavigationCompleted?.Invoke(sender, args);
    }

    public TypedEventHandler<WebView2ExBasicMapping, EventArgs> CoreWebView2Initialized;
    public IAsyncAction EnsureCoreWebView2Async() => WebView2RuntimeTCS.Task.AsAsyncAction();
    public event TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs> NavigationCompleted;
}
