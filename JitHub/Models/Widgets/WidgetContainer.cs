using System.Text.Json.Serialization;

namespace JitHub.Models.Widgets;

public class Widget
{
    public string Name { get; set; }
    public string Type { get; set; }
    public string ID { get; set; }
    public WidgetSize Size { get; set; }
    [JsonIgnore]
    public Widget Self => this;
}
