using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Services;

public sealed partial class GitHubImageService : IGitHubLeasedImageService
{
    private readonly ConcurrentDictionary<LeasedFlightKey, SharedLeasedImageRequest> _leasedMisses =
        new(LeasedFlightKeyComparer.Instance);
    private readonly ConcurrentDictionary<LeasedFlightKey, SharedLeasedImageRequest> _leasedRefreshes =
        new(LeasedFlightKeyComparer.Instance);

    Task<GitHubImageSourceLease?> IGitHubLeasedImageService.GetLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken) =>
        GetOrFetchLeasedCoreAsync(
            sourceUrl,
            (cached, token) => FetchHttpAsync(sourceUrl, cached, scope, admission, token),
            scope,
            admission,
            cancellationToken);

    Task<GitHubImageSourceLease?> IGitHubLeasedImageService.TryGetCachedLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken) =>
        TryGetCachedLeasedCoreAsync(sourceUrl, scope, admission, cancellationToken);

    Task<GitHubImageSourceLease?> IGitHubLeasedImageService.GetOrFetchLeasedAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken) =>
        GetOrFetchLeasedCoreAsync(
            sourceUrl,
            (cached, token) => FetchWithRetryAsync(fetcher, cached, token),
            GitHubImageFetchScope.UserApprovedHttps,
            admission,
            cancellationToken);

    private async Task<GitHubImageSourceLease?> TryGetCachedLeasedCoreAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAllowedSource(sourceUrl, scope))
            return null;

        long accountId = _partitionProvider();
        using IAccountWorkLease? accountLease = EnterAccountWork(accountId, cancellationToken);
        CancellationToken operationToken = accountLease?.CancellationToken ?? cancellationToken;
        string cacheKey = GetCacheKey(accountId, sourceUrl);
        if (await GetCacheEntryForLeasedResolutionAsync(cacheKey, operationToken)
                .ConfigureAwait(false) is null)
        {
            return null;
        }

        return await ReadCachedImageLeaseAsync(cacheKey, admission, operationToken)
            .ConfigureAwait(false);
    }

    private async Task<GitHubImageSourceLease?> GetOrFetchLeasedCoreAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAllowedSource(sourceUrl, scope))
            return null;

        long accountId = _partitionProvider();
        using IAccountWorkLease? accountLease = EnterAccountWork(accountId, cancellationToken);
        CancellationToken operationToken = accountLease?.CancellationToken ?? cancellationToken;
        string cacheKey = GetCacheKey(accountId, sourceUrl);
        GitHubImageCacheEntry? cachedEntry = await GetCacheEntryForLeasedResolutionAsync(
                cacheKey,
                operationToken)
            .ConfigureAwait(false);

        if (cachedEntry is not null && DateTimeOffset.UtcNow - cachedEntry.CachedAt <= Freshness)
        {
            GitHubImageSourceLease? cached = await ReadCachedImageLeaseAsync(
                    cacheKey,
                    admission,
                    operationToken)
                .ConfigureAwait(false);
            if (cached is not null)
                return cached;

            // The generation disappeared between metadata lookup and source admission.
            // Fall through to an ordinary miss rather than returning a stale array.
            cachedEntry = null;
        }

        var key = new LeasedFlightKey(cacheKey, admission);
        ConcurrentDictionary<LeasedFlightKey, SharedLeasedImageRequest> flights =
            cachedEntry is null ? _leasedMisses : _leasedRefreshes;
        Task<GitHubImageSourceLease?> sharedTask = GetSharedLeasedTask(
            flights,
            key,
            token => FetchAndStoreLeasedWithLeaseAsync(
                accountId,
                cacheKey,
                sourceUrl,
                cachedEntry,
                fetcher,
                admission,
                token),
            admission as IMarkdownImageSourceWorkTracker,
            cancellationToken);

        GitHubImageSourceLease? resolved = await sharedTask.ConfigureAwait(false);
        if (resolved is not null)
            return resolved;

        // A failed stale revalidation is a cache miss from the flight's perspective.
        // Re-open the currently committed generation under a fresh exact reservation;
        // the old cache array was never kept alive under a released source lease.
        return cachedEntry is null
            ? null
            : await ReadCachedImageLeaseAsync(cacheKey, admission, operationToken)
                .ConfigureAwait(false);
    }

    private async Task<GitHubImageCacheEntry?> GetCacheEntryForLeasedResolutionAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGet(cacheKey, out GitHubImageCacheRead? memoryRead) &&
            memoryRead is not null)
        {
            return RefreshCachedTimestamp(memoryRead).Entry;
        }

        return await _cacheStore.TryGetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubImageSourceLease?> ReadCachedImageLeaseAsync(
        string cacheKey,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_memoryCache.TryGet(cacheKey, out GitHubImageCacheRead? memoryRead) &&
                memoryRead is not null)
            {
                long expectedLength = memoryRead.Entry.ByteLength;
                string expectedPath = memoryRead.Entry.FilePath;
                // Keep only scalar identity across the admission wait. If the cache
                // evicts this generation while we wait, the local request must not
                // become an uncharged owner of its byte array.
                memoryRead = null;
                if (expectedLength <= 0 || expectedLength > MaxImageBytes)
                {
                    _memoryCache.Remove(cacheKey);
                    continue;
                }

                IDisposable? reservation = await admission.ReserveAsync(
                        expectedLength,
                        cancellationToken)
                    .ConfigureAwait(false);
                GitHubImageCacheRead? current = null;
                try
                {
                    // Waiting for weighted admission yields. Do not return a generation
                    // that was replaced or invalidated while the caller waited.
                    if (!_memoryCache.TryGet(cacheKey, out current) ||
                        current is null ||
                        current.Entry.ByteLength != expectedLength ||
                        !string.Equals(current.Entry.FilePath, expectedPath, StringComparison.Ordinal) ||
                        current.Bytes.LongLength != expectedLength)
                    {
                        if (current is not null && current.Bytes.LongLength != current.Entry.ByteLength)
                            _memoryCache.Remove(cacheKey);
                        current = null;
                        continue;
                    }

                    GitHubImageCacheRead currentRead = RefreshCachedTimestamp(current);
                    var image = new GitHubCachedImage(
                        currentRead.Entry.FilePath,
                        currentRead.Bytes,
                        currentRead.Entry.ContentType,
                        IsFromCache: true,
                        IsStale: DateTimeOffset.UtcNow - currentRead.Entry.CachedAt > Freshness);
                    GitHubImageSourceLease result = new(image, reservation);
                    reservation = null;
                    current = null;
                    return result;
                }
                finally
                {
                    current = null;
                    reservation?.Dispose();
                }
            }

            if (_cacheStore is GitHubImageCacheStore concreteStore)
            {
                AdmittedGitHubImageCacheRead? admitted = await concreteStore.TryReadAdmittedAsync(
                        cacheKey,
                        admission,
                        MaxImageBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (admitted is null)
                    return null;

                try
                {
                    GitHubImageCacheRead read = admitted.Value;
                    if (read.Bytes.Length == 0 || read.Bytes.Length > MaxImageBytes ||
                        read.Bytes.LongLength != read.Entry.ByteLength)
                    {
                        admitted.Dispose();
                        admitted = null;
                        continue;
                    }

                    _memoryCache.Set(cacheKey, read);
                    var image = new GitHubCachedImage(
                        read.Entry.FilePath,
                        read.Bytes,
                        read.Entry.ContentType,
                        IsFromCache: true,
                        IsStale: DateTimeOffset.UtcNow - read.Entry.CachedAt > Freshness);
                    GitHubImageSourceLease result = new(image, admitted);
                    admitted = null;
                    return result;
                }
                finally
                {
                    admitted?.Dispose();
                }
            }

            // An injected cache store does not expose an admitted-read contract.
            // Its TryReadAsync implementation may allocate or retain bytes before
            // returning, so a reservation made from scalar metadata here would be
            // post-allocation accounting, not strict preallocation admission.
            // Fail closed for this leased read; the concrete production store above
            // has the required atomic metadata/admission/read path.
            return null;
        }
    }

    private async Task<GitHubImageSourceLease?> FetchAndStoreLeasedWithLeaseAsync(
        long accountId,
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheEntry? cachedEntry,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        using IAccountWorkLease? accountLease = EnterAccountWork(accountId, cancellationToken);
        return await FetchAndStoreLeasedAsync(
                cacheKey,
                sourceUrl,
                cachedEntry,
                fetcher,
                admission,
                accountLease?.CancellationToken ?? cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GitHubImageSourceLease?> FetchAndStoreLeasedAsync(
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheEntry? cachedEntry,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        IDisposable? sourceReservation = null;
        try
        {
            GitHubImageDownload? download = await fetcher(cachedEntry, cancellationToken)
                .ConfigureAwait(false);
            sourceReservation = download?.SourceByteLease;
            cancellationToken.ThrowIfCancellationRequested();

            if (download?.IsNotModified == true && cachedEntry is not null)
            {
                sourceReservation?.Dispose();
                sourceReservation = null;
                await _cacheStore.MarkFreshAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                return await ReadCachedImageLeaseAsync(cacheKey, admission, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (download?.Bytes is not { Length: > 0 } bytes ||
                bytes.Length > MaxImageBytes ||
                !TryDetectSupportedImageContentType(bytes, download.ContentType, out string contentType))
            {
                throw new InvalidDataException("Remote content is not a supported image.");
            }

            if (sourceReservation is null)
            {
                throw new InvalidOperationException(
                    "A leased image fetcher must transfer the reservation acquired before materializing its bytes.");
            }

            if (Volatile.Read(ref _disposed) != 0)
                throw new OperationCanceledException("The GitHub image service is disposed.", cancellationToken);

            string extension = GetExtension(contentType, sourceUrl);
            GitHubImageCacheEntry stored = await _cacheStore.PutAsync(
                    cacheKey,
                    bytes,
                    extension,
                    new GitHubImageCacheWriteMetadata(
                        download.ETag,
                        download.LastModified,
                        contentType),
                    cancellationToken)
                .ConfigureAwait(false);
            var cacheRead = new GitHubImageCacheRead(stored, bytes);
            _memoryCache.Set(cacheKey, cacheRead);

            var image = new GitHubCachedImage(
                stored.FilePath,
                bytes,
                stored.ContentType,
                IsFromCache: false);
            GitHubImageSourceLease result = new(image, sourceReservation);
            sourceReservation = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (cachedEntry is not null)
        {
            // Preserve stale-while-revalidate behavior without returning a raw cached
            // array. The caller reopens the cache generation under its own lease.
            return null;
        }
        finally
        {
            sourceReservation?.Dispose();
        }
    }

    private static Task<GitHubImageSourceLease?> GetSharedLeasedTask(
        ConcurrentDictionary<LeasedFlightKey, SharedLeasedImageRequest> tasks,
        LeasedFlightKey key,
        Func<CancellationToken, Task<GitHubImageSourceLease?>> taskFactory,
        IMarkdownImageSourceWorkTracker? workTracker,
        CancellationToken callerCancellationToken)
    {
        while (true)
        {
            SharedLeasedImageRequest request = tasks.GetOrAdd(
                key,
                _ => new SharedLeasedImageRequest(taskFactory));
            if (request.TryAddWaiter())
            {
                request.Start(workTracker);
                return AwaitSharedLeasedAsync(tasks, key, request, callerCancellationToken);
            }

            tasks.TryRemove(new KeyValuePair<LeasedFlightKey, SharedLeasedImageRequest>(key, request));
        }
    }

    private static async Task<GitHubImageSourceLease?> AwaitSharedLeasedAsync(
        ConcurrentDictionary<LeasedFlightKey, SharedLeasedImageRequest> tasks,
        LeasedFlightKey key,
        SharedLeasedImageRequest request,
        CancellationToken callerCancellationToken)
    {
        Task<bool> task = request.Task;
        try
        {
            bool hasImage = await task.WaitAsync(callerCancellationToken)
                .ConfigureAwait(false);
            return hasImage ? request.RetainResult() : null;
        }
        finally
        {
            SharedImageRequestRelease release = request.ReleaseWaiter(task.IsCompleted);
            try
            {
                if (release.ShouldCancel)
                    request.Cancel();
            }
            catch (Exception exception)
            {
                // Cancellation is best-effort cleanup. A token callback must never
                // strand the flight in the dictionary or bypass root retirement.
                HandledFailureReporter.Report(exception, "github-image-flight-cancel");
            }
            finally
            {
                if (release.ShouldRetire)
                {
                    try
                    {
                        tasks.TryRemove(new KeyValuePair<LeasedFlightKey, SharedLeasedImageRequest>(key, request));
                    }
                    finally
                    {
                        request.DisposeWhenComplete();
                    }
                }
            }
        }
    }

    private void CancelLeasedRequests()
    {
        foreach (SharedLeasedImageRequest request in _leasedMisses.Values)
            CancelLeasedRequestSafely(request);
        foreach (SharedLeasedImageRequest request in _leasedRefreshes.Values)
            CancelLeasedRequestSafely(request);
    }

    private static void CancelLeasedRequestSafely(SharedLeasedImageRequest request)
    {
        try
        {
            request.Cancel();
        }
        catch (Exception exception)
        {
            // Service disposal must proceed even if a cancellation callback or
            // cancellation infrastructure fails unexpectedly.
            HandledFailureReporter.Report(exception, "github-image-flight-cancel");
        }
    }

    private readonly record struct LeasedFlightKey(
        string CacheKey,
        IMarkdownImageSourceByteAdmission Admission);

    private sealed class LeasedFlightKeyComparer : IEqualityComparer<LeasedFlightKey>
    {
        internal static readonly LeasedFlightKeyComparer Instance = new();

        public bool Equals(LeasedFlightKey x, LeasedFlightKey y) =>
            string.Equals(x.CacheKey, y.CacheKey, StringComparison.Ordinal) &&
            ReferenceEquals(x.Admission, y.Admission);

        public int GetHashCode(LeasedFlightKey key) => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(key.CacheKey),
            RuntimeHelpers.GetHashCode(key.Admission));
    }

    private sealed class SharedLeasedImageRequest
    {
        private readonly object _gate = new();
        private readonly object _cancellationGate = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly TaskCompletionSource<bool> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _retirement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<CancellationToken, Task<GitHubImageSourceLease?>> _taskFactory;
        private GitHubImageSourceLease? _root;
        private bool _started;
        private int _waiterCount;
        private int _disposeScheduled;
        private bool _retired;
        private bool _completed;
        private bool _cancelStarted;
        private bool _cancellationSealed;
        private Task _cancellationCallbacks = System.Threading.Tasks.Task.CompletedTask;

        internal SharedLeasedImageRequest(
            Func<CancellationToken, Task<GitHubImageSourceLease?>> taskFactory)
        {
            _taskFactory = taskFactory;
        }

        internal Task<bool> Task => _completion.Task;

        internal void Start(IMarkdownImageSourceWorkTracker? workTracker)
        {
            lock (_gate)
            {
                if (_started)
                    return;
                _started = true;
            }

            try
            {
                // Register the source-free retirement signal before starting the
                // shared producer. If the resolver scope has closed, fail the
                // flight without allowing untracked host work to begin.
                workTracker?.TrackWork(_retirement.Task);
            }
            catch (Exception exception)
            {
                lock (_gate)
                    _completed = true;
                _completion.TrySetException(exception);
                return;
            }

            lock (_gate)
            {
                // Every waiter can cancel while TrackWork is registering. Do not
                // start a producer after the flight retired in that interval.
                if (_retired || _waiterCount == 0)
                {
                    _completed = true;
                    _completion.TrySetResult(false);
                    return;
                }
            }

            _ = RunAsync();
        }

        internal bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (_retired)
                    return false;
                _waiterCount++;
                return true;
            }
        }

        internal SharedImageRequestRelease ReleaseWaiter(bool taskCompleted)
        {
            lock (_gate)
            {
                if (_waiterCount <= 0)
                    throw new InvalidOperationException("Shared leased image waiter count is already zero.");

                _waiterCount--;
                if (_waiterCount > 0)
                    return new SharedImageRequestRelease(ShouldRetire: false, ShouldCancel: false);

                _retired = true;
                return new SharedImageRequestRelease(
                    ShouldRetire: true,
                    ShouldCancel: !taskCompleted);
            }
        }

        internal GitHubImageSourceLease? RetainResult()
        {
            lock (_gate)
            {
                if (!_completed || _retired)
                    throw new InvalidOperationException("The shared leased image result is no longer available.");
                return _root?.Retain();
            }
        }

        internal void Cancel()
        {
            lock (_cancellationGate)
            {
                if (_cancellationSealed || _cancelStarted)
                    return;

                _cancelStarted = true;
                try
                {
                    // CancelAsync marks the token canceled immediately, but does not
                    // run arbitrary registrations inline on the waiter/disposal path.
                    // Retirement awaits the returned task before disposing the CTS.
                    _cancellationCallbacks = _cancellationTokenSource.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    _cancellationCallbacks = System.Threading.Tasks.Task.CompletedTask;
                }
                catch (Exception exception)
                {
                    // Keep the failure observable by the retirement path while
                    // guaranteeing that cancellation cannot skip cleanup.
                    _cancellationCallbacks = System.Threading.Tasks.Task.FromException(exception);
                }
            }
        }

        internal void DisposeWhenComplete()
        {
            if (Interlocked.Exchange(ref _disposeScheduled, 1) != 0)
                return;

            _ = DisposeRequestWhenCompleteAsync();
        }

        private async Task RunAsync()
        {
            GitHubImageSourceLease? root = null;
            try
            {
                root = await _taskFactory(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_gate)
                    _completed = true;
                _completion.TrySetException(exception);
                return;
            }

            bool publishImage;
            GitHubImageSourceLease? discard = null;
            lock (_gate)
            {
                _completed = true;
                if (root is not null && (_retired || _waiterCount == 0))
                {
                    discard = root;
                    root = null;
                }
                else
                {
                    _root = root;
                }

                publishImage = _root is not null;
            }

            DisposeRootSafely(discard);
            _completion.TrySetResult(publishImage);
        }

        private async Task DisposeRequestWhenCompleteAsync()
        {
            try
            {
                try { await _completion.Task.ConfigureAwait(false); }
                catch { /* The awaiting consumers own user-visible failures. */ }

                Task callbacks;
                lock (_cancellationGate)
                {
                    _cancellationSealed = true;
                    callbacks = _cancellationCallbacks;
                }

                try
                {
                    await callbacks.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    HandledFailureReporter.Report(exception, "github-image-flight-cancellation-callback");
                }
                finally
                {
                    DisposeRootAndCancellation();
                }
            }
            catch (Exception exception)
            {
                HandledFailureReporter.Report(exception, "github-image-flight-retirement");
            }
            finally
            {
                // The tracker receives only this source-free signal. Complete it
                // after callback observation, root clearing/disposal, and CTS teardown.
                _retirement.TrySetResult();
            }
        }

        private void DisposeRootAndCancellation()
        {
            GitHubImageSourceLease? root;
            lock (_gate)
            {
                root = _root;
                _root = null;
            }

            DisposeRootSafely(root);
            _cancellationTokenSource.Dispose();
        }

        private static void DisposeRootSafely(GitHubImageSourceLease? root)
        {
            try
            {
                root?.Dispose();
            }
            catch (Exception exception)
            {
                HandledFailureReporter.Report(exception, "github-image-flight-lease-release");
            }
        }
    }
}
