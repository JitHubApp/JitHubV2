using System.Diagnostics;

namespace MarkdownRenderer.PerformanceHarness;

public static class MeasurementVisibilityReadinessWaiter
{
    public static Task<MeasurementVisibilitySample> WaitUntilQualifiedAsync(
        Func<MeasurementVisibilitySample?> captureSample,
        int timeoutMilliseconds = PerformanceMeasurementContract.ReleaseForegroundReadinessTimeoutMilliseconds,
        int pollIntervalMilliseconds = PerformanceMeasurementContract.ReleaseForegroundReadinessPollIntervalMilliseconds,
        CancellationToken cancellationToken = default)
        => WaitUntilQualifiedAsync(
            captureSample,
            timeoutMilliseconds,
            pollIntervalMilliseconds,
            cancellationToken,
            Stopwatch.GetTimestamp,
            static (delay, token) => Task.Delay(delay, token));

    // The clock and delay seam keeps policy tests deterministic under loaded
    // CI hosts. Production continues to use Stopwatch and cancellable Task.Delay
    // with the unchanged release timeout and poll interval.
    public static async Task<MeasurementVisibilitySample> WaitUntilQualifiedAsync(
        Func<MeasurementVisibilitySample?> captureSample,
        int timeoutMilliseconds,
        int pollIntervalMilliseconds,
        CancellationToken cancellationToken,
        Func<long> getTimestamp,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(captureSample);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pollIntervalMilliseconds);
        ArgumentNullException.ThrowIfNull(getTimestamp);
        ArgumentNullException.ThrowIfNull(delayAsync);

        long startedTimestamp = getTimestamp();
        TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(pollIntervalMilliseconds);
        int consecutiveQualifiedSamples = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MeasurementVisibilitySample? sample = captureSample();
            if (MeasurementVisibilityEvidenceValidator.IsQualifiedSample(sample))
            {
                consecutiveQualifiedSamples++;
                if (consecutiveQualifiedSamples >=
                    PerformanceMeasurementContract.ReleaseForegroundReadinessConsecutiveQualifiedSamples)
                {
                    return sample!;
                }
            }
            else
            {
                consecutiveQualifiedSamples = 0;
            }

            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(
                startedTimestamp,
                getTimestamp());
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Release measurement preflight timed out after {timeoutMilliseconds:N0} ms waiting for the benchmark window to be foreground, visible, unminimized, uncloaked, on-screen, and unobstructed.");
            }

            await delayAsync(
                remaining < pollInterval ? remaining : pollInterval,
                cancellationToken);
        }
    }
}
