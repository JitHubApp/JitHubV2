using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Resolves image sources for every document scope in a session: serves
/// admitted source-cache hits, de-duplicates concurrent fetches of one key,
/// and bounds host resolver concurrency with fetch slots and fair background
/// admission. Owns the image-fetch counters in the session snapshot.
/// </summary>
internal sealed class MarkdownSharedImageFetcher
{
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly MarkdownSessionAdmissions _admissions;
    private readonly MarkdownImageSourceCache _cache;
    private readonly object _flightGate = new();
    private readonly Dictionary<ImageSourceKey, SharedImageFetch> _sharedFlights = new(ImageSourceKeyComparer.Instance);
    private readonly SemaphoreSlim _fetchSlots;
    private readonly FairDocumentAdmission _backgroundAdmission;
    private long _imageFetches;
    private long _imageFetchMilliseconds;
    private long _imageFetchFailures;
    private long _imageFetchCancellations;
    private int _pendingImageFetches;
    private int _activeImageFetches;

    internal MarkdownSharedImageFetcher(
        MarkdownPerformanceSessionLifetime lifetime,
        MarkdownSessionAdmissions admissions,
        MarkdownImageSourceCache cache,
        MarkdownPerformanceOptions options)
    {
        _lifetime = lifetime;
        _admissions = admissions;
        _cache = cache;
        _fetchSlots = new SemaphoreSlim(options.MaxConcurrentImageFetches);
        _backgroundAdmission = new FairDocumentAdmission(
            options.MaxConcurrentImageFetches - options.ReservedVisibleImageFetches);
    }

    internal long Fetches => Interlocked.Read(ref _imageFetches);
    internal long FetchMilliseconds => Interlocked.Read(ref _imageFetchMilliseconds);
    internal long FetchFailures => Interlocked.Read(ref _imageFetchFailures);
    internal long FetchCancellations => Interlocked.Read(ref _imageFetchCancellations);
    internal int PendingFetches => Volatile.Read(ref _pendingImageFetches);
    internal int ActiveFetches => Volatile.Read(ref _activeImageFetches);

    internal void DisposeFetchSlots() => _fetchSlots.Dispose();

    internal async ValueTask<IMarkdownImageResolutionLease?> TryGetLeasedSourceAsync(
        ImageSourceKey key,
        MarkdownPerformanceDocumentScope owner,
        bool background,
        CancellationToken cancellationToken)
    {
        // An entry can be evicted or replaced while admission is queued. Keep
        // only a length/identity snapshot across the await, then revalidate it
        // before exposing the array. A stale snapshot releases its grant and
        // retries once; a later miss simply takes the normal resolver path.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!_cache.TryGetMetadata(key, out CachedImageSourceIdentity identity))
                return null;
            if (background && identity.ByteLength > _admissions.SourceBytes.BackgroundCapacity)
                throw new MarkdownImageSourceDeferredException();
            if (identity.ByteLength > _admissions.SourceBytes.Capacity)
                return null;

            IDisposable? reservation = identity.ByteLength == 0
                ? null
                : await _admissions.ReserveSourceBytesAsync(
                        owner,
                        identity.ByteLength,
                        background,
                        cancellationToken,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            try
            {
                if (!_cache.TryExpose(key, identity, out MarkdownImageAsset? asset) || asset is null)
                    continue;

                var lease = new MarkdownImageResolutionLease(
                    MarkdownImageResolution.Resolved(asset), reservation);
                reservation = null;
                return lease;
            }
            finally
            {
                reservation?.Dispose();
            }
        }

        return null;
    }

    private async Task<IMarkdownImageResolutionLease> FetchAsync(
        MarkdownPerformanceDocumentScope owner,
        ImageSourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        Action onResolveStarted,
        CancellationToken cancellationToken
        )
    {
        _lifetime.RegisterWork();
        Interlocked.Increment(ref _pendingImageFetches);
        IDisposable? backgroundLease = null;
        bool fetchAcquired = false;
        bool resolverStarted = false;
        try
        {
            // Source-cache hits are admitted before their byte[] is exposed and
            // before any fetch slot is held. A waiter for source capacity must
            // never occupy a network slot.
            IMarkdownImageResolutionLease? cachedLease = await TryGetLeasedSourceAsync(
                    key, owner, background, cancellationToken)
                .ConfigureAwait(false);
            if (cachedLease is not null)
                return cachedLease;

            if (background)
            {
                backgroundLease = await EnterBackgroundAdmissionAsync(
                    owner, key, context, cancellationToken).ConfigureAwait(false);
            }
            await _fetchSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            fetchAcquired = true;
            onResolveStarted();
            long started = Stopwatch.GetTimestamp();
            Interlocked.Decrement(ref _pendingImageFetches);
            Interlocked.Increment(ref _activeImageFetches);
            resolverStarted = true;
            Interlocked.Increment(ref _imageFetches);
            return await ResolveCountedAsync(
                owner, key, resolver, context, background, started, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !resolverStarted)
        {
            Interlocked.Increment(ref _imageFetchCancellations);
            throw;
        }
        finally
        {
            try
            {
                if (!resolverStarted)
                    Interlocked.Decrement(ref _pendingImageFetches);
                if (fetchAcquired) _fetchSlots.Release();
            }
            finally
            {
                try { backgroundLease?.Dispose(); }
                finally { _lifetime.RetireWork(); }
            }
        }
    }

    // Takes a fair background admission. A remote source waits while the
    // device is saving energy or on a metered network, and never holds the
    // admission while it waits.
    private async Task<IDisposable> EnterBackgroundAdmissionAsync(
        MarkdownPerformanceDocumentScope owner,
        ImageSourceKey key,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        bool remote = IsRemoteSource(key.Source, context);
        while (true)
        {
            // Paused remote work must not hold a speculative slot that
            // another document could use for local resources.
            while (remote && ShouldPauseRemotePrefetch())
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)
                    .ConfigureAwait(false);
            IDisposable backgroundLease = await _backgroundAdmission.EnterAsync(
                owner, cancellationToken).ConfigureAwait(false);
            if (!remote || !ShouldPauseRemotePrefetch())
                return backgroundLease;
            backgroundLease.Dispose();
        }
    }

    // Runs the resolver while it holds a fetch slot and records the fetch's
    // outcome and duration in the session counters.
    private async Task<IMarkdownImageResolutionLease> ResolveCountedAsync(
        MarkdownPerformanceDocumentScope owner,
        ImageSourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        long started,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveAndStoreAsync(
                owner, key, resolver, context, background, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _imageFetchCancellations);
            throw;
        }
        catch (MarkdownImageSourceDeferredException) when (background)
        {
            // A speculative source that needs a larger reservation is not
            // a failed image. A later visible request retries it.
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

    private async Task<IMarkdownImageResolutionLease> ResolveAndStoreAsync(
        MarkdownPerformanceDocumentScope owner,
        ImageSourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        CancellationToken cancellationToken)
    {
        IMarkdownImageResolutionLease resolutionLease;
        using var sourceAdmission = new ResolverSourceByteAdmission(
            _lifetime, _admissions, owner, background, cancellationToken);
        if (resolver is IMarkdownLeasedImageResolver leased)
        {
            resolutionLease = await leased.ResolveWithSourceLeaseAsync(
                    key.Source, context, sourceAdmission, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Compatibility resolvers have no transferable source
            // lifetime. Their results retain the historical behavior
            // and are outside the strict in-flight byte guarantee.
            MarkdownImageResolution resolution = resolver is IMarkdownImageSourceByteAdmittedResolver admitted
                ? await admitted.ResolveWithSourceByteAdmissionAsync(
                    key.Source, context, sourceAdmission, cancellationToken).ConfigureAwait(false)
                : await resolver.ResolveAsync(
                    key.Source, context, cancellationToken).ConfigureAwait(false);
            resolutionLease = MarkdownImageResolutionLease.CreateUnreserved(resolution);
        }

        ArgumentNullException.ThrowIfNull(resolutionLease);
        try
        {
            MarkdownImageResolution result = resolutionLease.Resolution;
            if (!string.IsNullOrEmpty(result.Asset?.CacheKey))
                _cache.Store(key, key, result);
            return resolutionLease;
        }
        catch
        {
            ImageResolutionLeaseCleanup.DisposeSafely(resolutionLease);
            throw;
        }
    }

    internal async Task<IMarkdownImageResolutionLease> ResolveSharedAsync(
        MarkdownPerformanceDocumentScope owner,
        ImageSourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background,
        CancellationToken cancellationToken
        )
    {
        SharedImageFetch flight = JoinOrCreateFlight(
            key, background, out SharedImageFetch? superseded, out bool startFlight);

        // Publish the shared task while holding the gate, but do not run the
        // async method's synchronous prefix there. SemaphoreSlim.WaitAsync can
        // complete inline, and a resolver is allowed to do synchronous work
        // before returning its ValueTask. Running that prefix outside the gate
        // keeps unrelated source flights admissible.
        if (startFlight)
            StartFlight(flight, owner, key, resolver, context, background);

        if (superseded is not null)
        {
            try { superseded.RequestCancellation(); }
            catch (ObjectDisposedException) { }
        }

        try
        {
            await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return flight.RetainResult();
        }
        finally
        {
            LeaveFlight(key, flight);
        }
    }

    // Joins the current flight for the key, or publishes a new one when none
    // exists or a visible request replaces a background flight. Every caller
    // is counted as a waiter before the gate is released.
    private SharedImageFetch JoinOrCreateFlight(
        ImageSourceKey key,
        bool background,
        out SharedImageFetch? superseded,
        out bool startFlight)
    {
        superseded = null;
        startFlight = false;
        SharedImageFetch flight;
        lock (_flightGate)
        {
            if (_lifetime.IsDisposed)
                throw new OperationCanceledException(_lifetime.Token);
            if (!_sharedFlights.TryGetValue(key, out flight!) ||
                (!background && flight.Background))
            {
                SessionWorkLease? flightLifetime = _lifetime.TryBeginWorkLease();
                if (flightLifetime is null)
                    throw new OperationCanceledException(_lifetime.Token);
                CancellationTokenSource? source = null;
                try
                {
                    if (_sharedFlights.TryGetValue(key, out SharedImageFetch? previous))
                    {
                        superseded = previous;
                        _sharedFlights.Remove(key);
                    }

                    source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    flight = new SharedImageFetch(source, background, _lifetime, flightLifetime);
                    _ = flight.Task.ContinueWith(
                        static completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    _sharedFlights.Add(key, flight);
                    source = null;
                    flightLifetime = null;
                    startFlight = true;
                }
                catch
                {
                    source?.Dispose();
                    flightLifetime?.Dispose();
                    throw;
                }
            }

            flight.WaiterCount++;
        }

        return flight;
    }

    private void StartFlight(
        SharedImageFetch startedFlight,
        MarkdownPerformanceDocumentScope owner,
        ImageSourceKey key,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context,
        bool background)
    {
        startedFlight.Start(
            () => FetchAsync(
                owner, key, resolver, context, background, startedFlight.MarkResolutionStarted,
                startedFlight.Cancellation.Token
                ),
            (result, operationCompletion) =>
            {
                try
                {
                    bool releaseRoot;
                    lock (_flightGate)
                    {
                        RemoveCurrentFlightNoLock(key, startedFlight);
                        bool published = startedFlight.SetResult(result);
                        releaseRoot = !published || startedFlight.WaiterCount == 0;
                    }
                    if (releaseRoot)
                        startedFlight.ReleaseRootSafely();
                }
                finally
                {
                    startedFlight.RetireCancellation(operationCompletion);
                }
            },
            (exception, operationCompletion) =>
            {
                try
                {
                    bool releaseRoot;
                    lock (_flightGate)
                    {
                        RemoveCurrentFlightNoLock(key, startedFlight);
                        startedFlight.SetException(exception);
                        releaseRoot = startedFlight.WaiterCount == 0;
                    }
                    if (releaseRoot)
                        startedFlight.ReleaseRootSafely();
                }
                finally
                {
                    startedFlight.RetireCancellation(operationCompletion);
                }
            });
    }

    // The last waiter to leave an unfinished flight cancels it; the last
    // waiter to leave a finished flight releases its shared result.
    private void LeaveFlight(ImageSourceKey key, SharedImageFetch flight)
    {
        bool cancel = false;
        bool releaseRoot = false;
        lock (_flightGate)
        {
            if (--flight.WaiterCount == 0)
            {
                if (!flight.Task.IsCompleted)
                {
                    RemoveCurrentFlightNoLock(key, flight);
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
            try { flight.RequestCancellation(); }
            catch (ObjectDisposedException) { }
        }
        if (releaseRoot)
            flight.ReleaseRootSafely();
    }

    private void RemoveCurrentFlightNoLock(ImageSourceKey key, SharedImageFetch flight)
    {
        if (_sharedFlights.TryGetValue(key, out SharedImageFetch? current) &&
            ReferenceEquals(current, flight))
            _sharedFlights.Remove(key);
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
}
