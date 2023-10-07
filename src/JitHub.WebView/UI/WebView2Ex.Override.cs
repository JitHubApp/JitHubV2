// Original: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/WebView2/WebView2.cpp
#nullable enable
using Windows.Foundation;
using Windows.UI.Xaml;
using Size = Windows.Foundation.Size;
using Point = Windows.Foundation.Point;


namespace WebView2Ex.UI;

partial class WebView2Ex
{
    protected override Size MeasureOverride(Size availableSize)
    {
        return base.MeasureOverride(availableSize);
    }

    // We could have a child Grid (see AddChildPanel) or a child TextBlock (see CreateMissingAnaheimWarning).
    // Make sure it is visited by the Arrange pass.
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Content is FrameworkElement child)
        {
            child.Arrange(new Rect(new Point(0, 0), finalSize));
            return finalSize;
        }

        return base.ArrangeOverride(finalSize);
    }
}
