using JitHub.Models.Widgets;
using System;
using Windows.UI.Xaml;

namespace JitHub.Views.Controls.Widgets.Registrations;

internal class ActivityListReg : WidgetBase
{
    public string Type => WidgetType.ActivityList;
    public const string Name = "Activities";

    public WidgetData Create()
    {
        var guid = Guid.NewGuid();
        return new WidgetData
        {
            ID = guid.ToString(),
            Type = Type,
            Name = Name,
            Size = WidgetSize.Small,
        };
    }

    public UIElement GetElement(string id)
    {
        return new ActivityList(id);
    }

    public string GetName()
    {
        return Name;
    }
}
