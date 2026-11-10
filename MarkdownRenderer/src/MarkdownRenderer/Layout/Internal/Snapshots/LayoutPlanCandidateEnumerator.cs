using System;

namespace MarkdownRenderer.Layout.Internal.Snapshots;

/// <summary>
/// Allocation-free enumerator over realization-plan candidates, optionally
/// filtered to measured roots.
/// </summary>
internal ref struct LayoutPlanCandidateEnumerator
{
    private readonly ReadOnlySpan<LayoutPlanCandidate> _candidates;
    private readonly ReadOnlySpan<int> _measuredRootOrdinals;
    private readonly bool _filterByMeasuredRoot;
    private int _nextCandidateIndex;
    private int _candidateRangeEnd;
    private int _nextMeasuredRootIndex;

    internal LayoutPlanCandidateEnumerator(ReadOnlySpan<LayoutPlanCandidate> candidates)
    {
        _candidates = candidates;
        _measuredRootOrdinals = default;
        _filterByMeasuredRoot = false;
        _nextCandidateIndex = 0;
        _candidateRangeEnd = 0;
        _nextMeasuredRootIndex = 0;
        Current = default;
    }

    internal LayoutPlanCandidateEnumerator(
        ReadOnlySpan<LayoutPlanCandidate> candidates,
        ReadOnlySpan<int> measuredRootOrdinals)
    {
        _candidates = candidates;
        _measuredRootOrdinals = measuredRootOrdinals;
        _filterByMeasuredRoot = true;
        _nextCandidateIndex = 0;
        _candidateRangeEnd = 0;
        _nextMeasuredRootIndex = 0;
        Current = default;
    }

    internal LayoutPlanCandidate Current { get; private set; }

    internal bool MoveNext()
    {
        if (!_filterByMeasuredRoot)
        {
            if (_nextCandidateIndex >= _candidates.Length)
                return false;

            Current = _candidates[_nextCandidateIndex++];
            return true;
        }

        while (true)
        {
            if (_nextCandidateIndex < _candidateRangeEnd)
            {
                Current = _candidates[_nextCandidateIndex++];
                return true;
            }

            if (_nextMeasuredRootIndex >= _measuredRootOrdinals.Length)
                return false;

            LayoutPlanCandidateIndex.TryFindLayoutPlanCandidateRange(
                _candidates,
                _measuredRootOrdinals[_nextMeasuredRootIndex++],
                out _nextCandidateIndex,
                out _candidateRangeEnd);
        }
    }
}
