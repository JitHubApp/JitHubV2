using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public enum DiagnosticsShutdownStatus
{
    Drained,
    NoStore,
    TimedOut,
    Failed
}

public sealed record DiagnosticsShutdownResult(
    DiagnosticsShutdownStatus Status,
    TimeSpan Elapsed,
    string? Detail = null)
{
    public bool IsSuccess => Status is DiagnosticsShutdownStatus.Drained or DiagnosticsShutdownStatus.NoStore;
}

public static class DiagnosticsShutdownCoordinator
{
    public static async Task<DiagnosticsShutdownResult> DrainAsync(
        ILocalDiagnosticsStore? store,
        TimeSpan timeout,
        Action<DiagnosticsShutdownResult>? reportFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (store is null)
        {
            return new DiagnosticsShutdownResult(DiagnosticsShutdownStatus.NoStore, TimeSpan.Zero);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            // Do not cancel the store's drain when the close budget expires. The bounded close
            // path may continue, but ProcessExit gets one final chance to await the same task.
            await store.ShutdownAsync(CancellationToken.None)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return new DiagnosticsShutdownResult(DiagnosticsShutdownStatus.Drained, stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            DiagnosticsShutdownResult result = new(
                DiagnosticsShutdownStatus.TimedOut,
                stopwatch.Elapsed,
                $"Diagnostics did not drain within {timeout.TotalSeconds:0.###} seconds.");
            reportFailure?.Invoke(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticsShutdownResult result = new(
                DiagnosticsShutdownStatus.Failed,
                stopwatch.Elapsed,
                $"{exception.GetType().Name}: {exception.Message}");
            reportFailure?.Invoke(result);
            return result;
        }
    }
}
