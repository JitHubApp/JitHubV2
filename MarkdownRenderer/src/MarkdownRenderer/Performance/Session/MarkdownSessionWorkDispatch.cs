using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Schedules session-owned layout work and image activations on their bounded
/// schedulers. Each accepted item holds one unit of session work and a
/// cancellation linked to the session lifetime until it completes.
/// </summary>
internal sealed class MarkdownSessionWorkDispatch
{
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly BoundedMarkdownLayoutScheduler _layoutWorkScheduler = new();
    private readonly BoundedMarkdownImageActivationScheduler _imageActivationScheduler = new();

    internal MarkdownSessionWorkDispatch(MarkdownPerformanceSessionLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    internal Task LayoutCompletion => _layoutWorkScheduler.Completion;

    internal Task ActivationCompletion => _imageActivationScheduler.Completion;

    /// <summary>Stops both schedulers from accepting work; layout first, as at session disposal.</summary>
    internal void Dispose()
    {
        _layoutWorkScheduler.Dispose();
        _imageActivationScheduler.Dispose();
    }

    internal void ReleaseLayoutResources() => _layoutWorkScheduler.ReleaseResources();

    internal Task<T> ScheduleLayoutWorkAsync<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken)
    {
        if (work is null)
            return Task.FromException<T>(new ArgumentNullException(nameof(work)));
        if (!_lifetime.TryRegisterWork())
            return CreateLayoutWorkFailure<T>(new OperationCanceledException(cancellationToken));

        CancellationTokenSource? linked = null;
        Task<T>? scheduled = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            scheduled = _layoutWorkScheduler.ScheduleAsync(priority, work, linked.Token);
        }
        catch (Exception exception)
        {
            linked?.Dispose();
            _lifetime.RetireWork();
            return CreateLayoutWorkFailure<T>(exception);
        }

        var completion = new LayoutWorkCompletion(_lifetime, linked);
        try
        {
            _ = scheduled.ContinueWith(
                static (_, state) => ((LayoutWorkCompletion)state!).Complete(),
                completion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch
        {
            // Retain the original await-and-retire behavior if completion
            // tracking itself cannot be attached.
            return AwaitLayoutWorkAndCompleteAsync(scheduled, completion);
        }

        return scheduled;
    }

    internal bool TryScheduleImageActivation(
        IMarkdownImageActivationDemand demand,
        Action startAsyncWork,
        Action onNotStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(startAsyncWork);
        ArgumentNullException.ThrowIfNull(onNotStarted);
        if (cancellationToken.IsCancellationRequested || !_lifetime.TryRegisterWork())
            return false;

        CancellationTokenSource? linked = null;
        ImageActivationCompletion? completion = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            ImageActivationCompletion workCompletion = new(_lifetime, linked, onNotStarted);
            completion = workCompletion;
            bool scheduled = _imageActivationScheduler.TrySchedule(
                demand,
                () =>
                {
                    try
                    {
                        startAsyncWork();
                    }
                    finally
                    {
                        workCompletion.Complete(started: true);
                    }
                },
                () => workCompletion.Complete(started: false),
                linked.Token);
            if (scheduled)
                return true;

            completion.Complete(started: false);
            return false;
        }
        catch (Exception exception)
        {
            if (completion is not null)
                completion.Complete(started: false);
            else
            {
                linked?.Dispose();
                _lifetime.RetireWork();
            }

            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] Image activation could not be scheduled: {exception.Message}");
            return false;
        }
    }

    private static Task<T> CreateLayoutWorkFailure<T>(Exception exception)
    {
        if (exception is not OperationCanceledException canceled)
            return Task.FromException<T>(exception);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.TrySetCanceled(canceled.CancellationToken);
        return completion.Task;
    }

    private static async Task<T> AwaitLayoutWorkAndCompleteAsync<T>(
        Task<T> scheduled,
        LayoutWorkCompletion completion)
    {
        try
        {
            return await scheduled.ConfigureAwait(false);
        }
        finally
        {
            completion.Complete();
        }
    }
}
