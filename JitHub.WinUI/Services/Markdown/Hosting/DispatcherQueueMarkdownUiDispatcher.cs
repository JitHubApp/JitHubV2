using System;
using JitHub.Markdown.Runtime;
using Microsoft.UI.Dispatching;

namespace JitHub.Services.Markdown.Hosting;

/// <summary>
/// Adapts a WinUI <see cref="DispatcherQueue"/> to <see cref="IMarkdownUiDispatcher"/>.
/// </summary>
internal sealed class DispatcherQueueMarkdownUiDispatcher : IMarkdownUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    private DispatcherQueueMarkdownUiDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

    /// <summary>Wraps <paramref name="dispatcherQueue"/>, preserving an unavailable queue as <see langword="null"/>.</summary>
    /// <param name="dispatcherQueue">The host's dispatcher queue.</param>
    /// <returns>The adapter, or <see langword="null"/> when the host has no dispatcher.</returns>
    public static IMarkdownUiDispatcher? From(DispatcherQueue? dispatcherQueue) =>
        dispatcherQueue is null ? null : new DispatcherQueueMarkdownUiDispatcher(dispatcherQueue);

    public bool TryEnqueue(Action callback) => _dispatcherQueue.TryEnqueue(() => callback());
}
