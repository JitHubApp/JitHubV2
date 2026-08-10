using System;
using System.Collections.Generic;
using System.Linq;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class IssueTelemetryTests
{
    [Fact]
    public void CanonicalEventsUseOnlyIdentifierFreeProperties()
    {
        RecordingTelemetryService telemetry = new();

        IssueTelemetry.TrackOpened(telemetry);
        IssueTelemetry.TrackListLoaded(telemetry, CacheState.Stale, "partial", TimeSpan.FromMilliseconds(180));
        IssueTelemetry.TrackSelected(telemetry);
        IssueTelemetry.TrackAction(telemetry, IssueActionKind.Metadata, IssueActionOutcome.PermissionDenied);
        IssueTelemetry.TrackPrefetchStarted(telemetry, IssuePrefetchReason.Hover);
        IssueTelemetry.TrackPrefetchCompleted(
            telemetry,
            IssuePrefetchReason.Hover,
            IssuePrefetchResult.Success,
            TimeSpan.FromMilliseconds(75));

        Assert.Equal(
            [
                "issues.opened",
                "issues.list.loaded",
                "issues.selected",
                "issues.action.executed",
                "issues.prefetch.started",
                "issues.prefetch.completed"
            ],
            telemetry.Events.Select(item => item.Name));
        Assert.All(telemetry.Events, item =>
        {
            Assert.Equal("repo", item.Properties["page"]);
            Assert.DoesNotContain(
                item.Properties.Keys,
                key => key is "repository" or "owner" or "user" or "issue" or "title" or "query" or "url");
        });
        Assert.Equal("permission_denied", telemetry.Events[3].Properties["result"]);
        Assert.Equal("hover", telemetry.Events[4].Properties["source"]);
        Assert.False(string.IsNullOrWhiteSpace(telemetry.Events[5].Properties["duration_bucket"]));
    }

    [Theory]
    [InlineData(IssueActionKind.Create, "create")]
    [InlineData(IssueActionKind.Edit, "edit")]
    [InlineData(IssueActionKind.Metadata, "metadata")]
    [InlineData(IssueActionKind.ToggleState, "toggle_state")]
    [InlineData(IssueActionKind.Comment, "comment")]
    [InlineData(IssueActionKind.Reaction, "reaction")]
    [InlineData(IssueActionKind.CommentReaction, "comment_reaction")]
    public void ActionsMapToStableTaxonomy(IssueActionKind action, string expected)
    {
        RecordingTelemetryService telemetry = new();

        IssueTelemetry.TrackAction(telemetry, action, IssueActionOutcome.Success);

        RecordedTelemetryEvent item = Assert.Single(telemetry.Events);
        Assert.Equal("issues.action.executed", item.Name);
        Assert.Equal(expected, item.Properties["action"]);
        Assert.Equal("success", item.Properties["result"]);
    }

    [Theory]
    [InlineData(IssueActionOutcome.Success, "success")]
    [InlineData(IssueActionOutcome.AuthenticationError, "auth_error")]
    [InlineData(IssueActionOutcome.PermissionDenied, "permission_denied")]
    [InlineData(IssueActionOutcome.NetworkError, "network_error")]
    [InlineData(IssueActionOutcome.Failure, "error")]
    public void ActionOutcomesSurviveSanitization(IssueActionOutcome outcome, string expected)
    {
        RecordingTelemetryService telemetry = new();

        IssueTelemetry.TrackAction(telemetry, IssueActionKind.Metadata, outcome);

        RecordedTelemetryEvent item = Assert.Single(telemetry.Events);
        IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(item.Properties);
        Assert.Equal(expected, sanitized["result"]);
    }

    [Theory]
    [InlineData(IssuePrefetchReason.NavigationHandoff, "navigation_handoff")]
    [InlineData(IssuePrefetchReason.Dwell, "dwell")]
    [InlineData(IssuePrefetchReason.Hover, "hover")]
    [InlineData(IssuePrefetchReason.Neighbor, "neighbor")]
    public void PrefetchReasonsUseCanonicalSanitizerSafeSources(
        IssuePrefetchReason reason,
        string expected)
    {
        RecordingTelemetryService telemetry = new();

        IssueTelemetry.TrackPrefetchStarted(telemetry, reason);

        RecordedTelemetryEvent item = Assert.Single(telemetry.Events);
        IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(item.Properties);
        Assert.Equal(expected, sanitized["source"]);
    }

    [Theory]
    [InlineData(IssuePrefetchResult.Success, "success")]
    [InlineData(IssuePrefetchResult.Cancelled, "cancelled")]
    [InlineData(IssuePrefetchResult.Unavailable, "unavailable")]
    [InlineData(IssuePrefetchResult.Failed, "failed")]
    public void PrefetchResultsUseCanonicalTerminalOutcomes(
        IssuePrefetchResult result,
        string expected)
    {
        RecordingTelemetryService telemetry = new();

        IssueTelemetry.TrackPrefetchCompleted(
            telemetry,
            IssuePrefetchReason.Neighbor,
            result,
            TimeSpan.FromMilliseconds(8));

        RecordedTelemetryEvent completed = Assert.Single(telemetry.Events);
        Assert.Equal(expected, completed.Properties["result"]);
    }

    [Fact]
    public void MyPageScheduledPrefetch_PreservesItsTelemetryOwner()
    {
        RecordingTelemetryService telemetry = new();

        IssueTelemetry.TrackPrefetchStarted(telemetry, IssuePrefetchReason.Dwell, "my");
        IssueTelemetry.TrackPrefetchCompleted(
            telemetry,
            IssuePrefetchReason.Dwell,
            IssuePrefetchResult.Unavailable,
            TimeSpan.Zero,
            "my");

        Assert.Collection(
            telemetry.Events,
            started => Assert.Equal("my", started.Properties["page"]),
            completed =>
            {
                Assert.Equal("my", completed.Properties["page"]);
                Assert.Equal(TelemetryTaxonomy.Results.Unavailable, completed.Properties["result"]);
            });
    }

    [Fact]
    public void ThrowingTelemetrySinkCannotAffectIssueBehavior()
    {
        ThrowingTelemetryService telemetry = new();

        IssueTelemetry.TrackOpened(telemetry);
        IssueTelemetry.TrackListLoaded(telemetry, CacheState.Fresh, "success", TimeSpan.Zero);
        IssueTelemetry.TrackSelected(telemetry);
        IssueTelemetry.TrackAction(telemetry, IssueActionKind.Comment, IssueActionOutcome.Success);
        IssueTelemetry.TrackPrefetchStarted(telemetry, IssuePrefetchReason.Dwell);
        IssueTelemetry.TrackPrefetchCompleted(
            telemetry,
            IssuePrefetchReason.Dwell,
            IssuePrefetchResult.Cancelled,
            TimeSpan.Zero);

        Assert.Equal(6, telemetry.Attempts);
    }

    private sealed class ThrowingTelemetryService : ITelemetryService
    {
        public int Attempts { get; private set; }

        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
            Attempts++;
            throw new InvalidOperationException("telemetry unavailable");
        }

        public void TrackMetric(
            string name,
            double value,
            IReadOnlyDictionary<string, string?>? properties = null)
            => throw new InvalidOperationException("telemetry unavailable");

        public IPerformanceTrace StartTrace(
            string name,
            IReadOnlyDictionary<string, string?>? properties = null)
            => throw new InvalidOperationException("telemetry unavailable");
    }
}
