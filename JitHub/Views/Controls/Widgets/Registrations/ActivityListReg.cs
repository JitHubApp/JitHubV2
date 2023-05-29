using JitHub.Models.Widgets;
using System;
using Windows.UI.Xaml;

namespace JitHub.Views.Controls.Widgets.Registrations;

internal class ActivityListReg : WidgetBase
{
    public string Type { get => WidgetType.ActivityList; }
    public const string Name = "Activities";

    public Widget Create()
    {
        var guid = Guid.NewGuid();
        return new Widget
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
