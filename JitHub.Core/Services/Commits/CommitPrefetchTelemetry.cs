using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public static class CommitPrefetchTelemetry
{
    public static async Task<CommitPrefetchOutcome> RunAsync(
        ICommitNavigationCache navigationCache,
        ITelemetryService telemetryService,
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TrackStarted(telemetryService, reason);

        CommitPrefetchOutcome outcome;
        try
        {
            outcome = await navigationCache.PrefetchWithResultAsync(
                token,
                userPartition,
                owner,
                repositoryName,
                sha,
                reason,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = CommitPrefetchOutcome.Canceled;
        }
        catch
        {
            outcome = CommitPrefetchOutcome.Failure;
        }

        TrackCompleted(
            telemetryService,
            reason,
            outcome switch
            {
                CommitPrefetchOutcome.Success => "success",
                CommitPrefetchOutcome.Canceled => "cancelled",
                CommitPrefetchOutcome.Suppressed => "suppressed",
                _ => "failed"
            },
            TelemetrySanitizer.CreateDurationBucket(stopwatch.Elapsed));
        return outcome;
    }

    private static void TrackStarted(ITelemetryService telemetryService, CommitPrefetchReason reason)
    {
        try
        {
            SafeTelemetryService.Wrap(telemetryService).TrackEvent(
                "commits.prefetch.started",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["source"] = TelemetryTaxonomy.EnumValue(reason),
                    ["result"] = "started"
                });
        }
        catch
        {
            // Telemetry is best-effort and must never affect prefetch or navigation.
        }
    }

    private static void TrackCompleted(
        ITelemetryService telemetryService,
        CommitPrefetchReason reason,
        string result,
        string durationBucket)
    {
        try
        {
            SafeTelemetryService.Wrap(telemetryService).TrackEvent(
                "commits.prefetch.completed",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["source"] = TelemetryTaxonomy.EnumValue(reason),
                    ["result"] = result,
                    ["duration_bucket"] = durationBucket
                });
        }
        catch
        {
            // Telemetry is best-effort and must never affect prefetch or navigation.
        }
    }
}
