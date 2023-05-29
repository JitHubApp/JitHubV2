using JitHub.Models.Widgets;
using System;
using Windows.UI.Xaml;

namespace JitHub.Views.Controls.Widgets.Registrations;

internal class RepoSideBarReg : WidgetBase
{
    public string Type { get => WidgetType.RepoSideBar; }
    public const string Name = "Repositories";

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
        return new RepoSideBar(id);
    }

    public string GetName()
    {
        return Name;
    }
}
