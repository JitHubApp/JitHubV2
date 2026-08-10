using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GitHubNotificationQueryService : IGitHubNotificationQueryService
{
    private readonly IGitHubQueryService _queryService;
    private readonly IGitHubRequestQueue _requestQueue;
    private readonly HttpClient _httpClient;

    public GitHubNotificationQueryService(
        IGitHubQueryService queryService,
        IGitHubRequestQueue requestQueue)
        : this(queryService, requestQueue, CreateDefaultHttpClient())
    {
    }

    internal GitHubNotificationQueryService(
        IGitHubQueryService queryService,
        IGitHubRequestQueue requestQueue,
        HttpClient httpClient)
    {
        _queryService = queryService;
        _requestQueue = requestQueue;
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
    }

    public Task<CachedResult<GitHubNotificationThread[]>> GetPageAsync(
        string accessToken,
        string userId,
        NotificationListFilter filter,
        int page,
        int pageSize,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            GitHubNotificationThread[] value = page == 1 ? CreatePreviewNotifications(filter) : [];
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<GitHubNotificationThread[]>(value, CacheState.Fresh, now, now.AddMinutes(5)));
        }

        int normalizedPage = Math.Max(1, page);
        int normalizedPageSize = Math.Clamp(pageSize, 1, 50);
        bool all = filter != NotificationListFilter.Unread;
        bool participating = filter == NotificationListFilter.Participating;
        string path = $"notifications?all={all.ToString().ToLowerInvariant()}&participating={participating.ToString().ToLowerInvariant()}&per_page={normalizedPageSize}&page={normalizedPage}";
        GitHubQuery<GitHubNotificationThread[]> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubNotificationThreadArray,
            ["notifications", $"notifications-{filter.ToString().ToLowerInvariant()}"]);
        return _queryService.GetAsync(query, fetchPolicy, cancellationToken);
    }

    public Task<CachedResult<GitHubNotificationSubscription>> GetSubscriptionAsync(
        string accessToken,
        string userId,
        string threadId,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<GitHubNotificationSubscription>(new(), CacheState.Fresh, now, now.AddMinutes(5)));
        }

        string normalizedThreadId = NormalizeThreadId(threadId);
        string path = $"notifications/threads/{Uri.EscapeDataString(normalizedThreadId)}/subscription";
        GitHubQuery<GitHubNotificationSubscription> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubNotificationSubscription,
            ["notifications", $"notification-thread-{normalizedThreadId}"]);
        return _queryService.GetAsync(query, fetchPolicy, cancellationToken);
    }

    public async Task MarkAllReadAsync(string accessToken, string userId, CancellationToken cancellationToken = default)
    {
        GitHubNotificationMarkReadRequest payload = new() { LastReadAt = DateTimeOffset.UtcNow };
        await SendMutationAsync(
            accessToken,
            userId,
            HttpMethod.Put,
            "notifications",
            JsonContent.Create(payload, GitHubJsonSerializerContext.Default.GitHubNotificationMarkReadRequest),
            "mark-all-read",
            cancellationToken);
        await InvalidateBestEffortAsync(userId, ["notifications", "dashboard-notifications"]);
    }

    public async Task MarkThreadReadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default)
    {
        string normalizedThreadId = NormalizeThreadId(threadId);
        await SendMutationAsync(
            accessToken,
            userId,
            HttpMethod.Patch,
            $"notifications/threads/{Uri.EscapeDataString(normalizedThreadId)}",
            content: null,
            $"read-{normalizedThreadId}",
            cancellationToken);
        await InvalidateThreadBestEffortAsync(userId, normalizedThreadId);
    }

    public Task<GitHubNotificationSubscription> SubscribeThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default) =>
        SetThreadSubscriptionAsync(accessToken, userId, threadId, ignored: false, "subscribe", cancellationToken);

    public async Task UnsubscribeThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        string normalizedThreadId = NormalizeThreadId(threadId);
        await SendMutationAsync(
            accessToken,
            userId,
            HttpMethod.Delete,
            $"notifications/threads/{Uri.EscapeDataString(normalizedThreadId)}/subscription",
            content: null,
            $"unsubscribe-{normalizedThreadId}",
            cancellationToken);
        await InvalidateThreadBestEffortAsync(userId, normalizedThreadId);
    }

    public Task<GitHubNotificationSubscription> MuteThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default) =>
        SetThreadSubscriptionAsync(accessToken, userId, threadId, ignored: true, "mute", cancellationToken);

    public Task<GitHubNotificationSubscription> UnmuteThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default) =>
        SetThreadSubscriptionAsync(accessToken, userId, threadId, ignored: false, "unmute", cancellationToken);

    private async Task<GitHubNotificationSubscription> SetThreadSubscriptionAsync(
        string accessToken,
        string userId,
        string threadId,
        bool ignored,
        string operation,
        CancellationToken cancellationToken)
    {
        string normalizedThreadId = NormalizeThreadId(threadId);
        GitHubNotificationSubscriptionUpdateRequest payload = new() { Ignored = ignored };
        GitHubNotificationSubscription fallback = new()
        {
            Subscribed = !ignored,
            Ignored = ignored
        };
        GitHubNotificationSubscription result = await SendMutationWithResponseAsync(
            accessToken,
            userId,
            HttpMethod.Put,
            $"notifications/threads/{Uri.EscapeDataString(normalizedThreadId)}/subscription",
            JsonContent.Create(payload, GitHubJsonSerializerContext.Default.GitHubNotificationSubscriptionUpdateRequest),
            $"{operation}-{normalizedThreadId}",
            GitHubJsonSerializerContext.Default.GitHubNotificationSubscription,
            fallback,
            cancellationToken);
        await InvalidateThreadBestEffortAsync(userId, normalizedThreadId);
        return result;
    }

    private async Task<T> SendMutationWithResponseAsync<T>(
        string accessToken,
        string userId,
        HttpMethod method,
        string path,
        HttpContent? content,
        string operation,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> responseType,
        T fallback,
        CancellationToken cancellationToken)
        where T : class
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            content?.Dispose();
            return fallback;
        }

        string partition = GitHubAccountPartition.Require(userId);
        try
        {
            return await _requestQueue.EnqueueForAccountAsync(
                partition,
                $"{partition}:{operation}",
                GitHubRequestPriority.Mutation,
                async token =>
                {
                    using HttpRequestMessage request = new(method, path) { Content = content };
                    AddGitHubHeaders(request, accessToken);
                    using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    await EnsureSuccessAsync(response, token);
                    if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified ||
                        response.Content.Headers.ContentLength == 0)
                    {
                        return fallback;
                    }

                    return await response.Content.ReadFromJsonAsync(responseType, token) ?? fallback;
                },
                cancellationToken);
        }
        finally
        {
            content?.Dispose();
        }
    }

    private async Task SendMutationAsync(
        string accessToken,
        string userId,
        HttpMethod method,
        string path,
        HttpContent? content,
        string operation,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            content?.Dispose();
            return;
        }

        string partition = GitHubAccountPartition.Require(userId);
        try
        {
            await _requestQueue.EnqueueForAccountAsync(
                partition,
                $"{partition}:{operation}",
                GitHubRequestPriority.Mutation,
                async token =>
                {
                    using HttpRequestMessage request = new(method, path) { Content = content };
                    AddGitHubHeaders(request, accessToken);
                    using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    await EnsureSuccessAsync(response, token);

                    return true;
                },
                cancellationToken);
        }
        finally
        {
            content?.Dispose();
        }
    }

    private static void AddGitHubHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("JitHub", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        string message = await ReadErrorMessageAsync(response, cancellationToken);
        throw response.StatusCode == HttpStatusCode.Unauthorized
            ? new GitHubAuthenticationException(message)
            : new GitHubApiException(response.StatusCode, message);
    }

    private Task InvalidateThreadBestEffortAsync(string userId, string threadId) =>
        InvalidateBestEffortAsync(
            userId,
            ["notifications", "dashboard-notifications", $"notification-thread-{threadId}"]);

    private async Task InvalidateBestEffortAsync(string userId, IReadOnlyCollection<string> tags)
    {
        try
        {
            await _queryService.InvalidateTagsAsync(
                GitHubAccountPartition.Require(userId),
                tags,
                CancellationToken.None);
        }
        catch (Exception)
        {
            // The remote mutation is authoritative. A local cache failure must not
            // turn a successful GitHub write into a retryable UI error.
        }
    }

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        string[] tags)
        where T : class
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            partition,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(partition, HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            GitHubRequestPriority.Visible);
    }

    private static string NormalizeThreadId(string threadId) =>
        string.IsNullOrWhiteSpace(threadId)
            ? throw new ArgumentException("A notification thread id is required.", nameof(threadId))
            : threadId.Trim();

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            GitHubApiError? error = await response.Content.ReadFromJsonAsync(
                GitHubJsonSerializerContext.Default.GitHubApiError,
                cancellationToken);
            return string.IsNullOrWhiteSpace(error?.Message)
                ? $"GitHub returned HTTP {(int)response.StatusCode}."
                : JitHub.WinUI.Helpers.UserFacingError.ForInternalMessage(
                    error.Message,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                    "notification-api");
        }
        catch
        {
            return $"GitHub returned HTTP {(int)response.StatusCode}.";
        }
    }

    private static HttpClient CreateDefaultHttpClient() => new() { BaseAddress = new Uri("https://api.github.com/") };

    private static GitHubNotificationThread[] CreatePreviewNotifications(NotificationListFilter filter)
    {
        if (ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled)
        {
            GitHubNotificationThread[] largeNotifications = ProductPerformanceLargeAccountFixture.CreateNotifications(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.NotificationCount));
            return filter == NotificationListFilter.Unread
                ? largeNotifications.Where(static item => item.Unread).ToArray()
                : largeNotifications;
        }

        GitHubRepository repository = new()
        {
            Id = 1,
            Name = "JitHubV2",
            FullName = "JitHubApp/JitHubV2",
            DefaultBranch = "main",
            Owner = new GitHubRepositoryOwner { Login = "JitHubApp" }
        };
        GitHubNotificationThread[] all =
        [
            CreatePreview("preview-issue", true, "mention", "Issue", "Review compact notification workspace", "https://api.github.com/repos/JitHubApp/JitHubV2/issues/42", repository, 1),
            CreatePreview("preview-pr", true, "review_requested", "PullRequest", "Polish the native profile workspace", "https://api.github.com/repos/JitHubApp/JitHubV2/pulls/37", repository, 2),
            CreatePreview("preview-release", false, "subscribed", "Release", "JitHub vNext preview", "https://api.github.com/repos/JitHubApp/JitHubV2/releases/8", repository, 3)
        ];
        return filter == NotificationListFilter.Unread ? [.. all.AsSpan(0, 2)] : all;
    }

    private static GitHubNotificationThread CreatePreview(
        string id,
        bool unread,
        string reason,
        string type,
        string title,
        string url,
        GitHubRepository repository,
        int hoursAgo) =>
        new()
        {
            Id = id,
            Unread = unread,
            Reason = reason,
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
            Subject = new GitHubNotificationSubject { Type = type, Title = title, Url = url },
            Repository = repository
        };
}
