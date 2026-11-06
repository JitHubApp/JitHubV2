using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;

namespace MarkdownRenderer;

/// <summary>
/// One admitted uncached parse shared by every waiter for the same source key.
/// Its admission flags and counters are guarded by the owning
/// <see cref="MarkdownParseCoordinator"/>'s gate; its cancellation source has
/// its own gate.
/// </summary>
internal sealed class MarkdownInFlightParse
{
    private readonly object _cancellationGate = new();
    private RetirableCancellationTokenSource? _cancellation = new();
    private Task _cancellationCallbacks = Task.CompletedTask;
    private Task _cancellationRetirement = Task.CompletedTask;

    internal MarkdownInFlightParse(MarkdownParseSourceKey key, long sourceBytes, long parseGeneration)
    {
        Key = key;
        SourceBytes = sourceBytes;
        ParseGeneration = parseGeneration;
        CancellationToken = _cancellation.Token;
    }

    internal readonly CancellationToken CancellationToken;

    internal bool IsAbandoned { get; set; }

    internal bool IsCompleted { get; set; }

    internal bool IsStarted { get; set; }

    internal readonly MarkdownParseSourceKey Key;

    internal readonly TaskCompletionSource<MarkdownDocument> Completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<MarkdownDocument> ParseTask => Completion.Task;

    internal readonly long ParseGeneration;

    internal LinkedListNode<MarkdownInFlightParse>? QueueNode { get; set; }

    internal string Source => Key.Source;

    internal readonly long SourceBytes;

    internal int SuccessfulWaiterCount { get; set; }

    internal int WaiterCount { get; set; }

    internal void RequestCancellation()
    {
        lock (_cancellationGate)
        {
            if (_cancellation is null || CancellationToken.IsCancellationRequested)
                return;

            _cancellationCallbacks =
                CancellationTokenSourceRetirement.RequestCancellation(_cancellation);
        }
    }

    /// <summary>
    /// Requests cancellation of each parse. With <paramref name="complete"/>,
    /// also completes it as canceled and retires its cancellation source; use
    /// that only for parses that never started.
    /// </summary>
    internal static void CancelAll(List<MarkdownInFlightParse>? operations, bool complete)
    {
        if (operations is null)
            return;

        foreach (MarkdownInFlightParse operation in operations)
        {
            CancellationToken token = operation.CancellationToken;
            operation.RequestCancellation();

            if (!complete)
                continue;

            operation.Completion.TrySetCanceled(token);
            _ = operation.RetireCancellation();
        }
    }

    internal Task RetireCancellation()
    {
        lock (_cancellationGate)
        {
            RetirableCancellationTokenSource? cancellation = _cancellation;
            if (cancellation is null)
                return _cancellationRetirement;

            _cancellation = null;
            _cancellationRetirement = CancellationTokenSourceRetirement.DisposeAfter(
                cancellation,
                _cancellationCallbacks);
            return _cancellationRetirement;
        }
    }
}
