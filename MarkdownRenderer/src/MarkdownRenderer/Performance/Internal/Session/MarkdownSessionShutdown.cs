using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarkdownRenderer.Performance.Internal.Session.ImageSources;

namespace MarkdownRenderer.Performance.Internal.Session;

/// <summary>
/// Finishes session shutdown after Dispose() has stopped new work. Each stage
/// is awaited in order and its failure is collected, so a later stage still
/// runs and the shutdown completion reports every failure.
/// </summary>
internal static class MarkdownSessionShutdown
{
    internal static async Task FinishAsync(
        Task cancellation,
        Task drain,
        TaskCompletionSource<bool> completed,
        MarkdownDeferredScenePlanRegistry scenePlans,
        MarkdownSessionWorkDispatch workDispatch,
        MarkdownSharedImageFetcher fetcher,
        MarkdownPerformanceSessionLifetime lifetime)
    {
        List<Exception>? failures = null;
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            // Parse/plan creation is included in the work drain above. Drain
            // after it so plans retired by the last in-flight parse are also
            // part of session shutdown, not only plans present at Dispose().
            await scenePlans.DrainRetirementsAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            await workDispatch.LayoutCompletion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            await workDispatch.ActivationCompletion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        try
        {
            workDispatch.ReleaseLayoutResources();
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }
        finally
        {
            fetcher.DisposeFetchSlots();
            lifetime.DisposeCancellation();
        }

        if (failures is null)
            completed.TrySetResult(true);
        else if (failures.Count == 1)
            completed.TrySetException(failures[0]);
        else
            completed.TrySetException(new AggregateException(failures));
    }
}
