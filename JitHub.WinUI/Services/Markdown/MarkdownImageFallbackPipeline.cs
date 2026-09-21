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
        return result ?? await TryAsync(second, attempt: 1, reportFailure, cancellationToken)
            .ConfigureAwait(false);
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
            throw;
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
