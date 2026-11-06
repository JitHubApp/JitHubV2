using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance;

/// <summary>
/// One document's view of the session's image services. It de-duplicates
/// requests per source within the document, links them to the document's
/// cancellation, keeps unkeyed results document-local, and releases those
/// entries when the document closes.
/// </summary>
internal sealed class MarkdownPerformanceDocumentScope : IMarkdownPerformanceDocumentScope
{
    private readonly IMarkdownPerformanceSessionInternal _session;
    private readonly MarkdownSessionImageServices _services;
    private readonly IMarkdownImageResolver _resolver;
    private readonly object _resolverCacheIdentity;
    private readonly MarkdownImageResolveContext _context;
    private readonly RetirableCancellationTokenSource _scopeLifetime;
    private readonly CancellationToken _token;
    private readonly object _inFlightGate = new();
    private readonly Dictionary<string, DocumentImageRequest> _inFlight =
        new(StringComparer.Ordinal);
    private int _disposed;

    internal MarkdownPerformanceDocumentScope(
        IMarkdownPerformanceSessionInternal session,
        MarkdownSessionImageServices services,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context)
    {
        _session = session;
        _services = services;
        _resolver = resolver;
        // Cache keys preserve resolver reference identity without owning
        // arbitrary host/plugin state after its last document is closed.
        _resolverCacheIdentity = services.Cache.GetResolverIdentity(resolver);
        _context = context;
        _scopeLifetime = RetirableCancellationTokenSource.CreateLinked(services.Lifetime.Token);
        _token = _scopeLifetime.Token;
    }

    internal bool Matches(
        IMarkdownPerformanceSessionInternal session,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) =>
        ReferenceEquals(_session, session) &&
        ReferenceEquals(_resolver, resolver) &&
        Equals(_context, context) &&
        Volatile.Read(ref _disposed) == 0;

    internal CancellationToken CancellationToken => _token;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    bool IMarkdownPerformanceDocumentScope.IsDisposed => IsDisposed;
    CancellationToken IMarkdownPerformanceDocumentScope.CancellationToken => CancellationToken;
    bool IMarkdownPerformanceDocumentScope.Matches(
        IMarkdownPerformanceSessionInternal session,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) => Matches(session, resolver, context);
    bool IMarkdownPerformanceDocumentScope.TryGetPrefetchedRasterIntrinsicMetadata(
        string source,
        out RasterIntrinsicMetadata metadata)
    {
        metadata = default;
        if (IsDisposed || !_services.Options.PrefetchDocumentImages ||
            !_services.Options.UseDisplaySizedRasterDecode ||
            source is null || source.Length > 2048)
        {
            return false;
        }

        ImageSourceKey sharedKey = new(_resolverCacheIdentity, _context, source, null);
        ImageSourceKey localKey = sharedKey with { DocumentScope = this };
        return _services.Cache.TryGetPrefetchedRasterIntrinsicMetadata(
            sharedKey, localKey, out metadata);
    }
    Task IMarkdownPerformanceDocumentScope.PrefetchAsync(
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken) => PrefetchAsync(sources, cancellationToken);

    async ValueTask<IMarkdownImageResolutionLease>
        IMarkdownPerformanceDocumentScope.ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
    {
        ValidateResolutionRequest(context);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _token);
        return await ResolveCoreWithSourceLeaseAsync(
                source, background: false, linked.Token)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        ValidateResolutionRequest(context);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _token);
        return await ResolveCoreAsync(source, background: false, linked.Token)
            .ConfigureAwait(false);
    }

    private void ValidateResolutionRequest(MarkdownImageResolveContext context)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new OperationCanceledException(_token);
        if (!Equals(context, _context))
            throw new InvalidOperationException("The image resolution context changed during document preparation.");
    }

    internal async Task PrefetchAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken)
    {
        if (!_services.Options.PrefetchDocumentImages || sources.Count == 0)
            return;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _token);
        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions
            {
                CancellationToken = linked.Token,
                MaxDegreeOfParallelism = _services.Options.MaxConcurrentImageFetches -
                    _services.Options.ReservedVisibleImageFetches,
            },
            async (source, token) =>
            {
                try
                {
                    if (!_services.Options.UseDisplaySizedRasterDecode)
                    {
                        await ResolveCoreAsync(source, background: true, token).ConfigureAwait(false);
                        return;
                    }

                    if (((IMarkdownPerformanceDocumentScope)this)
                        .TryGetPrefetchedRasterIntrinsicMetadata(source, out _))
                    {
                        return;
                    }

                    IMarkdownImageResolutionLease? sourceLease = null;
                    try
                    {
                        sourceLease = await ResolveCoreWithSourceLeaseAsync(
                                source, background: true, token)
                            .ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        MarkdownImageAsset? asset = sourceLease.Resolution.Asset;
                        if (asset is null ||
                            !TryCapturePrefetchedRasterTarget(
                                source, asset, out ImageSourceKey storedKey, out CachedImageSourceIdentity identity))
                        {
                            return;
                        }

                        // Header parsing is bounded but scans untrusted input.
                        // Keep it on the opted-in prefetch worker and inside
                        // the existing CPU/source admission lifetimes.
                        using IDisposable cpuAdmission = await _services.Admissions.EnterCpuPreparationAsync(
                                this, exclusive: false, token)
                            .ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (!RasterIntrinsicMetadata.TryRead(
                                asset.Bytes, out RasterIntrinsicMetadata metadata, token))
                        {
                            return;
                        }
                        token.ThrowIfCancellationRequested();
                        TryPublishPrefetchedRasterIntrinsicMetadata(
                            storedKey, identity, asset, metadata);
                    }
                    finally
                    {
                        if (sourceLease is not null)
                            ImageResolutionLeaseCleanup.DisposeSafely(sourceLease);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch
                {
                    // Visible loads remain authoritative and can retry failures.
                }
            }).ConfigureAwait(false);
    }

    private bool TryCapturePrefetchedRasterTarget(
        string source,
        MarkdownImageAsset asset,
        out ImageSourceKey storedKey,
        out CachedImageSourceIdentity identity)
    {
        identity = default;
        storedKey = default;
        if (IsDisposed || _services.Lifetime.IsDisposed)
            return false;

        ImageSourceKey sharedKey = new(_resolverCacheIdentity, _context, source, null);
        ImageSourceKey localKey = sharedKey with { DocumentScope = this };
        // Select the same shared-vs-document key used by Store before
        // capturing its identity; a later replacement must not accept
        // metadata parsed from this lease.
        storedKey = string.IsNullOrEmpty(asset.CacheKey) ? localKey : sharedKey;
        return _services.Cache.TryGetIdentity(storedKey, asset, out identity);
    }

    private void TryPublishPrefetchedRasterIntrinsicMetadata(
        ImageSourceKey storedKey,
        CachedImageSourceIdentity identity,
        MarkdownImageAsset asset,
        RasterIntrinsicMetadata metadata)
    {
        if (IsDisposed || _services.Lifetime.IsDisposed)
            return;
        _services.Cache.TryPublishRasterIntrinsicMetadata(storedKey, identity, asset, metadata);
    }

    private async Task<MarkdownImageResolution> ResolveCoreAsync(
        string source,
        bool background,
        CancellationToken cancellationToken
        )
    {
        IMarkdownImageResolutionLease lease = await ResolveCoreWithSourceLeaseAsync(
                source, background, cancellationToken
                )
            .ConfigureAwait(false);
        try
        {
            return lease.Resolution;
        }
        finally
        {
            // Compatibility callers receive the historical raw result.
            // Their byte lifetime is outside the strict retained-source
            // guarantee, so release any transferred provider reservation.
            ImageResolutionLeaseCleanup.DisposeSafely(lease);
        }
    }

    private async Task<IMarkdownImageResolutionLease> ResolveCoreWithSourceLeaseAsync(
        string source,
        bool background,
        CancellationToken cancellationToken
        )
    {
        ImageSourceKey key = new(_resolverCacheIdentity, _context, source, null);
        ImageSourceKey localKey = key with { DocumentScope = this };
        IMarkdownImageResolutionLease? cachedLease = await _services.Fetcher.TryGetLeasedSourceAsync(
                key, this, background, cancellationToken)
            .ConfigureAwait(false);
        cachedLease ??= await _services.Fetcher.TryGetLeasedSourceAsync(
                localKey, this, background, cancellationToken)
            .ConfigureAwait(false);
        if (cachedLease is not null)
            return cachedLease;

        DocumentImageRequest request = JoinOrCreateRequest(
            source, background, out DocumentImageRequest? abandoned, out bool startRequest);
        if (abandoned is not null)
        {
            try { abandoned.RequestCancellation(); }
            catch (ObjectDisposedException) { }
        }

        // Starting ResolveSharedAsync inside this gate would run through
        // its first incomplete await. That path can acquire a fetch slot
        // and call a host resolver synchronously, blocking every other
        // source in this document while the host is doing unrelated work.
        if (startRequest)
            StartRequest(request, source, key, localKey, background);
        try
        {
            await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return request.RetainResult();
        }
        finally
        {
            LeaveRequest(source, request);
        }
    }

    // Joins this document's request for the source, or creates one. A
    // visible request replaces a background one, which the caller cancels.
    private DocumentImageRequest JoinOrCreateRequest(
        string source,
        bool background,
        out DocumentImageRequest? abandoned,
        out bool startRequest)
    {
        abandoned = null;
        startRequest = false;
        DocumentImageRequest request;
        lock (_inFlightGate)
        {
            if (_disposed != 0)
                throw new OperationCanceledException(_token);
            if (_inFlight.TryGetValue(source, out DocumentImageRequest? existing) &&
                !background && existing.Background)
            {
                // A queued or paused prefetch must never hold up a visible
                // image. Its cancellation retires the speculative slot.
                abandoned = existing;
                _inFlight.Remove(source);
            }

            if (!_inFlight.TryGetValue(source, out request!))
            {
                SessionWorkLease? requestLifetime = _services.Lifetime.TryBeginWorkLease();
                if (requestLifetime is null)
                    throw new OperationCanceledException(_services.Lifetime.Token);
                RetirableCancellationTokenSource? linked = null;
                try
                {
                    linked = RetirableCancellationTokenSource.CreateLinked(_token);
                    request = new DocumentImageRequest(
                        linked, background, _services.Lifetime, requestLifetime)
                    { WaiterCount = 1 };
                    _ = request.Task.ContinueWith(
                        static completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    _inFlight.Add(source, request);
                    linked = null;
                    requestLifetime = null;
                    startRequest = true;
                }
                catch
                {
                    linked?.Dispose();
                    requestLifetime?.Dispose();
                    throw;
                }
            }
            else
            {
                request.WaiterCount++;
            }
        }

        return request;
    }

    private void StartRequest(
        DocumentImageRequest startedRequest,
        string source,
        ImageSourceKey key,
        ImageSourceKey localKey,
        bool background)
    {
        startedRequest.Start(
            () => _services.Fetcher.ResolveSharedAsync(
                this, key, _resolver, _context, background,
                startedRequest.Cancellation.Token
                ),
            (result, operationCompletion) =>
            {
                bool ownsRoot = false;
                try
                {
                    MarkdownImageResolution resolution = result.Resolution;
                    if (string.IsNullOrEmpty(resolution.Asset?.CacheKey))
                        _services.Cache.Store(key, localKey, resolution);

                    bool releaseRoot;
                    lock (_inFlightGate)
                    {
                        RemoveCurrentRequestNoLock(source, startedRequest);
                        bool published = startedRequest.SetResult(result);
                        ownsRoot = true;
                        releaseRoot = !published || startedRequest.WaiterCount == 0;
                    }
                    if (releaseRoot)
                        startedRequest.ReleaseRootSafely();
                }
                catch
                {
                    if (!ownsRoot)
                        ImageResolutionLeaseCleanup.DisposeSafely(result);
                    throw;
                }
                finally
                {
                    startedRequest.RetireCancellation(operationCompletion);
                }
            },
            (exception, operationCompletion) =>
            {
                try
                {
                    lock (_inFlightGate)
                    {
                        RemoveCurrentRequestNoLock(source, startedRequest);
                        startedRequest.SetException(exception);
                    }
                }
                finally
                {
                    startedRequest.RetireCancellation(operationCompletion);
                }
            });
    }

    // The last waiter to leave an unfinished request cancels it; the last
    // waiter to leave a finished request releases its result.
    private void LeaveRequest(string source, DocumentImageRequest request)
    {
        bool cancel = false;
        bool releaseRoot = false;
        lock (_inFlightGate)
        {
            if (--request.WaiterCount == 0)
            {
                if (!request.Task.IsCompleted)
                {
                    RemoveCurrentRequestNoLock(source, request);
                    cancel = true;
                }
                else
                {
                    releaseRoot = true;
                }
            }
        }
        if (cancel)
        {
            try { request.RequestCancellation(); }
            catch (ObjectDisposedException) { }
        }
        if (releaseRoot)
            request.ReleaseRootSafely();
    }

    private void RemoveCurrentRequestNoLock(string source, DocumentImageRequest request)
    {
        if (_inFlight.TryGetValue(source, out DocumentImageRequest? current) &&
            ReferenceEquals(current, request))
            _inFlight.Remove(source);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Task[] admitted;
        lock (_inFlightGate)
        {
            admitted = new Task[_inFlight.Count];
            int index = 0;
            foreach (DocumentImageRequest request in _inFlight.Values)
                admitted[index++] = request.Task;
        }
        _ = CancellationTokenSourceRetirement.CancelAndDisposeAfter(
            _scopeLifetime, Task.WhenAll(admitted));
        _services.Cache.RemoveDocumentEntries(this);
    }
}
