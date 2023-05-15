using System.Numerics;
using Windows.UI.Xaml;

namespace WebView2Ex.UI;

partial class WebView2Ex
{
	void HandleRendered(object sender, object args)
	{
		if (CoreWebView2 is not null)
		{
			// Check if the position of the WebView inside the app has changed
			CheckAndUpdateWebViewPosition();
			// Check if the position of the window itself has changed
			CheckAndUpdateWindowPosition();
			// Check if the visibility property of a parent element has changed
			CheckAndUpdateVisibility();
		}
	}

	void HandleSizeChanged(object sender, SizeChangedEventArgs args)
	{
		SetCoreWebViewAndVisualSize((float)args.NewSize.Width, (float)args.NewSize.Height);
	}
}
