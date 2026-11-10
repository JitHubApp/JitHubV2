using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JitHub.Core.Services.GitHub.Caching;
using JitHub.Core.Services.Images;
using JitHub.Core.Services.Images.Caching;
using JitHub.Core.Tests.TestSupport;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;
using static JitHub.Core.Tests.Services.Images.GitHubImageServiceLeasedTestDoubles;

namespace JitHub.Core.Tests.Services.Images;

/// <summary>
/// How shared image fetches fan out to waiters and retire when waiters cancel, callbacks
/// block or throw, or the host shuts down.
/// </summary>
public sealed class GitHubImageServiceFlightRetirementTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHubImageServiceLeasedTests",
        Guid.NewGuid().ToString("N"));

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
        await fetchStarted.Task.WaitAsync(AsyncTestWait.HangGuard);
        Task<GitHubImageSourceLease?> secondTask = leased.GetOrFetchLeasedAsync(
            "https://images.example.com/leased-fanout.png", fetcher, admission);
        await Task.Delay(50);
        finishFetch.TrySetResult();

        GitHubImageSourceLease?[] handles = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(AsyncTestWait.HangGuard);
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
        await admission.TrackedWork[0].WaitAsync(AsyncTestWait.HangGuard);
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
            await fetchStarted.Task.WaitAsync(AsyncTestWait.HangGuard);
            retirement = Assert.Single(admission.TrackedWork);
            callerCancellation.Cancel();

            Task completed = await Task.WhenAny(canceledFlight, Task.Delay(AsyncTestWait.HangGuard));
            Assert.Same(canceledFlight, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFlight);
            await callbackEntered.Task.WaitAsync(AsyncTestWait.HangGuard);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, admission.ActiveBytes);
        }
        finally
        {
            releaseCallback.TrySetResult();
            await hostRegistration.DisposeAsync();
        }

        await callbackFinished.Task.WaitAsync(AsyncTestWait.HangGuard);
        await retirement.WaitAsync(AsyncTestWait.HangGuard);
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
            () => pending.WaitAsync(AsyncTestWait.HangGuard));
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
        await fetchStarted.Task.WaitAsync(AsyncTestWait.HangGuard);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        finishFetch.TrySetResult();
        await admission.WaitUntilReleasedAsync().WaitAsync(AsyncTestWait.HangGuard);
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
            await fetchStarted.Task.WaitAsync(AsyncTestWait.HangGuard);
            callerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFlight);
            await callbackInvoked.Task.WaitAsync(AsyncTestWait.HangGuard);
            await callbackFinished.Task.WaitAsync(AsyncTestWait.HangGuard);

            GitHubImageFetcher replacementFetcher = async (_, token) =>
            {
                IDisposable reservation = await admission.ReserveAsync(PngBytes.Length, token);
                return new GitHubImageDownload(PngBytes, "image/png", SourceByteLease: reservation);
            };
            using GitHubImageSourceLease? replacement = await leased.GetOrFetchLeasedAsync(
                "https://images.example.com/leased-callback-fault.png",
                replacementFetcher,
                admission).WaitAsync(AsyncTestWait.HangGuard);

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
            await fetchStarted.Task.WaitAsync(AsyncTestWait.HangGuard);

            Assert.Null(Record.Exception(service.Dispose));
            await callbackFinished.Task.WaitAsync(AsyncTestWait.HangGuard);
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
            await fetchStarted.Task.WaitAsync(AsyncTestWait.HangGuard);
            callerCancellation.Cancel();
            Task completed = await Task.WhenAny(canceledFlight, Task.Delay(AsyncTestWait.HangGuard));
            Assert.Same(canceledFlight, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFlight);
            await callbackEntered.Task.WaitAsync(AsyncTestWait.HangGuard);

            GitHubImageFetcher replacementFetcher = async (_, token) =>
            {
                IDisposable reservation = await admission.ReserveAsync(PngBytes.Length, token);
                return new GitHubImageDownload(PngBytes, "image/png", SourceByteLease: reservation);
            };
            using GitHubImageSourceLease? replacement = await leased.GetOrFetchLeasedAsync(
                "https://images.example.com/leased-callback-block.png",
                replacementFetcher,
                admission).WaitAsync(AsyncTestWait.HangGuard);

            Assert.NotNull(replacement);
            Assert.Equal(PngBytes, replacement!.Image.Bytes);
        }
        finally
        {
            releaseCallback.TrySetResult();
            callerCancellation.Cancel();
            await hostRegistration.DisposeAsync();
        }

        await callbackFinished.Task.WaitAsync(AsyncTestWait.HangGuard);
    }

    private GitHubImageService CreateService(HttpMessageHandler handler)
    {
        Directory.CreateDirectory(_root);
        return new GitHubImageService(
            new GitHubImageCacheStore(_root, GitHubCachePolicy.Default),
            new HttpClient(handler),
            ownsHttpClient: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
