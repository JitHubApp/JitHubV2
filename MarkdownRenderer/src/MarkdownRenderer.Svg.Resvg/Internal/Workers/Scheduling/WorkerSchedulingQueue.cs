using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

internal sealed class WorkerSchedulingQueue<T>
    where T : class
{
    private const int MaximumAffinityBypassesPerHead = 1;
    private readonly List<Entry> _entries = [];

    public int Count => _entries.Count;

    public int VisibleCount => _entries.Count(entry => entry.Priority == MarkdownSvgRenderPriority.Visible);

    public int VisibleOpenCount => _entries.Count(entry =>
        entry.Priority == MarkdownSvgRenderPriority.Visible && entry.IsOpen);

    public int VisibleRenderCount => _entries.Count(entry =>
        entry.Priority == MarkdownSvgRenderPriority.Visible && entry.IsRender);

    public int VisibleTextRenderCount => _entries.Count(entry =>
        entry.Priority == MarkdownSvgRenderPriority.Visible &&
        entry.IsRender &&
        entry.RequiresFontCatalog);

    public void Enqueue(
        T item,
        MarkdownSvgRenderPriority priority,
        bool isRender,
        bool requiresFontCatalog = false,
        bool isOpen = false,
        long preferredWorkerInstanceId = 0,
        bool isSecondaryFontProbe = false) =>
        _entries.Add(new Entry(
            item,
            priority,
            isRender,
            requiresFontCatalog,
            isOpen,
            preferredWorkerInstanceId,
            isSecondaryFontProbe));

    public bool Remove(T item)
    {
        int index = _entries.FindIndex(entry => ReferenceEquals(entry.Item, item));
        if (index < 0)
            return false;
        _entries.RemoveAt(index);
        return true;
    }

    public bool Contains(T item) => _entries.Any(entry => ReferenceEquals(entry.Item, item));

    /// <summary>
    /// Promotion is monotonic and leaves the entry at its original list index,
    /// preserving arrival order when it joins the visible FIFO tier.
    /// </summary>
    public bool PromoteVisible(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        int index = _entries.FindIndex(entry => ReferenceEquals(entry.Item, item));
        if (index < 0 || _entries[index].Priority == MarkdownSvgRenderPriority.Visible)
            return false;
        _entries[index].Priority = MarkdownSvgRenderPriority.Visible;
        return true;
    }

    /// <summary>Synchronizes already-promoted tickets before counters/dispatch.</summary>
    public int PromoteVisibleWhere(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        int promoted = 0;
        foreach (Entry entry in _entries)
        {
            if (entry.Priority != MarkdownSvgRenderPriority.Visible && predicate(entry.Item))
            {
                entry.Priority = MarkdownSvgRenderPriority.Visible;
                promoted++;
            }
        }
        return promoted;
    }

    public bool TryDequeuePrimary(out T? item)
        => TryDequeuePrimary(preferredWorkerInstanceId: 0, out item);

    public bool TryDequeuePrimary(long preferredWorkerInstanceId, out T? item)
    {
        int index = _entries.FindIndex(entry =>
            !entry.IsSecondaryFontProbe && entry.Priority == MarkdownSvgRenderPriority.Visible);
        if (index < 0)
            index = _entries.FindIndex(entry => !entry.IsSecondaryFontProbe);

        if (index >= 0 &&
            preferredWorkerInstanceId != 0 &&
            _entries[index].AffinityBypasses < MaximumAffinityBypassesPerHead)
        {
            int fifoIndex = index;
            MarkdownSvgRenderPriority selectedPriority = _entries[index].Priority;
            for (int candidate = index + 1; candidate < _entries.Count; candidate++)
            {
                Entry entry = _entries[candidate];
                if (!entry.IsSecondaryFontProbe &&
                    entry.Priority == selectedPriority &&
                    entry.PreferredWorkerInstanceId == preferredWorkerInstanceId)
                {
                    index = candidate;
                    break;
                }
            }

            if (index != fifoIndex)
                _entries[fifoIndex].AffinityBypasses++;
        }

        return TryRemoveAt(index, out item);
    }

    public bool TryDequeueSecondary(bool fontCatalogReady, out T? item)
        => TryDequeueSecondary(0, fontCatalogReady, out item);

    public bool TryDequeueSecondaryFontProbe(long secondaryWorkerInstanceId, out T? item)
    {
        int index = _entries.FindIndex(entry =>
            entry.Priority == MarkdownSvgRenderPriority.Visible &&
            entry.IsSecondaryFontProbe &&
            entry.PreferredWorkerInstanceId == secondaryWorkerInstanceId);
        return TryRemoveAt(index, out item);
    }

    public bool TryDequeueSecondary(
        long preferredWorkerInstanceId,
        bool fontCatalogReady,
        out T? item)
    {
        static bool IsEligible(Entry entry, bool hasFontCatalog) =>
            entry.Priority == MarkdownSvgRenderPriority.Visible &&
            entry.IsRender &&
            (hasFontCatalog || !entry.RequiresFontCatalog);

        int index = _entries.FindIndex(entry => IsEligible(entry, fontCatalogReady));
        if (index >= 0 &&
            preferredWorkerInstanceId != 0 &&
            _entries[index].AffinityBypasses < MaximumAffinityBypassesPerHead)
        {
            int fifoIndex = index;
            for (int candidate = index + 1; candidate < _entries.Count; candidate++)
            {
                Entry entry = _entries[candidate];
                if (IsEligible(entry, fontCatalogReady) &&
                    entry.PreferredWorkerInstanceId == preferredWorkerInstanceId)
                {
                    index = candidate;
                    break;
                }
            }

            if (index != fifoIndex)
                _entries[fifoIndex].AffinityBypasses++;
        }

        return TryRemoveAt(index, out item);
    }

    public bool TryDequeueOldestVisibleForSecondaryAffinity(
        long secondaryWorkerInstanceId,
        bool fontCatalogReady,
        out T? item) =>
        TryDequeueOldestVisibleForSecondaryAffinity(
            secondaryWorkerInstanceId,
            fontCatalogReady,
            canDispatchSecondary: true,
            out item);

    public bool TryDequeueOldestVisibleForSecondaryAffinity(
        long secondaryWorkerInstanceId,
        bool fontCatalogReady,
        bool canDispatchSecondary,
        out T? item)
    {
        if (!canDispatchSecondary)
        {
            item = null;
            return false;
        }

        int index = _entries.FindIndex(entry =>
            entry.Priority == MarkdownSvgRenderPriority.Visible);
        if (index >= 0)
        {
            Entry oldestVisible = _entries[index];
            if (secondaryWorkerInstanceId > 0 &&
                oldestVisible.IsRender &&
                (fontCatalogReady || !oldestVisible.RequiresFontCatalog) &&
                oldestVisible.PreferredWorkerInstanceId == secondaryWorkerInstanceId)
            {
                // This removes the actual visible FIFO head (not a later
                // eligible item), so it does not consume an affinity bypass.
                return TryRemoveAt(index, out item);
            }
        }

        item = null;
        return false;
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

    private sealed class Entry(
        T item,
        MarkdownSvgRenderPriority priority,
        bool isRender,
        bool requiresFontCatalog,
        bool isOpen,
        long preferredWorkerInstanceId,
        bool isSecondaryFontProbe)
    {
        public T Item { get; } = item;
        public MarkdownSvgRenderPriority Priority { get; set; } = priority;
        public bool IsRender { get; } = isRender;
        public bool RequiresFontCatalog { get; } = requiresFontCatalog;
        public bool IsOpen { get; } = isOpen;
        public long PreferredWorkerInstanceId { get; } = preferredWorkerInstanceId;
        public bool IsSecondaryFontProbe { get; } = isSecondaryFontProbe;
        public int AffinityBypasses { get; set; }
    }
}
