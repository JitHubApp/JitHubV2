using Windows.UI.Xaml;

namespace JitHub.Models.Widgets;

internal interface WidgetBase
{
    string Type { get; }
    UIElement GetElement(string id);
    Widget Create();
}
