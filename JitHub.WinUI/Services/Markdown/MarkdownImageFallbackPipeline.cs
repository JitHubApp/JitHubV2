using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services.Markdown;

/// <summary>
/// Runs independent, safe image representations in order. Transport failures
/// are isolated to one representation, while document/navigation cancellation
/// stops the pipeline immediately.
/// </summary>
internal static class MarkdownImageFallbackPipeline
{
    internal static async Task<T?> FirstSuccessfulAsync<T>(
        Func<CancellationToken, Task<T?>> first,
        Func<CancellationToken, Task<T?>> second,
        Action<int, Exception> reportFailure,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(reportFailure);

        T? result = await TryAsync(first, attempt: 0, reportFailure, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is not null)
            return result;

        result = await TryAsync(second, attempt: 1, reportFailure, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
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
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (hedgeDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(hedgeDelay));

        cancellationToken.ThrowIfCancellationRequested();
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? secondCancellation = null;
        Task<T?> firstTask = TryAsync(
            first, 0, reportFailure, firstCancellation.Token);
        Task<T?>? secondTask = null;
        try
        {
            Task delay = Task.Delay(hedgeDelay, delayCancellation.Token);
            if (await Task.WhenAny(firstTask, delay).ConfigureAwait(false) == firstTask)
            {
                T? primary = await firstTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (primary is not null)
                    return primary;

                secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                secondTask = TryAsync(
                    second, 1, reportFailure, secondCancellation.Token);
                return await secondTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            secondTask = TryAsync(
                second, 1, reportFailure, secondCancellation.Token);
            Task<T?> completed = await Task.WhenAny(firstTask, secondTask)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            T? result = await completed.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not null)
                return result;

            Task<T?> remaining = completed == firstTask ? secondTask : firstTask;
            return await remaining.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            delayCancellation.Cancel();
            firstCancellation.Cancel();
            secondCancellation?.Cancel();
            try
            {
                await firstTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A logging callback could itself fail after the winning
                // representation completed. The loser is still observed.
            }
            if (secondTask is not null)
            {
                try
                {
                    await secondTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Preserve the caller cancellation or winning result.
                }
            }
            secondCancellation?.Dispose();
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
            reportFailure(attempt, exception);
            return null;
        }
    }
}
