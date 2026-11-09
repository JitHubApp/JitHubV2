using System.Collections.Generic;

namespace JitHub.Services;

public enum CommitActionKind
{
    Comment,
    CopySha,
    CopyDiff,
    CopyPath,
    BrowseFiles,
    ShowSearchTools,
    HideSearchTools,
    ShowFilters,
    ToggleFileNavigator,
    CollapseDiffFile,
    ExpandDiffFile
}

public enum CommitActionOutcome
{
    Success,
    AuthenticationError,
    Failure
}

public static class CommitActionTelemetry
{
    public static void Track(
        ITelemetryService telemetryService,
        CommitActionKind action,
        CommitActionOutcome outcome)
    {
        try
        {
            SafeTelemetryService.Wrap(telemetryService).TrackEvent(
                    "commits.action.executed",
                    new Dictionary<string, string?>
                    {
                        ["page"] = "repo",
                        ["action"] = action switch
                        {
                            CommitActionKind.Comment => "comment",
                            CommitActionKind.CopySha => "copy_sha",
                            CommitActionKind.CopyDiff => TelemetryTaxonomy.Actions.CopyDiff,
                            CommitActionKind.CopyPath => TelemetryTaxonomy.Actions.CopyPath,
                            CommitActionKind.BrowseFiles => "browse_files",
                            CommitActionKind.ShowSearchTools => "show_search_tools",
                            CommitActionKind.HideSearchTools => "hide_search_tools",
                            CommitActionKind.ShowFilters => "show_filters",
                            CommitActionKind.ToggleFileNavigator => "toggle_file_navigator",
                            CommitActionKind.CollapseDiffFile => "collapse_diff_file",
                            CommitActionKind.ExpandDiffFile => "expand_diff_file",
                            _ => "unknown"
                        },
                        ["result"] = outcome switch
                        {
                            CommitActionOutcome.Success => "success",
                            CommitActionOutcome.AuthenticationError => "auth_error",
                            _ => "error"
                        }
                    });
        }
        catch
        {
            // Telemetry is best-effort and must never affect commit actions.
        }
    }
}
