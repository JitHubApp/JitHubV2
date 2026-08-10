using System;
using System.Diagnostics;
using System.Threading;

namespace JitHub.Services;

internal sealed class ScheduledPrefetchCompletion<TResult>
{
    private readonly Action<TResult, TimeSpan>? _completed;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _completionState;

    public ScheduledPrefetchCompletion(Action<TResult, TimeSpan>? completed)
    {
        _completed = completed;
    }

    public void Complete(TResult result)
    {
        if (Interlocked.Exchange(ref _completionState, 1) != 0)
        {
            return;
        }

        _stopwatch.Stop();
        try
        {
            _completed?.Invoke(result, _stopwatch.Elapsed);
        }
        catch
        {
            // Prefetch observers are best-effort and cannot affect cache work.
        }
    }
}
