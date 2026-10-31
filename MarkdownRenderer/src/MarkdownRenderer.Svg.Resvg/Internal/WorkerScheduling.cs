using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal static class WorkerSchedulingPolicy
{
    // Process launch and the one-time Windows font catalog are initialization,
    // not untrusted SVG work. Keep their deadline independent from the immutable
    // three-second per-content ceiling so a cold machine cannot poison valid SVGs.
    public static readonly TimeSpan InitializationDeadline = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SecondaryIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CanceledVisibleBlockingGrace = TimeSpan.FromMilliseconds(100);

    public static bool CanUseSecondary(
        Architecture architecture,
        int logicalProcessorCount,
        bool energySaverOn,
        bool energySaverQueryFailed = false) =>
        architecture is Architecture.X64 or Architecture.Arm64 &&
        logicalProcessorCount >= 8 &&
        !energySaverOn &&
        !energySaverQueryFailed;

    public static bool CanDispatchSecondary(
        bool machineEligible,
        bool workerReady,
        bool workerBusy) =>
        machineEligible && workerReady && !workerBusy;

    public static bool ShouldStartSecondary(
        bool canUseSecondary,
        bool primaryBusy,
        bool secondaryExists,
        int queuedVisibleUncachedRenders) =>
        canUseSecondary &&
        primaryBusy &&
        !secondaryExists &&
        queuedVisibleUncachedRenders >= 2;

    public static bool ShouldWarmSecondaryFontCatalog(
        long primaryReadyGeneration,
        long secondaryReadyGeneration,
        long requestedGeneration,
        int queuedVisibleTextRenders) =>
        WorkerFontCatalogGeneration.IsReadyFor(primaryReadyGeneration, requestedGeneration) &&
        !WorkerFontCatalogGeneration.IsReadyFor(secondaryReadyGeneration, requestedGeneration) &&
        queuedVisibleTextRenders >= 2;

    // Kept for the scheduling tests and call sites that only need to describe
    // initial generation-zero readiness.
    public static bool ShouldWarmSecondaryFontCatalog(
        bool primaryReady,
        bool secondaryReady,
        int queuedVisibleTextRenders) =>
        ShouldWarmSecondaryFontCatalog(
            primaryReady ? 0 : -1,
            secondaryReady ? 0 : -1,
            requestedGeneration: 0,
            queuedVisibleTextRenders);

    // A failed optional worker is a batch-scoped sentinel. Keep it registered
    // while queued work drains so Release cannot immediately trigger another
    // startup attempt through PumpLocked.
    public static bool ShouldRetainFailedSecondaryAfterRelease(
        bool hasFailed,
        int queuedWorkCount) =>
        hasFailed && queuedWorkCount > 0;

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

/// <summary>
/// Monotonic font-catalog generation bookkeeping shared by the worker process
/// and its scheduling readiness state. A value of -1 means no catalog has
/// been confirmed by the host yet; generation zero is a valid initial catalog.
/// </summary>
internal sealed class WorkerFontCatalogGeneration
{
    public const long MaximumGeneration = uint.MaxValue;
    private long _readyGeneration = -1;

    public long ReadyGeneration => Interlocked.Read(ref _readyGeneration);

    public bool IsReadyFor(long requestedGeneration) =>
        IsReadyFor(ReadyGeneration, requestedGeneration);

    public static bool IsReadyFor(long readyGeneration, long requestedGeneration) =>
        requestedGeneration >= 0 &&
        requestedGeneration <= MaximumGeneration &&
        readyGeneration >= requestedGeneration;

    public static void ValidateRequest(long requestedGeneration)
    {
        if (requestedGeneration is < 0 or > MaximumGeneration)
            throw new WorkerProtocolException("The SVG font generation exceeds the protocol limit.");
    }

    public void MarkReady(long readyGeneration)
    {
        if (readyGeneration is < 0 or > MaximumGeneration)
            throw new WorkerProtocolException("The SVG worker returned an invalid font generation.");

        long observed = Interlocked.Read(ref _readyGeneration);
        while (readyGeneration > observed)
        {
            long prior = Interlocked.CompareExchange(ref _readyGeneration, readyGeneration, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    public static bool IsSupportedGeneration(long generation) =>
        generation is >= 0 and <= MaximumGeneration;
}

internal static class WorkerFontCatalogProtocolPolicy
{
    public static void ValidateProbeResponse(long requestedGeneration, WorkerResponse response)
    {
        if (requestedGeneration is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration ||
            response.ReadyFontGeneration is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration)
        {
            throw new WorkerProtocolException("The SVG worker returned an out-of-range font generation.");
        }

        if (response.Status == WorkerStatus.Ok && response.ReadyFontGeneration < requestedGeneration)
        {
            throw new WorkerProtocolException("The SVG worker reported fonts ready before the requested generation.");
        }

        if (response.Status == WorkerStatus.FontCatalogPending &&
            (requestedGeneration == 0
                ? response.ReadyFontGeneration != 0
                : response.ReadyFontGeneration >= requestedGeneration))
        {
            throw new WorkerProtocolException("The SVG worker returned an inconsistent pending font generation.");
        }

        if (response.Status == WorkerStatus.FontCatalogFailure &&
            (requestedGeneration == 0
                ? response.ReadyFontGeneration != 0
                : response.ReadyFontGeneration >= requestedGeneration))
        {
            throw new WorkerProtocolException("The SVG worker returned an inconsistent failed font generation.");
        }
    }
}

internal static class WorkerFontWaitAdmissionPolicy
{
    public static int CountVisibleDemand(int queuedCount, int detachedCount)
    {
        if (queuedCount < 0 || detachedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(queuedCount));
        return checked(queuedCount + detachedCount);
    }

    public static bool CanAdmitNew(int queuedCount, int detachedWaitCount, int queueCapacity) =>
        queuedCount >= 0 && detachedWaitCount >= 0 && queueCapacity > 0 &&
        queuedCount < queueCapacity && queuedCount + detachedWaitCount < queueCapacity;

    public static bool CanReenter(int queuedCount, int queueCapacity) =>
        queuedCount >= 0 && queueCapacity > 0 && queuedCount < queueCapacity;

    public static bool CanReserveFromActive(
        int queuedCount,
        int detachedWaitCount,
        int queueCapacity,
        int activeSlotCount,
        bool leaseIsActive = true,
        bool alreadyReserved = false) =>
        leaseIsActive && !alreadyReserved &&
        queuedCount >= 0 && detachedWaitCount >= 0 && queueCapacity > 0 && activeSlotCount > 0 &&
        queuedCount + detachedWaitCount < queueCapacity + activeSlotCount;
}

internal sealed class WorkerFontWaitReservation : IDisposable
{
    private Action? _release;
    private IDisposable? _promotionRegistration;
    private Action? _consumed;
    private int _state;
    private int _countsAsVisibleRender;
    private int _countsAsVisibleTextRender;
    private int _promotionApplied;
    private readonly bool _isRender;
    private readonly bool _requiresFontCatalog;

    public WorkerFontWaitReservation(
        Action release,
        bool countsAsVisibleRender = false,
        bool countsAsVisibleTextRender = false,
        MarkdownSvgRenderDemand? demandTicket = null,
        bool isRender = false,
        bool requiresFontCatalog = false,
        Action? consumed = null)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        if (countsAsVisibleTextRender && !countsAsVisibleRender)
            throw new ArgumentException("A visible text render must also count as a visible render.", nameof(countsAsVisibleTextRender));
        _countsAsVisibleRender = countsAsVisibleRender ? 1 : 0;
        _countsAsVisibleTextRender = countsAsVisibleTextRender ? 1 : 0;
        DemandTicket = demandTicket;
        _isRender = isRender;
        _requiresFontCatalog = requiresFontCatalog;
        _consumed = consumed;
        _promotionApplied = countsAsVisibleRender || countsAsVisibleTextRender ? 1 : 0;
    }

    public bool IsActive => Volatile.Read(ref _state) == 0;
    public bool CountsAsVisibleRender => Volatile.Read(ref _countsAsVisibleRender) != 0;
    public bool CountsAsVisibleTextRender => Volatile.Read(ref _countsAsVisibleTextRender) != 0;
    internal MarkdownSvgRenderDemand? DemandTicket { get; }

    internal void SetPromotionRegistration(IDisposable registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (Interlocked.CompareExchange(ref _promotionRegistration, registration, null) is not null)
            throw new InvalidOperationException("A font-wait reservation already has a promotion registration.");
        if (!IsActive)
            Interlocked.Exchange(ref _promotionRegistration, null)?.Dispose();
    }

    /// <summary>Called only while the owning worker-pool state lock is held.</summary>
    internal bool PromoteVisible(out bool addedVisibleRender, out bool addedVisibleTextRender)
    {
        addedVisibleRender = false;
        addedVisibleTextRender = false;
        if (!IsActive || DemandTicket?.Priority != MarkdownSvgRenderPriority.Visible ||
            Interlocked.Exchange(ref _promotionApplied, 1) != 0)
            return false;

        if (_isRender)
        {
            addedVisibleRender = Interlocked.Exchange(ref _countsAsVisibleRender, 1) == 0;
            if (_requiresFontCatalog)
                addedVisibleTextRender = Interlocked.Exchange(ref _countsAsVisibleTextRender, 1) == 0;
        }
        return true;
    }

    internal void DisposePromotionRegistration() =>
        Interlocked.Exchange(ref _promotionRegistration, null)?.Dispose();

    public bool TryConsume()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return false;
        Interlocked.Exchange(ref _release, null);
        DisposePromotionRegistration();
        Interlocked.Exchange(ref _consumed, null)?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            DisposePromotionRegistration();
            Interlocked.Exchange(ref _consumed, null);
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}

/// <summary>
/// Readiness state for the optional worker. A secondary slot is not eligible
/// for content dispatch while process startup is in progress. A live worker
/// remains eligible for geometry while its font catalog refreshes.
/// </summary>
internal sealed class SecondaryWorkerReadiness
{
    private int _isWarming;
    private int _workerReady;
    private long _fontCatalogReadyGeneration = -1;
    private long _fontCatalogFailedGeneration = -1;
    private int _hasFailed;

    public bool IsWarming => Volatile.Read(ref _isWarming) != 0;
    public bool IsWorkerReady => Volatile.Read(ref _workerReady) != 0;
    public long FontCatalogReadyGeneration => Interlocked.Read(ref _fontCatalogReadyGeneration);
    public long FontCatalogFailedGeneration => Interlocked.Read(ref _fontCatalogFailedGeneration);
    public bool IsFontCatalogReady => FontCatalogReadyGeneration >= 0;
    public bool HasFailed => Volatile.Read(ref _hasFailed) != 0;

    public bool CanDispatch(bool requiresFontCatalog, long requestedGeneration = 0) =>
        IsWorkerReady &&
        (!requiresFontCatalog || WorkerFontCatalogGeneration.IsReadyFor(
            FontCatalogReadyGeneration,
            requestedGeneration));

    public bool TryBeginWarmup(bool workerAlreadyReady, bool fontCatalogAlreadyReady)
        => TryBeginWarmup(workerAlreadyReady, fontCatalogAlreadyReady ? 0 : -1);

    public bool TryBeginWarmup(bool workerAlreadyReady, long fontCatalogReadyGeneration)
    {
        if (HasFailed)
            return false;
        if (Interlocked.CompareExchange(ref _isWarming, 1, 0) != 0)
            return false;

        Volatile.Write(ref _workerReady, workerAlreadyReady ? 1 : 0);
        Interlocked.Exchange(
            ref _fontCatalogReadyGeneration,
            workerAlreadyReady ? fontCatalogReadyGeneration : -1);
        return true;
    }

    public void MarkWorkerReady() => Volatile.Write(ref _workerReady, 1);

    public void MarkFontCatalogReady(long generation)
    {
        if (generation is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration)
            throw new WorkerProtocolException("The SVG worker returned an invalid font generation.");

        long observed = Interlocked.Read(ref _fontCatalogReadyGeneration);
        long publishedGeneration = observed;
        while (generation > observed)
        {
            long prior = Interlocked.CompareExchange(ref _fontCatalogReadyGeneration, generation, observed);
            if (prior == observed)
            {
                publishedGeneration = generation;
                break;
            }
            observed = prior;
            publishedGeneration = prior;
        }

        // Clear only failures covered by the catalog actually published. An
        // older in-flight completion must not erase a newer failure marker.
        long failedGeneration = Interlocked.Read(ref _fontCatalogFailedGeneration);
        while (failedGeneration >= 0 && publishedGeneration >= failedGeneration)
        {
            long prior = Interlocked.CompareExchange(ref _fontCatalogFailedGeneration, -1, failedGeneration);
            if (prior == failedGeneration)
                break;
            failedGeneration = prior;
        }
    }

    public void MarkFontCatalogFailed(long generation)
    {
        if (generation is < 0 or > WorkerFontCatalogGeneration.MaximumGeneration)
            throw new WorkerProtocolException("The SVG worker returned an invalid failed font generation.");
        if (generation <= FontCatalogReadyGeneration)
            return;

        long observed = Interlocked.Read(ref _fontCatalogFailedGeneration);
        while (generation > observed)
        {
            long prior = Interlocked.CompareExchange(ref _fontCatalogFailedGeneration, generation, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    public void MarkFontCatalogReady() => MarkFontCatalogReady(0);

    public void CompleteWarmup() => Volatile.Write(ref _isWarming, 0);

    public void FailWarmup()
    {
        Volatile.Write(ref _hasFailed, 1);
        Volatile.Write(ref _workerReady, 0);
        Interlocked.Exchange(ref _fontCatalogReadyGeneration, -1);
        Interlocked.Exchange(ref _fontCatalogFailedGeneration, -1);
        Volatile.Write(ref _isWarming, 0);
    }

    public async Task WarmAsync(
        Func<CancellationToken, Task> startWorkerAsync,
        Func<bool> shouldWarmFontCatalog,
        Func<CancellationToken, Task<long>> warmFontCatalogAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startWorkerAsync);
        ArgumentNullException.ThrowIfNull(shouldWarmFontCatalog);
        ArgumentNullException.ThrowIfNull(warmFontCatalogAsync);

        try
        {
            await startWorkerAsync(cancellationToken).ConfigureAwait(false);
            MarkWorkerReady();
            if (shouldWarmFontCatalog())
            {
                long readyGeneration = await warmFontCatalogAsync(cancellationToken).ConfigureAwait(false);
                if (readyGeneration >= 0)
                    MarkFontCatalogReady(readyGeneration);
            }

            CompleteWarmup();
        }
        catch
        {
            FailWarmup();
            throw;
        }
    }

    public async Task WarmAsync(
        Func<CancellationToken, Task> startWorkerAsync,
        Func<bool> shouldWarmFontCatalog,
        Func<CancellationToken, Task> warmFontCatalogAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(warmFontCatalogAsync);
        await WarmAsync(
            startWorkerAsync,
            shouldWarmFontCatalog,
            async token =>
            {
                await warmFontCatalogAsync(token).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }
}

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
