using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

/// <summary>
/// Runs independent, safe image representations in order. Transport failures
/// are isolated to one representation, while document/navigation cancellation
/// stops the pipeline immediately.
/// </summary>
internal static partial class MarkdownImageFallbackPipeline
{
    internal static async Task<T?> FirstSuccessfulAsync<T>(
        Func<CancellationToken, Task<T?>> first,
        Func<CancellationToken, Task<T?>> second,
        Action<int, Exception> reportFailure,
        CancellationToken cancellationToken,
        Action<T>? disposeAbandonedResult = null,
        Action<Task>? retirementObserver = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(reportFailure);

        cancellationToken.ThrowIfCancellationRequested();
        CandidateCancellation? firstCancellation = null;
        CandidateCancellation? secondCancellation = null;
        Task<T?>? firstTask = null;
        Task<T?>? secondTask = null;
        Task<T?>? selected = null;
        TaskCompletionSource? retirement = null;
        try
        {
            retirement = RegisterRetirement(retirementObserver);
            firstCancellation = new CandidateCancellation(cancellationToken);
            firstTask = TryAsync(first, 0, reportFailure, firstCancellation.Token);
            T? result = await firstTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not null)
            {
                selected = firstTask;
                return result;
            }

            secondCancellation = new CandidateCancellation(cancellationToken);
            secondTask = TryAsync(second, 1, reportFailure, secondCancellation.Token);
            result = await secondTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            selected = secondTask;
            return result;
        }
        finally
        {
            firstCancellation?.RequestCancellation();
            secondCancellation?.RequestCancellation();
            Task firstRetirement = RetireCandidateAsync(firstTask ?? Task.FromResult<T?>(null), selected, disposeAbandonedResult,
                reportFailure, 0, firstCancellation);
            Task secondRetirement = secondCancellation is null ? Task.CompletedTask :
                RetireCandidateAsync(secondTask ?? Task.FromResult<T?>(null), selected, disposeAbandonedResult,
                    reportFailure, 1, secondCancellation);
            _ = CompleteRetirementAsync(retirement, firstRetirement, secondRetirement);
        }
    }

    /// <summary>
    /// Starts a second, independent representation only when the first is slow
    /// or fails. The first successful result wins; a failed hedge never hides a
    /// later successful primary. Losing requests are canceled through their
    /// waiter tokens, without canceling another caller's shared image fetch.
    /// </summary>
    internal static async Task<T?> FirstSuccessfulHedgedAsync<T>(
        Func<CancellationToken, Task<T?>> first,
        Func<CancellationToken, Task<T?>> second,
        Action<int, Exception> reportFailure,
        TimeSpan hedgeDelay,
        CancellationToken cancellationToken,
        Action<T>? disposeAbandonedResult = null,
        Action<Task>? retirementObserver = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (hedgeDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(hedgeDelay));

        cancellationToken.ThrowIfCancellationRequested();
        CandidateCancellation? firstCancellation = null;
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CandidateCancellation? secondCancellation = null;
        Task<T?>? firstTask = null;
        Task<T?>? secondTask = null;
        Task<T?>? selected = null;
        TaskCompletionSource? retirement = null;
        try
        {
            retirement = RegisterRetirement(retirementObserver);
            firstCancellation = new CandidateCancellation(cancellationToken);
            firstTask = TryAsync(first, 0, reportFailure, firstCancellation.Token);
            Task delay = Task.Delay(hedgeDelay, delayCancellation.Token);
            if (await Task.WhenAny(firstTask, delay).WaitAsync(cancellationToken)
                .ConfigureAwait(false) == firstTask)
            {
                T? primary = await firstTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (primary is not null)
                {
                    selected = firstTask;
                    return primary;
                }

                secondCancellation = new CandidateCancellation(cancellationToken);
                secondTask = TryAsync(
                    second, 1, reportFailure, secondCancellation.Token);
                T? fallback = await secondTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                selected = secondTask;
                return fallback;
            }

            cancellationToken.ThrowIfCancellationRequested();
            secondCancellation = new CandidateCancellation(cancellationToken);
            secondTask = TryAsync(
                second, 1, reportFailure, secondCancellation.Token);
            Task<T?> completed = await Task.WhenAny(firstTask, secondTask)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            T? result = await completed.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not null)
            {
                selected = completed;
                return result;
            }

            Task<T?> remaining = completed == firstTask ? secondTask : firstTask;
            result = await remaining.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            selected = remaining;
            return result;
        }
        finally
        {
            delayCancellation.Cancel();
            firstCancellation?.RequestCancellation();
            secondCancellation?.RequestCancellation();
            // The winning result must not wait for an unresponsive loser.
            // Keep both candidate sources alive until their registrations and
            // operations retire, observing any late failures in the background.
            Task firstRetirement = RetireCandidateAsync(firstTask ?? Task.FromResult<T?>(null), selected, disposeAbandonedResult,
                reportFailure, 0, firstCancellation);
            Task secondRetirement = secondCancellation is null ? Task.CompletedTask :
                RetireCandidateAsync(secondTask ?? Task.FromResult<T?>(null), selected, disposeAbandonedResult,
                    reportFailure, 1, secondCancellation);
            _ = CompleteRetirementAsync(retirement, firstRetirement, secondRetirement);
        }
    }

    private static TaskCompletionSource? RegisterRetirement(Action<Task>? observer)
    {
        if (observer is null)
            return null;
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Register a source-free lifetime before either producer starts.
            // A registration failure cannot leave untracked transport work.
            observer(retirement.Task);
            return retirement;
        }
        catch
        {
            retirement.TrySetResult();
            throw;
        }
    }

    private static async Task CompleteRetirementAsync(
        TaskCompletionSource? completion, Task first, Task second)
    {
        try
        {
            await Task.WhenAll(first, second).ConfigureAwait(false);
            completion?.TrySetResult();
        }
        catch (Exception exception)
        {
            completion?.TrySetException(exception);
            if (completion is not null)
                _ = completion.Task.Exception;
        }
    }

    private static async Task RetireCandidateAsync<T>(
        Task<T?> task,
        Task<T?>? selected,
        Action<T>? disposeAbandonedResult,
        Action<int, Exception> reportFailure,
        int attempt,
        CandidateCancellation? cancellation = null)
        where T : class
    {
        try
        {
            if (cancellation is not null)
            {
                try { await cancellation.StopRelayingAsync().ConfigureAwait(false); }
                catch (Exception) { /* Cancellation registration failures are observed. */ }
            }

            T? result;
            try { result = await task.ConfigureAwait(false); }
            catch (Exception) { return; /* Actual completion failures are observed. */ }
            if (task != selected && result is not null && disposeAbandonedResult is not null)
            {
                try { disposeAbandonedResult(result); }
                catch (Exception exception)
                {
                    // Cleanup diagnostics are observational: neither a cleanup
                    // nor a reporter failure may replace the chosen result.
                    try { reportFailure(attempt, exception); }
                    catch (Exception) { }
                }
            }
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private static async Task<T?> TryAsync<T>(
        Func<CancellationToken, Task<T?>> operation,
        int attempt,
        Action<int, Exception> reportFailure,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller canceled, or the other representation succeeded.
            return null;
        }
        catch (Exception exception)
        {
            // HttpClient uses OperationCanceledException for its own timeout.
            // When the caller's token remains live, that is a recoverable
            // transport failure rather than document cancellation.
            try { reportFailure(attempt, exception); }
            catch (Exception) { /* An observational sink cannot stop fallback. */ }
            return null;
        }
    }

    /// <summary>
    /// A linked CTS would run resolver registrations on the caller's Cancel()
    /// stack and lose its callback task when CancelAsync is called a second time.
    /// Relay cancellation explicitly, retaining its first task through retirement.
    /// </summary>
    private sealed partial class CandidateCancellation : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _registration;
        private Task? _callbacks;
        private bool _sealed;

        internal CandidateCancellation(CancellationToken caller)
        {
            Token = _source.Token;
            _registration = caller.UnsafeRegister(
                static state => ((CandidateCancellation)state!).RequestCancellation(), this);
        }

        internal CancellationToken Token { get; }

        internal void RequestCancellation()
        {
            lock (_gate)
            {
                if (_sealed || _callbacks is not null)
                    return;
                try { _callbacks = _source.CancelAsync(); }
                catch (Exception exception) { _callbacks = Task.FromException(exception); }
            }
        }

        internal async Task StopRelayingAsync()
        {
            Task callbacks;
            lock (_gate)
            {
                _sealed = true;
                callbacks = _callbacks ?? Task.CompletedTask;
            }
            await _registration.DisposeAsync().ConfigureAwait(false);
            await callbacks.ConfigureAwait(false);
        }

        public void Dispose() => _source.Dispose();
    }
}
