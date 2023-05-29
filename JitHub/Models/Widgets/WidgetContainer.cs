using JitHub.Services;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace JitHub.Models.Widgets;

// Widget data created to be
// 1. Passed to widget control
// 2. Display on the dashboard
public class Widget
{
    public string Name { get; set; }
    public string Type { get; set; }
    public string ID { get; set; }
    public WidgetSize Size { get; set; }

    [JsonIgnore]
    public Widget Self => this;

    [JsonIgnore]
    public ICommand Delete { get; set; }
}
