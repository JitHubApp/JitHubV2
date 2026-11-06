using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Traversal-ordered realization-plan candidates and the sorted subset of
/// measured roots that own at least one candidate. Guarded by the snapshot
/// layout gate.
/// </summary>
internal sealed class LayoutPlanCandidateIndex
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly LayoutPlanCandidate[] _layoutPlanCandidates;
    // Only measured roots that contain at least one realization candidate are
    // retained here. This keeps lazy plan publication proportional to the
    // measured candidate subset instead of rescanning every root or candidate.
    // The list is nullable so eager snapshots pay no additional allocation.
    private List<int>? _measuredLayoutPlanRootOrdinals;
    private bool _measuredLayoutPlanRootOrdinalsAreSorted = true;

    internal ReadOnlySpan<LayoutPlanCandidate> Candidates => _layoutPlanCandidates;

    internal List<int>? MeasuredRootOrdinals => _measuredLayoutPlanRootOrdinals;

    internal bool MeasuredRootOrdinalsAreSorted
    {
        get => _measuredLayoutPlanRootOrdinalsAreSorted;
        set => _measuredLayoutPlanRootOrdinalsAreSorted = value;
    }

    internal LayoutPlanCandidateIndex(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        LayoutPlanCandidate[] candidates)
    {
        _geometry = geometry;
        _recovery = recovery;
        _layoutPlanCandidates = candidates;
    }

    /// <summary>
    /// Provides the immutable, traversal-ordered subset of boxes that can
    /// contribute realization plans. The caller must hold a snapshot read
    /// lease while using the box references because their native geometry is
    /// mutable across layout commits.
    /// </summary>
    internal ReadOnlySpan<LayoutPlanCandidate> GetLayoutPlanCandidatesUnderPaint()
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        return _recovery.IsRequired
            ? ReadOnlySpan<LayoutPlanCandidate>.Empty
            : _layoutPlanCandidates;
    }

    /// <summary>
    /// Enumerates every currently measured realization candidate in document
    /// order. For a lazy document-order placement this visits only candidate
    /// ranges belonging to measured roots; eager and custom-placement layouts
    /// retain the conservative full-candidate traversal. The caller must hold
    /// the snapshot interaction lease for the entire enumeration.
    /// </summary>
    internal LayoutPlanCandidateEnumerator GetMeasuredLayoutPlanCandidatesUnderPaint()
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        if (_recovery.IsRequired)
            return new LayoutPlanCandidateEnumerator(ReadOnlySpan<LayoutPlanCandidate>.Empty);

        if (!_geometry.LazyLayoutEnabled ||
            _geometry.MeasuredTopLevelBlocks is null ||
            !_geometry.CurrentPlacementGenerationNoLock().IsDocumentOrder)
        {
            return new LayoutPlanCandidateEnumerator(_layoutPlanCandidates);
        }

        List<int>? measuredRoots = _measuredLayoutPlanRootOrdinals;
        if (measuredRoots is null || measuredRoots.Count == 0)
            return new LayoutPlanCandidateEnumerator(
                _layoutPlanCandidates,
                ReadOnlySpan<int>.Empty);

        if (!_measuredLayoutPlanRootOrdinalsAreSorted)
            throw new InvalidOperationException(
                "Measured layout-plan roots must be ordered before they are published.");

        return new LayoutPlanCandidateEnumerator(
            _layoutPlanCandidates,
            CollectionsMarshal.AsSpan(measuredRoots));
    }

    internal void AddMeasuredLayoutPlanRootOrdinalNoLock(int ordinal)
    {
        if (!TryFindLayoutPlanCandidateRange(_layoutPlanCandidates, ordinal, out _, out _))
            return;

        List<int>? measuredRoots = _measuredLayoutPlanRootOrdinals;
        if (measuredRoots is null)
        {
            measuredRoots = new List<int>();
            _measuredLayoutPlanRootOrdinals = measuredRoots;
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
        }

        // A root transitions from unmeasured to measured only once. The mask
        // check in EnsureMeasuredBand is the duplicate fence, so do not pay a
        // linear list membership scan while admitting a large measured band.
        if (measuredRoots.Count > 0 && ordinal < measuredRoots[^1])
            _measuredLayoutPlanRootOrdinalsAreSorted = false;
        measuredRoots.Add(ordinal);
    }

    internal void RebuildMeasuredLayoutPlanRootOrdinalsNoLock()
    {
        bool[]? measuredMask = _geometry.MeasuredTopLevelBlocks;
        if (measuredMask is null || _layoutPlanCandidates.Length == 0)
        {
            _measuredLayoutPlanRootOrdinals = null;
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
            return;
        }

        List<int>? measuredRoots = null;
        int previousOrdinal = -1;
        foreach (LayoutPlanCandidate candidate in _layoutPlanCandidates)
        {
            int ordinal = candidate.TopLevelOrdinal;
            if (ordinal == previousOrdinal)
                continue;
            previousOrdinal = ordinal;
            if (ordinal >= 0 && ordinal < measuredMask.Length && measuredMask[ordinal])
                (measuredRoots ??= new List<int>()).Add(ordinal);
        }

        _measuredLayoutPlanRootOrdinals = measuredRoots;
        _measuredLayoutPlanRootOrdinalsAreSorted = true;
    }

    internal void SortMeasuredLayoutPlanRootOrdinalsNoLock()
    {
        if (_measuredLayoutPlanRootOrdinals is not { Count: > 1 } measuredRoots ||
            _measuredLayoutPlanRootOrdinalsAreSorted)
        {
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
            return;
        }

        measuredRoots.Sort();
        _measuredLayoutPlanRootOrdinalsAreSorted = true;
    }

    internal void RemoveUnmeasuredLayoutPlanRootOrdinalsNoLock()
    {
        if (_measuredLayoutPlanRootOrdinals is not { } measuredRoots ||
            _geometry.MeasuredTopLevelBlocks is not { } measuredMask)
        {
            return;
        }

        // Rollback is exceptional and may follow allocation/native failure.
        // Compact once after restoring the authoritative mask instead of
        // repeatedly removing individual roots from the middle of the list.
        int destination = 0;
        for (int source = 0; source < measuredRoots.Count; source++)
        {
            int ordinal = measuredRoots[source];
            if (ordinal >= 0 && ordinal < measuredMask.Length && measuredMask[ordinal])
                measuredRoots[destination++] = ordinal;
        }
        if (destination < measuredRoots.Count)
            measuredRoots.RemoveRange(destination, measuredRoots.Count - destination);
        if (measuredRoots.Count == 0)
        {
            _measuredLayoutPlanRootOrdinals = null;
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
        }

        // Removing entries preserves relative order whether or not the list
        // had been sorted before the failed transaction.
    }

    internal static bool TryFindLayoutPlanCandidateRange(
        ReadOnlySpan<LayoutPlanCandidate> candidates,
        int ordinal,
        out int start,
        out int end)
    {
        int low = 0;
        int high = candidates.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (candidates[middle].TopLevelOrdinal < ordinal)
                low = middle + 1;
            else
                high = middle;
        }

        start = low;
        high = candidates.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (candidates[middle].TopLevelOrdinal <= ordinal)
                low = middle + 1;
            else
                high = middle;
        }

        end = low;
        return start < end;
    }


    internal void ResetMeasuredRootOrdinalsNoLock()
    {
        _measuredLayoutPlanRootOrdinals = null;
        _measuredLayoutPlanRootOrdinalsAreSorted = true;
    }
}
