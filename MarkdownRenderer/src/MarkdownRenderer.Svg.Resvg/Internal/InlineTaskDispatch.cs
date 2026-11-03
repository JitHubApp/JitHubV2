namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Runs an asynchronous delegate on the current pool thread and reports its
/// failure through the returned task, as <see cref="Task.Run(Func{Task}, CancellationToken)"/>
/// does.
/// </summary>
internal static class InlineTaskDispatch
{
    /// <summary>
    /// Invokes <paramref name="operation"/> inline. Cancellation by
    /// <paramref name="cancellationToken"/> yields a canceled task, and any other
    /// exception a faulted one, so a caller never sees a synchronous throw that
    /// depends on which thread it called from.
    /// </summary>
    /// <typeparam name="TResult">The result type of the operation.</typeparam>
    /// <param name="operation">The delegate to run on the current thread.</param>
    /// <param name="cancellationToken">The token whose cancellation maps to a canceled task.</param>
    /// <returns>The operation's task, or a canceled or faulted task if it threw.</returns>
    public static Task<TResult> Invoke<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return operation();
        }
        catch (OperationCanceledException exception) when (
            exception.CancellationToken == cancellationToken &&
            cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TResult>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<TResult>(exception);
        }
    }
}
