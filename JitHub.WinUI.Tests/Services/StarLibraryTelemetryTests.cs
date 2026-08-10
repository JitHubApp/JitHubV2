using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class StarLibraryTelemetryTests
{
    [Fact]
    public void MissingToken_EmitsOpenedAuthenticationOutcome()
    {
        RecordingTelemetryService telemetry = new();

        StarLibraryTelemetry.TrackAuthenticationUnavailable(telemetry, TimeSpan.FromMilliseconds(3));

        RecordedTelemetryEvent opened = Assert.Single(telemetry.Events);
        Assert.Equal("stars.opened", opened.Name);
        Assert.Equal(TelemetryTaxonomy.Sources.Shell, opened.Properties["source"]);
        Assert.Equal(TelemetryTaxonomy.Results.AuthError, opened.Properties["result"]);
        Assert.Equal("miss", opened.Properties["cache_state"]);
    }
}
