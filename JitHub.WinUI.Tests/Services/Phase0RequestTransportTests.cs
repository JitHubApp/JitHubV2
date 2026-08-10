using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[CollectionDefinition(RequestTransportTimingCollection.Name, DisableParallelization = true)]
public sealed class RequestTransportTimingCollection
{
    public const string Name = "Request transport timing";
}

[Collection(RequestTransportTimingCollection.Name)]
public sealed class Phase0RequestTransportTests
{
    [Fact]
    public async Task RequestQueue_DedupesInFlightRequests()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 2, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        int calls = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = queue.EnqueueAsync("same", GitHubRequestPriority.Visible, async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return 42;
        });
        Task<int> second = queue.EnqueueAsync("same", GitHubRequestPriority.Visible, async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return 84;
        });

        release.SetResult();
        int[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal([42, 42], results);
    }

    [Fact]
    public async Task RequestQueue_DoesNotDedupeMutationsWithIdenticalKeys()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 2);
        int calls = 0;
        TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = queue.EnqueueAsync("same-mutation", GitHubRequestPriority.Mutation, async _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                bothStarted.TrySetResult();
            }

            await release.Task;
            return 42;
        });
        Task<int> second = queue.EnqueueAsync("same-mutation", GitHubRequestPriority.Mutation, async _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                bothStarted.TrySetResult();
            }

            await release.Task;
            return 84;
        });

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();

        int[] results = await Task.WhenAll(first, second);
        Assert.Equal([42, 84], results);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RequestQueue_ForegroundLaneIsNeverBlockedByQueuedPrefetchWork()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        TaskCompletionSource releaseBackground = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource backgroundStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> activeBackground = queue.EnqueueAsync("prefetch-active", GitHubRequestPriority.Prefetch, async _ =>
        {
            backgroundStarted.SetResult();
            await releaseBackground.Task;
            return 1;
        });
        await backgroundStarted.Task;
        Task<int> queuedBackground = queue.EnqueueAsync("prefetch-queued", GitHubRequestPriority.BackgroundRefresh, _ => Task.FromResult(2));
        Task<int> foreground = queue.EnqueueAsync("foreground", GitHubRequestPriority.UserInitiated, _ => Task.FromResult(3));

        Task completed = await Task.WhenAny(foreground, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(foreground, completed);
        Assert.Equal(3, await foreground);
        Assert.False(queuedBackground.IsCompleted);

        releaseBackground.SetResult();
        int[] completedValues = await Task.WhenAll(activeBackground, queuedBackground);
        Assert.Equal(new[] { 1, 2 }, completedValues);
    }

    [Fact]
    public async Task RequestQueue_ForegroundJoinPromotesQueuedBackgroundDedupe()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        TaskCompletionSource releaseBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource blockerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<int> blocker = queue.EnqueueAsync("background-blocker", GitHubRequestPriority.BackgroundRefresh, async _ =>
        {
            blockerStarted.SetResult();
            await releaseBlocker.Task;
            return 1;
        });
        await blockerStarted.Task;

        Task<int> background = queue.EnqueueAsync("shared-read", GitHubRequestPriority.BackgroundRefresh, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(42);
        });
        await WaitUntilAsync(() => queue.InFlightCount == 2);

        Task<int> foreground = queue.EnqueueAsync("shared-read", GitHubRequestPriority.UserInitiated, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(84);
        });

        Assert.Equal(42, await foreground.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(42, await background.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, calls);
        Assert.False(blocker.IsCompleted);

        releaseBlocker.SetResult();
        Assert.Equal(1, await blocker);
    }

    [Fact]
    public async Task RequestQueue_CallerCancellationDoesNotCancelNeededSharedWork()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        TaskCompletionSource workStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseWork = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sharedTokenCancelled = false;

        Task<int> needed = queue.EnqueueAsync("shared-cancellation", GitHubRequestPriority.Visible, async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => sharedTokenCancelled = true);
            workStarted.SetResult();
            await releaseWork.Task;
            return 42;
        });
        await workStarted.Task;

        using CancellationTokenSource callerCancellation = new();
        Task<int> cancelledCaller = queue.EnqueueAsync(
            "shared-cancellation",
            GitHubRequestPriority.UserInitiated,
            _ => Task.FromResult(84),
            callerCancellation.Token);
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCaller);
        Assert.False(needed.IsCompleted);
        Assert.False(sharedTokenCancelled);

        releaseWork.SetResult();
        Assert.Equal(42, await needed);
        Assert.False(sharedTokenCancelled);
    }

    [Fact]
    public async Task RequestQueue_LastPrefetchSubscriberCancellationStopsActiveWork()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        TaskCompletionSource workStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource transportCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource callerCancellation = new();
        Task<int> prefetch = queue.EnqueueAsync(
            "cancel-active-prefetch",
            GitHubRequestPriority.Prefetch,
            async token =>
            {
                using CancellationTokenRegistration registration = token.Register(transportCancelled.SetResult);
                workStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            },
            callerCancellation.Token);
        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);
        await transportCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => queue.InFlightCount == 0);
    }

    [Fact]
    public async Task RequestQueue_CancelledQueuedPrefetchIsRemovedBeforeTransportStarts()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        TaskCompletionSource blockerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> blocker = queue.EnqueueAsync("prefetch-cancel-blocker", GitHubRequestPriority.Prefetch, async _ =>
        {
            blockerStarted.SetResult();
            await releaseBlocker.Task;
            return 1;
        });
        await blockerStarted.Task;

        int transportCalls = 0;
        using CancellationTokenSource callerCancellation = new();
        Task<int> queued = queue.EnqueueAsync(
            "prefetch-cancel-before-start",
            GitHubRequestPriority.Prefetch,
            _ =>
            {
                Interlocked.Increment(ref transportCalls);
                return Task.FromResult(2);
            },
            callerCancellation.Token);
        await WaitUntilAsync(() => queue.InFlightCount == 2);

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await WaitUntilAsync(() => queue.InFlightCount == 1);
        releaseBlocker.SetResult();

        Assert.Equal(1, await blocker);
        Assert.Equal(0, Volatile.Read(ref transportCalls));
    }

    [Fact]
    public async Task RequestQueue_CancelledPrefetchDoesNotCancelPromotedForegroundSubscriber()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        TaskCompletionSource blockerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> blocker = queue.EnqueueAsync("promotion-background-blocker", GitHubRequestPriority.Prefetch, async _ =>
        {
            blockerStarted.SetResult();
            await releaseBlocker.Task;
            return 1;
        });
        await blockerStarted.Task;

        TaskCompletionSource sharedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseShared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sharedTokenCancelled = false;
        int transportCalls = 0;
        using CancellationTokenSource prefetchCancellation = new();
        Task<int> prefetch = queue.EnqueueAsync(
            "promoted-shared-read",
            GitHubRequestPriority.Prefetch,
            async token =>
            {
                Interlocked.Increment(ref transportCalls);
                using CancellationTokenRegistration registration = token.Register(() => sharedTokenCancelled = true);
                sharedStarted.SetResult();
                await releaseShared.Task;
                return 42;
            },
            prefetchCancellation.Token);

        Task<int> foreground = queue.EnqueueAsync(
            "promoted-shared-read",
            GitHubRequestPriority.UserInitiated,
            _ => Task.FromResult(84));
        await sharedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        prefetchCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);
        Assert.False(sharedTokenCancelled);
        releaseShared.SetResult();
        Assert.Equal(42, await foreground.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Volatile.Read(ref transportCalls));
        Assert.False(sharedTokenCancelled);
        Assert.False(blocker.IsCompleted);

        releaseBlocker.SetResult();
        Assert.Equal(1, await blocker);
    }

    [Fact]
    public async Task LatestWinsHoverStorm_DoesNotAccumulateQueuedNetworkRequests()
    {
        GitHubRequestQueue queue = new(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 1);
        LatestWinsPrefetchScheduler scheduler = new();
        TaskCompletionSource finalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Task<int>> finalRequestAssigned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int transportCalls = 0;
        int startedItem = -1;

        for (int item = 0; item < 100; item++)
        {
            int captured = item;
            scheduler.Schedule(
                TimeSpan.FromMilliseconds(30),
                () =>
                {
                    CancellationTokenSource cancellation = new();
                    Task<int> request = queue.EnqueueAsync(
                        $"hover-item-{captured}",
                        GitHubRequestPriority.Prefetch,
                        async token =>
                        {
                            Interlocked.Increment(ref transportCalls);
                            Volatile.Write(ref startedItem, captured);
                            finalStarted.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                            return captured;
                        },
                        cancellation.Token);
                    finalRequestAssigned.TrySetResult(request);
                    return new CancellationHandle(cancellation);
                });
        }

        await finalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<int> finalRequest = await finalRequestAssigned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref transportCalls));
        Assert.Equal(99, Volatile.Read(ref startedItem));
        Assert.Equal(1, queue.InFlightCount);

        scheduler.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalRequest);
        await WaitUntilAsync(() => queue.InFlightCount == 0);
    }

    [Fact]
    public async Task RestTransport_AddsConditionalHeadersAndParsesNotModifiedHeaders()
    {
        HttpRequestMessage? captured = null;
        StubHttpMessageHandler handler = new(request =>
        {
            captured = request;
            HttpResponseMessage response = new(HttpStatusCode.NotModified);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-2\"");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "42");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1893456000");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", "search");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return response;
        });
        GitHubRestTransport transport = new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });
        DateTimeOffset lastModified = DateTimeOffset.UtcNow.AddMinutes(-10);

        GitHubRestResponse<Phase0TestPayload> response = await transport.SendJsonAsync(
            new GitHubRestRequest(
                GitHubAuthenticationConstants.PublicAccessToken,
                HttpMethod.Get,
                "test/path",
                "\"etag-1\"",
                lastModified),
            Phase0TestJsonContext.Default.Phase0TestPayload);

        Assert.NotNull(captured);
        Assert.Equal("\"etag-1\"", Assert.Single(captured!.Headers.IfNoneMatch).Tag);
        Assert.Equal(lastModified, captured.Headers.IfModifiedSince);
        Assert.True(response.IsNotModified);
        Assert.Equal("\"etag-2\"", response.ETag);
        Assert.Equal(42, response.RateLimitRemaining);
        Assert.Equal("search", response.RateLimitResource);
        Assert.Equal(TimeSpan.FromSeconds(3), response.RetryAfter);
    }

    [Theory]
    [Trait("Category", "ReleaseSecurity")]
    [InlineData("https://attacker.example/private")]
    [InlineData("//attacker.example/private")]
    [InlineData("\\\\attacker.example\\private")]
    public async Task RestTransport_RejectsOffOriginAndNetworkPathsBeforeSendingBearerToken(string path)
    {
        int sends = 0;
        StubHttpMessageHandler handler = new(_ =>
        {
            Interlocked.Increment(ref sends);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        GitHubRestTransport transport = new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            transport.SendJsonAsync(
                new GitHubRestRequest("secret-token", HttpMethod.Get, path),
                Phase0TestJsonContext.Default.Phase0TestPayload));

        Assert.Equal(0, sends);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void RestTransport_RejectsAnOffOriginBaseAddress()
    {
        StubHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Throws<InvalidOperationException>(() =>
            new GitHubRestTransport(new HttpClient(handler) { BaseAddress = new Uri("https://attacker.example/") }));
    }

    [Fact]
    public void QueryKeys_IsolateMediaTypeAndResultRepresentation()
    {
        string baseline = GitHubQueryKeys.Create(
            "u1",
            HttpMethod.Get,
            "repos/octo/app",
            "application/vnd.github+json",
            typeof(Phase0TestPayload));
        string mediaVariant = GitHubQueryKeys.Create(
            "u1",
            HttpMethod.Get,
            "repos/octo/app",
            "application/vnd.github.star+json",
            typeof(Phase0TestPayload));
        string typeVariant = GitHubQueryKeys.Create(
            "u1",
            HttpMethod.Get,
            "repos/octo/app",
            "application/vnd.github+json",
            typeof(Phase0TestPayload[]));

        Assert.NotEqual(baseline, mediaVariant);
        Assert.NotEqual(baseline, typeVariant);
        Assert.Equal(
            baseline,
            GitHubQueryKeys.CreateDedupeKey(
                "u1",
                HttpMethod.Get,
                "repos/octo/app",
                "application/vnd.github+json",
                typeof(Phase0TestPayload)));
    }

    [Fact]
    public async Task RestTransport_ThrowsAuthenticationExceptionWithGitHubMessage()
    {
        StubHttpMessageHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"Bad credentials\"}", Encoding.UTF8, "application/json")
            };
            return response;
        });
        GitHubRestTransport transport = new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        GitHubAuthenticationException exception = await Assert.ThrowsAsync<GitHubAuthenticationException>(() =>
            transport.SendJsonAsync(
                new GitHubRestRequest("token", HttpMethod.Get, "user"),
                Phase0TestJsonContext.Default.Phase0TestPayload));

        Assert.Equal("Bad credentials", exception.Message);
    }

    [Fact]
    public async Task RestTransport_ThrowsRateLimitExceptionWithRetryDelay()
    {
        StubHttpMessageHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"secondary rate limit\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", "graphql");
            return response;
        });
        GitHubRestTransport transport = new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        GitHubRateLimitException exception = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            transport.SendJsonAsync(
                new GitHubRestRequest("token", HttpMethod.Get, "user"),
                Phase0TestJsonContext.Default.Phase0TestPayload));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryDelay);
        Assert.Equal("graphql", exception.RateLimitResource);
    }

    [Fact]
    public void RetryPolicy_UsesRateLimitResetWhenRemainingIsZero()
    {
        DateTimeOffset now = new(2026, 05, 17, 12, 00, 00, TimeSpan.Zero);

        TimeSpan? delay = GitHubRetryPolicy.CalculateRetryDelay(
            HttpStatusCode.Forbidden,
            rateLimitRemaining: 0,
            rateLimitReset: now.AddSeconds(12),
            retryAfter: null,
            now);

        Assert.Equal(TimeSpan.FromSeconds(13), delay);
    }

    [Fact]
    public void RetryPolicy_UsesDefaultDelayForSecondaryRateLimit()
    {
        TimeSpan? delay = GitHubRetryPolicy.CalculateRetryDelay(
            HttpStatusCode.Forbidden,
            rateLimitRemaining: 42,
            rateLimitReset: null,
            retryAfter: null,
            DateTimeOffset.UtcNow);

        Assert.Equal(GitHubRetryPolicy.DefaultSecondaryRateLimitDelay, delay);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class CancellationHandle(CancellationTokenSource cancellation) : IDisposable
    {
        private CancellationTokenSource? _cancellation = cancellation;

        public void Dispose()
        {
            CancellationTokenSource? current = Interlocked.Exchange(ref _cancellation, null);
            if (current is null)
            {
                return;
            }

            current.Cancel();
            current.Dispose();
        }
    }
}
