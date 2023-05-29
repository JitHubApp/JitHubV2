using Windows.UI.Xaml;

namespace JitHub.Models.Widgets;

public interface WidgetBase
{
    string Type { get; }
    UIElement GetElement(string id);
    string GetName();
    Widget Create();
}
