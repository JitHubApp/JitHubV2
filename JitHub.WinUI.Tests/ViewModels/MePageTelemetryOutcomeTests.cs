using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class MePageTelemetryOutcomeTests
{
    [Theory]
    [InlineData("issues.list.loaded")]
    [InlineData("pull_requests.list.loaded")]
    public void CompleteLoad_EmitsOneTruthfulSuccessTrace(string eventName)
    {
        RecordingTelemetryService telemetry = new();

        using (IPerformanceTrace trace = telemetry.StartTrace(eventName))
        {
            trace.SetProperty(
                "result",
                MeListTelemetryOutcomePolicy.ForCompletedLoad(
                    identityRefreshFailed: false,
                    PagedDataCompleteness.Complete));
        }

        RecordedTelemetryTrace recordedTrace = Assert.Single(telemetry.Traces);
        Assert.Equal(eventName, recordedTrace.Name);
        Assert.Equal(TelemetryTaxonomy.Results.Success, recordedTrace.Properties["result"]);
    }

    [Theory]
    [InlineData(true, PagedDataCompleteness.Complete)]
    [InlineData(false, PagedDataCompleteness.Partial)]
    [InlineData(false, PagedDataCompleteness.ApiLimited)]
    public void PartialLoad_NeverReportsSuccess(
        bool identityRefreshFailed,
        PagedDataCompleteness completeness)
    {
        string result = MeListTelemetryOutcomePolicy.ForCompletedLoad(
            identityRefreshFailed,
            completeness);

        Assert.Equal(TelemetryTaxonomy.Results.Partial, result);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), "cancelled")]
    [InlineData(typeof(GitHubAuthenticationException), "auth_error")]
    [InlineData(typeof(HttpRequestException), "error")]
    [InlineData(typeof(InvalidOperationException), "error")]
    public void TerminalException_MapsToTruthfulResult(Type exceptionType, string expected)
    {
        Exception exception = exceptionType == typeof(GitHubAuthenticationException)
            ? new GitHubAuthenticationException("authentication failed")
            : (Exception)Activator.CreateInstance(exceptionType, "failure")!;

        Assert.Equal(expected, MeListTelemetryOutcomePolicy.ForException(exception));
    }
}
