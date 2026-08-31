using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubProfileQueryServiceTests
{
    [Fact]
    public async Task GetProfileAsync_PublicPreviewReturnsRichNativeProfileSnapshot()
    {
        GitHubProfileQueryService service = CreateService();

        GitHubUserProfileSnapshot snapshot = await service.GetProfileAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "preview",
            "octocat",
            forceAuthenticatedUser: false);

        Assert.Equal("octocat", snapshot.User.Value.Login);
        Assert.True(snapshot.Readme.Value.Exists);
        Assert.Contains("JitHub", snapshot.Readme.Value.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(snapshot.Contributions.Value.Weeks);
        Assert.True(snapshot.Contributions.Value.TotalContributions > 0);
        Assert.NotEmpty(snapshot.PinnedItems.Value);
        Assert.NotEmpty(snapshot.Repositories.Value);
        Assert.NotEmpty(snapshot.Followers.Value);
        Assert.NotEmpty(snapshot.Following.Value);
        Assert.NotEmpty(snapshot.PublicActivity.Value);
        Assert.NotEmpty(snapshot.Organizations.Value);
        Assert.Contains(snapshot.Highlights.Value, static item => item.Id == "developer-program");
        Assert.Contains(snapshot.Highlights.Value, static item => item.Id == "hireable");
    }

    [Fact]
    public async Task GetProfileAsync_LoadsOnlyVisibleProfileSections()
    {
        FakeQueryService queryService = new();
        FakeCacheStore cacheStore = new();
        FakeGraphQlQueryService graphQlQueryService = new(CreateGraphQlUser("octocat"));
        GitHubProfileQueryService service = CreateService(queryService, cacheStore, graphQlQueryService);

        GitHubUserProfileSnapshot snapshot = await service.GetProfileAsync(
            "token",
            "42",
            "octocat",
            forceAuthenticatedUser: false);

        Assert.Equal("octocat", snapshot.User.Value.Login);
        Assert.True(snapshot.Readme.Value.Exists);
        Assert.Equal("# Hello profile", snapshot.Readme.Value.Markdown);
        Assert.Equal("README.md", snapshot.Readme.Value.Path);
        Assert.Empty(snapshot.Repositories.Value);
        Assert.Empty(snapshot.StarredRepositories.Value);
        Assert.Empty(snapshot.Followers.Value);
        Assert.Empty(snapshot.Following.Value);
        Assert.Empty(snapshot.PublicActivity.Value);
        Assert.Single(snapshot.Organizations.Value);
        Assert.Equal(3, snapshot.Contributions.Value.TotalContributions);
        Assert.Single(snapshot.PinnedItems.Value);
        Assert.True(snapshot.ViewerState.Value.ViewerCanFollow);
        Assert.Contains(snapshot.Highlights.Value, static item => item.Id == "github-star");
        Assert.Contains(snapshot.Highlights.Value, static item => item.Id == "developer-program");

        Assert.Contains(queryService.Paths, static path => path == "users/octocat");
        Assert.Contains(queryService.Paths, static path => path == "repos/octocat/octocat/readme");
        Assert.Contains(queryService.Paths, static path => path == "users/octocat/orgs?per_page=100&page=1");
        Assert.DoesNotContain(queryService.Paths, static path => path.Contains("/repos?", StringComparison.Ordinal));
        Assert.DoesNotContain(queryService.Paths, static path => path.Contains("/starred?", StringComparison.Ordinal));
        Assert.DoesNotContain(queryService.Paths, static path => path.Contains("/followers?", StringComparison.Ordinal));
        Assert.DoesNotContain(queryService.Paths, static path => path.Contains("/following?", StringComparison.Ordinal));
        Assert.DoesNotContain(queryService.Paths, static path => path.Contains("/events/public?", StringComparison.Ordinal));
        Assert.Single(graphQlQueryService.Requests);
        Assert.Equal("octocat", graphQlQueryService.Requests[0].Request.Variables?["login"]);
        Assert.Equal(HttpMethod.Post, graphQlQueryService.Requests[0].CacheQuery.Method);
        Assert.Contains("profile-graphql", graphQlQueryService.Requests[0].CacheQuery.Tags!);
        Assert.Equal([QueryFetchPolicy.StaleFirst], graphQlQueryService.FetchPolicies);
    }

    [Fact]
    public async Task ProfileSectionMethods_LoadOnlyRequestedSectionThroughCache()
    {
        FakeQueryService queryService = new();
        GitHubProfileQueryService service = CreateService(queryService);

        DashboardSectionResult<GitHubRepository[]> repositories = await service.GetRepositoriesAsync("token", "42", "octocat");
        DashboardSectionResult<GitHubRepository[]> stars = await service.GetStarredRepositoriesAsync("token", "42", "octocat");
        DashboardSectionResult<GitHubUser[]> followers = await service.GetFollowersAsync("token", "42", "octocat");
        DashboardSectionResult<GitHubUser[]> following = await service.GetFollowingAsync("token", "42", "octocat");
        DashboardSectionResult<GitHubActivityEvent[]> activity = await service.GetPublicActivityAsync("token", "42", "octocat");

        Assert.Single(repositories.Value);
        Assert.Single(stars.Value);
        Assert.Single(followers.Value);
        Assert.Single(following.Value);
        Assert.Single(activity.Value);
        Assert.Equal(
            [
                "users/octocat/repos?sort=updated&direction=desc&per_page=50&page=1",
                "users/octocat/starred?sort=updated&direction=desc&per_page=50&page=1",
                "users/octocat/followers?per_page=50&page=1",
                "users/octocat/following?per_page=50&page=1",
                "users/octocat/events/public?per_page=50&page=1"
            ],
            queryService.Paths);
    }

    [Fact]
    public async Task ProfileFullModesRequestIndependentCachedPages()
    {
        FakeQueryService queryService = new();
        GitHubProfileQueryService service = CreateService(queryService);

        await service.GetRepositoriesPageAsync("token", "42", "octocat", 2);
        await service.GetStarredRepositoriesPageAsync("token", "42", "octocat", 3);
        await service.GetFollowersPageAsync("token", "42", "octocat", 4);
        await service.GetFollowingPageAsync("token", "42", "octocat", 5);
        await service.GetPublicActivityPageAsync("token", "42", "octocat", 6);

        Assert.Equal(
            [
                "users/octocat/repos?sort=updated&direction=desc&per_page=50&page=2",
                "users/octocat/starred?sort=updated&direction=desc&per_page=50&page=3",
                "users/octocat/followers?per_page=50&page=4",
                "users/octocat/following?per_page=50&page=5",
                "users/octocat/events/public?per_page=50&page=6"
            ],
            queryService.Paths);
    }

    [Fact]
    public async Task PublicActivitySixthFullPage_IsExplicitlyApiLimitedAtThreeHundredEvents()
    {
        FakeQueryService queryService = new() { ReturnFullActivityPages = true };
        GitHubProfileQueryService service = CreateService(queryService);

        DashboardSectionResult<GitHubActivityEvent[]> result = await service.GetPublicActivityPageAsync(
            "token", "42", "octocat", 6);

        Assert.Equal(50, result.Value.Length);
        Assert.Equal(PagedDataCompleteness.ApiLimited, result.Completeness);
        Assert.Equal(300, result.LoadedItemCount);
        Assert.Equal(6, result.LoadedPageCount);
        Assert.Contains(
            queryService.Paths,
            static path => path == "users/octocat/events/public?per_page=50&page=6");
    }

    [Fact]
    public async Task PublicActivityPastGitHubCap_DoesNotIssueAnotherRequest()
    {
        FakeQueryService queryService = new() { ReturnFullActivityPages = true };
        GitHubProfileQueryService service = CreateService(queryService);

        DashboardSectionResult<GitHubActivityEvent[]> result = await service.GetPublicActivityPageAsync(
            "token", "42", "octocat", 7);

        Assert.Empty(result.Value);
        Assert.Equal(PagedDataCompleteness.ApiLimited, result.Completeness);
        Assert.Equal(300, result.LoadedItemCount);
        Assert.Empty(queryService.Paths);
    }

    [Fact]
    public async Task GetProfileAsync_MissingReadmeReturnsCompactEmptyStateWithoutFailingSnapshot()
    {
        FakeQueryService queryService = new()
        {
            ReadmeNotFound = true
        };
        GitHubProfileQueryService service = CreateService(queryService, new FakeCacheStore(), new FakeGraphQlQueryService(CreateGraphQlUser("octocat")));

        GitHubUserProfileSnapshot snapshot = await service.GetProfileAsync(
            "token",
            "42",
            "octocat",
            forceAuthenticatedUser: false);

        Assert.False(snapshot.Readme.Value.Exists);
        Assert.Equal("octocat/octocat", snapshot.Readme.Value.RepositoryFullName);
        Assert.False(snapshot.Readme.HasError);
        Assert.Equal(CacheState.Fresh, snapshot.Readme.CacheState);
        Assert.Empty(snapshot.Repositories.Value);
    }

    [Fact]
    public async Task FollowUserAsync_UsesGitHubRelationshipEndpointAndInvalidatesProfileTags()
    {
        FakeCacheStore cacheStore = new();
        CaptureHttpHandler handler = new(new GitHubUser { Login = "octocat" }, HttpStatusCode.NoContent);
        GitHubProfileQueryService service = CreateService(
            new FakeQueryService(),
            cacheStore,
            new FakeGraphQlQueryService(CreateGraphQlUser("octocat")),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        await service.FollowUserAsync("token", "42", "octocat");

        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal(new Uri("https://api.github.com/user/following/octocat"), handler.Request.RequestUri);
        Assert.Contains(cacheStore.InvalidatedTags, static tags => tags.Contains("profile-graphql"));
    }

    [Fact]
    public async Task UnfollowUserAsync_UsesGitHubRelationshipEndpointAndInvalidatesProfileTags()
    {
        FakeCacheStore cacheStore = new();
        CaptureHttpHandler handler = new(new GitHubUser { Login = "octocat" }, HttpStatusCode.NoContent);
        GitHubProfileQueryService service = CreateService(
            new FakeQueryService(),
            cacheStore,
            new FakeGraphQlQueryService(CreateGraphQlUser("octocat")),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        await service.UnfollowUserAsync("token", "42", "octocat");

        Assert.Equal(HttpMethod.Delete, handler.Request!.Method);
        Assert.Equal(new Uri("https://api.github.com/user/following/octocat"), handler.Request.RequestUri);
        Assert.Contains(cacheStore.InvalidatedTags, static tags => tags.Contains("profile-following"));
    }

    [Fact]
    public async Task GetProfileAsync_OmitsUnsupportedBadgesInsteadOfInventingAchievements()
    {
        FakeQueryService queryService = new()
        {
            UserType = string.Empty,
            UserHireable = false,
            UserSiteAdmin = false
        };
        GitHubProfileGraphQlUser graphQlUser = new()
        {
            Login = "octocat",
            ContributionsCollection = new GitHubProfileContributionsCollection
            {
                ContributionCalendar = new GitHubProfileContributionCalendarPayload
                {
                    TotalContributions = 0,
                    Weeks = []
                }
            }
        };
        GitHubProfileQueryService service = CreateService(queryService, new FakeCacheStore(), new FakeGraphQlQueryService(graphQlUser));

        GitHubUserProfileSnapshot snapshot = await service.GetProfileAsync(
            "token",
            "42",
            "octocat",
            forceAuthenticatedUser: false);

        Assert.Empty(snapshot.Highlights.Value);
    }

    [Fact]
    public async Task Organizations_AutoPageAndRemainIsolatedFromOtherProfileSections()
    {
        FakeQueryService queryService = new() { ReturnPagedOrganizations = true };
        GitHubProfileQueryService service = CreateService(queryService);

        GitHubUserProfileSnapshot snapshot = await service.GetProfileAsync(
            "token", "42", "octocat", forceAuthenticatedUser: false);

        Assert.Equal(101, snapshot.Organizations.Value.Length);
        Assert.Equal(PagedDataCompleteness.Complete, snapshot.Organizations.Completeness);
        Assert.Equal(2, snapshot.Organizations.LoadedPageCount);
        Assert.True(snapshot.Readme.Value.Exists);
        Assert.Equal(3, snapshot.Contributions.Value.TotalContributions);
        Assert.Contains(queryService.Paths, static path => path == "users/octocat/orgs?per_page=100&page=2");
        Assert.Contains(GitHubRequestPriority.BackgroundRefresh, queryService.Priorities);
    }

    [Fact]
    public async Task OrganizationLaterPageRefreshFailure_RetainsCachedRowsAndDoesNotFailProfile()
    {
        FakeQueryService queryService = new()
        {
            ReturnPagedOrganizations = true,
            FailSecondOrganizationRefresh = true
        };
        GitHubProfileQueryService service = CreateService(queryService);

        GitHubUserProfileSnapshot snapshot = await service.GetProfileAsync(
            "token", "42", "octocat", forceAuthenticatedUser: false);

        Assert.Equal(101, snapshot.Organizations.Value.Length);
        Assert.Equal(PagedDataCompleteness.Partial, snapshot.Organizations.Completeness);
        Assert.Equal(2, snapshot.Organizations.LoadedPageCount);
        Assert.True(snapshot.Organizations.HasError);
        Assert.True(snapshot.Readme.Value.Exists);
        Assert.Equal(3, snapshot.Contributions.Value.TotalContributions);
    }

    [Fact]
    public async Task UpdateAuthenticatedProfileAsync_SendsRestSupportedFieldsAndInvalidatesProfileCache()
    {
        FakeCacheStore cacheStore = new();
        CaptureHttpHandler handler = new(new GitHubUser
        {
            Id = 42,
            Login = "octocat",
            Name = "Octo Cat"
        });
        GitHubProfileQueryService service = CreateService(
            new FakeQueryService(),
            cacheStore,
            new FakeGraphQlQueryService(CreateGraphQlUser("octocat")),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        GitHubUser user = await service.UpdateAuthenticatedProfileAsync(
            "token",
            "42",
            new GitHubUserProfileUpdateRequest
            {
                Name = "Octo Cat",
                Blog = "https://github.com/octocat",
                TwitterUsername = "octocat",
                Company = "@github",
                Location = "San Francisco",
                Hireable = true,
                Bio = "Native app fan."
            });

        Assert.Equal("octocat", user.Login);
        Assert.Equal(HttpMethod.Patch, handler.Request!.Method);
        Assert.Equal(new Uri("https://api.github.com/user"), handler.Request.RequestUri);
        Assert.Contains("\"name\":\"Octo Cat\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"blog\":\"https://github.com/octocat\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"twitter_username\":\"octocat\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"company\":\"@github\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"location\":\"San Francisco\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"hireable\":true", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"bio\":\"Native app fan.\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("avatar", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["profile", "profile-user"], cacheStore.InvalidatedTags.Single());
    }

    [Fact]
    public async Task AccountRemoval_CancelsAndDrainsProfileUpdateBeforeCacheInvalidation()
    {
        using ApplicationTaskCoordinator coordinator = new();
        AccountWorkQuiescence accountWork = new(coordinator);
        GitHubRequestQueue requestQueue = new(accountWork);
        BlockingHttpHandler handler = new();
        FakeCacheStore cacheStore = new();
        GitHubProfileQueryService service = new(
            new FakeQueryService(),
            cacheStore,
            new FakeGraphQlQueryService(CreateGraphQlUser("octocat")),
            requestQueue,
            coordinator,
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        Task<GitHubUser> mutation = service.UpdateAuthenticatedProfileAsync(
            "token",
            "42",
            new GitHubUserProfileUpdateRequest { Name = "Updated name" });
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.CancelAccountAsync("42").WaitAsync(TimeSpan.FromSeconds(2));
        await accountWork.QuiesceAsync("42").WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.True(handler.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.Empty(cacheStore.InvalidatedTags);
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }

    [Fact]
    public async Task CallerCancellation_CancelsProfileUpdateBeforeCacheInvalidation()
    {
        using ApplicationTaskCoordinator coordinator = new();
        AccountWorkQuiescence accountWork = new(coordinator);
        GitHubRequestQueue requestQueue = new(accountWork);
        BlockingHttpHandler handler = new();
        FakeCacheStore cacheStore = new();
        GitHubProfileQueryService service = new(
            new FakeQueryService(),
            cacheStore,
            new FakeGraphQlQueryService(CreateGraphQlUser("octocat")),
            requestQueue,
            coordinator,
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });
        using CancellationTokenSource cancellation = new();

        Task<GitHubUser> mutation = service.UpdateAuthenticatedProfileAsync(
            "token",
            "42",
            new GitHubUserProfileUpdateRequest { Name = "Updated name" },
            cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.True(handler.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.Empty(cacheStore.InvalidatedTags);
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }

    [Fact]
    public async Task Shutdown_CancelsAndDrainsFollowMutation()
    {
        using ApplicationTaskCoordinator coordinator = new();
        AccountWorkQuiescence accountWork = new(coordinator);
        GitHubRequestQueue requestQueue = new(accountWork);
        BlockingHttpHandler handler = new();
        FakeCacheStore cacheStore = new();
        GitHubProfileQueryService service = new(
            new FakeQueryService(),
            cacheStore,
            new FakeGraphQlQueryService(CreateGraphQlUser("octocat")),
            requestQueue,
            coordinator,
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

        Task mutation = service.FollowUserAsync("token", "42", "octocat");
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        ApplicationTaskShutdownResult shutdown = await coordinator
            .ShutdownAsync(TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.True(shutdown.Completed);
        Assert.Equal(0, shutdown.PendingTaskCount);
        Assert.True(handler.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.Empty(cacheStore.InvalidatedTags);
    }

    private static GitHubProfileQueryService CreateService(
        IGitHubQueryService? queryService = null,
        FakeCacheStore? cacheStore = null,
        FakeGraphQlQueryService? graphQlQueryService = null,
        HttpClient? httpClient = null) =>
        new(
            queryService ?? new FakeQueryService(),
            cacheStore ?? new FakeCacheStore(),
            graphQlQueryService ?? new FakeGraphQlQueryService(CreateGraphQlUser("octocat")),
            httpClient ?? new HttpClient { BaseAddress = new Uri("https://api.github.com/") });

    private static GitHubProfileGraphQlUser CreateGraphQlUser(string login) => new()
    {
        Login = login,
        IsViewer = false,
        ViewerCanFollow = true,
        ViewerIsFollowing = false,
        IsDeveloperProgramMember = true,
        IsGitHubStar = true,
        ContributionsCollection = new GitHubProfileContributionsCollection
        {
            ContributionCalendar = new GitHubProfileContributionCalendarPayload
            {
                TotalContributions = 3,
                Weeks =
                [
                    new()
                    {
                        ContributionDays =
                        [
                            new()
                            {
                                Date = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                                ContributionCount = 3,
                                Color = "#56b87d",
                                Weekday = 1
                            }
                        ]
                    }
                ]
            }
        },
        PinnedItems = new GitHubProfilePinnedItemsConnection
        {
            Nodes =
            [
                new()
                {
                    TypeName = "Repository",
                    Name = "jithub",
                    NameWithOwner = $"{login}/jithub",
                    Description = "Native GitHub client.",
                    Url = $"https://github.com/{login}/jithub",
                    StargazerCount = 42,
                    ForkCount = 7,
                    UpdatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                    PrimaryLanguage = new GitHubProfileLanguage { Name = "C#", Color = "#178600" }
                }
            ]
        }
    };

    private sealed class FakeQueryService : IGitHubQueryService
    {
        public bool ReadmeNotFound { get; init; }

        public string UserType { get; init; } = "User";

        public bool UserHireable { get; init; } = true;

        public bool UserSiteAdmin { get; init; }

        public bool ReturnPagedOrganizations { get; init; }

        public bool FailSecondOrganizationRefresh { get; init; }

        public bool ReturnFullActivityPages { get; init; }

        public List<string> Paths { get; } = [];

        public List<GitHubRequestPriority> Priorities { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Paths.Add(query.RelativePath);
            Priorities.Add(query.Priority);
            if (ReadmeNotFound && query.RelativePath.EndsWith("/readme", StringComparison.Ordinal))
            {
                throw new GitHubApiException(HttpStatusCode.NotFound, "Not Found");
            }

            if (FailSecondOrganizationRefresh &&
                fetchPolicy == QueryFetchPolicy.NetworkOnly &&
                query.RelativePath.Contains("/orgs?", StringComparison.Ordinal) &&
                query.RelativePath.Contains("page=2", StringComparison.Ordinal))
            {
                throw new HttpRequestException("organization page 2 refresh unavailable");
            }

            object payload = ResolvePayload(query.RelativePath, typeof(T));
            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState.Stale,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                DateTimeOffset.UtcNow.AddMinutes(-5),
                IsRefreshInProgress: true));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private object ResolvePayload(string path, Type type)
        {
            if (type == typeof(GitHubUser))
            {
                return new GitHubUser
                {
                    Id = 42,
                    Login = "octocat",
                    Name = "Octo Cat",
                    Bio = "Native app fan.",
                    PublicRepos = 9,
                    Followers = 12,
                    Following = 4,
                    PublicGists = 2,
                    Type = UserType,
                    Hireable = UserHireable,
                    SiteAdmin = UserSiteAdmin
                };
            }

            if (type == typeof(GitHubRepositoryContent))
            {
                string markdown = Convert.ToBase64String(Encoding.UTF8.GetBytes("# Hello profile"));
                return new GitHubRepositoryContent
                {
                    Name = "README.md",
                    Path = "README.md",
                    Content = markdown,
                    Encoding = "base64",
                    HtmlUrl = "https://github.com/octocat/octocat/blob/main/README.md"
                };
            }

            if (type == typeof(GitHubRepository[]))
            {
                return new[]
                {
                    new GitHubRepository
                    {
                        Id = path.Contains("starred", StringComparison.Ordinal) ? 2 : 1,
                        Name = path.Contains("starred", StringComparison.Ordinal) ? "starred" : "repo",
                        FullName = path.Contains("starred", StringComparison.Ordinal) ? "octocat/starred" : "octocat/repo",
                        Description = "A repository",
                        Language = "C#",
                        StargazersCount = 42,
                        ForksCount = 3,
                        Owner = new GitHubRepositoryOwner { Login = "octocat" }
                    }
                };
            }

            if (type == typeof(GitHubUser[]))
            {
                return new[]
                {
                    new GitHubUser
                    {
                        Id = path.Contains("following", StringComparison.Ordinal) ? 12 : 11,
                        Login = path.Contains("following", StringComparison.Ordinal) ? "following-user" : "follower-user",
                        AvatarUrl = "https://avatars.githubusercontent.com/u/1",
                        Bio = "Native profile test user."
                    }
                };
            }

            if (type == typeof(GitHubActivityEvent[]))
            {
                int count = ReturnFullActivityPages ? GitHubProfilePageSizes.Activity : 1;
                return Enumerable.Range(1, count)
                    .Select(index => new GitHubActivityEvent
                    {
                        Id = $"activity-{index}",
                        Type = "WatchEvent",
                        Public = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Actor = new GitHubActor { Login = "octocat" },
                        Repo = new GitHubActivityRepository { Id = 1, Name = "octocat/repo" }
                    })
                    .ToArray();
            }

            if (type == typeof(GitHubOrganization[]))
            {
                if (ReturnPagedOrganizations)
                {
                    int start = path.Contains("page=2", StringComparison.Ordinal) ? 101 : 1;
                    int count = start == 1 ? 100 : 1;
                    return Enumerable.Range(start, count)
                        .Select(static id => new GitHubOrganization
                        {
                            Id = id,
                            Login = $"organization-{id}",
                            Description = $"Organization {id}"
                        })
                        .ToArray();
                }

                return new[]
                {
                    new GitHubOrganization
                    {
                        Id = 1,
                        Login = "github",
                        Description = "GitHub"
                    }
                };
            }

            throw new InvalidOperationException($"No fake payload for {type.Name} at {path}.");
        }
    }

    private sealed class FakeCacheStore : IGitHubCacheStore
    {
        public List<GitHubQuery<GitHubProfileGraphQlData>> PutQueries { get; } = [];

        public List<IReadOnlyCollection<string>> InvalidatedTags { get; } = [];

        public Task<CachedResult<T>?> TryGetAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class =>
            Task.FromResult<CachedResult<T>?>(null);

        public Task PutAsync<T>(
            GitHubQuery<T> query,
            GitHubRestResponse<T> response,
            CancellationToken cancellationToken = default)
            where T : class
        {
            if (query is GitHubQuery<GitHubProfileGraphQlData> graphQlQuery)
            {
                PutQueries.Add(graphQlQuery);
            }

            return Task.CompletedTask;
        }

        public Task MarkRevalidatedAsync<T>(
            GitHubQuery<T> query,
            GitHubRestResponse<T> response,
            CancellationToken cancellationToken = default)
            where T : class =>
            Task.CompletedTask;

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
        {
            InvalidatedTags.Add([.. tags]);
            return Task.CompletedTask;
        }

        public Task ClearAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<long> GetTotalPayloadBytesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<long> GetTotalMetadataBytesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task EnforceCapsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGraphQlQueryService(GitHubProfileGraphQlUser user) : IGitHubGraphQlQueryService
    {
        public List<GitHubGraphQlQuery<GitHubProfileGraphQlData>> Requests { get; } = [];

        public List<QueryFetchPolicy> FetchPolicies { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubGraphQlQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Requests.Add((GitHubGraphQlQuery<GitHubProfileGraphQlData>)(object)query);
            FetchPolicies.Add(fetchPolicy);
            object data = new GitHubProfileGraphQlData
            {
                User = user,
                Viewer = user.IsViewer ? user : null
            };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>((T)data, CacheState.Fresh, now, now.AddMinutes(30)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubGraphQlQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);
    }

    private sealed class CaptureHttpHandler(GitHubUser user, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (statusCode == HttpStatusCode.NoContent)
            {
                return new HttpResponseMessage(statusCode);
            }

            string json = JsonSerializer.Serialize(user, GitHubJsonSerializerContext.Default.GitHubUser);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class BlockingHttpHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("A blocked profile mutation unexpectedly resumed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }
}
