using System;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Threading;

/// <summary>
/// The production <see cref="IMarkdownUiScheduler"/>: the control's
/// <see cref="DispatcherQueue"/>. It holds only the queue, so a callback or
/// timer that captures it never keeps the control alive.
/// </summary>
internal sealed class DispatcherQueueUiScheduler : IMarkdownUiScheduler
{
    private readonly DispatcherQueue _queue;

    private DispatcherQueueUiScheduler(DispatcherQueue queue)
    {
        _queue = queue;
    }

    /// <summary>The scheduler for <paramref name="queue"/>, or null when there is no queue.</summary>
    internal static IMarkdownUiScheduler? For(DispatcherQueue? queue) =>
        queue is null ? null : new DispatcherQueueUiScheduler(queue);

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public bool TryEnqueue(MarkdownUiPriority priority, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _queue.TryEnqueue(ToDispatcherPriority(priority), callback.Invoke);
    }

    public IMarkdownUiTimer CreateTimer() => new DispatcherQueueUiTimer(_queue.CreateTimer());

    private static DispatcherQueuePriority ToDispatcherPriority(MarkdownUiPriority priority) => priority switch
    {
        MarkdownUiPriority.Low => DispatcherQueuePriority.Low,
        MarkdownUiPriority.High => DispatcherQueuePriority.High,
        _ => DispatcherQueuePriority.Normal,
    };
}
