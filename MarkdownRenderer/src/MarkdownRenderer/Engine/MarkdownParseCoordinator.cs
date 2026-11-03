using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer;

/// <summary>
/// Owns one engine's parse admission: the completed-document cache, the shared
/// in-flight parses and their queue, the admission limits, and disposal of the
/// engine's owned resources once running work and cancellation callbacks have
/// exited. All of that state is guarded by one gate so cache admission stays
/// atomic with in-flight completion.
/// </summary>
internal sealed class MarkdownParseCoordinator
{
    private readonly object _parseGate = new();
    private readonly MarkdownCompletedParseCache _completedParses;
    private readonly Dictionary<MarkdownParseSourceKey, MarkdownInFlightParse> _inFlightParses =
        new(MarkdownParseSourceKeyComparer.Instance);
    private readonly LinkedList<MarkdownInFlightParse> _queuedParses = new();
    private readonly MarkdownDocumentParser _parser;
    private readonly MarkdownParseLimits _parseLimits;
    private readonly long _parseCacheBudgetBytes;
    private readonly IReadOnlyList<IDisposable> _ownedResources;
    private readonly MarkdownExtensionCallbackLifetime? _extensionCallbackLifetime;
    private int _activeParseCount;
    private int _pendingCancellationRetirementCount;
    private int _outstandingParseCount;
    private long _outstandingSourceBytes;
    private long _sourceKeyHashCount;
    private long _parseGeneration;
    private bool _disposeRequested;
    private bool _extensionCallbacksRetired;
    private bool _ownedResourcesDisposed;

    internal MarkdownParseCoordinator(
        MarkdownDocumentParser parser,
        MarkdownParseLimits parseLimits,
        long parseCacheBudgetBytes,
        IReadOnlyList<IDisposable> ownedResources,
        MarkdownExtensionCallbackLifetime? extensionCallbackLifetime)
    {
        _parser = parser;
        _parseLimits = parseLimits;
        _parseCacheBudgetBytes = parseCacheBudgetBytes;
        _completedParses = new MarkdownCompletedParseCache(parseCacheBudgetBytes);
        _ownedResources = ownedResources;
        _extensionCallbackLifetime = extensionCallbackLifetime;
        _extensionCallbacksRetired = extensionCallbackLifetime is null;
    }

    internal int ActiveParseCount
    {
        get
        {
            lock (_parseGate)
                return _activeParseCount;
        }
    }

    internal int CompletedParseCount
    {
        get
        {
            lock (_parseGate)
                return _completedParses.Count;
        }
    }

    internal int QueuedParseCount
    {
        get
        {
            lock (_parseGate)
                return _queuedParses.Count;
        }
    }

    internal int OutstandingParseCount
    {
        get
        {
            lock (_parseGate)
                return _outstandingParseCount;
        }
    }

    internal long SourceKeyHashCount => Interlocked.Read(ref _sourceKeyHashCount);

    internal Task<MarkdownDocument> ParseAsync(
        string? source,
        bool deferOffscreenScenes,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<MarkdownDocument>(cancellationToken);

        string sourceSnapshot = source ?? string.Empty;
        if (sourceSnapshot.Length > _parseLimits.MaximumSourceLength)
        {
            return Task.FromException<MarkdownDocument>(new ArgumentOutOfRangeException(
                nameof(source),
                $"Markdown source cannot exceed {_parseLimits.MaximumSourceLength} UTF-16 code units."));
        }

        long sourceBytes = checked((long)sourceSnapshot.Length * sizeof(char));

        // The overwhelmingly common renderer path assigns the exact same
        // immutable string instance repeatedly. Check the most recent completed
        // lookup by identity before computing String.GetHashCode, which is
        // O(source length) and otherwise makes a 10 MiB cache hit cost several
        // milliseconds. The entry pointer never outlives its admitted cache
        // entry, and the lookup source is held weakly so an equal but distinct
        // source cannot extend cache retention or escape the configured budget.
        // With completed caching disabled there can never be an identity hit,
        // so avoid an otherwise redundant lock before the normal admission
        // lock. This keeps cache-disabled and concurrent miss workloads from
        // paying two serialized gate acquisitions per request.
        if (_parseCacheBudgetBytes > 0)
        {
            lock (_parseGate)
            {
                if (_disposeRequested)
                {
                    return Task.FromException<MarkdownDocument>(
                        new ObjectDisposedException(nameof(MarkdownEngine)));
                }

                if (_completedParses.TryGetBySourceIdentity(
                        sourceSnapshot,
                        deferOffscreenScenes,
                        out var identityCachedDocument))
                {
                    return Task.FromResult(identityCachedDocument);
                }
            }
        }

        MarkdownParseSourceKey sourceKey = CreateSourceKey(sourceSnapshot, deferOffscreenScenes);
        MarkdownInFlightParse inFlight;
        bool startParse = false;
        lock (_parseGate)
        {
            if (_disposeRequested)
            {
                return Task.FromException<MarkdownDocument>(
                    new ObjectDisposedException(nameof(MarkdownEngine)));
            }

            if (_completedParses.TryGet(sourceKey, out var cachedDocument))
                return Task.FromResult(cachedDocument);

            if (!_inFlightParses.TryGetValue(sourceKey, out inFlight!) || inFlight.IsAbandoned)
            {
                if (_outstandingParseCount >= _parseLimits.MaximumOutstandingParseCount ||
                    _outstandingSourceBytes > _parseLimits.MaximumOutstandingSourceBytes - sourceBytes)
                {
                    return Task.FromException<MarkdownDocument>(new InvalidOperationException(
                        "The markdown parse admission limit is full. Retry after an outstanding parse completes."));
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

            inFlight.WaiterCount++;
        }

        if (startParse)
            StartParse(inFlight);

        return AwaitParseAsync(inFlight, cancellationToken);
    }

    private async Task<MarkdownDocument> AwaitParseAsync(
        MarkdownInFlightParse inFlight,
        CancellationToken cancellationToken)
    {
        bool receivedDocument = false;
        try
        {
            if (!cancellationToken.CanBeCanceled)
            {
                MarkdownDocument document = await inFlight.ParseTask.ConfigureAwait(false);
                receivedDocument = true;
                return document;
            }

            MarkdownDocument cancelableDocument = await inFlight.ParseTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            receivedDocument = true;
            return cancelableDocument;
        }
        finally
        {
            ReleaseWaiter(inFlight, receivedDocument);
        }
    }

    private void StartParse(MarkdownInFlightParse inFlight)
    {
        _ = Task.Run(() => ExecuteParseAsync(inFlight), CancellationToken.None);
    }

    private async Task ExecuteParseAsync(MarkdownInFlightParse inFlight)
    {
        MarkdownDocument? document = null;
        long documentWeight = 0;
        bool publish = false;
        Exception? failure = null;
        try
        {
            document = await _parser.ParseAsync(
                    inFlight.Source,
                    inFlight.Key.DeferOffscreenScenes,
                    inFlight.CancellationToken)
                .ConfigureAwait(false);
            inFlight.CancellationToken.ThrowIfCancellationRequested();
            if (_parseCacheBudgetBytes > 0)
            {
                long minimumWeight = MarkdownDocumentWeightEstimator.EstimateMinimum(document);
                documentWeight = minimumWeight > _parseCacheBudgetBytes
                    ? minimumWeight
                    : MarkdownDocumentWeightEstimator.Estimate(document, inFlight.CancellationToken);
            }
            inFlight.CancellationToken.ThrowIfCancellationRequested();
            publish = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        MarkdownInFlightParse? next = null;
        CancellationToken operationToken = inFlight.CancellationToken;
        Task cancellationRetirement = inFlight.RetireCancellation();
        TrackCancellationRetirement(cancellationRetirement);
        try
        {
            next = CompleteInFlight(
                inFlight,
                publish ? document : null,
                documentWeight);
            if (next is not null)
                StartParse(next);

            DisposeOwnedResourcesIfReady();
        }
        catch (Exception cleanupFailure)
        {
            // Coordination/disposal failures must never strand every waiter on
            // an uncompleted task. Preserve a parse failure when one already
            // exists; otherwise surface the cleanup failure deterministically.
            failure ??= cleanupFailure;
        }
        finally
        {
            if (failure is OperationCanceledException && operationToken.IsCancellationRequested)
                inFlight.Completion.TrySetCanceled(operationToken);
            else if (failure is not null)
                inFlight.Completion.TrySetException(failure);
            else
                inFlight.Completion.TrySetResult(document!);
        }
    }

    private void ReleaseWaiter(MarkdownInFlightParse inFlight, bool receivedDocument)
    {
        bool cancelAbandonedWork = false;
        bool completeQueuedCancellation = false;
        CancellationToken queuedCancellationToken = default;
        lock (_parseGate)
        {
            if (inFlight.WaiterCount > 0)
                inFlight.WaiterCount--;
            if (receivedDocument)
                inFlight.SuccessfulWaiterCount++;

            if (inFlight.WaiterCount != 0)
                return;

            if (inFlight.IsCompleted)
            {
                // Completion and caller cancellation can race after the parsed
                // document has been admitted but before the completion source
                // is published. If every waiter observed cancellation/failure,
                // retire only the entry created by this operation. A newer
                // same-source parse must never be evicted by the older caller.
                if (inFlight.SuccessfulWaiterCount == 0)
                    _completedParses.RemoveIfUnobserved(inFlight.Key, inFlight.ParseGeneration);
                return;
            }

            if (inFlight.IsAbandoned)
                return;

            inFlight.IsAbandoned = true;
            if (_inFlightParses.TryGetValue(inFlight.Key, out var current) &&
                ReferenceEquals(current, inFlight))
            {
                _inFlightParses.Remove(inFlight.Key);
            }

            if (!inFlight.IsStarted)
            {
                if (inFlight.QueueNode is not null)
                {
                    _queuedParses.Remove(inFlight.QueueNode);
                    inFlight.QueueNode = null;
                }

                inFlight.IsCompleted = true;
                _outstandingParseCount--;
                _outstandingSourceBytes -= inFlight.SourceBytes;
                queuedCancellationToken = inFlight.CancellationToken;
                completeQueuedCancellation = true;
            }
            else
            {
                cancelAbandonedWork = true;
            }
        }

        // The operation was already removed and marked abandoned under the
        // gate, so it cannot publish while it unwinds. CancelAsync marks the
        // token immediately but keeps extension registrations off this caller.
        if (cancelAbandonedWork)
            inFlight.RequestCancellation();
        else if (completeQueuedCancellation)
            inFlight.RequestCancellation();

        if (completeQueuedCancellation)
        {
            inFlight.Completion.TrySetCanceled(queuedCancellationToken);
            _ = inFlight.RetireCancellation();
        }
    }

    private MarkdownInFlightParse? CompleteInFlight(
        MarkdownInFlightParse inFlight,
        MarkdownDocument? document,
        long documentWeight)
    {
        lock (_parseGate)
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
            if (document is not null &&
                isCurrent &&
                !inFlight.IsAbandoned &&
                !inFlight.CancellationToken.IsCancellationRequested)
            {
                _completedParses.Add(
                    inFlight.Key,
                    document,
                    documentWeight,
                    inFlight.ParseGeneration);
            }

            while (!_disposeRequested && _queuedParses.First is { } first)
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
    }

    /// <summary>
    /// Cancels admitted work, rejects later parses, and defers resource release
    /// until running extension work and cancellation registrations have exited.
    /// </summary>
    internal void Dispose()
    {
        List<MarkdownInFlightParse>? queued = null;
        List<MarkdownInFlightParse>? running = null;
        Task? extensionCallbackRetirement = null;
        lock (_parseGate)
        {
            if (_disposeRequested)
                return;

            _disposeRequested = true;
            extensionCallbackRetirement = _extensionCallbackLifetime?.BeginDispose();
            if (extensionCallbackRetirement is null || extensionCallbackRetirement.IsCompleted)
                _extensionCallbacksRetired = true;
            _completedParses.Clear();

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

        if (extensionCallbackRetirement is { IsCompleted: false })
        {
            _ = extensionCallbackRetirement.ContinueWith(
                static (_, state) => ((MarkdownParseCoordinator)state!).OnExtensionCallbacksRetired(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        CancelOperations(running, complete: false);
        CancelOperations(queued, complete: true);
        DisposeOwnedResourcesIfReady();
    }

    private static void CancelOperations(List<MarkdownInFlightParse>? operations, bool complete)
    {
        if (operations is null)
            return;

        foreach (MarkdownInFlightParse operation in operations)
        {
            CancellationToken token = operation.CancellationToken;
            operation.RequestCancellation();

            if (!complete)
                continue;

            operation.Completion.TrySetCanceled(token);
            _ = operation.RetireCancellation();
        }
    }

    private void TrackCancellationRetirement(Task retirement)
    {
        if (retirement.IsCompleted)
            return;

        lock (_parseGate)
            _pendingCancellationRetirementCount++;

        _ = retirement.ContinueWith(
            static (_, state) => ((MarkdownParseCoordinator)state!).OnCancellationRetired(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnCancellationRetired()
    {
        lock (_parseGate)
            _pendingCancellationRetirementCount--;

        DisposeOwnedResourcesIfReady();
    }

    private void OnExtensionCallbacksRetired()
    {
        lock (_parseGate)
            _extensionCallbacksRetired = true;

        DisposeOwnedResourcesIfReady();
    }

    private void DisposeOwnedResourcesIfReady()
    {
        IReadOnlyList<IDisposable>? resources = null;
        lock (_parseGate)
        {
            // The callback lease may have retired synchronously while its
            // RunContinuationsAsynchronously notification is still queued.
            // Observe the completed lifetime directly on this cleanup path so
            // disposal does not depend on a free ThreadPool worker.
            if (!_extensionCallbacksRetired && _extensionCallbackLifetime?.IsRetired == true)
                _extensionCallbacksRetired = true;

            if (!_disposeRequested ||
                _ownedResourcesDisposed ||
                _activeParseCount != 0 ||
                _pendingCancellationRetirementCount != 0 ||
                !_extensionCallbacksRetired)
                return;

            _ownedResourcesDisposed = true;
            resources = _ownedResources;
        }

        for (int index = resources.Count - 1; index >= 0; index--)
        {
            try
            {
                resources[index].Dispose();
            }
            catch
            {
                // Cleanup is best-effort and all owners must be visited. Feature
                // services expose their own diagnostics before engine teardown.
            }
        }
    }

    private MarkdownParseSourceKey CreateSourceKey(string source, bool deferOffscreenScenes)
    {
        Interlocked.Increment(ref _sourceKeyHashCount);
        return MarkdownParseSourceKey.Create(source, deferOffscreenScenes);
    }
}
