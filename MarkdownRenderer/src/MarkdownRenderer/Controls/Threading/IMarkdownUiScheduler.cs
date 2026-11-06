using System;

namespace MarkdownRenderer.Controls.Threading;

/// <summary>
/// The UI thread a renderer's owners post work to and run timers on.
/// </summary>
/// <remarks>
/// The control's implementation wraps its <c>DispatcherQueue</c>; tests supply
/// one that runs posted work when they choose. Owners that hold a scheduler
/// treat a null one as a control without a dispatcher.
/// </remarks>
internal interface IMarkdownUiScheduler
{
    /// <summary>True when the calling thread is the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>
    /// Posts <paramref name="callback"/> to the UI thread. Returns false when
    /// the queue rejected it, for example during shutdown. It never runs the
    /// callback inline.
    /// </summary>
    bool TryEnqueue(MarkdownUiPriority priority, Action callback);

    /// <summary>Creates a stopped timer that ticks on the UI thread.</summary>
    IMarkdownUiTimer CreateTimer();
}
