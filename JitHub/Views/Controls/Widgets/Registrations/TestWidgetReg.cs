using JitHub.Models.Widgets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace JitHub.Views.Controls.Widgets.Registrations;

internal class TestWidgetReg : WidgetBase
{
    public string Type { get => WidgetType.TestOne; }

    public Widget Create()
    {
        var guid = Guid.NewGuid();
        return new Widget
        {
            ID = guid.ToString(),
            Type = Type,
            Name = "Test Widget",
            Size = WidgetSize.Small,
        };
    }

    public UIElement GetElement(string id)
    {
        return new TestWidget(id);
    }
}
