using JitHub.Models.Widgets;
using System.Collections.Generic;
using Windows.UI.Xaml;

namespace JitHub.Services;

internal interface IWidgetService
{
    UIElement Get(string id);
    ICollection<Widget> GetAll();
    Widget Create(string type);
    void Register(WidgetBase widget);
    void Initialize();
}
