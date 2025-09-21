using JitHub.Models.Widgets;
using System.Collections.Generic;
using Windows.UI.Xaml;

namespace JitHub.Services;

internal interface IWidgetService
{
    UIElement Get(string id);
    ICollection<WidgetData> GetAll();
    WidgetData Create(string type);
    void Delete(string id);
    void Register(WidgetBase widget);
    ICollection<WidgetBase> GetAllRegs();
    void Initialize();
}
