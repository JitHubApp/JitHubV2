using System;

namespace JitHub.Services.Markdown;

internal static class MarkdownRendererDisposalCommit
{
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
}
