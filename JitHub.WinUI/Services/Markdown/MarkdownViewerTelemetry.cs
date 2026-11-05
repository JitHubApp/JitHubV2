using System;
using System.Collections.Generic;
using JitHub.Markdown;

namespace JitHub.Services.Markdown;

/// <summary>
/// Emits a Markdown host's telemetry events with the shared property schema.
/// </summary>
internal sealed class MarkdownViewerTelemetry
{
    private readonly ITelemetryService? _telemetryService;
    private readonly Func<string?> _getHostKind;

    /// <param name="telemetryService">The telemetry sink, or <see langword="null"/> to emit nothing.</param>
    /// <param name="getHostKind">Reads the host kind at the time of each event.</param>
    public MarkdownViewerTelemetry(ITelemetryService? telemetryService, Func<string?> getHostKind)
    {
        _telemetryService = telemetryService;
        _getHostKind = getHostKind ?? throw new ArgumentNullException(nameof(getHostKind));
    }

    /// <summary>Records one Markdown event.</summary>
    /// <param name="eventName">The telemetry event name.</param>
    /// <param name="action">The user or system action, if any.</param>
    /// <param name="result">The outcome.</param>
    /// <param name="resource">The resource the event concerns, if any.</param>
    /// <param name="policy">The policy that shaped the outcome, if any.</param>
    /// <param name="errorKind">The error category, if any.</param>
    /// <param name="source">What initiated the event.</param>
    public void TrackMarkdownEvent(
        string eventName,
        string? action,
        string result,
        string? resource = null,
        string? policy = null,
        string? errorKind = null,
        string source = TelemetryTaxonomy.Sources.User)
    {
        if (_telemetryService is null)
        {
            return;
        }

        var properties = new Dictionary<string, string?>
        {
            ["feature"] = "markdown",
            ["section"] = MarkdownHostContract.GetTelemetrySection(_getHostKind()),
            ["source"] = source,
            ["action"] = action,
            ["result"] = result,
            ["resource"] = resource,
            ["policy"] = policy,
            ["error_kind"] = errorKind,
        };
        _telemetryService.TrackEvent(eventName, properties);
    }
}
