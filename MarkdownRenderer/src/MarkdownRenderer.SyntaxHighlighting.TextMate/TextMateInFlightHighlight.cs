using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// One shared provider call and its waiters. Cancellation is requested off the
/// caller thread and its source is disposed only after the call completes.
/// </summary>
internal sealed class TextMateInFlightHighlight
{
    private readonly object _cancellationGate = new();
    private Task? _cancellationTask;
    private bool _disposeCancellationRequested;
    private bool _cancellationDisposed;

    public CancellationTokenSource WorkCancellation { get; } = new();

    public Task<TextMateGrammarHighlightResult?> Task { get; set; } = null!;

    public int WaiterCount { get; set; }

    public void RequestCancellation()
    {
        lock (_cancellationGate)
        {
            if (_disposeCancellationRequested || _cancellationDisposed || _cancellationTask is not null)
                return;

            _cancellationTask = WorkCancellation.CancelAsync();
            _ = _cancellationTask.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeCancellationAsync()
    {
        Task? cancellationTask;
        lock (_cancellationGate)
        {
            if (_cancellationDisposed)
                return;

            _disposeCancellationRequested = true;
            cancellationTask = _cancellationTask;
        }

        if (cancellationTask is not null)
        {
            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation is best-effort optional work. The fault is
                // observed here and must not replace the provider result.
            }
        }

        lock (_cancellationGate)
        {
            if (_cancellationDisposed)
                return;

            WorkCancellation.Dispose();
            _cancellationDisposed = true;
        }
    }
}
