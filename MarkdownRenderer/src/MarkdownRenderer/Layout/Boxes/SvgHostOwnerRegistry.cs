using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The accepted owners of one SVG admission domain: reserved and committed
/// owner slots, their LRU order, the owner version that slot waiters watch,
/// and pressure parking of the least recently used owners.
/// </summary>
internal sealed class SvgHostOwnerRegistry
{
    private const int MaximumAcceptedOwnersPerDomain = 512;

    private readonly object _gate = new();
    private readonly List<SvgHostOwnerEntry> _owners = new(MaximumAcceptedOwnersPerDomain);
    private int _reservedOwnerSlots;
    private long _ownerVersion;
    private long _useClock;
    private TaskCompletionSource<long>? _ownerVersionChanged;

    internal long OwnerVersion
    {
        get { lock (_gate) return _ownerVersion; }
    }

    internal static int CompareParkCandidateOrder(
        int leftTier,
        long leftLastUse,
        int rightTier,
        long rightLastUse)
    {
        int tier = leftTier.CompareTo(rightTier);
        return tier != 0 ? tier : leftLastUse.CompareTo(rightLastUse);
    }

    internal SvgHostOwnerSlot? TryReserveOwnerSlot(
        object admissionDomain,
        out long observedOwnerVersion)
    {
        PruneDeadOwners();
        lock (_gate)
        {
            observedOwnerVersion = _ownerVersion;
            if (_owners.Count + _reservedOwnerSlots >= MaximumAcceptedOwnersPerDomain)
                return null;

            var slot = new SvgHostOwnerSlot(admissionDomain, this);
            _reservedOwnerSlots++;
            return slot;
        }
    }

    internal SvgHostOwnerEntry Commit(
        ImageBox owner,
        ImageBox.SvgTileRenderPlan plan,
        MarkdownSvgRenderDemand demand,
        SvgHostOwnerSlot slot,
        long sourceReservationBytes)
    {
        var entry = new SvgHostOwnerEntry(
            owner,
            plan,
            demand,
            sourceReservationBytes,
            slot,
            lastUse: 0);
        lock (_gate)
        {
            if (_reservedOwnerSlots <= 0 ||
                _owners.Count >= MaximumAcceptedOwnersPerDomain)
            {
                throw new InvalidOperationException("The reserved SVG owner slot was lost.");
            }

            _reservedOwnerSlots--;
            entry.LastUse = ++_useClock;
            _owners.Add(entry);
            return entry;
        }
    }

    internal void Touch(SvgHostOwnerEntry entry)
    {
        lock (_gate)
        {
            if (_owners.Contains(entry))
                entry.LastUse = ++_useClock;
        }
    }

    internal void Release(SvgHostOwnerSlot slot, SvgHostOwnerEntry? entry)
    {
        TaskCompletionSource<long>? changed = null;
        long version = 0;
        lock (_gate)
        {
            if (entry is null)
            {
                if (_reservedOwnerSlots > 0)
                    _reservedOwnerSlots--;
            }
            else
            {
                int index = -1;
                for (int candidateIndex = 0; candidateIndex < _owners.Count; candidateIndex++)
                {
                    if (ReferenceEquals(_owners[candidateIndex], entry))
                    {
                        index = candidateIndex;
                        break;
                    }
                }
                if (index >= 0)
                    _owners.RemoveAt(index);
            }

            _ownerVersion++;
            version = _ownerVersion;
            changed = _ownerVersionChanged;
            _ownerVersionChanged = null;
        }

        // All signal continuations are asynchronous and run after the
        // coordinator gate is released.
        changed?.TrySetResult(version);
    }

    internal long GetOwnerVersion() => OwnerVersion;

    internal Task WaitForOwnerVersionChangeAsync(long observedVersion, CancellationToken token)
    {
        lock (_gate)
        {
            if (_ownerVersion != observedVersion)
                return Task.CompletedTask;
            return (_ownerVersionChanged ??= NewVersionSignal()).Task.WaitAsync(token);
        }
    }

    internal (long ScheduledBytes, int ScheduledVictims) TryParkOwners(
        long requiredSourceReservationBytes,
        MarkdownSvgRenderPriority requesterPriority,
        bool requiresOwnerSlot,
        int maximumVictims,
        Func<bool>? isStillPending)
    {
        if (maximumVictims <= 0)
            return (0, 0);

        PruneDeadOwners();
        (SvgHostOwnerEntry Entry, long LastUse)[] owners;
        lock (_gate)
        {
            owners = new (SvgHostOwnerEntry Entry, long LastUse)[_owners.Count];
            for (int index = 0; index < _owners.Count; index++)
            {
                SvgHostOwnerEntry entry = _owners[index];
                owners[index] = (entry, entry.LastUse);
            }
        }

        var candidates = new List<(SvgHostOwnerEntry Entry, ImageBox Owner,
            ImageBox.SvgTileRenderPlan Plan, long DemandVersion, int Tier, long LastUse)>();
        foreach ((SvgHostOwnerEntry entry, long lastUse) in owners)
        {
            long demandVersion = entry.Demand.Version;
            if (!entry.Owner.TryGetTarget(out ImageBox? owner) ||
                !entry.Plan.TryGetTarget(out ImageBox.SvgTileRenderPlan? plan) ||
                !owner.TryGetSvgPressureParkTier(plan, demandVersion, out int tier))
            {
                continue;
            }
            if (requesterPriority == MarkdownSvgRenderPriority.Overscan && tier != 0)
                continue;
            candidates.Add((entry, owner, plan, demandVersion, tier, lastUse));
        }

        candidates.Sort(static (left, right) =>
            CompareParkCandidateOrder(left.Tier, left.LastUse, right.Tier, right.LastUse));

        long scheduledBytes = 0;
        int victims = 0;
        int maximumParkTier = requesterPriority == MarkdownSvgRenderPriority.Overscan ? 0 : 1;
        foreach ((SvgHostOwnerEntry entry, ImageBox owner, ImageBox.SvgTileRenderPlan plan,
            long demandVersion, _, _) in candidates)
        {
            if (victims >= maximumVictims ||
                (requiresOwnerSlot && victims >= 1) ||
                (!requiresOwnerSlot && scheduledBytes >= requiredSourceReservationBytes))
            {
                break;
            }

            if (isStillPending is not null && !isStillPending())
                break;

            bool parked;
            try { parked = owner.TryParkSvgDocument(plan, demandVersion, maximumParkTier); }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] owner pressure park failed safely: {exception.GetType().Name}");
                continue;
            }
            if (!parked)
                continue;
            scheduledBytes = SaturatingAdd(scheduledBytes, entry.SourceReservationBytes);
            victims++;
        }

        return (scheduledBytes, victims);
    }

    internal void PruneDeadOwners()
    {
        TaskCompletionSource<long>? changed = null;
        long version = 0;
        lock (_gate)
        {
            bool removed = false;
            for (int index = _owners.Count - 1; index >= 0; index--)
            {
                SvgHostOwnerEntry entry = _owners[index];
                if (entry.Owner.TryGetTarget(out _) && entry.Plan.TryGetTarget(out _))
                    continue;
                _owners.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                version = ++_ownerVersion;
                changed = _ownerVersionChanged;
                _ownerVersionChanged = null;
            }
        }

        // Slot-change waiters are always signaled after the registry lock.
        changed?.TrySetResult(version);
    }

    private static TaskCompletionSource<long> NewVersionSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long SaturatingAdd(long value, long addend) =>
        value > long.MaxValue - addend ? long.MaxValue : value + addend;
}
