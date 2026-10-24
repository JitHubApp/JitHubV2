using System;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

internal static class MarkdownRendererDisposalCommit
{
    internal static Task Execute(
        Action detachAndDispose,
        Func<bool> disposalCompletedSuccessfully,
        Action retirementBegan,
        Func<Task> drainRetiredResources,
        Func<Task> releaseTracking)
    {
        ArgumentNullException.ThrowIfNull(detachAndDispose);
        ArgumentNullException.ThrowIfNull(disposalCompletedSuccessfully);
        ArgumentNullException.ThrowIfNull(retirementBegan);
        ArgumentNullException.ThrowIfNull(drainRetiredResources);
        ArgumentNullException.ThrowIfNull(releaseTracking);

        // Keep the existing synchronous disposal confirmation boundary, then
        // keep the borrower tracked until all resources retired by that dispose
        // have actually finished draining.
        detachAndDispose();
        if (!disposalCompletedSuccessfully())
            throw new InvalidOperationException("The Markdown renderer did not complete disposal.");

        retirementBegan();
        Task drain = drainRetiredResources()
            ?? throw new InvalidOperationException("The Markdown renderer returned no retirement drain task.");
        return ReleaseTrackingAfterDrainAsync(drain, releaseTracking);
    }

    internal static void Execute(
        Action detachAndDispose,
        Func<bool> disposalCompletedSuccessfully,
        Action releaseTracking)
    {
        ArgumentNullException.ThrowIfNull(detachAndDispose);
        ArgumentNullException.ThrowIfNull(disposalCompletedSuccessfully);
        ArgumentNullException.ThrowIfNull(releaseTracking);

        // A Dispose retry can be a no-op if the control latched its disposed
        // state before a teardown step threw. Only the control's completion
        // signal, not a normal return from Dispose, can retire shared services.
        detachAndDispose();
        if (!disposalCompletedSuccessfully())
            throw new InvalidOperationException("The Markdown renderer did not complete disposal.");
        releaseTracking();
    }

    private static async Task ReleaseTrackingAfterDrainAsync(
        Task drain,
        Func<Task> releaseTracking)
    {
        await drain.ConfigureAwait(false);
        Task release = releaseTracking()
            ?? throw new InvalidOperationException("The Markdown renderer returned no tracking-release task.");
        await release.ConfigureAwait(false);
    }
}
