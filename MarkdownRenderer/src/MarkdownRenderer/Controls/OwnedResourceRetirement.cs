using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using static MarkdownRenderer.TaskFaultObservation;

namespace MarkdownRenderer.Controls;

/// <summary>Defers owned-resource disposal until all work using it has retired.</summary>
internal static class OwnedResourceRetirement
{
    /// <summary>
    /// Preserves every outstanding lifetime across repeated retirement
    /// boundaries. A later unload, reload, or provider replacement must not
    /// forget work that was already detached from the active task set.
    /// </summary>
    internal static Task CombineLifetimes(Task? previous, Task? current)
    {
        Task first = previous ?? Task.CompletedTask;
        Task second = current ?? Task.CompletedTask;
        if (first.IsCompletedSuccessfully)
            return second;
        if (second.IsCompletedSuccessfully)
            return first;
        if (ReferenceEquals(first, second))
            return first;

        Task combined = Task.WhenAll(first, second);
        ObserveFault(combined);
        return combined;
    }

    internal static Task DisposeAfter(
        IDisposable resource,
        Task? lifetime,
        Action<Exception>? failureObserver = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Task retirement = DisposeAfterCoreAsync(
            resource,
            lifetime ?? Task.CompletedTask,
            failureObserver);
        ObserveFault(retirement);
        return retirement;
    }

    /// <summary>
    /// Defers disposal until prior users retire, but unlike <see cref="DisposeAfter"/>
    /// propagates both work and disposal failures after always attempting the
    /// dispose. Use this for provider ownership barriers whose completion is a
    /// release-gate result, not merely best-effort cleanup.
    /// </summary>
    internal static Task DisposeAfterPropagating(
        IDisposable resource,
        Task? lifetime)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Task retirement = DisposeAfterPropagatingCoreAsync(
            resource,
            lifetime ?? Task.CompletedTask);
        ObserveFault(retirement);
        return retirement;
    }

    private static async Task DisposeAfterPropagatingCoreAsync(
        IDisposable resource,
        Task lifetime)
    {
        List<Exception>? failures = null;
        try
        {
            await lifetime.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>(2)).Add(exception);
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>(2)).Add(exception);
        }

        if (failures is null)
            return;
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private static async Task DisposeAfterCoreAsync(
        IDisposable resource,
        Task lifetime,
        Action<Exception>? failureObserver)
    {
        try
        {
            await lifetime.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ObserveFailure(failureObserver, exception);
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            ObserveFailure(failureObserver, exception);
        }
    }

    private static void ObserveFailure(
        Action<Exception>? failureObserver,
        Exception exception)
    {
        try { failureObserver?.Invoke(exception); }
        catch
        {
            // Retirement diagnostics are observational and must not strand or
            // fault a detached resource lifetime.
        }
    }

}
