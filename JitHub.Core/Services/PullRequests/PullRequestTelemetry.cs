using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace JitHub.Services;

public static class PullRequestTelemetry
{
    public static void TrackOpened(
        ITelemetryService telemetry,
        string page,
        string source) =>
        TrackSafely(
            telemetry,
            "pull_requests.opened",
            new Dictionary<string, string?>
            {
                ["page"] = page,
                ["source"] = source
            });

    public static void TrackListLoaded(
        ITelemetryService telemetry,
        string page,
        string result,
        TimeSpan duration,
        CacheState? cacheState = null,
        int? count = null,
        string? errorKind = null) =>
        TrackSafely(
            telemetry,
            "pull_requests.list.loaded",
            new Dictionary<string, string?>
            {
                ["page"] = page,
                ["result"] = result,
                ["cache_state"] = cacheState?.ToString().ToLowerInvariant(),
                ["count_bucket"] = count is null ? null : TelemetryTaxonomy.CountBucket(count.Value),
                ["error_kind"] = errorKind,
                ["duration_bucket"] = BucketDuration(duration)
            });

    public static void TrackAction(
        ITelemetryService telemetry,
        string action,
        string result) =>
        TrackSafely(
            telemetry,
            "pull_requests.action.executed",
            new Dictionary<string, string?>
            {
                ["page"] = "repo",
                ["action"] = action,
                ["result"] = result
            });

    public static void TrackPrefetchStarted(
        ITelemetryService telemetry,
        PullRequestPrefetchReason reason,
        string page = "repo") =>
        TrackSafely(
            telemetry,
            "pull_requests.prefetch.started",
            new Dictionary<string, string?>
            {
                ["page"] = page,
                ["source"] = NormalizeReason(reason)
            });

    public static async Task ObservePrefetchAsync(
        ITelemetryService telemetry,
        PullRequestPrefetchReason reason,
        Func<Task> startPrefetch)
    {
        ArgumentNullException.ThrowIfNull(startPrefetch);
        TrackPrefetchStarted(telemetry, reason);
        Stopwatch stopwatch = Stopwatch.StartNew();
        string result = TelemetryTaxonomy.Results.Success;
        try
        {
            await startPrefetch().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = TelemetryTaxonomy.Results.Cancelled;
        }
        catch
        {
            result = TelemetryTaxonomy.Results.Failed;
        }
        finally
        {
            stopwatch.Stop();
            TrackPrefetchCompleted(telemetry, reason, result, stopwatch.Elapsed);
        }
    }

    public static async Task ObservePrefetchAsync(
        ITelemetryService telemetry,
        PullRequestPrefetchReason reason,
        Func<Task<PullRequestPrefetchResult>> startPrefetch)
    {
        ArgumentNullException.ThrowIfNull(startPrefetch);
        TrackPrefetchStarted(telemetry, reason);
        Stopwatch stopwatch = Stopwatch.StartNew();
        string result;
        try
        {
            result = NormalizePrefetchResult(await startPrefetch().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            result = TelemetryTaxonomy.Results.Cancelled;
        }
        catch
        {
            result = TelemetryTaxonomy.Results.Failed;
        }
        finally
        {
            stopwatch.Stop();
        }

        TrackPrefetchCompleted(telemetry, reason, result, stopwatch.Elapsed);
    }

    public static void TrackPrefetchCompleted(
        ITelemetryService telemetry,
        PullRequestPrefetchReason reason,
        string result,
        TimeSpan duration,
        string page = "repo") =>
        TrackSafely(
            telemetry,
            "pull_requests.prefetch.completed",
            new Dictionary<string, string?>
            {
                ["page"] = page,
                ["source"] = NormalizeReason(reason),
                ["result"] = result,
                ["duration_bucket"] = BucketDuration(duration)
            });

    public static void TrackPrefetchCompleted(
        ITelemetryService telemetry,
        PullRequestPrefetchReason reason,
        PullRequestPrefetchResult result,
        TimeSpan duration,
        string page = "repo") =>
        TrackPrefetchCompleted(
            telemetry,
            reason,
            NormalizePrefetchResult(result),
            duration,
            page);

    public static void TrackSafely(
        ITelemetryService telemetry,
        string name,
        IReadOnlyDictionary<string, string?>? properties = null)
    {
        try
        {
            SafeTelemetryService.Wrap(telemetry).TrackEvent(name, properties);
        }
        catch
        {
            // Telemetry must never alter a completed user action or background task.
        }
    }

    private static string NormalizeReason(PullRequestPrefetchReason reason) => reason switch
    {
        PullRequestPrefetchReason.NavigationHandoff => TelemetryTaxonomy.Sources.NavigationHandoff,
        PullRequestPrefetchReason.Dwell => TelemetryTaxonomy.Sources.Dwell,
        PullRequestPrefetchReason.Hover => TelemetryTaxonomy.Sources.Hover,
        PullRequestPrefetchReason.Neighbor => TelemetryTaxonomy.Sources.Neighbor,
        _ => "unknown"
    };

    private static string NormalizePrefetchResult(PullRequestPrefetchResult result) => result switch
    {
        PullRequestPrefetchResult.Success => TelemetryTaxonomy.Results.Success,
        PullRequestPrefetchResult.Cancelled => TelemetryTaxonomy.Results.Cancelled,
        PullRequestPrefetchResult.Failed => TelemetryTaxonomy.Results.Failed,
        _ => TelemetryTaxonomy.Results.Unavailable
    };

    private static string BucketDuration(TimeSpan duration) =>
        TelemetrySanitizer.CreateDurationBucket(duration);
}
