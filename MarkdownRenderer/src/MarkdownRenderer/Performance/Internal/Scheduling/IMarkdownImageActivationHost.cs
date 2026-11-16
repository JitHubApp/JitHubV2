namespace MarkdownRenderer.Performance.Internal.Scheduling;

/// <summary>
/// The scheduler side an activation calls back into. Both calls run
/// asynchronously (demand promotion, token cancellation) and take the
/// scheduler's gate themselves.
/// </summary>
internal interface IMarkdownImageActivationHost
{
    /// <summary>Moves a still-queued overscan activation whose demand became visible.</summary>
    void PromoteQueuedActivation(MarkdownImageActivation activation);

    /// <summary>Removes a queued or waiting activation and retires it as not started.</summary>
    void CancelQueued(MarkdownImageActivation activation);
}
