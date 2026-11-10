using System.Collections.Generic;

namespace JitHub.Markdown.Telemetry;

/// <summary>
/// Receives the telemetry events a Markdown host emits. The app's telemetry
/// service implements it; a host given no sink emits nothing.
/// </summary>
public interface IMarkdownTelemetrySink
{
    /// <summary>Records one event.</summary>
    /// <param name="name">The event name.</param>
    /// <param name="properties">The event's identifier-free category properties, if any.</param>
    void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null);
}
