using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class Phase3DashboardQueryServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsCachedSectionsIndependently()
    {
        FakeDashboardQueryService queryService = new()
        {
            CacheState = CacheState.Stale,
            IsRefreshInProgress = true
        };
        GitHubDashboardQueryService service = new(queryService);
        GitHubUser user = CreateUser();

        DashboardHomeSnapshot snapshot = await service.GetSnapshotAsync("token", "42", user);

        Assert.Equal(user, snapshot.User);
        Assert.Equal(CacheState.Stale, snapshot.RecentRepositories.CacheState);
        Assert.True(snapshot.RecentRepositories.IsRefreshInProgress);
        Assert.True(snapshot.RecentActivity.IsRefreshInProgress);
        Assert.Single(snapshot.RecentRepositories.Value);
        Assert.Equal("octo/app", snapshot.RecentRepositories.Value[0].FullName);
        Assert.Equal(["new", "old"], snapshot.RecentActivity.Value.Select(static item => item.Id).ToArray());
        Assert.Contains(queryService.Paths, static path => path.StartsWith("notifications?", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.StartsWith("search/issues?", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.StartsWith("search/repositories?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSnapshotAsync_NotificationAuthFailureRequiresReconnectOnlyForNotifications()
    {
        FakeDashboardQueryService queryService = new()
        {
            FailNotificationsWithForbidden = true
        };
        GitHubDashboardQueryService service = new(queryService);

        DashboardHomeSnapshot snapshot = await service.GetSnapshotAsync("token", "42", CreateUser());

        Assert.True(snapshot.Notifications.RequiresReconnect);
        Assert.Equal(CacheState.Error, snapshot.Notifications.CacheState);
        Assert.Single(snapshot.RecentRepositories.Value);
        Assert.NotEmpty(snapshot.Metrics.Value);
    }

    [Fact]
    public void ActivityMerger_DedupesAndSortsNewestFirst()
    {
        GitHubActivityEvent duplicateOld = CreateActivity("same", DateTimeOffset.UtcNow.AddHours(-3));
        GitHubActivityEvent duplicateNew = CreateActivity("same", DateTimeOffset.UtcNow.AddHours(-1));
        GitHubActivityEvent newest = CreateActivity("newest", DateTimeOffset.UtcNow);

        GitHubActivityEvent[] merged = DashboardActivityMerger.Merge(
            [duplicateOld, newest],
            [duplicateNew],
            take: 10);

        Assert.Equal(["newest", "same"], merged.Select(static item => item.Id).ToArray());
        Assert.Equal(duplicateNew.CreatedAt, merged[1].CreatedAt);
    }

    [Fact]
    public void RecommendationBuilder_UsesRealSignalsAndDoesNotInventTrending()
    {
        GitHubRepository recent = CreateRepository(1, "octo/app", language: "C#");
        GitHubRepository starred = CreateRepository(2, "octo/tool", stars: 900);
        GitHubRepository searched = CreateRepository(3, "dotnet/runtime", language: "C#", stars: 100_000);

        GitHubRepository[] recommendations = DashboardRecommendationBuilder.Build(
            [recent],
            [starred],
            [searched],
            take: 5);

        Assert.Equal(["octo/tool", "dotnet/runtime", "octo/app"], recommendations.Select(static repo => repo.FullName).ToArray());
        Assert.Equal("C#", DashboardRecommendationBuilder.SelectPrimaryLanguage([recent]));
        Assert.DoesNotContain(recommendations, static repo => repo.FullName.Contains("trending", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NotificationThread_DeserializesRestPayload()
    {
        const string json = """
            {
              "id": "1",
              "unread": true,
              "reason": "mention",
              "updated_at": "2026-05-18T10:00:00Z",
              "subject": {
                "title": "Fix cache refresh",
                "url": "https://api.github.com/repos/octo/app/issues/42",
                "type": "Issue"
              },
              "repository": {
                "id": 1,
                "name": "app",
                "full_name": "octo/app",
                "owner": { "login": "octo" }
              }
            }
            """;

        GitHubNotificationThread? thread = JsonSerializer.Deserialize(
            json,
            Phase0GitHubJsonSerializerContext.Default.GitHubNotificationThread);

        Assert.NotNull(thread);
        Assert.True(thread!.Unread);
        Assert.Equal("Fix cache refresh", thread.Subject.Title);
        Assert.Equal("Issue", thread.Subject.Type);
        Assert.Equal("octo/app", thread.Repository.FullName);
    }

    [Fact]
    public void OAuthLoginUri_UsesLeastPrivilegeDefaultScopes()
    {
        GitHubClientService client = new();

        Uri uri = client.CreateLoginUri("client-id", "state", "jithub://auth");
        string decoded = Uri.UnescapeDataString(uri.Query);

        Assert.Contains("scope=user repo notifications", decoded);
        Assert.DoesNotContain("gist", decoded);
        Assert.DoesNotContain("delete_repo", decoded);
    }

    [Fact]
    public void OAuthLoginUri_AddsDestructiveScopeOnlyWhenRequested()
    {
        GitHubClientService client = new();

        Uri uri = client.CreateLoginUri(
            "client-id",
            "state",
            "jithub://auth",
            ["delete_repo", "delete_repo"]);
        string decoded = Uri.UnescapeDataString(uri.Query);

        Assert.Contains("scope=user repo notifications delete_repo", decoded);
        Assert.Equal(1, decoded.Split("delete_repo", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void OAuthScopePolicy_RecognizesGrantedAndMissingScopes()
    {
        HashSet<string> granted = new(["user", "repo", "notifications", "delete_repo"], StringComparer.Ordinal);

        Assert.True(OAuthScopePolicy.HasAll(granted, ["delete_repo"]));
        Assert.True(OAuthScopePolicy.HasAll(granted, []));
        Assert.False(OAuthScopePolicy.HasAll(granted, ["admin:org"]));
    }

    [Fact]
    public async Task GetTokenScopesAsync_ParsesGrantedScopesHeader()
    {
        using HttpClient httpClient = new(new StubHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
            response.Headers.Add("X-OAuth-Scopes", "user, repo, notifications, delete_repo");
            return response;
        }));
        GitHubClientService client = new(httpClient);

        IReadOnlySet<string> scopes = await client.GetTokenScopesAsync("token");

        Assert.True(scopes.SetEquals(["user", "repo", "notifications", "delete_repo"]));
    }

    [Fact]
    public async Task GetTokenScopesAsync_ReturnsEmptyWhenGitHubOmitsHeader()
    {
        using HttpClient httpClient = new(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        }));
        GitHubClientService client = new(httpClient);

        IReadOnlySet<string> scopes = await client.GetTokenScopesAsync("token");

        Assert.Empty(scopes);
    }

    private static GitHubUser CreateUser() => new()
    {
        Id = 42,
        Login = "octo",
        Name = "Octo",
        PublicRepos = 8,
        Followers = 21
    };

    private static GitHubRepository CreateRepository(
        long id,
        string fullName,
        string language = "C#",
        int stars = 42)
    {
        string[] parts = fullName.Split('/', 2);
        return new GitHubRepository
        {
            Id = id,
            Name = parts[1],
            FullName = fullName,
            Description = "Repository description",
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{fullName}",
            Language = language,
            StargazersCount = stars,
            ForksCount = 3,
            Owner = new GitHubRepositoryOwner
            {
                Login = parts[0]
            },
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
    }

    private static GitHubActivityEvent CreateActivity(string id, DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            Type = "WatchEvent",
            CreatedAt = createdAt,
            Actor = new GitHubActor
            {
                Id = 1,
                Login = "octo"
            },
            Repo = new GitHubActivityRepository
            {
                Id = 1,
                Name = "octo/app"
            }
        };

    private sealed class FakeDashboardQueryService : IGitHubQueryService
    {
        public CacheState CacheState { get; set; } = CacheState.Fresh;

        public bool IsRefreshInProgress { get; set; }

        public bool FailNotificationsWithForbidden { get; set; }

        public List<string> Paths { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Paths.Add(query.RelativePath);
            if (FailNotificationsWithForbidden && query.RelativePath.StartsWith("notifications?", StringComparison.Ordinal))
            {
                throw new GitHubApiException(HttpStatusCode.Forbidden, "Resource not accessible by integration.");
            }

            object payload = ResolvePayload(query.RelativePath, typeof(T));
            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(4),
                IsRefreshInProgress));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static object ResolvePayload(string path, Type type)
        {
            if (type == typeof(GitHubRepository[]) && path.StartsWith("user/repos", StringComparison.Ordinal))
            {
                return new[] { CreateRepository(1, "octo/app") };
            }

            if (type == typeof(GitHubRepository[]) && path.StartsWith("user/starred", StringComparison.Ordinal))
            {
                return new[] { CreateRepository(2, "octo/tool", stars: 900) };
            }

            if (type == typeof(GitHubRepositorySearchResponse))
            {
                return new GitHubRepositorySearchResponse
                {
                    Items = [CreateRepository(3, "dotnet/runtime", stars: 100_000)]
                };
            }

            if (type == typeof(GitHubActivityEvent[]) && path.Contains("received_events", StringComparison.Ordinal))
            {
                return new[] { CreateActivity("old", DateTimeOffset.UtcNow.AddHours(-2)) };
            }

            if (type == typeof(GitHubActivityEvent[]))
            {
                return new[] { CreateActivity("new", DateTimeOffset.UtcNow.AddMinutes(-5)) };
            }

            if (type == typeof(GitHubNotificationThread[]))
            {
                return new[]
                {
                    new GitHubNotificationThread
                    {
                        Id = "n1",
                        Reason = "mention",
                        Unread = true,
                        Subject = new GitHubNotificationSubject
                        {
                            Title = "Fix cache refresh",
                            Type = "Issue",
                            Url = "https://api.github.com/repos/octo/app/issues/42"
                        },
                        Repository = CreateRepository(1, "octo/app")
                    }
                };
            }

            if (type == typeof(GitHubSearchCountResponse))
            {
                return new GitHubSearchCountResponse { TotalCount = path.Contains("type%3Apr", StringComparison.Ordinal) ? 4 : 9 };
            }

            throw new InvalidOperationException($"No fake payload for {type.Name} at {path}.");
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
