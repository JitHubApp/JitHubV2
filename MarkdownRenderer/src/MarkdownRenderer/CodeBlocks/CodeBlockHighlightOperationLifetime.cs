using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Registration and freshness policy for a control-owned code-highlighting
/// operation. This type is intentionally UI-neutral so its reentrancy and
/// retirement behavior can be verified without constructing a WinUI control.
/// </summary>
internal static class CodeBlockHighlightOperationLifetime
{
    /// <summary>
    /// Copies the token before invoking provider code. A concurrently retired
    /// cancellation source can no longer be queried safely, while the copied
    /// token remains valid after its source is disposed.
    /// </summary>
    internal static bool TryCaptureToken(
        CancellationTokenSource? source,
        out CancellationToken token)
    {
        token = default;
        if (source is null)
            return false;

        try
        {
            token = source.Token;
            return !token.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reserves the operation in its owner's task set before invoking provider
    /// code, which may reenter the owner synchronously. The reserved task stays
    /// pending until the actual operation has completed, even if it completes
    /// faulted or canceled.
    /// </summary>
    internal static void StartAndTrack(Action<Task> track, Func<Task> start)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(start);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            track(completion.Task);
        }
        catch
        {
            // A partially completed registration must not leave an owner-held
            // reservation pending when no operation can be started.
            completion.TrySetResult();
            throw;
        }

        Task operation;
        try
        {
            operation = start() ?? throw new InvalidOperationException(
                "A code-highlighting operation returned a null task.");
        }
        catch
        {
            // No operation task was returned, so there is no work left for
            // retirement to await. Preserve the original exception for the
            // scheduler's diagnostics path.
            completion.TrySetResult();
            throw;
        }

        _ = operation.ContinueWith(
            static (completed, state) =>
            {
                // The reservation represents lifetime only. Observe faults
                // here, but don't turn cancellation/failure into an ownership
                // failure or make callers await the provider result twice.
                if (completed.IsFaulted)
                    _ = completed.Exception;

                ((TaskCompletionSource)state!).TrySetResult();
            },
            completion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Revalidates a scheduling snapshot after a synchronous provider callback
    /// without querying its potentially reentrant Revision property again.
    /// </summary>
    internal static bool IsCurrentAfterProviderCallback(
        ICodeHighlighter? currentProvider,
        ICodeHighlighter requestProvider,
        int currentGeneration,
        int requestGeneration,
        bool isDisposed,
        bool isUnloaded,
        CancellationToken operationToken)
        => !isDisposed &&
           !isUnloaded &&
           !operationToken.IsCancellationRequested &&
           currentGeneration == requestGeneration &&
           ReferenceEquals(currentProvider, requestProvider);

    /// <summary>
    /// Applies the provider revision result only if the callback did not
    /// reenter and retire or replace the operation while reading it.
    /// </summary>
    internal static bool IsCurrentAfterRevisionCallback(
        ICodeHighlighter? currentProvider,
        ICodeHighlighter requestProvider,
        int currentGeneration,
        int requestGeneration,
        bool isDisposed,
        bool isUnloaded,
        CancellationToken operationToken,
        int observedRevision,
        int requestRevision)
        => observedRevision == requestRevision &&
           IsCurrentAfterProviderCallback(
               currentProvider,
               requestProvider,
               currentGeneration,
               requestGeneration,
               isDisposed,
               isUnloaded,
               operationToken);
}
