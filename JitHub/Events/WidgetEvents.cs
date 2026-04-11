using JitHub.Models.Widgets;

namespace JitHub.Events;

internal sealed class WidgetEditEvent
{
    public bool Value
    {
        get;
    }
    public WidgetEditEvent(bool value)
    {
        Value = value;
    }
}

internal sealed class WidgetCreationEvent
{
    public WidgetData Value
    {

        get;
    }

    public WidgetCreationEvent(WidgetData value)
    {
        Value = value;
    }
}
