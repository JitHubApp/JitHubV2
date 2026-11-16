using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Engine.Internal;

/// <summary>
/// The admitted uncached parses of one engine: the in-flight entry per source
/// key, the FIFO of parses waiting for a concurrency slot, and the counters the
/// admission limits are checked against. Not thread-safe:
/// <see cref="MarkdownParseCoordinator"/> calls every member under its parse
/// gate, together with the completed-parse cache, so admission, completion and
/// cache publication are one atomic step.
/// </summary>
internal sealed class MarkdownInFlightParseTable
{
    private readonly Dictionary<MarkdownParseSourceKey, MarkdownInFlightParse> _inFlightParses =
        new(MarkdownParseSourceKeyComparer.Instance);
    private readonly LinkedList<MarkdownInFlightParse> _queuedParses = new();
    private readonly MarkdownParseLimits _parseLimits;
    private int _activeParseCount;
    private int _outstandingParseCount;
    private long _outstandingSourceBytes;
    private long _parseGeneration;

    internal MarkdownInFlightParseTable(MarkdownParseLimits parseLimits)
    {
        _parseLimits = parseLimits;
    }

    internal int ActiveCount => _activeParseCount;

    internal int QueuedCount => _queuedParses.Count;

    internal int OutstandingCount => _outstandingParseCount;

    /// <summary>
    /// Returns the live in-flight parse for the key, or admits a new one that is
    /// started now (<paramref name="startParse"/>) or queued. Returns false only
    /// when a new parse would exceed the outstanding count or byte limit.
    /// </summary>
    internal bool TryJoinOrAdmit(
        MarkdownParseSourceKey sourceKey,
        long sourceBytes,
        [NotNullWhen(true)] out MarkdownInFlightParse? inFlight,
        out bool startParse)
    {
        startParse = false;
        if (!_inFlightParses.TryGetValue(sourceKey, out inFlight) || inFlight.IsAbandoned)
        {
            if (_outstandingParseCount >= _parseLimits.MaximumOutstandingParseCount ||
                _outstandingSourceBytes > _parseLimits.MaximumOutstandingSourceBytes - sourceBytes)
            {
                inFlight = null;
                return false;
            }

            inFlight = new MarkdownInFlightParse(
                sourceKey,
                sourceBytes,
                unchecked(++_parseGeneration));
            _inFlightParses[sourceKey] = inFlight;
            _outstandingParseCount++;
            _outstandingSourceBytes += sourceBytes;
            TaskFaultObservation.ObserveFault(inFlight.ParseTask);

            if (_activeParseCount < _parseLimits.MaximumConcurrentParseCount)
            {
                inFlight.IsStarted = true;
                _activeParseCount++;
                startParse = true;
            }
            else
            {
                inFlight.QueueNode = _queuedParses.AddLast(inFlight);
            }
        }

        return true;
    }

    /// <summary>
    /// Marks a parse whose last waiter left as abandoned and unregisters it. A
    /// parse that never started is completed and released here and the method
    /// returns true; a running parse returns false and releases its slot when it
    /// unwinds through <see cref="Complete"/>.
    /// </summary>
    internal bool Abandon(MarkdownInFlightParse inFlight)
    {
        inFlight.IsAbandoned = true;
        if (_inFlightParses.TryGetValue(inFlight.Key, out var current) &&
            ReferenceEquals(current, inFlight))
        {
            _inFlightParses.Remove(inFlight.Key);
        }

        if (inFlight.IsStarted)
            return false;

        if (inFlight.QueueNode is not null)
        {
            _queuedParses.Remove(inFlight.QueueNode);
            inFlight.QueueNode = null;
        }

        inFlight.IsCompleted = true;
        _outstandingParseCount--;
        _outstandingSourceBytes -= inFlight.SourceBytes;
        return true;
    }

    /// <summary>
    /// Releases a finished running parse. Returns whether it was still the
    /// registered parse for its key, which is required to publish its document.
    /// </summary>
    internal bool Complete(MarkdownInFlightParse inFlight)
    {
        bool isCurrent =
            _inFlightParses.TryGetValue(inFlight.Key, out var current) &&
            ReferenceEquals(current, inFlight);
        if (isCurrent)
            _inFlightParses.Remove(inFlight.Key);

        inFlight.IsCompleted = true;
        _activeParseCount--;
        _outstandingParseCount--;
        _outstandingSourceBytes -= inFlight.SourceBytes;
        return isCurrent;
    }

    /// <summary>
    /// Dequeues the next live queued parse and gives it the freed concurrency
    /// slot. Returns null when the queue is empty or the engine is disposing.
    /// </summary>
    internal MarkdownInFlightParse? StartNextQueued(bool disposeRequested)
    {
        while (!disposeRequested && _queuedParses.First is { } first)
        {
            _queuedParses.RemoveFirst();
            MarkdownInFlightParse next = first.Value;
            next.QueueNode = null;
            if (next.IsAbandoned || next.IsCompleted)
                continue;

            next.IsStarted = true;
            _activeParseCount++;
            return next;
        }

        return null;
    }

    /// <summary>
    /// Abandons every registered parse for disposal. Queued parses are completed
    /// and released here; running parses keep their slot until they unwind.
    /// </summary>
    internal void AbandonAll(
        ref List<MarkdownInFlightParse>? running,
        ref List<MarkdownInFlightParse>? queued)
    {
        foreach (MarkdownInFlightParse operation in _inFlightParses.Values)
        {
            operation.IsAbandoned = true;
            if (operation.IsStarted)
            {
                (running ??= new List<MarkdownInFlightParse>()).Add(operation);
                continue;
            }

            operation.IsCompleted = true;
            operation.QueueNode = null;
            _outstandingParseCount--;
            _outstandingSourceBytes -= operation.SourceBytes;
            (queued ??= new List<MarkdownInFlightParse>()).Add(operation);
        }

        _inFlightParses.Clear();
        _queuedParses.Clear();
    }
}
