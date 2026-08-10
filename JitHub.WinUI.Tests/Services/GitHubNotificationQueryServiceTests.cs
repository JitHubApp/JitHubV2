using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubNotificationQueryServiceTests
{
    [Theory]
    [InlineData(NotificationListFilter.Unread, "notifications?all=false&participating=false&per_page=50&page=1")]
    [InlineData(NotificationListFilter.All, "notifications?all=true&participating=false&per_page=25&page=2")]
    [InlineData(NotificationListFilter.Participating, "notifications?all=true&participating=true&per_page=1&page=1")]
    public async Task GetPage_UsesExpectedFilterAndClampsPagination(
        NotificationListFilter filter,
        string expectedPath)
    {
        RecordingQueryService queryService = new();
        GitHubNotificationQueryService service = new(queryService, new ImmediateRequestQueue(), new HttpClient(new RecordingHandler()));
        int page = filter == NotificationListFilter.All ? 2 : 0;
        int pageSize = filter switch
        {
            NotificationListFilter.Unread => 100,
            NotificationListFilter.All => 25,
            _ => 0
        };

        await service.GetPageAsync("token", "42", filter, page, pageSize);

        Assert.Equal(expectedPath, queryService.LastRelativePath);
        Assert.Contains("notifications", queryService.LastTags);
        Assert.Contains($"notifications-{filter.ToString().ToLowerInvariant()}", queryService.LastTags);
    }

    [Fact]
    public async Task GetSubscription_UsesThreadScopedCacheTag()
    {
        RecordingQueryService queryService = new();
        GitHubNotificationQueryService service = new(queryService, new ImmediateRequestQueue(), new HttpClient(new RecordingHandler()));

        await service.GetSubscriptionAsync("token", "42", " 123 ");

        Assert.Equal("notifications/threads/123/subscription", queryService.LastRelativePath);
        Assert.Contains("notification-thread-123", queryService.LastTags);
    }

    [Fact]
    public async Task ThreadMutations_UseGitHubMethodsAndInvalidateSharedCaches()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new();
        GitHubNotificationQueryService service = new(
            queryService,
            new ImmediateRequestQueue(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        await service.MarkThreadReadAsync("token", "42", "123");
        AssertRequest(handler, HttpMethod.Patch, "notifications/threads/123");

        GitHubNotificationSubscription followed = await service.SubscribeThreadAsync("token", "42", "123");
        AssertRequest(handler, HttpMethod.Put, "notifications/threads/123/subscription");
        Assert.Contains("\"ignored\":false", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("subscribed", handler.LastBody, StringComparison.Ordinal);
        Assert.True(followed.Subscribed);
        Assert.False(followed.Ignored);

        await service.UnsubscribeThreadAsync("token", "42", "123");
        AssertRequest(handler, HttpMethod.Delete, "notifications/threads/123/subscription");
        Assert.Equal(string.Empty, handler.LastBody);

        GitHubNotificationSubscription muted = await service.MuteThreadAsync("token", "42", "123");
        AssertRequest(handler, HttpMethod.Put, "notifications/threads/123/subscription");
        Assert.Contains("\"ignored\":true", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("subscribed", handler.LastBody, StringComparison.Ordinal);
        Assert.True(muted.Ignored);

        GitHubNotificationSubscription unmuted = await service.UnmuteThreadAsync("token", "42", "123");
        AssertRequest(handler, HttpMethod.Put, "notifications/threads/123/subscription");
        Assert.Contains("\"ignored\":false", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("subscribed", handler.LastBody, StringComparison.Ordinal);
        Assert.True(unmuted.Subscribed);
        Assert.False(unmuted.Ignored);

        Assert.Contains(queryService.InvalidatedTagSets, tags =>
            tags.Contains("notifications") &&
            tags.Contains("dashboard-notifications") &&
            tags.Contains("notification-thread-123"));
    }

    [Fact]
    public async Task SubscriptionMutation_UsesReturnedGitHubStateWhenAvailable()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new(
            HttpStatusCode.OK,
            "{\"subscribed\":true,\"ignored\":false,\"reason\":\"manual\"}");
        GitHubNotificationQueryService service = new(
            queryService,
            new ImmediateRequestQueue(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        GitHubNotificationSubscription result = await service.SubscribeThreadAsync("token", "42", "123");

        Assert.True(result.Subscribed);
        Assert.False(result.Ignored);
        Assert.Equal("manual", result.Reason);
    }

    [Fact]
    public async Task MarkAllRead_UsesMutationLaneAndInvalidatesDashboardPreview()
    {
        RecordingQueryService queryService = new();
        ImmediateRequestQueue requestQueue = new();
        RecordingHandler handler = new(HttpStatusCode.Accepted);
        GitHubNotificationQueryService service = new(
            queryService,
            requestQueue,
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        await service.MarkAllReadAsync("token", "42");

        AssertRequest(handler, HttpMethod.Put, "notifications");
        Assert.Equal(GitHubRequestPriority.Mutation, requestQueue.LastPriority);
        Assert.Contains("last_read_at", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains(queryService.InvalidatedTagSets, tags =>
            tags.Contains("notifications") && tags.Contains("dashboard-notifications"));
    }

    [Theory]
    [InlineData("mark-all-read")]
    [InlineData("mark-read")]
    [InlineData("subscribe")]
    [InlineData("unsubscribe")]
    [InlineData("mute")]
    [InlineData("unmute")]
    public async Task SuccessfulRemoteMutation_IsNotFailedByCacheInvalidation(string operation)
    {
        RecordingQueryService queryService = new() { InvalidationException = new OperationCanceledException("cache unavailable") };
        RecordingHandler handler = new();
        GitHubNotificationQueryService service = new(
            queryService,
            new ImmediateRequestQueue(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        switch (operation)
        {
            case "mark-all-read":
                await service.MarkAllReadAsync("token", "42");
                break;
            case "mark-read":
                await service.MarkThreadReadAsync("token", "42", "123");
                break;
            case "subscribe":
                await service.SubscribeThreadAsync("token", "42", "123");
                break;
            case "unsubscribe":
                await service.UnsubscribeThreadAsync("token", "42", "123");
                break;
            case "mute":
                await service.MuteThreadAsync("token", "42", "123");
                break;
            case "unmute":
                await service.UnmuteThreadAsync("token", "42", "123");
                break;
        }

        Assert.Equal(1, handler.RequestCount);
        Assert.Single(queryService.InvalidatedTagSets);
    }

    [Theory]
    [InlineData("mark-read")]
    [InlineData("subscribe")]
    [InlineData("unsubscribe")]
    [InlineData("mute")]
    [InlineData("unmute")]
    public async Task NotModified_IsSuccessfulIdempotentMutationOutcome(string operation)
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new(HttpStatusCode.NotModified);
        GitHubNotificationQueryService service = new(
            queryService,
            new ImmediateRequestQueue(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        switch (operation)
        {
            case "mark-read":
                await service.MarkThreadReadAsync("token", "42", "123");
                break;
            case "subscribe":
                GitHubNotificationSubscription subscribed = await service.SubscribeThreadAsync("token", "42", "123");
                Assert.True(subscribed.Subscribed);
                break;
            case "unsubscribe":
                await service.UnsubscribeThreadAsync("token", "42", "123");
                break;
            case "mute":
                GitHubNotificationSubscription muted = await service.MuteThreadAsync("token", "42", "123");
                Assert.True(muted.Ignored);
                break;
            case "unmute":
                GitHubNotificationSubscription unmuted = await service.UnmuteThreadAsync("token", "42", "123");
                Assert.True(unmuted.Subscribed);
                break;
        }

        Assert.Equal(1, handler.RequestCount);
        Assert.Single(queryService.InvalidatedTagSets);
    }

    [Fact]
    public void PublicContract_DoesNotExposeUnsupportedMarkUnreadMutation()
    {
        Assert.DoesNotContain(
            typeof(IGitHubNotificationQueryService).GetMethods(),
            static method => string.Equals(method.Name, "MarkThreadUnreadAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicPreview_ReturnsDeterministicFirstPageWithoutNetwork()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new();
        GitHubNotificationQueryService service = new(queryService, new ImmediateRequestQueue(), new HttpClient(handler));

        CachedResult<GitHubNotificationThread[]> first = await service.GetPageAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            NotificationListFilter.Unread,
            1,
            50);
        CachedResult<GitHubNotificationThread[]> second = await service.GetPageAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            NotificationListFilter.Unread,
            2,
            50);

        Assert.Equal(2, first.Value?.Length);
        Assert.Empty(second.Value!);
        Assert.Null(queryService.LastRelativePath);
        Assert.Equal(0, handler.RequestCount);
    }

    private static void AssertRequest(RecordingHandler handler, HttpMethod method, string relativePath)
    {
        Assert.Equal(method, handler.LastMethod);
        Assert.Equal(relativePath, handler.LastRelativePath);
    }

    private sealed class RecordingQueryService : IGitHubQueryService
    {
        public string? LastRelativePath { get; private set; }
        public IReadOnlyCollection<string> LastTags { get; private set; } = [];
        public List<IReadOnlyCollection<string>> InvalidatedTagSets { get; } = [];
        public Exception? InvalidationException { get; init; }

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            LastRelativePath = query.RelativePath;
            LastTags = query.Tags ?? [];
            object value = typeof(T) == typeof(GitHubNotificationThread[])
                ? Array.Empty<GitHubNotificationThread>()
                : new GitHubNotificationSubscription();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>((T)value, CacheState.Fresh, now, now.AddMinutes(5)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class => GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
        {
            InvalidatedTagSets.Add(tags);
            if (InvalidationException is not null)
            {
                return Task.FromException(InvalidationException);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateRequestQueue : IGitHubRequestQueue
    {
        public GitHubRequestPriority LastPriority { get; private set; }

        public Task<T> EnqueueAsync<T>(
            string dedupeKey,
            GitHubRequestPriority priority,
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default)
        {
            LastPriority = priority;
            return work(cancellationToken);
        }
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode = HttpStatusCode.ResetContent,
        string? responseBody = null) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string LastRelativePath { get; private set; } = string.Empty;
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastRelativePath = request.RequestUri?.PathAndQuery.TrimStart('/') ?? string.Empty;
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HttpResponseMessage response = new(statusCode);
            if (responseBody is not null)
            {
                response.Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
