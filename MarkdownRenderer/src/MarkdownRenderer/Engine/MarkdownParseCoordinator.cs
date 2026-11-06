using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// <remarks>
/// This file is longer than 500 lines because admission, in-flight completion
/// and deferred disposal read and write the same gated state: disposal waits
/// on the active parse count and pending cancellation retirements that
/// completion updates, under the same lock. The cache and in-flight table
/// themselves live in <see cref="MarkdownCompletedParseCache"/> and
/// <see cref="MarkdownInFlightParseTable"/>.
/// </remarks>
internal sealed class MarkdownParseCoordinator
{
    private readonly object _parseGate = new();
    private readonly MarkdownCompletedParseCache _completedParses;
    private readonly MarkdownInFlightParseTable _inFlightParses;
    private readonly MarkdownDocumentParser _parser;
    private readonly MarkdownParseLimits _parseLimits;
    private readonly long _parseCacheBudgetBytes;
    private readonly IReadOnlyList<IDisposable> _ownedResources;
    private readonly MarkdownExtensionCallbackLifetime? _extensionCallbackLifetime;
    private int _pendingCancellationRetirementCount;
    private long _sourceKeyHashCount;
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
        _inFlightParses = new MarkdownInFlightParseTable(parseLimits);
        _ownedResources = ownedResources;
        _extensionCallbackLifetime = extensionCallbackLifetime;
        _extensionCallbacksRetired = extensionCallbackLifetime is null;
    }

    internal int ActiveParseCount
    {
        get
        {
            lock (_parseGate)
                return _inFlightParses.ActiveCount;
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
                return _inFlightParses.QueuedCount;
        }
    }

    internal int OutstandingParseCount
    {
        get
        {
            lock (_parseGate)
                return _inFlightParses.OutstandingCount;
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
        if (_parseCacheBudgetBytes > 0 &&
            TryCompleteFromSourceIdentity(
                sourceSnapshot,
                deferOffscreenScenes,
                out Task<MarkdownDocument>? identityResult))
        {
            return identityResult;
        }

        MarkdownParseSourceKey sourceKey = CreateSourceKey(sourceSnapshot, deferOffscreenScenes);
        if (!TryJoinOrAdmit(
                sourceKey,
                sourceBytes,
                out MarkdownInFlightParse? inFlight,
                out bool startParse,
                out Task<MarkdownDocument>? immediateResult))
        {
            return immediateResult;
        }

        if (startParse)
            StartParse(inFlight);

        return AwaitParseAsync(inFlight, cancellationToken);
    }

    /// <summary>
    /// Completes the request from the identity shortcut, or with the disposal
    /// failure, under the gate. Returns false when the keyed path must run.
    /// </summary>
    private bool TryCompleteFromSourceIdentity(
        string sourceSnapshot,
        bool deferOffscreenScenes,
        [NotNullWhen(true)] out Task<MarkdownDocument>? result)
    {
        lock (_parseGate)
        {
            if (_disposeRequested)
            {
                result = Task.FromException<MarkdownDocument>(
                    new ObjectDisposedException(nameof(MarkdownEngine)));
                return true;
            }

            if (_completedParses.TryGetBySourceIdentity(
                    sourceSnapshot,
                    deferOffscreenScenes,
                    out var identityCachedDocument))
            {
                result = Task.FromResult(identityCachedDocument);
                return true;
            }
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Under the gate, returns a cached document, joins the current in-flight
    /// parse for the key, or admits a new one (started now or queued). Returns
    /// false with <paramref name="immediateResult"/> when the request completes
    /// without waiting: disposed engine, cache hit, or a full admission limit.
    /// </summary>
    private bool TryJoinOrAdmit(
        MarkdownParseSourceKey sourceKey,
        long sourceBytes,
        [NotNullWhen(true)] out MarkdownInFlightParse? joined,
        out bool startParse,
        [NotNullWhen(false)] out Task<MarkdownDocument>? immediateResult)
    {
        joined = null;
        startParse = false;
        immediateResult = null;
        lock (_parseGate)
        {
            if (_disposeRequested)
            {
                immediateResult = Task.FromException<MarkdownDocument>(
                    new ObjectDisposedException(nameof(MarkdownEngine)));
                return false;
            }

            if (_completedParses.TryGet(sourceKey, out var cachedDocument))
            {
                immediateResult = Task.FromResult(cachedDocument);
                return false;
            }

            if (!_inFlightParses.TryJoinOrAdmit(
                    sourceKey,
                    sourceBytes,
                    out MarkdownInFlightParse? inFlight,
                    out startParse))
            {
                immediateResult = Task.FromException<MarkdownDocument>(new InvalidOperationException(
                    "The markdown parse admission limit is full. Retry after an outstanding parse completes."));
                return false;
            }

            inFlight.WaiterCount++;
            joined = inFlight;
        }

        return true;
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
                documentWeight = MarkdownDocumentWeightEstimator.EstimateForBudget(
                    document,
                    _parseCacheBudgetBytes,
                    inFlight.CancellationToken);
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

            if (_inFlightParses.Abandon(inFlight))
            {
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
            bool isCurrent = _inFlightParses.Complete(inFlight);
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

            return _inFlightParses.StartNextQueued(_disposeRequested);
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
            _inFlightParses.AbandonAll(ref running, ref queued);
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
        MarkdownInFlightParse.CancelAll(running, complete: false);
        MarkdownInFlightParse.CancelAll(queued, complete: true);
        DisposeOwnedResourcesIfReady();
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
                _inFlightParses.ActiveCount != 0 ||
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
