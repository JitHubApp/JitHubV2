using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MarkdownRenderer.Performance.Internal.CompletedScenes;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>
/// Completed scene descriptors and unconsumed layout handoffs for one plan.
/// Completed metadata is bounded by the immutable source work table and holds
/// weak graph references only. Strong reuse belongs to the session's aggregate
/// cache, keyed by the owning plan; unconsumed publications are transient
/// layout handoffs. Not thread-safe: the owning plan calls it under its gate.
/// </summary>
internal sealed class DeferredSceneResultStore(MarkdownCompletedSceneCache completedCache, object cacheOwner)
{
    private readonly Dictionary<string, MarkdownCompletedSceneDescriptor> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownDeferredSceneResult> _pendingResults = new(StringComparer.Ordinal);
    private readonly MarkdownCompletedSceneCache _completedCache = completedCache;
    private readonly object _cacheOwner = cacheOwner;

    internal bool TryGetDescriptor(
        string marker,
        [NotNullWhen(true)] out MarkdownCompletedSceneDescriptor? descriptor) =>
        _completed.TryGetValue(marker, out descriptor);

    internal object? GetProjectionIdentity(string marker) => _completed[marker].ProjectionIdentity;

    internal void TryGetMetadata(string marker, out object? metadataLease) =>
        _completedCache.TryGetMetadata(_cacheOwner, marker, out metadataLease);

    internal bool TryGetCompleted(
        string marker,
        bool consumeHandoff,
        out MarkdownDeferredSceneResult? result)
    {
        result = null;
        if (!_completed.TryGetValue(marker, out MarkdownCompletedSceneDescriptor? descriptor))
            return false;
        if (_pendingResults.TryGetValue(marker, out result))
        {
            if (consumeHandoff)
            {
                _completedCache.Retain(_cacheOwner, marker, result, descriptor.EstimatedGraphWeightBytes);
                _pendingResults.Remove(marker);
            }
            return true;
        }
        if (_completedCache.TryGetResult(_cacheOwner, marker, out result))
            return true;
        if (!descriptor.TryGetOriginal(out result))
            return false;
        // UI layout and scheduling may reuse a live immutable result, but must
        // not reconstruct its content graph. Restoration runs on the worker.
        _completedCache.Retain(_cacheOwner, marker, result!, descriptor.EstimatedGraphWeightBytes);
        return true;
    }

    /// <summary>
    /// A publication can leave the band before a candidate consumes it. Moves
    /// that handoff into bounded retention rather than leaving forgotten
    /// results pinned for every previously visited viewport.
    /// </summary>
    internal void RetireHandoffsOutside(HashSet<string> visibleMarkers)
    {
        if (_pendingResults.Count > 0)
        {
            List<string>? retiredHandoffs = null;
            foreach ((string marker, MarkdownDeferredSceneResult result) in _pendingResults)
            {
                if (!visibleMarkers.Contains(marker))
                {
                    _completedCache.Retain(_cacheOwner, marker, result, _completed[marker].EstimatedGraphWeightBytes);
                    (retiredHandoffs ??= []).Add(marker);
                }
            }
            if (retiredHandoffs is not null)
                foreach (string marker in retiredHandoffs)
                    _pendingResults.Remove(marker);
        }
    }

    /// <summary>Publishes a freshly prepared result as a pending layout handoff.</summary>
    internal void Publish(
        string marker,
        MarkdownCompletedSceneDescriptor descriptor,
        MarkdownDeferredSceneResult result)
    {
        // Transfer the construction-only strong metadata owner to its separate
        // session-wide byte budget before publishing the weak descriptor. This
        // does not walk the scene graph.
        RetainMetadata(marker, descriptor);
        _completed[marker] = descriptor;
        _pendingResults[marker] = result;
    }

    /// <summary>Commits a graph rebind's result straight into bounded retention.</summary>
    internal void CommitRebind(
        string marker,
        MarkdownCompletedSceneDescriptor descriptor,
        MarkdownDeferredSceneResult result)
    {
        RetainMetadata(marker, descriptor);
        _completed[marker] = descriptor;
        _completedCache.Retain(_cacheOwner, marker, result, descriptor.EstimatedGraphWeightBytes);
    }

    internal void Clear()
    {
        _completed.Clear();
        _pendingResults.Clear();
        _completedCache.RemoveOwner(_cacheOwner);
    }

    private void RetainMetadata(string marker, MarkdownCompletedSceneDescriptor descriptor)
    {
        if (descriptor.DetachMetadataOwner() is { } metadata)
            _completedCache.RetainMetadata(_cacheOwner, marker, metadata, descriptor.MetadataWeightBytes);
    }
}
