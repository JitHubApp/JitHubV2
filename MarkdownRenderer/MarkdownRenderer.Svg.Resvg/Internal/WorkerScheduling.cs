using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal static class WorkerSchedulingPolicy
{
    public static readonly TimeSpan SecondaryIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CanceledVisibleBlockingGrace = TimeSpan.FromMilliseconds(100);

    public static bool CanUseSecondary(
        Architecture architecture,
        int logicalProcessorCount,
        bool energySaverOn) =>
        architecture is Architecture.X64 or Architecture.Arm64 &&
        logicalProcessorCount >= 8 &&
        !energySaverOn;

    public static bool ShouldStartSecondary(
        bool canUseSecondary,
        bool primaryBusy,
        bool secondaryExists,
        int queuedVisibleUncachedRenders) =>
        canUseSecondary &&
        primaryBusy &&
        !secondaryExists &&
        queuedVisibleUncachedRenders >= 2;

    public static bool ShouldRetireSecondary(
        DateTimeOffset idleSince,
        DateTimeOffset now) =>
        now - idleSince >= SecondaryIdleTimeout;

    public static bool ShouldTerminateCanceledActive(
        bool activeRequestCanceled,
        int queuedVisibleRequests,
        DateTimeOffset blockedSince,
        DateTimeOffset now) =>
        activeRequestCanceled &&
        queuedVisibleRequests > 0 &&
        now - blockedSince >= CanceledVisibleBlockingGrace;
}

internal sealed class WorkerSchedulingQueue<T>
    where T : class
{
    private readonly List<Entry> _entries = [];

    public int Count => _entries.Count;

    public int VisibleCount => _entries.Count(entry => entry.Priority == MarkdownSvgRenderPriority.Visible);

    public int VisibleRenderCount => _entries.Count(entry =>
        entry.Priority == MarkdownSvgRenderPriority.Visible && entry.IsRender);

    public void Enqueue(T item, MarkdownSvgRenderPriority priority, bool isRender) =>
        _entries.Add(new Entry(item, priority, isRender));

    public bool Remove(T item)
    {
        int index = _entries.FindIndex(entry => ReferenceEquals(entry.Item, item));
        if (index < 0)
            return false;
        _entries.RemoveAt(index);
        return true;
    }

    public bool TryDequeuePrimary(out T? item)
    {
        int index = _entries.FindIndex(entry => entry.Priority == MarkdownSvgRenderPriority.Visible);
        if (index < 0 && _entries.Count != 0)
            index = 0;
        return TryRemoveAt(index, out item);
    }

    public bool TryDequeueSecondary(out T? item)
    {
        int index = _entries.FindIndex(entry =>
            entry.Priority == MarkdownSvgRenderPriority.Visible && entry.IsRender);
        return TryRemoveAt(index, out item);
    }

    public T[] Drain()
    {
        T[] items = _entries.Select(entry => entry.Item).ToArray();
        _entries.Clear();
        return items;
    }

    private bool TryRemoveAt(int index, out T? item)
    {
        if (index < 0)
        {
            item = null;
            return false;
        }
        item = _entries[index].Item;
        _entries.RemoveAt(index);
        return true;
    }

    private sealed record Entry(T Item, MarkdownSvgRenderPriority Priority, bool IsRender);
}
