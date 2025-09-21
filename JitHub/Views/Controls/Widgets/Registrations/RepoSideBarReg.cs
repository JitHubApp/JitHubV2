using JitHub.Models.Widgets;
using System;
using Windows.UI.Xaml;

namespace JitHub.Views.Controls.Widgets.Registrations;

internal class RepoSideBarReg : WidgetBase
{
    public string Type => WidgetType.RepoSideBar;
    public const string Name = "Repositories";

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
        return new RepoSideBar(id);
    }

    public string GetName()
    {
        return Name;
    }
}
