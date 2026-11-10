using MarkdownRenderer.Controls.Internal.Threading;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A UI scheduler that runs posted work only when the test drains it, records
/// every post with its priority, and hands out timers the test fires by hand.
/// Posts may arrive from worker threads.
/// </summary>
internal sealed class ManualUiScheduler : IMarkdownUiScheduler
{
    private readonly object _sync = new();
    private readonly Queue<Action> _pending = new();
    private readonly List<MarkdownUiPriority> _posted = [];

    /// <summary>What <see cref="HasThreadAccess"/> reports.</summary>
    internal bool OnUiThread { get; set; } = true;

    /// <summary>True to reject every post, as a shut-down queue does.</summary>
    internal bool RejectPosts { get; set; }

    /// <summary>True to make <see cref="CreateTimer"/> throw, as a failed native timer does.</summary>
    internal bool FailTimerCreation { get; set; }

    /// <summary>Every timer created, in order.</summary>
    internal List<ManualUiTimer> Timers { get; } = [];

    /// <summary>The priority of every post, accepted or rejected, in order.</summary>
    internal IReadOnlyList<MarkdownUiPriority> Posted
    {
        get { lock (_sync) return _posted.ToArray(); }
    }

    internal int PendingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    public bool HasThreadAccess => OnUiThread;

    public bool TryEnqueue(MarkdownUiPriority priority, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            _posted.Add(priority);
            if (RejectPosts)
                return false;
            _pending.Enqueue(callback);
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    public IMarkdownUiTimer CreateTimer()
    {
        if (FailTimerCreation)
            throw new InvalidOperationException("The test failed timer creation.");
        var timer = new ManualUiTimer();
        Timers.Add(timer);
        return timer;
    }

    /// <summary>Runs the work posted so far, but not work it posts in turn. Returns how many ran.</summary>
    internal int RunPending()
    {
        Action[] batch;
        lock (_sync)
        {
            batch = _pending.ToArray();
            _pending.Clear();
        }
        foreach (Action callback in batch)
            callback();
        return batch.Length;
    }

    /// <summary>Waits until at least <paramref name="count"/> posts are pending.</summary>
    internal void WaitForPending(int count = 1)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        lock (_sync)
        {
            while (_pending.Count < count)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException($"Expected {count} posted callbacks, saw {_pending.Count}.");
                Monitor.Wait(_sync, remaining);
            }
        }
    }
}
