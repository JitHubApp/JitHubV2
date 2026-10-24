using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JitHub.Services;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubImageServiceLeasedTests : IDisposable
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHubImageServiceLeasedTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void InvalidCarrierConstructionDoesNotTakeReservationOwnership()
    {
        var reservation = new TrackingDisposable();

        Assert.Throws<ArgumentException>(() => new GitHubImageSourceLease(
            new GitHubCachedImage("test", [], "image/png", false),
            reservation));

        Assert.Equal(0, reservation.DisposeCount);
    }

    [Fact]
    public void DisposedHandlesDoNotKeepTheSourceArrayAlive()
    {
        (GitHubImageSourceLease[] handles, WeakReference<byte[]> bytes) = CreateDisposedHandles();

        for (int attempt = 0; attempt < 3 && IsAlive(bytes); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(IsAlive(bytes));
        GC.KeepAlive(handles);
    }

    [Fact]
    public async Task MemoryCacheHitKeepsExactReservationUntilLastHandleIsDisposed()
    {
        using var service = CreateService(new ImageResponseHandler(PngBytes));
        Assert.NotNull(await service.GetAsync("https://avatars.githubusercontent.com/u/leased.png"));
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        GitHubImageSourceLease? first = await leased.TryGetCachedLeasedAsync(
            "https://avatars.githubusercontent.com/u/leased.png",
            GitHubImageFetchScope.TrustedGitHub,
            admission);
        Assert.NotNull(first);
        Assert.Equal(PngBytes, first!.Image.Bytes);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(PngBytes.Length, admission.LastReservationBytes);

        GitHubImageSourceLease second = first.Retain();
        first.Dispose();
        first.Dispose();
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(PngBytes, second.Image.Bytes);

        second.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Throws<ObjectDisposedException>(() => _ = second.Image);
    }

    [Fact]
    public async Task ColdDiskCacheHitTransfersAdmissionReservationToConsumer()
    {
        const string source = "https://images.example.com/leased-disk.png";
        using (GitHubImageService populate = CreateService(new ImageResponseHandler(PngBytes)))
            Assert.NotNull(await populate.GetAsync(source, GitHubImageFetchScope.UserApprovedHttps));

        var noNetwork = new ImageResponseHandler(PngBytes);
        using GitHubImageService service = CreateService(noNetwork);
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        using GitHubImageSourceLease? image = await leased.TryGetCachedLeasedAsync(
            source,
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Image.Bytes);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(0, noNetwork.RequestCount);
        image.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task HttpFetchTransfersPreallocationReservationThroughCacheCommitAndConsumerLifetime()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        GitHubImageSourceLease? image = await leased.GetLeasedAsync(
            "https://images.example.com/leased-http.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Image.Bytes);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(PngBytes.Length, admission.LastReservationBytes);
        image.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task UnknownLengthChunkedHttpFetchIsAdmittedBeforeItsFirstRead()
    {
        var admission = new TrackingAdmission(maximumReservationBytes: 64);
        var handler = new UnknownLengthImageResponseHandler(
            PngBytes,
            () => Assert.True(admission.ActiveBytes >= PngBytes.Length));
        using GitHubImageService service = CreateService(handler);
        IGitHubLeasedImageService leased = service;

        GitHubImageSourceLease? image = await leased.GetLeasedAsync(
            "https://images.example.com/leased-chunked.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Image.Bytes);
        Assert.Equal(64, admission.ActiveBytes);
        image.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task RejectedNonCamoSvgReleasesAdmissionAndFaultedTaskDoesNotRetainReadBuffer()
    {
        RejectedSvgRun run = StartRejectedNonCamoSvgFetch();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => run.Task);
            Assert.Equal(0, run.Admission.ActiveBytes);

            WeakReference<byte[]>[] readBuffers = Assert.IsType<SpillCaptureImageContent>(run.Handler.Content)
                .Stream.CapturedBuffers;
            Assert.NotEmpty(readBuffers);
            for (int attempt = 0; attempt < 5 && AreAnyBuffersAlive(readBuffers); attempt++)
                ForceCollection();

            Assert.False(AreAnyBuffersAlive(readBuffers),
                "The failed fetch task retained a source buffer after releasing its reservation.");
            GC.KeepAlive(run.Task);
        }
        finally
        {
            run.Service.Dispose();
        }
    }

    [Fact]
    public async Task UnknownLengthSpillDropsScratchBuffersBeforeWaitingForFinalAdmission()
    {
        const int sourceLength = (1024 * 1024) + 4096;
        byte[] sourceBytes = new byte[sourceLength];
        PngBytes.AsSpan(0, 8).CopyTo(sourceBytes);
        for (int index = PngBytes.Length; index < sourceBytes.Length; index++)
            sourceBytes[index] = (byte)(index * 31);

        var admission = new BlockingFinalGrantAdmission(maximumReservationBytes: 2L * 1024 * 1024);
        var handler = new SpillCaptureImageResponseHandler(sourceBytes);
        using GitHubImageService service = CreateService(handler);
        IGitHubLeasedImageService leased = service;
        Task<GitHubImageSourceLease?> pending = leased.GetLeasedAsync(
            "https://images.example.com/leased-spill-scratch.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        GitHubImageSourceLease? image = null;
        Exception? completionFailure = null;
        try
        {
            await admission.FinalGrantRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(sourceLength, admission.FinalGrantBytes);
            Assert.Equal(0, admission.ActiveBytes);
            WeakReference<byte[]>[] scratchBuffers = Assert.IsType<SpillCaptureImageContent>(
                handler.Content).Stream.CapturedBuffers;
            Assert.Equal(2, scratchBuffers.Length);

            ForceCollection();
            Assert.False(AreAnyBuffersAlive(scratchBuffers));
        }
        finally
        {
            admission.ReleaseFinalGrant.TrySetResult();
            try
            {
                image = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception)
            {
                completionFailure = exception;
            }
        }

        if (completionFailure is not null)
            throw new InvalidOperationException("The admitted spill fetch did not complete after releasing its final grant.", completionFailure);

        try
        {
            Assert.NotNull(image);
            Assert.Equal(sourceBytes, image!.Image.Bytes);
            Assert.Equal(sourceLength, admission.ActiveBytes);
            Assert.Equal(sourceLength, admission.LastGrantedBytes);
        }
        finally
        {
            image?.Dispose();
        }

        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task SharedFetchGivesWaitersIndependentHandlesAndReleasesRootAfterFanout()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingWorkAdmission();
        var fetchStarted = NewSignal();
        var finishFetch = NewSignal();
        int fetchCount = 0;
        IGitHubLeasedImageService leased = service;
        GitHubImageFetcher fetcher = async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref fetchCount);
            Assert.Equal(1, admission.TrackWorkCount);
            fetchStarted.TrySetResult();
            await finishFetch.Task.WaitAsync(cancellationToken);
            IDisposable reservation = await admission.ReserveAsync(PngBytes.Length, cancellationToken);
            return new GitHubImageDownload(PngBytes, "image/png", SourceByteLease: reservation);
        };

        Task<GitHubImageSourceLease?> firstTask = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-fanout.png", fetcher, admission);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<GitHubImageSourceLease?> secondTask = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-fanout.png", fetcher, admission);
        await Task.Delay(50);
        finishFetch.TrySetResult();

        GitHubImageSourceLease?[] handles = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(handles, handle => Assert.NotNull(handle));
        Assert.NotSame(handles[0], handles[1]);
        Assert.Equal(1, fetchCount);
        Assert.Equal(1, admission.TrackWorkCount);
        _ = Assert.Single(admission.TrackedWork);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);

        handles[0]!.Dispose();
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        Assert.Equal(PngBytes, handles[1]!.Image.Bytes);
        handles[1]!.Dispose();
        await admission.TrackedWork[0].WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task HostRetirementTrackingWaitsForBlockingCancellationCallbacksWithoutBlockingWaiter()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingWorkAdmission();
        var fetchStarted = NewSignal();
        var callbackEntered = NewSignal();
        var callbackFinished = NewSignal();
        var releaseCallback = NewSignal();
        CancellationTokenRegistration hostRegistration = default;
        IGitHubLeasedImageService leased = service;
        GitHubImageFetcher blockedFetcher = async (_, cancellationToken) =>
        {
            Assert.Equal(1, admission.TrackWorkCount);
            // Keep the host callback registered independently of the producer.
            // Otherwise Task.Delay cancellation can finish the producer first,
            // and its using-dispose may unregister this callback before it runs.
            hostRegistration = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                try
                {
                    releaseCallback.Task.GetAwaiter().GetResult();
                }
                finally
                {
                    callbackFinished.TrySetResult();
                }
            });
            fetchStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };

        using var callerCancellation = new CancellationTokenSource();
        Task<GitHubImageSourceLease?> canceledFlight = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-tracked-callback-block.png",
            blockedFetcher,
            admission,
            callerCancellation.Token);
        Task retirement;

        try
        {
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = Assert.Single(admission.TrackedWork);
            callerCancellation.Cancel();

            Task completed = await Task.WhenAny(canceledFlight, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(canceledFlight, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFlight);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, admission.ActiveBytes);
        }
        finally
        {
            releaseCallback.TrySetResult();
            await hostRegistration.DisposeAsync();
        }

        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, admission.TrackWorkCount);
    }

    [Fact]
    public async Task ClosedRetirementTrackerPreventsUntrackedFetcherFromStarting()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingWorkAdmission();
        admission.CloseTracking();
        int fetchCount = 0;
        IGitHubLeasedImageService leased = service;
        GitHubImageFetcher fetcher = (_, _) =>
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<GitHubImageDownload?>(
                new GitHubImageDownload(PngBytes, "image/png"));
        };

        Task<GitHubImageSourceLease?> pending = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-closed-tracker.png",
            fetcher,
            admission);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, admission.TrackWorkCount);
        Assert.Equal(0, fetchCount);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task LastCanceledWaiterDisposesLateFetcherReservationWithoutCachingIt()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingAdmission();
        var fetchStarted = NewSignal();
        var finishFetch = NewSignal();
        IGitHubLeasedImageService leased = service;
        GitHubImageFetcher fetcher = async (_, _) =>
        {
            fetchStarted.TrySetResult();
            await finishFetch.Task;
            IDisposable reservation = await admission.ReserveAsync(PngBytes.Length, CancellationToken.None);
            return new GitHubImageDownload(PngBytes, "image/png", SourceByteLease: reservation);
        };

        using var cancellation = new CancellationTokenSource();
        Task<GitHubImageSourceLease?> pending = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-late-cancel.png",
            fetcher,
            admission,
            cancellation.Token);
        await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        finishFetch.TrySetResult();
        await admission.WaitUntilReleasedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Null(await service.TryGetCachedAsync(
            "https://images.example.com/leased-late-cancel.png",
            GitHubImageFetchScope.UserApprovedHttps));
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotSkipFlightRetirement()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingAdmission();
        var fetchStarted = NewSignal();
        var callbackInvoked = NewSignal();
        var callbackFinished = NewSignal();
        CancellationTokenRegistration hostRegistration = default;
        IGitHubLeasedImageService leased = service;
        GitHubImageFetcher blockedFetcher = async (_, cancellationToken) =>
        {
            hostRegistration = cancellationToken.Register(() =>
            {
                callbackInvoked.TrySetResult();
                try
                {
                    throw new InvalidOperationException("Injected cancellation callback failure.");
                }
                finally
                {
                    callbackFinished.TrySetResult();
                }
            });
            fetchStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };

        using var callerCancellation = new CancellationTokenSource();
        Task<GitHubImageSourceLease?> canceledFlight = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-callback-fault.png",
            blockedFetcher,
            admission,
            callerCancellation.Token);
        try
        {
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            callerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFlight);
            await callbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

            GitHubImageFetcher replacementFetcher = async (_, token) =>
            {
                IDisposable reservation = await admission.ReserveAsync(PngBytes.Length, token);
                return new GitHubImageDownload(PngBytes, "image/png", SourceByteLease: reservation);
            };
            using GitHubImageSourceLease? replacement = await leased.GetOrFetchLeasedAsync(
                "https://images.example.com/leased-callback-fault.png",
                replacementFetcher,
                admission).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(replacement);
            Assert.Equal(PngBytes, replacement!.Image.Bytes);
            Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        }
        finally
        {
            callerCancellation.Cancel();
            await hostRegistration.DisposeAsync();
        }
    }

    [Fact]
    public async Task ServiceDisposeDoesNotThrowWhenFlightCancellationCallbackThrows()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingAdmission();
        var fetchStarted = NewSignal();
        var callbackFinished = NewSignal();
        CancellationTokenRegistration hostRegistration = default;
        IGitHubLeasedImageService leased = service;
        GitHubImageFetcher blockedFetcher = async (_, cancellationToken) =>
        {
            hostRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    throw new InvalidOperationException("Injected cancellation callback failure during service disposal.");
                }
                finally
                {
                    callbackFinished.TrySetResult();
                }
            });
            fetchStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };

        Task<GitHubImageSourceLease?> pending = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-dispose-callback-fault.png",
            blockedFetcher,
            admission);
        try
        {
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(Record.Exception(service.Dispose));
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, admission.ActiveBytes);
        }
        finally
        {
            service.Dispose();
            await hostRegistration.DisposeAsync();
        }
    }

    [Fact]
    public async Task BlockingCancellationCallbackDoesNotBlockCanceledWaiterOrPreventNewFlight()
    {
        using GitHubImageService service = CreateService(new ImageResponseHandler(PngBytes));
        var admission = new TrackingAdmission();
        var fetchStarted = NewSignal();
        var callbackEntered = NewSignal();
        var callbackFinished = NewSignal();
        var releaseCallback = NewSignal();
        CancellationTokenRegistration hostRegistration = default;
        IGitHubLeasedImageService leased = service;
        GitHubImageFetcher blockedFetcher = async (_, cancellationToken) =>
        {
            hostRegistration = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                try
                {
                    // Deliberately model an uncooperative synchronous dependency callback;
                    // CancelAsync must keep this wait off the canceled consumer's path.
                    releaseCallback.Task.GetAwaiter().GetResult();
                }
                finally
                {
                    callbackFinished.TrySetResult();
                }
            });
            fetchStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };

        using var callerCancellation = new CancellationTokenSource();
        Task<GitHubImageSourceLease?> canceledFlight = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-callback-block.png",
            blockedFetcher,
            admission,
            callerCancellation.Token);
        try
        {
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            callerCancellation.Cancel();
            Task completed = await Task.WhenAny(canceledFlight, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(canceledFlight, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFlight);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            GitHubImageFetcher replacementFetcher = async (_, token) =>
            {
                IDisposable reservation = await admission.ReserveAsync(PngBytes.Length, token);
                return new GitHubImageDownload(PngBytes, "image/png", SourceByteLease: reservation);
            };
            using GitHubImageSourceLease? replacement = await leased.GetOrFetchLeasedAsync(
                "https://images.example.com/leased-callback-block.png",
                replacementFetcher,
                admission).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(replacement);
            Assert.Equal(PngBytes, replacement!.Image.Bytes);
        }
        finally
        {
            releaseCallback.TrySetResult();
            callerCancellation.Cancel();
            await hostRegistration.DisposeAsync();
        }

        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task StaleRefreshReturnsAConsumerLeasedCachedGeneration(HttpStatusCode refreshStatus)
    {
        const string source = "https://avatars.githubusercontent.com/u/leased-stale.png";
        var handler = new RevalidationHandler(PngBytes, refreshStatus);
        using GitHubImageService service = CreateService(handler);
        GitHubCachedImage initial = (await service.GetAsync(source))!;
        File.SetLastWriteTimeUtc(initial.FilePath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(8)));
        var admission = new TrackingAdmission();
        IGitHubLeasedImageService leased = service;

        using GitHubImageSourceLease? stale = await leased.GetLeasedAsync(
            source,
            GitHubImageFetchScope.TrustedGitHub,
            admission);

        Assert.NotNull(stale);
        Assert.Equal(PngBytes, stale!.Image.Bytes);
        Assert.True(stale.Image.IsFromCache);
        Assert.Equal(refreshStatus == HttpStatusCode.NotFound, stale.Image.IsStale);
        Assert.Null(stale.Image.RefreshTask);
        Assert.Equal(PngBytes.Length, admission.ActiveBytes);
        stale.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private GitHubImageService CreateService(HttpMessageHandler handler)
    {
        Directory.CreateDirectory(_root);
        return new GitHubImageService(
            new GitHubImageCacheStore(_root, GitHubCachePolicy.Default),
            new HttpClient(handler),
            ownsHttpClient: true);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> reference) => reference.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AreAnyBuffersAlive(WeakReference<byte[]>[] references)
    {
        foreach (WeakReference<byte[]> reference in references)
        {
            if (reference.TryGetTarget(out _))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RejectedSvgRun StartRejectedNonCamoSvgFetch()
    {
        byte[] malformedSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        TrackingAdmission admission = new();
        var handler = new SpillCaptureImageResponseHandler(
            malformedSvg,
            contentType: "image/svg+xml",
            knownLength: true);
        GitHubImageService service = CreateService(handler);
        Task<GitHubCachedImage?> task = service.GetAsync(
            "https://images.example.com/malformed.svg",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);
        return new RejectedSvgRun(service, handler, admission, task);
    }

    private sealed record RejectedSvgRun(
        GitHubImageService Service,
        SpillCaptureImageResponseHandler Handler,
        TrackingAdmission Admission,
        Task<GitHubCachedImage?> Task);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (GitHubImageSourceLease[] Handles, WeakReference<byte[]> Bytes)
        CreateDisposedHandles()
    {
        byte[] sourceBytes = new byte[4096];
        var weakBytes = new WeakReference<byte[]>(sourceBytes);
        var root = new GitHubImageSourceLease(
            new GitHubCachedImage("test", sourceBytes, "image/png", false),
            new TrackingDisposable());
        GitHubImageSourceLease retained = root.Retain();
        root.Dispose();
        retained.Dispose();
        return ([root, retained], weakBytes);
    }

    private sealed class TrackingAdmission(long maximumReservationBytes = GitHubImageService.MaxImageBytes)
        : IMarkdownImageSourceByteAdmission
    {
        private long _activeBytes;
        private long _lastReservationBytes;
        private readonly TaskCompletionSource _released = NewSignal();

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => maximumReservationBytes;
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long LastReservationBytes => Interlocked.Read(ref _lastReservationBytes);

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes <= 0 || bytes > MaximumReservationBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            Interlocked.Add(ref _activeBytes, bytes);
            Interlocked.Exchange(ref _lastReservationBytes, bytes);
            return ValueTask.FromResult<IDisposable>(new Reservation(this, bytes));
        }

        internal Task WaitUntilReleasedAsync() => _released.Task;

        private sealed class Reservation(TrackingAdmission owner, long bytes) : IDisposable
        {
            private TrackingAdmission? _owner = owner;

            public void Dispose()
            {
                TrackingAdmission? current = Interlocked.Exchange(ref _owner, null);
                if (current is null)
                    return;

                Interlocked.Add(ref current._activeBytes, -bytes);
                current._released.TrySetResult();
            }
        }
    }

    private sealed class BlockingFinalGrantAdmission(long maximumReservationBytes)
        : IMarkdownImageSourceByteAdmission
    {
        private readonly TaskCompletionSource _releaseFinalGrant = NewSignal();
        private long _activeBytes;
        private long _finalGrantBytes;
        private long _lastGrantedBytes;
        private int _reserveCount;

        internal TaskCompletionSource FinalGrantRequested { get; } = NewSignal();
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long FinalGrantBytes => Interlocked.Read(ref _finalGrantBytes);
        internal long LastGrantedBytes => Interlocked.Read(ref _lastGrantedBytes);

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => maximumReservationBytes;

        internal TaskCompletionSource ReleaseFinalGrant => _releaseFinalGrant;

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes <= 0 || bytes > MaximumReservationBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));

            int request = Interlocked.Increment(ref _reserveCount);
            if (request == 1)
            {
                Interlocked.Add(ref _activeBytes, bytes);
                Interlocked.Exchange(ref _lastGrantedBytes, bytes);
                return ValueTask.FromResult<IDisposable>(new Reservation(this, bytes));
            }

            if (request != 2)
                throw new InvalidOperationException("The spill test expected exactly one final admission request.");

            Interlocked.Exchange(ref _finalGrantBytes, bytes);
            FinalGrantRequested.TrySetResult();
            return new ValueTask<IDisposable>(GrantFinalAsync(bytes, cancellationToken));
        }

        private async Task<IDisposable> GrantFinalAsync(long bytes, CancellationToken cancellationToken)
        {
            await _releaseFinalGrant.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _activeBytes, bytes);
            Interlocked.Exchange(ref _lastGrantedBytes, bytes);
            return new Reservation(this, bytes);
        }

        private sealed class Reservation(BlockingFinalGrantAdmission owner, long bytes) : IDisposable
        {
            private BlockingFinalGrantAdmission? _owner = owner;

            public void Dispose()
            {
                BlockingFinalGrantAdmission? current = Interlocked.Exchange(ref _owner, null);
                if (current is not null)
                    Interlocked.Add(ref current._activeBytes, -bytes);
            }
        }
    }

    private sealed class TrackingWorkAdmission :
        IMarkdownImageSourceByteAdmission,
        IMarkdownImageSourceWorkTracker
    {
        private readonly TrackingAdmission _bytes = new();
        private readonly object _gate = new();
        private readonly List<Task> _trackedWork = [];
        private bool _trackingClosed;
        private int _trackWorkCount;

        public bool IsSpeculative => _bytes.IsSpeculative;
        public long MaximumReservationBytes => _bytes.MaximumReservationBytes;
        internal long ActiveBytes => _bytes.ActiveBytes;
        internal int TrackWorkCount => Volatile.Read(ref _trackWorkCount);

        internal Task[] TrackedWork
        {
            get
            {
                lock (_gate)
                    return [.. _trackedWork];
            }
        }

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken) =>
            _bytes.ReserveAsync(bytes, cancellationToken);

        public void TrackWork(Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);
            Interlocked.Increment(ref _trackWorkCount);
            lock (_gate)
            {
                if (_trackingClosed)
                    throw new InvalidOperationException("The test work-tracking scope is closed.");
                _trackedWork.Add(completion);
            }
        }

        internal void CloseTracking()
        {
            lock (_gate)
                _trackingClosed = true;
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ImageResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SpillCaptureImageResponseHandler(
        byte[] bytes,
        string contentType = "image/png",
        bool knownLength = false) : HttpMessageHandler
    {
        internal SpillCaptureImageContent? Content { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = new SpillCaptureImageContent(bytes, knownLength);
            Content = content;
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SpillCaptureImageContent(byte[] bytes, bool knownLength = false) : HttpContent
    {
        internal SpillCaptureImageStream Stream { get; } = new(bytes);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? bytes.Length : 0;
            return knownLength;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(Stream);
    }

    private sealed class SpillCaptureImageStream(byte[] bytes) : Stream
    {
        private readonly List<WeakReference<byte[]>> _destinationBuffers = [];
        private int _position;

        internal WeakReference<byte[]>[] CapturedBuffers => [.. _destinationBuffers];

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RememberDestination(destination);
            int read = Math.Min(destination.Length, bytes.Length - _position);
            bytes.AsMemory(_position, read).CopyTo(destination);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void RememberDestination(Memory<byte> destination)
        {
            if (!MemoryMarshal.TryGetArray(
                    (ReadOnlyMemory<byte>)destination,
                    out ArraySegment<byte> segment) ||
                segment.Array is not { } array)
            {
                return;
            }

            foreach (WeakReference<byte[]> existingReference in _destinationBuffers)
            {
                if (existingReference.TryGetTarget(out byte[]? existing) &&
                    ReferenceEquals(existing, array))
                {
                    return;
                }
            }

            _destinationBuffers.Add(new WeakReference<byte[]>(array));
        }
    }

    private sealed class UnknownLengthImageResponseHandler(byte[] bytes, Action onFirstRead) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthChunkedImageContent(bytes, onFirstRead)
            });
        }
    }

    private sealed class UnknownLengthChunkedImageContent(byte[] bytes, Action onFirstRead) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ChunkedReadStream(bytes, onFirstRead));
    }

    private sealed class ChunkedReadStream(byte[] bytes, Action onFirstRead) : Stream
    {
        private int _position;
        private int _readObserved;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObserveRead();
            int read = Math.Min(Math.Min(count, 3), bytes.Length - _position);
            bytes.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveRead();
            int read = Math.Min(Math.Min(buffer.Length, 3), bytes.Length - _position);
            bytes.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void ObserveRead()
        {
            if (Interlocked.Exchange(ref _readObserved, 1) == 0)
                onFirstRead();
        }
    }

    private sealed class RevalidationHandler(byte[] bytes, HttpStatusCode refreshStatus) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(refreshStatus));
        }
    }
}
