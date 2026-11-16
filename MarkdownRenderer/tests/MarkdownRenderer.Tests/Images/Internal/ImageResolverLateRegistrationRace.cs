namespace MarkdownRenderer.Tests.Images.Internal;

/// <summary>
/// Drives the race where a resolver registers on its cancellation token only
/// after the deadline wrapper is already waiting: cancellation then completes
/// the pending result synchronously, at the same moment the wrapper decides to
/// abandon it. The abandoned-result disposer blocks until the test releases it.
/// </summary>
internal sealed class ImageResolverLateRegistrationRace(TimeSpan livenessTimeout) : IDisposable
{
    private CancellationTokenRegistration _registration;

    internal ManualResetEventSlim ReleaseDisposer { get; } = new();

    internal TaskCompletionSource<TrackedResult> Pending { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource AllowLateRegistration { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource RegistrationReady { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource DisposerEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<Task> ObservedCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TrackedResult Result { get; } = new();

    internal ValueTask<TrackedResult> ResolveWithLateCancellationRegistration(CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            await AllowLateRegistration.Task.ConfigureAwait(false);
            _registration = token.Register(
                static state =>
                {
                    var completion = ((TaskCompletionSource<TrackedResult> Pending, TrackedResult Result))state!;
                    completion.Pending.TrySetResult(completion.Result);
                },
                (Pending, Result));
            RegistrationReady.TrySetResult();
        });
        return new ValueTask<TrackedResult>(Pending.Task);
    }

    internal void DisposeAbandonedAfterRelease(TrackedResult abandoned)
    {
        DisposerEntered.TrySetResult();
        if (!ReleaseDisposer.Wait(livenessTimeout))
            throw new TimeoutException("The test did not release the disposer.");
        abandoned.Dispose();
    }

    internal void ObserveAbandonedCompletion(Task completion) => ObservedCompletion.TrySetResult(completion);

    internal void DisposeRegistration() => _registration.Dispose();

    public void Dispose() => ReleaseDisposer.Dispose();
}
