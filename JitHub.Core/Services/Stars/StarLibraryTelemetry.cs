using System;
using System.Collections.Generic;

namespace JitHub.Services;

public static class StarLibraryTelemetry
{
    public static void TrackOpened(
        ITelemetryService telemetry,
        string result,
        string cacheState,
        TimeSpan duration) =>
        SafeTelemetryService.Wrap(telemetry).TrackEvent(
            "stars.opened",
            new Dictionary<string, string?>
            {
                ["source"] = TelemetryTaxonomy.Sources.Shell,
                ["cache_state"] = cacheState,
                ["result"] = result,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });

    public static void TrackAuthenticationUnavailable(
        ITelemetryService telemetry,
        TimeSpan duration) =>
        TrackOpened(telemetry, TelemetryTaxonomy.Results.AuthError, "miss", duration);
}
