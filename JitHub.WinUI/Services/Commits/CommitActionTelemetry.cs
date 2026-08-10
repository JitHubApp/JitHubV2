using System.Collections.Generic;

namespace JitHub.Services;

public enum CommitActionKind
{
    Comment,
    CopySha,
    BrowseFiles
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
                        CommitActionKind.BrowseFiles => "browse_files",
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
