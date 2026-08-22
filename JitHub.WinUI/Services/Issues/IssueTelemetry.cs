using System;
using System.Collections.Generic;

namespace JitHub.Services;

public enum IssueActionKind
{
    Create,
    Edit,
    Metadata,
    ToggleState,
    Comment,
    CommentEdit,
    CommentDelete,
    CommentPin,
    CommentUnpin,
    CommentHide,
    CommentUnhide,
    QuoteReply,
    CopyLink,
    CopyMarkdown,
    Reaction,
    CommentReaction
}

public enum IssueActionOutcome
{
    Success,
    AuthenticationError,
    PermissionDenied,
    NetworkError,
    Cancelled,
    Failure
}

public static class IssueTelemetry
{
    public static void TrackOpened(ITelemetryService telemetryService) =>
        Track(telemetryService, "issues.opened", new Dictionary<string, string?>
        {
            ["page"] = "repo",
            ["source"] = TelemetryTaxonomy.Sources.Navigation
        });

    public static void TrackListLoaded(
        ITelemetryService telemetryService,
        CacheState cacheState,
        string result,
        TimeSpan duration) =>
        Track(telemetryService, "issues.list.loaded", new Dictionary<string, string?>
        {
            ["page"] = "repo",
            ["source"] = TelemetryTaxonomy.Sources.Query,
            ["cache_state"] = cacheState.ToString().ToLowerInvariant(),
            ["result"] = NormalizeResult(result),
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });

    public static void TrackSelected(ITelemetryService telemetryService) =>
        Track(telemetryService, "issues.selected", new Dictionary<string, string?>
        {
            ["page"] = "repo",
            ["source"] = TelemetryTaxonomy.Sources.List
        });

    public static void TrackPrefetchStarted(
        ITelemetryService telemetryService,
        IssuePrefetchReason reason,
        string page = "repo") =>
        Track(telemetryService, "issues.prefetch.started", new Dictionary<string, string?>
        {
            ["page"] = page,
            ["source"] = FormatPrefetchReason(reason)
        });

    public static void TrackPrefetchCompleted(
        ITelemetryService telemetryService,
        IssuePrefetchReason reason,
        IssuePrefetchResult result,
        TimeSpan duration,
        string page = "repo") =>
        Track(telemetryService, "issues.prefetch.completed", new Dictionary<string, string?>
        {
            ["page"] = page,
            ["source"] = FormatPrefetchReason(reason),
            ["result"] = result switch
            {
                IssuePrefetchResult.Success => TelemetryTaxonomy.Results.Success,
                IssuePrefetchResult.Cancelled => TelemetryTaxonomy.Results.Cancelled,
                IssuePrefetchResult.Failed => TelemetryTaxonomy.Results.Failed,
                _ => TelemetryTaxonomy.Results.Unavailable
            },
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });

    public static void TrackAction(
        ITelemetryService telemetryService,
        IssueActionKind action,
        IssueActionOutcome outcome) =>
        Track(telemetryService, "issues.action.executed", new Dictionary<string, string?>
        {
            ["page"] = "repo",
            ["action"] = action switch
            {
                IssueActionKind.Create => TelemetryTaxonomy.Actions.Create,
                IssueActionKind.Edit => TelemetryTaxonomy.Actions.Edit,
                IssueActionKind.Metadata => TelemetryTaxonomy.Actions.Metadata,
                IssueActionKind.ToggleState => TelemetryTaxonomy.Actions.ToggleState,
                IssueActionKind.Comment => TelemetryTaxonomy.Actions.Comment,
                IssueActionKind.CommentEdit => TelemetryTaxonomy.Actions.CommentEdit,
                IssueActionKind.CommentDelete => TelemetryTaxonomy.Actions.CommentDelete,
                IssueActionKind.CommentPin => TelemetryTaxonomy.Actions.CommentPin,
                IssueActionKind.CommentUnpin => TelemetryTaxonomy.Actions.CommentUnpin,
                IssueActionKind.CommentHide => TelemetryTaxonomy.Actions.CommentHide,
                IssueActionKind.CommentUnhide => TelemetryTaxonomy.Actions.CommentUnhide,
                IssueActionKind.QuoteReply => TelemetryTaxonomy.Actions.QuoteReply,
                IssueActionKind.CopyLink => TelemetryTaxonomy.Actions.CopyLink,
                IssueActionKind.CopyMarkdown => TelemetryTaxonomy.Actions.CopyMarkdown,
                IssueActionKind.Reaction => TelemetryTaxonomy.Actions.Reaction,
                IssueActionKind.CommentReaction => TelemetryTaxonomy.Actions.CommentReaction,
                _ => "unknown"
            },
            ["result"] = outcome switch
            {
                IssueActionOutcome.Success => TelemetryTaxonomy.Results.Success,
                IssueActionOutcome.AuthenticationError => TelemetryTaxonomy.Results.AuthError,
                IssueActionOutcome.PermissionDenied => TelemetryTaxonomy.Results.PermissionDenied,
                IssueActionOutcome.NetworkError => TelemetryTaxonomy.Results.NetworkError,
                IssueActionOutcome.Cancelled => TelemetryTaxonomy.Results.Cancelled,
                _ => TelemetryTaxonomy.Results.Error
            }
        });

    private static string FormatPrefetchReason(IssuePrefetchReason reason) => reason switch
    {
        IssuePrefetchReason.NavigationHandoff => TelemetryTaxonomy.Sources.NavigationHandoff,
        IssuePrefetchReason.Dwell => TelemetryTaxonomy.Sources.Dwell,
        IssuePrefetchReason.Hover => TelemetryTaxonomy.Sources.Hover,
        IssuePrefetchReason.Neighbor => TelemetryTaxonomy.Sources.Neighbor,
        _ => "unknown"
    };

    private static string NormalizeResult(string result) => result switch
    {
        "success" => TelemetryTaxonomy.Results.Success,
        "partial" => TelemetryTaxonomy.Results.Partial,
        "cancelled" => TelemetryTaxonomy.Results.Cancelled,
        "auth_error" => TelemetryTaxonomy.Results.AuthError,
        "network_error" => TelemetryTaxonomy.Results.NetworkError,
        _ => TelemetryTaxonomy.Results.Error
    };

    private static void Track(
        ITelemetryService telemetryService,
        string name,
        IReadOnlyDictionary<string, string?> properties)
    {
        try
        {
            SafeTelemetryService.Wrap(telemetryService).TrackEvent(name, properties);
        }
        catch
        {
            // Product behavior must never depend on best-effort telemetry.
        }
    }
}
