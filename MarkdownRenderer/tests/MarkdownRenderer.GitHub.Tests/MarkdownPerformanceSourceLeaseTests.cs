using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.Session;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.MarkdownPerformanceSourceLeaseFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how session shutdown and cancellation retire source resolver
/// work: detached work and its faults, closed resolution scopes, legacy
/// reservations, late results, and canceled flight callbacks.
/// </summary>
public sealed class MarkdownPerformanceSourceLeaseTests
{
    [Fact]
    public async Task SessionShutdownWaitsForDetachedResolverWorkWithoutAnySourceReservation()
    {
        var detachedCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new TrackedWorkResolver(tracker => tracker.TrackWork(detachedCompletion.Task));
        var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        Task? shutdown = null;
        IMarkdownImageResolutionLease? resolutionLease = null;
        try
        {
            var context = new MarkdownImageResolveContext(null);
            using (IMarkdownPerformanceDocumentScope document =
                   ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context))
            {
                resolutionLease = await document.ResolveWithSourceLeaseAsync(
                    "detached-without-bytes", context, CancellationToken.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
            shutdown = session.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);

            detachedCompletion.TrySetResult(true);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = null;
        }
        finally
        {
            detachedCompletion.TrySetResult(true);
            resolutionLease?.Dispose();
            if (shutdown is not null)
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DetachedResolverFaultIsObservedWithoutEscapingSessionShutdown()
    {
        var detachedCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new TrackedWorkResolver(tracker => tracker.TrackWork(detachedCompletion.Task));
        var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        Task? shutdown = null;
        IMarkdownImageResolutionLease? resolutionLease = null;
        try
        {
            var context = new MarkdownImageResolveContext(null);
            using (IMarkdownPerformanceDocumentScope document =
                   ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context))
            {
                resolutionLease = await document.ResolveWithSourceLeaseAsync(
                    "detached-fault", context, CancellationToken.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            shutdown = session.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            detachedCompletion.TrySetException(new InvalidOperationException(
                "private-source-marker-must-not-escape"));

            // Detached faults are observed for diagnostics, but they cannot turn
            // a successful resolution or session drain into a user-visible fault.
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = null;
        }
        finally
        {
            detachedCompletion.TrySetResult(true);
            resolutionLease?.Dispose();
            if (shutdown is not null)
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ResolverWorkRegistrationIsRejectedAfterResolutionScopeCloses()
    {
        var resolver = new TrackedWorkResolver(static _ => { });
        await using var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using (IMarkdownPerformanceDocumentScope document =
               ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context))
        {
            using IMarkdownImageResolutionLease resolution = await document.ResolveWithSourceLeaseAsync(
                "post-close", context, CancellationToken.None);
        }

        Assert.Throws<InvalidOperationException>(
            () => resolver.Tracker!.TrackWork(Task.CompletedTask));
    }

    [Fact]
    public async Task LegacyResolveAsyncReleasesTransferredReservationAfterCopyingResult()
    {
        var resolver = new LeasedResolver(async (source, admission, token) =>
            await CreateLeasedResultAsync(source, 4, admission, token, () => [1, 2, 3, 4]));
        await using var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);

        MarkdownImageResolution resolution = await document.ResolveAsync(
            "compat.png", context, CancellationToken.None);

        Assert.NotNull(resolution.Asset);
        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task CanceledWaiterReleasesLateNoncooperativeResultAndDrainsSession()
    {
        WeakReference<byte[]>? lateBytes = null;
        var resolver = new LateLeasedResolver(bytes => lateBytes = new WeakReference<byte[]>(bytes));
        var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);
        using var cancellation = new RetirableCancellationTokenSource();

        Task<IMarkdownImageResolutionLease> pending = document.ResolveWithSourceLeaseAsync(
            "late.png", context, cancellation.Token).AsTask();
        Task? shutdown = null;
        try
        {
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            shutdown = session.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);

            resolver.Release.TrySetResult(true);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
            Assert.Equal(1, resolver.CallCount);
            await AssertCollectedAsync(lateBytes ?? throw new InvalidOperationException(
                "The late resolver did not create its source buffer."));
        }
        finally
        {
            resolver.Release.TrySetResult(true);
            cancellation.Cancel();
            await DisposeWhenCompleteAsync(pending);
            if (shutdown is not null)
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SessionShutdownWaitsForCanceledFlightCallbackAndLateResolverResult()
    {
        var callbackStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new CallbackLeasedResolver(() =>
        {
            callbackStarted.TrySetResult(true);
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);
        using var cancellation = new RetirableCancellationTokenSource();
        Task<IMarkdownImageResolutionLease> pending = document.ResolveWithSourceLeaseAsync(
            "blocked-callback.png", context, cancellation.Token).AsTask();
        Task? shutdown = null;

        try
        {
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            shutdown = session.DisposeAsync().AsTask();
            resolver.ReleaseResolver.TrySetResult(true);
            await WaitUntilAsync(() => session.GetSnapshot().InFlightSourceBytes == 0);

            // The resolver has returned its late result and source admission
            // has been released, but the CTS callback is still executing.
            Assert.False(shutdown.IsCompleted);
            releaseCallback.TrySetResult(true);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            resolver.ReleaseResolver.TrySetResult(true);
            releaseCallback.TrySetResult(true);
            cancellation.Cancel();
            await DisposeWhenCompleteAsync(pending);
            if (shutdown is not null)
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CancellationRequestAndRetirementHandshakeCannotLoseRunningCallbacks()
    {
        using var source = new RetirableCancellationTokenSource();
        var callbackStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = source.Token.Register(() =>
        {
            callbackStarted.TrySetResult(true);
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        var handshake = new CancellationRetirementHandshake(source);
        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Task?> request = Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(false);
            return handshake.RequestCancellation();
        });
        Task<Task?> beginRetirement = Task.Run(async () =>
        {
            await start.Task.ConfigureAwait(false);
            return handshake.BeginRetirement();
        });
        start.TrySetResult(true);

        try
        {
            Task? requestedCallbacks = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Task? retiredCallbacks = await beginRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(requestedCallbacks, retiredCallbacks);

            Task retirement = CancellationTokenSourceRetirement.DisposeAfter(
                source, retiredCallbacks, Task.CompletedTask);
            if (requestedCallbacks is not null)
            {
                await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(retirement.IsCompleted);
            }
            else
            {
                Assert.False(source.IsCancellationRequested);
            }

            releaseCallback.TrySetResult(true);
            if (requestedCallbacks is not null)
                await requestedCallbacks.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseCallback.TrySetResult(true);
            if (request.IsCompletedSuccessfully)
            {
                Task? requestedCallbacks = await request;
                if (requestedCallbacks is not null)
                    await requestedCallbacks.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task SessionObservesAndRecordsCanceledFlightCallbackException()
    {
        var resolver = new CallbackLeasedResolver(
            static () => throw new InvalidOperationException("test cancellation callback"));
        var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);
        using var cancellation = new RetirableCancellationTokenSource();
        Task<IMarkdownImageResolutionLease> pending = document.ResolveWithSourceLeaseAsync(
            "throwing-callback.png", context, cancellation.Token).AsTask();
        Task? shutdown = null;

        try
        {
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await resolver.CallbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            shutdown = session.DisposeAsync().AsTask();
            resolver.ReleaseResolver.TrySetResult(true);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, session.CancellationRetirementFailures);
        }
        finally
        {
            resolver.ReleaseResolver.TrySetResult(true);
            cancellation.Cancel();
            await DisposeWhenCompleteAsync(pending);
            if (shutdown is not null)
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class TrackedWorkResolver(
        Action<IMarkdownImageSourceWorkTracker> registerWork) : IMarkdownLeasedImageResolver
    {
        internal IMarkdownImageSourceWorkTracker? Tracker { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.NotHandled);

        public ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMarkdownImageSourceWorkTracker tracker = admission as IMarkdownImageSourceWorkTracker ??
                throw new InvalidOperationException(
                    "The source admission did not provide a detached-work tracker.");
            Tracker = tracker;
            registerWork(tracker);
            return ValueTask.FromResult(new MarkdownImageResolutionLease(
                MarkdownImageResolution.Unavailable,
                sourceReservation: null));
        }
    }

    private sealed class LateLeasedResolver(Action<byte[]>? onBytesCreated = null) : IMarkdownLeasedImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.NotHandled);

        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            IDisposable? reservation = await admission.ReserveAsync(4, cancellationToken);
            MarkdownImageResolutionLease? result = null;
            try
            {
                byte[] bytes = [1, 2, 3, 4];
                onBytesCreated?.Invoke(bytes);
                result = new MarkdownImageResolutionLease(
                    MarkdownImageResolution.Resolved(
                        new MarkdownImageAsset(bytes, "image/png", CacheKey: source)),
                    reservation);
                reservation = null;
                Started.TrySetResult(true);
                // This simulates a host transport that cannot abort once it has
                // handed the response body to a native decoder.
                await Release.Task;
                return result!;
            }
            catch
            {
                reservation?.Dispose();
                result?.Dispose();
                throw;
            }
        }
    }

    private sealed class CallbackLeasedResolver(Action callback) : IMarkdownLeasedImageResolver
    {
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseResolver { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.NotHandled);

        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            IDisposable? reservation = await admission.ReserveAsync(4, cancellationToken);
            MarkdownImageResolutionLease? result = null;
            try
            {
                byte[] bytes = [1, 2, 3, 4];
                result = new MarkdownImageResolutionLease(
                    MarkdownImageResolution.Resolved(
                        new MarkdownImageAsset(bytes, "image/png", CacheKey: source)),
                    reservation);
                reservation = null;
                _ = cancellationToken.Register(() =>
                {
                    CallbackInvoked.TrySetResult(true);
                    callback();
                });
                Started.TrySetResult(true);
                // Deliberately ignore cancellation after creating the result to
                // exercise the late-success cleanup and callback-retirement race.
                await ReleaseResolver.Task.ConfigureAwait(false);
                return result!;
            }
            catch
            {
                reservation?.Dispose();
                result?.Dispose();
                throw;
            }
        }
    }
}
