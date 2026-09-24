using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Diagnostics;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Shares bounded image preparation between document views in one security partition.
/// The host owns this service; controls borrow it and own their document scopes.
/// </summary>
public sealed class MarkdownPerformanceSession : IMarkdownPerformanceSessionInternal, IDisposable, IAsyncDisposable
{
    private readonly object _cacheGate = new();
    private readonly Dictionary<SourceKey, CachedSource> _sourceCache = new(SourceKeyComparer.Instance);
    private readonly LinkedList<CachedSource> _sourceRecency = new();
    private readonly Dictionary<DocumentScope, HashSet<SourceKey>> _documentSourceKeys = new();
    private readonly object _flightGate = new();
    private readonly Dictionary<SourceKey, SharedFetch> _sharedFlights = new(SourceKeyComparer.Instance);
    private readonly SemaphoreSlim _fetchSlots;
    private readonly FairDocumentAdmission _backgroundAdmission;
    private readonly FairDocumentAdmission _cpuPreparationAdmission;
    private readonly FairDocumentAdmission _scenePreparationAdmission;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly bool _memoryPressureSubscribed;
    private readonly object _workGate = new();
    private TaskCompletionSource<bool>? _workDrained;
    private TaskCompletionSource<bool>? _shutdownCompleted;
    private int _activeWork;
    private long _sourceCacheBytes;
    private long _sourceCacheHits;
    private long _imageFetches;
    private long _imageFetchMilliseconds;
    private long _imageFetchFailures;
    private long _imageFetchCancellations;
    private long _sourceCacheEvictions;
    private int _pendingImageFetches;
    private int _activeImageFetches;
    private long _cpuPreparations;
    private long _cpuPreparationMilliseconds;
    private long _scenePreparations;
    private long _scenePreparationMilliseconds;
    private int _disposed;

    /// <summary>Creates an opt-in session. Use a distinct session for each account/security partition.</summary>
    public MarkdownPerformanceSession(MarkdownPerformanceOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _lifetimeToken = _lifetime.Token;
        _fetchSlots = new SemaphoreSlim(options.MaxConcurrentImageFetches);
        _backgroundAdmission = new FairDocumentAdmission(
            options.MaxConcurrentImageFetches - options.ReservedVisibleImageFetches);
        _cpuPreparationAdmission = new FairDocumentAdmission(options.MaxConcurrentCpuPreparations);
        _scenePreparationAdmission = new FairDocumentAdmission(options.MaxConcurrentScenePreparations);
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            _memoryPressureSubscribed = true;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            // The byte-budgeted LRU still applies in headless and unpackaged hosts.
        }
    }

    /// <summary>Gets the immutable settings captured at construction.</summary>
    public MarkdownPerformanceOptions Options { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    bool IMarkdownPerformanceSessionInternal.IsDisposed => IsDisposed;

    IMarkdownPerformanceDocumentScope IMarkdownPerformanceSessionInternal.OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) => OpenDocument(resolver, context);

    ValueTask<IDisposable> IMarkdownPerformanceSessionInternal.EnterCpuPreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        EnterCpuPreparationAsync(documentOwner, cancellationToken);

    ValueTask<IMarkdownScenePreparationLease> IMarkdownPerformanceSessionInternal.EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        EnterScenePreparationAsync(documentOwner, cancellationToken);

    /// <summary>Returns aggregate counts without source URLs or content.</summary>
    public MarkdownPerformanceSnapshot GetSnapshot()
    {
        long retainedBytes;
        lock (_cacheGate)
            retainedBytes = _sourceCacheBytes;
        return new MarkdownPerformanceSnapshot(
            retainedBytes,
            Interlocked.Read(ref _sourceCacheHits),
            Interlocked.Read(ref _imageFetches),
            Interlocked.Read(ref _imageFetchMilliseconds),
            Interlocked.Read(ref _imageFetchFailures),
            Interlocked.Read(ref _imageFetchCancellations),
            Interlocked.Read(ref _sourceCacheEvictions),
            Volatile.Read(ref _pendingImageFetches),
            Volatile.Read(ref _activeImageFetches),
            Interlocked.Read(ref _cpuPreparations),
            Interlocked.Read(ref _cpuPreparationMilliseconds),
            Interlocked.Read(ref _scenePreparations),
            Interlocked.Read(ref _scenePreparationMilliseconds));
    }

    /// <summary>Releases retained source bytes; active visible work is unaffected.</summary>
    public void Trim()
    {
        lock (_cacheGate)
        {
            _sourceCache.Clear();
            _sourceRecency.Clear();
            _documentSourceKeys.Clear();
            _sourceCacheBytes = 0;
        }
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args) => Trim();

    internal DocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context)
    {
        lock (_workGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new DocumentScope(this, resolver, context);
        }
    }

    internal async ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken)
    {
        RegisterWork();
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeToken);
            IDisposable admissionLease = await _cpuPreparationAdmission.EnterAsync(
                documentOwner, linked.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _cpuPreparations);
            return new CpuPreparationLease(admissionLease, this, Stopwatch.GetTimestamp());
        }
        catch
        {
            RetireWork();
            throw;
        }
    }

    internal async ValueTask<IMarkdownScenePreparationLease> EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken)
    {
        RegisterWork();
        CancellationTokenSource? linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeToken);
            IDisposable admissionLease = await _scenePreparationAdmission.EnterAsync(
                documentOwner, linked.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _scenePreparations);
            return new ScenePreparationLease(admissionLease, linked, this, Stopwatch.GetTimestamp());
        }
        catch
        {
            linked?.Dispose();
            RetireWork();
            throw;
        }
    }

    private void RegisterWork()
    {
        lock (_workGate)
        {
            if (_disposed != 0)
                throw new OperationCanceledException(_lifetimeToken);
            _activeWork++;
        }
    }

    private void RetireWork()
    {
        lock (_workGate)
        {
            if (--_activeWork == 0)
                _workDrained?.TrySetResult(true);
        }
    }

    private bool TryGet(SourceKey key, out MarkdownImageResolution resolution)
    {
        resolution = default;
        if (key.Source.Length > 2048)
            return false;
        lock (_cacheGate)
        {
            if (IsDisposed)
                return false;
            if (!_sourceCache.TryGetValue(key, out CachedSource? cached))
                return false;
            _sourceRecency.Remove(cached.Node);
            _sourceRecency.AddFirst(cached.Node);
            Interlocked.Increment(ref _sourceCacheHits);
            resolution = MarkdownImageResolution.Resolved(cached.Asset);
            return true;
        }
    }

    private void Store(SourceKey sharedKey, SourceKey localKey, MarkdownImageResolution resolution)
    {
        MarkdownImageAsset? asset = resolution.Asset;
        if (asset is null || sharedKey.Source.Length > 2048 ||
            asset.Bytes.Length > Options.SourceCacheBudgetBytes)
            return;

        SourceKey key = string.IsNullOrEmpty(asset.CacheKey) ? localKey : sharedKey;

        lock (_cacheGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || key.DocumentScope?.IsDisposed == true)
                return;
            if (_sourceCache.TryGetValue(key, out CachedSource? old))
            {
                _sourceRecency.Remove(old.Node);
                _sourceCache.Remove(key);
                _sourceCacheBytes -= old.Asset.Bytes.Length;
            }
            var entry = new CachedSource(key, asset);
            entry.Node = _sourceRecency.AddFirst(entry);
            _sourceCache.Add(key, entry);
            _sourceCacheBytes += asset.Bytes.Length;
            if (key.DocumentScope is { } scope)
            {
                if (!_documentSourceKeys.TryGetValue(scope, out HashSet<SourceKey>? keys))
                    _documentSourceKeys.Add(scope, keys = new HashSet<SourceKey>(SourceKeyComparer.Instance));
                keys.Add(key);
            }
            while (_sourceCacheBytes > Options.SourceCacheBudgetBytes &&
                   _sourceRecency.Last is { } last)
            {
                _sourceRecency.RemoveLast();
                _sourceCache.Remove(last.Value.Key);
                _sourceCacheBytes -= last.Value.Asset.Bytes.Length;
                if (last.Value.Key.DocumentScope is { } evictedScope &&
                    _documentSourceKeys.TryGetValue(evictedScope, out HashSet<SourceKey>? evictedKeys))
                {
                    evictedKeys.Remove(last.Value.Key);
                    if (evictedKeys.Count == 0)
                        _documentSourceKeys.Remove(evictedScope);
                }
                Interlocked.Increment(ref _sourceCacheEvictions);
            }
        }
    }

    private void RemoveDocumentEntries(DocumentScope scope)
    {
        lock (_cacheGate)
        {
            if (!_documentSourceKeys.Remove(scope, out HashSet<SourceKey>? keys))
                return;
            foreach (SourceKey key in keys)
            {
                if (!_sourceCache.Remove(key, out CachedSource? cached))
                    continue;
                _sourceRecency.Remove(cached.Node);
                _sourceCacheBytes -= cached.Asset.Bytes.Length;
            }
        }
    }

    private async Task<MarkdownImageResolution> FetchAsync(
        DocumentScope owner,
        SourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        Action onResolveStarted,
        CancellationToken cancellationToken)
    {
        RegisterWork();
        Interlocked.Increment(ref _pendingImageFetches);
        IDisposable? backgroundLease = null;
        bool fetchAcquired = false;
        bool resolverStarted = false;
        try
        {
            if (background)
            {
                bool remote = IsRemoteSource(key.Source, context);
                while (true)
                {
                    // Paused remote work must not hold a speculative slot that
                    // another document could use for local resources.
                    while (remote && ShouldPauseRemotePrefetch())
                        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)
                            .ConfigureAwait(false);
                    backgroundLease = await _backgroundAdmission.EnterAsync(
                        owner, cancellationToken).ConfigureAwait(false);
                    if (!remote || !ShouldPauseRemotePrefetch())
                        break;
                    backgroundLease.Dispose();
                    backgroundLease = null;
                }
            }
            await _fetchSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            fetchAcquired = true;
            if (TryGet(key, out MarkdownImageResolution cached))
                return cached;

            onResolveStarted();
            long started = Stopwatch.GetTimestamp();
            Interlocked.Decrement(ref _pendingImageFetches);
            Interlocked.Increment(ref _activeImageFetches);
            resolverStarted = true;
            Interlocked.Increment(ref _imageFetches);
            try
            {
                MarkdownImageResolution resolution = await resolver.ResolveAsync(
                    key.Source, context, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(resolution.Asset?.CacheKey))
                    Store(key, key, resolution);
                return resolution;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _imageFetchCancellations);
                throw;
            }
            catch
            {
                Interlocked.Increment(ref _imageFetchFailures);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _activeImageFetches);
                long elapsedTicks = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref _imageFetchMilliseconds,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                MarkdownPerformanceEventSource.Log.ResourceWork(1, elapsedTicks, 1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !resolverStarted)
        {
            Interlocked.Increment(ref _imageFetchCancellations);
            throw;
        }
        finally
        {
            if (!resolverStarted)
                Interlocked.Decrement(ref _pendingImageFetches);
            if (fetchAcquired) _fetchSlots.Release();
            backgroundLease?.Dispose();
            RetireWork();
        }
    }

    private async Task<MarkdownImageResolution> ResolveSharedAsync(
        DocumentScope owner,
        SourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        CancellationToken cancellationToken)
    {
        SharedFetch flight;
        SharedFetch? superseded = null;
        lock (_flightGate)
        {
            if (IsDisposed)
                throw new OperationCanceledException(_lifetimeToken);
            if (!_sharedFlights.TryGetValue(key, out flight!) ||
                (!background && flight.Background && !flight.HasStartedResolution &&
                 !flight.Task.IsCompleted))
            {
                if (flight is not null)
                {
                    superseded = flight;
                    _sharedFlights.Remove(key);
                }

                var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                flight = new SharedFetch(source, background);
                flight.Task = FetchAsync(
                    owner, key, resolver, context, background, flight.MarkResolutionStarted,
                    source.Token);
                _sharedFlights.Add(key, flight);
                _ = flight.Task.ContinueWith(
                    static (completed, state) =>
                    {
                        var (session, sourceKey, request) =
                            ((MarkdownPerformanceSession Session, SourceKey Key, SharedFetch Request))state!;
                        _ = completed.Exception;
                        lock (session._flightGate)
                        {
                            if (session._sharedFlights.TryGetValue(sourceKey, out SharedFetch? current) &&
                                ReferenceEquals(current, request))
                                session._sharedFlights.Remove(sourceKey);
                        }
                        request.Cancellation.Dispose();
                    },
                    (this, key, flight),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            flight.WaiterCount++;
        }

        if (superseded is not null)
        {
            try { _ = superseded.Cancellation.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        try
        {
            return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bool cancel = false;
            lock (_flightGate)
            {
                if (--flight.WaiterCount == 0 && !flight.Task.IsCompleted)
                {
                    if (_sharedFlights.TryGetValue(key, out SharedFetch? current) &&
                        ReferenceEquals(current, flight))
                        _sharedFlights.Remove(key);
                    cancel = true;
                }
            }
            if (cancel)
            {
                try { _ = flight.Cancellation.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private sealed class SharedFetch(CancellationTokenSource cancellation, bool background)
    {
        private int _resolutionStarted;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal bool Background { get; } = background;
        internal Task<MarkdownImageResolution> Task { get; set; } = null!;
        internal int WaiterCount { get; set; }
        internal bool HasStartedResolution => Volatile.Read(ref _resolutionStarted) != 0;
        internal void MarkResolutionStarted() => Volatile.Write(ref _resolutionStarted, 1);
    }

    private static bool ShouldPauseRemotePrefetch()
    {
        try
        {
            if (PowerManager.EnergySaverStatus == EnergySaverStatus.On)
                return true;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            // Unpackaged and headless hosts do not always expose power state.
        }

        try
        {
            ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return cost is not null &&
                (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable ||
                 cost.Roaming || cost.OverDataLimit);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            return false;
        }
    }

    private static bool IsRemoteSource(string source, MarkdownImageResolveContext context)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            (context.BaseUri is null || !Uri.TryCreate(context.BaseUri, source, out uri)))
            return false;
        return uri.Scheme is "https" or "http";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task drain;
        TaskCompletionSource<bool> completed;
        lock (_workGate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            _workDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeWork == 0)
                _workDrained.SetResult(true);
            drain = _workDrained.Task;
            completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCompleted = completed;
        }

        Task cancellation = _lifetime.CancelAsync();
        if (_memoryPressureSubscribed)
        {
            try { Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased; }
            catch (Exception exception) when (
                exception is System.Runtime.InteropServices.COMException or
                PlatformNotSupportedException or TypeInitializationException)
            {
            }
        }
        Trim();
        _ = FinishShutdownAsync(cancellation, drain, completed);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? completion;
        lock (_workGate)
            completion = _shutdownCompleted?.Task;
        if (completion is not null)
            await completion.ConfigureAwait(false);
    }

    private async Task FinishShutdownAsync(
        Task cancellation,
        Task drain,
        TaskCompletionSource<bool> completed)
    {
        Exception? failure = null;
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            _fetchSlots.Dispose();
            _lifetime.Dispose();
        }

        if (failure is null)
            completed.TrySetResult(true);
        else
            completed.TrySetException(failure);
    }

    internal sealed class DocumentScope : IMarkdownPerformanceDocumentScope
    {
        private readonly MarkdownPerformanceSession _session;
        private readonly IMarkdownImageResolver _resolver;
        private readonly MarkdownImageResolveContext _context;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _token;
        private readonly object _inFlightGate = new();
        private readonly Dictionary<string, InflightRequest> _inFlight =
            new(StringComparer.Ordinal);
        private int _disposed;

        internal DocumentScope(
            MarkdownPerformanceSession session,
            IMarkdownImageResolver resolver,
            MarkdownImageResolveContext context)
        {
            _session = session;
            _resolver = resolver;
            _context = context;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(session._lifetimeToken);
            _token = _lifetime.Token;
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
        Task IMarkdownPerformanceDocumentScope.PrefetchAsync(
            IReadOnlyList<string> sources,
            CancellationToken cancellationToken) => PrefetchAsync(sources, cancellationToken);

        /// <inheritdoc />
        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new OperationCanceledException(_token);
            if (!Equals(context, _context))
                throw new InvalidOperationException("The image resolution context changed during document preparation.");
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _token);
            return await ResolveCoreAsync(source, background: false, linked.Token)
                .ConfigureAwait(false);
        }

        internal async Task PrefetchAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken)
        {
            if (!_session.Options.PrefetchDocumentImages || sources.Count == 0)
                return;
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _token);
            await Parallel.ForEachAsync(
                sources,
                new ParallelOptions
                {
                    CancellationToken = linked.Token,
                    MaxDegreeOfParallelism = _session.Options.MaxConcurrentImageFetches -
                        _session.Options.ReservedVisibleImageFetches,
                },
                async (source, token) =>
                {
                    try
                    {
                        await ResolveCoreAsync(source, background: true, token).ConfigureAwait(false);
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

        private async Task<MarkdownImageResolution> ResolveCoreAsync(
            string source,
            bool background,
            CancellationToken cancellationToken)
        {
            SourceKey key = new(_resolver, _context, source, null);
            SourceKey localKey = key with { DocumentScope = this };
            if (_session.TryGet(key, out MarkdownImageResolution cached) ||
                _session.TryGet(localKey, out cached))
                return cached;

            InflightRequest request;
            InflightRequest? abandoned = null;
            lock (_inFlightGate)
            {
                if (_disposed != 0)
                    throw new OperationCanceledException(_token);
                if (_inFlight.TryGetValue(source, out InflightRequest? existing) &&
                    !background && existing.Background &&
                    !existing.Task.IsCompleted)
                {
                    // A queued or paused prefetch must never hold up a visible
                    // image. Its cancellation retires the speculative slot.
                    abandoned = existing;
                    _inFlight.Remove(source);
                }

                if (!_inFlight.TryGetValue(source, out request!))
                {
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(_token);
                    request = new InflightRequest(linked, background);
                    request.Task = _session.ResolveSharedAsync(
                        this, key, _resolver, _context, background, linked.Token);
                    _inFlight.Add(source, request);
                    _ = request.Task.ContinueWith(
                        static (completed, state) =>
                        {
                            var (scope, key, request) =
                                ((DocumentScope Scope, string Key, InflightRequest Request))state!;
                            _ = completed.Exception; // Observe faults even after every waiter cancels.
                            lock (scope._inFlightGate)
                            {
                                if (scope._inFlight.TryGetValue(key, out var current) &&
                                    ReferenceEquals(current, request))
                                    scope._inFlight.Remove(key);
                            }
                            request.Cancellation.Dispose();
                        },
                        (this, source, request),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            if (abandoned is not null)
            {
                try { _ = abandoned.Cancellation.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
            MarkdownImageResolution result = await request.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(result.Asset?.CacheKey))
                _session.Store(key, localKey, result);
            return result;
        }

        private sealed class InflightRequest(
            CancellationTokenSource cancellation,
            bool background)
        {
            internal CancellationTokenSource Cancellation { get; } = cancellation;
            internal bool Background { get; } = background;
            internal Task<MarkdownImageResolution> Task { get; set; } = null!;
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
                foreach (InflightRequest request in _inFlight.Values)
                    admitted[index++] = request.Task;
            }
            _ = CancellationTokenSourceRetirement.CancelAndDisposeAfter(
                _lifetime, Task.WhenAll(admitted));
            _session.RemoveDocumentEntries(this);
        }
    }

    private readonly record struct SourceKey(
        IMarkdownImageResolver Resolver,
        MarkdownImageResolveContext Context,
        string Source,
        DocumentScope? DocumentScope);

    private sealed class SourceKeyComparer : IEqualityComparer<SourceKey>
    {
        internal static readonly SourceKeyComparer Instance = new();

        public bool Equals(SourceKey x, SourceKey y) =>
            ReferenceEquals(x.Resolver, y.Resolver) &&
            ReferenceEquals(x.DocumentScope, y.DocumentScope) &&
            Equals(x.Context, y.Context) &&
            string.Equals(x.Source, y.Source, StringComparison.Ordinal);

        public int GetHashCode(SourceKey key) => HashCode.Combine(
            RuntimeHelpers.GetHashCode(key.Resolver),
            key.DocumentScope is null ? 0 : RuntimeHelpers.GetHashCode(key.DocumentScope),
            key.Context,
            StringComparer.Ordinal.GetHashCode(key.Source));
    }

    private sealed class CachedSource(SourceKey key, MarkdownImageAsset asset)
    {
        internal SourceKey Key { get; } = key;
        internal MarkdownImageAsset Asset { get; } = asset;
        internal LinkedListNode<CachedSource> Node { get; set; } = null!;
    }

    private sealed class CpuPreparationLease(
        IDisposable admissionLease,
        MarkdownPerformanceSession session,
        long started) : IDisposable
    {
        private IDisposable? _admissionLease = admissionLease;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _admissionLease, null) is not { } held)
                return;
            try
            {
                held.Dispose();
            }
            finally
            {
                Interlocked.Add(ref session._cpuPreparationMilliseconds,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                session.RetireWork();
            }
        }
    }

    private sealed class ScenePreparationLease(
        IDisposable admissionLease,
        CancellationTokenSource cancellation,
        MarkdownPerformanceSession session,
        long started) : IMarkdownScenePreparationLease
    {
        private IDisposable? _admissionLease = admissionLease;
        public CancellationToken CancellationToken => cancellation.Token;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _admissionLease, null) is not { } held)
                return;
            try
            {
                held.Dispose();
            }
            finally
            {
                cancellation.Dispose();
                Interlocked.Add(ref session._scenePreparationMilliseconds,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                session.RetireWork();
            }
        }
    }
}
