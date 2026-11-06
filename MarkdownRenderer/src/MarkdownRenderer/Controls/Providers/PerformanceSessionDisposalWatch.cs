using System;
using System.Threading;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.Providers;

/// <summary>
/// Watches a loaded view's borrowed performance session and, when the
/// session is disposed, requests one full rebuild on the UI thread so the
/// rebuild drops the disposed session.
/// </summary>
/// <remarks>
/// The session raises Disposed on any thread, so the handler reads only
/// volatile state and posts the rebuild to the UI thread. The posted callback
/// checks again that the session is still the watched one and that the view
/// is still loaded and not disposed, because a session replacement, an
/// unload or disposal can land before the callback runs.
/// </remarks>
internal sealed class PerformanceSessionDisposalWatch
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly Func<Action, bool> _postToUiThread;
    private readonly Action _requestFullRebuild;
    private IMarkdownPerformanceSessionInternal? _watched;

    /// <param name="lifetime">The view's load state.</param>
    /// <param name="postToUiThread">
    /// Queues a callback on the view's UI thread and returns whether the
    /// dispatcher accepted it. May be called on any thread.
    /// </param>
    /// <param name="requestFullRebuild">Requests a full rebuild; called on the UI thread only.</param>
    internal PerformanceSessionDisposalWatch(
        MarkdownViewLifetime lifetime,
        Func<Action, bool> postToUiThread,
        Action requestFullRebuild)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(postToUiThread);
        ArgumentNullException.ThrowIfNull(requestFullRebuild);
        _lifetime = lifetime;
        _postToUiThread = postToUiThread;
        _requestFullRebuild = requestFullRebuild;
    }

    /// <summary>
    /// Watches <paramref name="session"/> in place of any session watched
    /// before. A session that is already disposed reports at once.
    /// </summary>
    internal void Watch(IMarkdownPerformanceSessionInternal session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Disposed -= OnDisposed;
        Volatile.Write(ref _watched, session);
        session.Disposed += OnDisposed;
    }

    /// <summary>Stops watching. A rebuild already posted for the old session is dropped when it runs.</summary>
    internal void Stop()
    {
        IMarkdownPerformanceSessionInternal? previous = Interlocked.Exchange(ref _watched, null);
        if (previous is not null)
            previous.Disposed -= OnDisposed;
    }

    private void OnDisposed(object? sender, EventArgs args)
    {
        if (!IsWatchedWhileLoaded(sender))
            return;

        _ = _postToUiThread(() =>
        {
            // RebuildInputCapture drops the disposed borrowed session, so the
            // rebuild uses the eager engine/legacy parser path, replacing any
            // committed code fallback with its native scene.
            if (IsWatchedWhileLoaded(sender))
                _requestFullRebuild();
        });
    }

    private bool IsWatchedWhileLoaded(object? session) =>
        ReferenceEquals(session, Volatile.Read(ref _watched)) &&
        !_lifetime.IsDisposed &&
        !_lifetime.IsUnloaded;
}
