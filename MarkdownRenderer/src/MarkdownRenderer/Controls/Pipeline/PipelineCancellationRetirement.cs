using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Retires the cancellation sources of rebuild, image and lazy layout work:
/// a source is cancelled at once and disposed only after the work it scopes
/// has drained, so late callbacks never observe a disposed token.
/// </summary>
internal sealed class PipelineCancellationRetirement
{
    private readonly SnapshotRetirementTracker _retirements;

    internal PipelineCancellationRetirement(SnapshotRetirementTracker retirements) =>
        _retirements = retirements;

    /// <summary>
    /// Cancels <paramref name="cts"/> and disposes it once both its
    /// cancellation callbacks and <paramref name="lifetime"/> finish, tracking
    /// each task with the snapshot retirements. When
    /// <paramref name="cancellationStartTimestamp"/> is set, reports how long
    /// the superseded pipeline <paramref name="supersededGeneration"/> took to stop.
    /// </summary>
    internal Task Retire(
        RetirableCancellationTokenSource? cts,
        Task? lifetime,
        long supersededGeneration = 0,
        long cancellationStartTimestamp = 0)
    {
        if (lifetime is not null)
            _retirements.Track(lifetime);

        Task retirement;
        if (cts is null)
        {
            retirement = lifetime ?? Task.CompletedTask;
        }
        else
        {
            // Retain the work and cancellation-callback tasks independently:
            // CancelAndDisposeAfter intentionally suppresses their failures so
            // it can always dispose the source.
            Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(cts);
            _retirements.Track(callbacks);
            retirement = CancellationTokenSourceRetirement.DisposeAfter(
                cts,
                callbacks,
                lifetime);
            _retirements.Track(retirement);
        }

        var performance = MarkdownPerformanceEventSource.Log;
        if (cancellationStartTimestamp != 0 &&
            (lifetime is null || lifetime.IsCompleted))
        {
            performance.SupersededPipelineStopped(
                supersededGeneration,
                Stopwatch.GetTimestamp() - cancellationStartTimestamp);
        }
        else if (cancellationStartTimestamp != 0 && lifetime is not null)
        {
            _ = lifetime.ContinueWith(
                static (_, state) =>
                {
                    var observation = (PipelineCancellationObservation)state!;
                    MarkdownPerformanceEventSource.Log.SupersededPipelineStopped(
                        observation.Generation,
                        Stopwatch.GetTimestamp() - observation.CancellationStartTimestamp);
                },
                new PipelineCancellationObservation(
                    supersededGeneration,
                    cancellationStartTimestamp),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return retirement;
    }
}
