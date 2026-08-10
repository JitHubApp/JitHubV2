using System.Collections.Generic;
using System.Linq;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CommitActionTelemetryTests
{
    public static IEnumerable<object[]> ActionCases()
    {
        yield return [CommitActionKind.Comment, "comment"];
        yield return [CommitActionKind.CopySha, "copy_sha"];
        yield return [CommitActionKind.BrowseFiles, "browse_files"];
    }

    [Theory]
    [MemberData(nameof(ActionCases))]
    public void Track_EmitsCanonicalIdentifierFreeAction(
        CommitActionKind action,
        string expectedAction)
    {
        RecordingTelemetryService telemetry = new();

        CommitActionTelemetry.Track(telemetry, action, CommitActionOutcome.Success);

        RecordedTelemetryEvent recorded = Assert.Single(telemetry.Events);
        Assert.Equal("commits.action.executed", recorded.Name);
        Assert.Equal("repo", recorded.Properties["page"]);
        Assert.Equal(expectedAction, recorded.Properties["action"]);
        Assert.Equal("success", recorded.Properties["result"]);
        Assert.DoesNotContain(
            recorded.Properties.Keys,
            key => key is "repository" or "owner" or "sha" or "path" or "title");
    }

    [Theory]
    [InlineData(CommitActionOutcome.AuthenticationError, "auth_error")]
    [InlineData(CommitActionOutcome.Failure, "error")]
    public void Track_MapsFailureOutcomes(CommitActionOutcome outcome, string expectedResult)
    {
        RecordingTelemetryService telemetry = new();

        CommitActionTelemetry.Track(telemetry, CommitActionKind.Comment, outcome);

        Assert.Equal(expectedResult, Assert.Single(telemetry.Events).Properties["result"]);
    }
}
