using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GitHubProfileQueryService : IGitHubProfileQueryService
{
    private const int RepositoryPageSize = GitHubProfilePageSizes.Repositories;
    private const int StarredPageSize = GitHubProfilePageSizes.Stars;
    private const int PeoplePageSize = GitHubProfilePageSizes.People;
    private const int ActivityPageSize = GitHubProfilePageSizes.Activity;
    private const int MaximumActivityCount = GitHubProfilePageSizes.ActivityMaximum;
    private const int OrganizationPageSize = 100;
    private const int MaximumOrganizationCount = 5000;
    private readonly IGitHubQueryService _queryService;
    private readonly IGitHubCacheStore _cacheStore;
    private readonly IGitHubGraphQlQueryService _graphQlQueryService;
    private readonly IGitHubRequestQueue _requestQueue;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly HttpClient _httpClient;

    public GitHubProfileQueryService(
        IGitHubQueryService queryService,
        IGitHubCacheStore cacheStore,
        IGitHubGraphQlQueryService graphQlQueryService,
        IGitHubRequestQueue requestQueue,
        IApplicationTaskCoordinator taskCoordinator)
        : this(
            queryService,
            cacheStore,
            graphQlQueryService,
            requestQueue,
            taskCoordinator,
            CreateDefaultHttpClient())
    {
    }

    internal GitHubProfileQueryService(
        IGitHubQueryService queryService,
        IGitHubCacheStore cacheStore,
        IGitHubGraphQlQueryService graphQlQueryService,
        HttpClient httpClient)
        : this(
            queryService,
            cacheStore,
            graphQlQueryService,
            new GitHubRequestQueue(),
            new ApplicationTaskCoordinator(),
            httpClient)
    {
    }

    internal GitHubProfileQueryService(
        IGitHubQueryService queryService,
        IGitHubCacheStore cacheStore,
        IGitHubGraphQlQueryService graphQlQueryService,
        IGitHubRequestQueue requestQueue,
        IApplicationTaskCoordinator taskCoordinator,
        HttpClient httpClient)
    {
        _queryService = queryService;
        _cacheStore = cacheStore;
        _graphQlQueryService = graphQlQueryService;
        _requestQueue = requestQueue;
        _taskCoordinator = taskCoordinator;
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
    }

    public async Task<DashboardSectionResult<GitHubUser>> GetIdentityAsync(
        string accessToken,
        string userId,
        string? login,
        bool forceAuthenticatedUser,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubUser preview = CreatePreviewSnapshot(login).User.Value;

            return new DashboardSectionResult<GitHubUser>(
                preview,
                CacheState.Fresh,
                now,
                now.AddMinutes(5));
        }

        return await GetSectionAsync(
            () => GetUserAsync(
                accessToken,
                GitHubAccountPartition.Require(userId),
                login,
                forceAuthenticatedUser,
                cancellationToken),
            new GitHubUser { Login = login ?? string.Empty });
    }

    public async Task<GitHubUserProfileSnapshot> GetProfileAsync(
        string accessToken,
        string userId,
        string? login,
        bool forceAuthenticatedUser,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return CreatePreviewSnapshot(login);
        }

        string normalizedUserId = GitHubAccountPartition.Require(userId);
        DashboardSectionResult<GitHubUser> user = await GetSectionAsync(
            () => GetUserAsync(accessToken, normalizedUserId, login, forceAuthenticatedUser, cancellationToken),
            new GitHubUser { Login = login ?? string.Empty });

        string targetLogin = string.IsNullOrWhiteSpace(user.Value.Login)
            ? login ?? string.Empty
            : user.Value.Login;
        if (string.IsNullOrWhiteSpace(targetLogin))
        {
            return new GitHubUserProfileSnapshot(
                user,
                DashboardSectionResult<GitHubProfileReadme>.Empty(GitHubProfileReadme.Missing(string.Empty)),
                DashboardSectionResult<GitHubContributionCalendar>.Empty(CreateEmptyCalendar()),
                DashboardSectionResult<GitHubPinnedProfileItem[]>.Empty([]),
                DashboardSectionResult<GitHubRepository[]>.Empty([]),
                DashboardSectionResult<GitHubRepository[]>.Empty([]),
                DashboardSectionResult<GitHubUser[]>.Empty([]),
                DashboardSectionResult<GitHubUser[]>.Empty([]),
                DashboardSectionResult<GitHubActivityEvent[]>.Empty([]),
                DashboardSectionResult<GitHubOrganization[]>.Empty([]),
                DashboardSectionResult<GitHubProfileViewerState>.Empty(new(false, false, false, string.Empty, string.Empty)),
                DashboardSectionResult<GitHubProfileHighlight[]>.Empty([]));
        }

        Task<DashboardSectionResult<GitHubProfileReadme>> readmeTask = GetSectionAsync(
            () => GetReadmeAsync(accessToken, normalizedUserId, targetLogin, cancellationToken),
            GitHubProfileReadme.Missing(targetLogin));
        Task<DashboardSectionResult<GitHubOrganization[]>> organizationsTask = GetOrganizationsSectionAsync(
            accessToken,
            normalizedUserId,
            targetLogin,
            cancellationToken);
        Task<DashboardSectionResult<GitHubProfileGraphQlData>> graphQlTask = GetSectionAsync(
            () => GetGraphQlDataAsync(accessToken, normalizedUserId, targetLogin, forceAuthenticatedUser, cancellationToken),
            new GitHubProfileGraphQlData());

        await Task.WhenAll(readmeTask, organizationsTask, graphQlTask);
        DashboardSectionResult<GitHubProfileGraphQlData> graphQl = graphQlTask.Result;
        GitHubProfileGraphQlUser? graphQlUser = forceAuthenticatedUser
            ? graphQl.Value.Viewer ?? graphQl.Value.User
            : graphQl.Value.User ?? graphQl.Value.Viewer;

        DashboardSectionResult<GitHubContributionCalendar> contributions = ProjectSection(graphQl, MapContributionCalendar(graphQlUser));
        DashboardSectionResult<GitHubPinnedProfileItem[]> pinned = ProjectSection(graphQl, MapPinnedItems(graphQlUser));
        DashboardSectionResult<GitHubProfileViewerState> viewerState = ProjectSection(graphQl, MapViewerState(graphQlUser));
        DashboardSectionResult<GitHubProfileHighlight[]> highlights = ProjectSection(graphQl, MapHighlights(user.Value, graphQlUser));

        return new GitHubUserProfileSnapshot(
            user,
            readmeTask.Result,
            contributions,
            pinned,
            DashboardSectionResult<GitHubRepository[]>.Empty([]),
            DashboardSectionResult<GitHubRepository[]>.Empty([]),
            DashboardSectionResult<GitHubUser[]>.Empty([]),
            DashboardSectionResult<GitHubUser[]>.Empty([]),
            DashboardSectionResult<GitHubActivityEvent[]>.Empty([]),
            organizationsTask.Result,
            viewerState,
            highlights);
    }

    public async Task<GitHubUser> UpdateAuthenticatedProfileAsync(
        string accessToken,
        string userId,
        GitHubUserProfileUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return CreatePreviewUser();
        }

        string partition = GitHubAccountPartition.Require(userId);
        return await RunTrackedMutationAsync(
            partition,
            "profile.mutation.update",
            async ownedToken => await _requestQueue.EnqueueForAccountAsync(
                partition,
                $"{partition}:profile:update",
                GitHubRequestPriority.Mutation,
                async mutationToken =>
                {
                    using HttpRequestMessage message = new(HttpMethod.Patch, "user");
                    AddGitHubHeaders(message, accessToken);
                    message.Content = JsonContent.Create(
                        request,
                        GitHubJsonSerializerContext.Default.GitHubUserProfileUpdateRequest);

                    using HttpResponseMessage response = await _httpClient.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        mutationToken);
                    await EnsureSuccessAsync(response, mutationToken);
                    GitHubUser? user = await response.Content.ReadFromJsonAsync(
                        GitHubJsonSerializerContext.Default.GitHubUser,
                        mutationToken);
                    if (user is null)
                    {
                        throw new GitHubApiException(
                            HttpStatusCode.OK,
                            "GitHub returned an empty user profile.");
                    }

                    await _cacheStore.InvalidateTagsAsync(
                        partition,
                        ["profile", "profile-user"],
                        mutationToken);
                    return user;
                },
                ownedToken),
            cancellationToken);
    }

    public async Task<DashboardSectionResult<GitHubRepository[]>> GetRepositoriesAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default)
    {
        return await GetRepositoriesPageAsync(accessToken, userId, login, 1, cancellationToken);
    }

    public async Task<DashboardSectionResult<GitHubRepository[]>> GetRepositoriesPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default)
    {
        page = RequirePage(page);
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubRepository[] items = page == 1 ? CreatePreviewRepositories(login) : [];
            return new DashboardSectionResult<GitHubRepository[]>(items, CacheState.Fresh, now, now.AddMinutes(5));
        }

        return await GetSectionAsync(
            () => LoadRepositoriesAsync(accessToken, GitHubAccountPartition.Require(userId), login, page, cancellationToken),
            Array.Empty<GitHubRepository>());
    }

    public async Task<DashboardSectionResult<GitHubRepository[]>> GetStarredRepositoriesAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default)
    {
        return await GetStarredRepositoriesPageAsync(accessToken, userId, login, 1, cancellationToken);
    }

    public async Task<DashboardSectionResult<GitHubRepository[]>> GetStarredRepositoriesPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default)
    {
        page = RequirePage(page);
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubRepository[] items = page == 1 ? CreatePreviewRepositories(login).Take(4).ToArray() : [];
            return new DashboardSectionResult<GitHubRepository[]>(items, CacheState.Fresh, now, now.AddMinutes(5));
        }

        return await GetSectionAsync(
            () => LoadStarredAsync(accessToken, GitHubAccountPartition.Require(userId), login, page, cancellationToken),
            Array.Empty<GitHubRepository>());
    }

    public async Task<DashboardSectionResult<GitHubUser[]>> GetFollowersAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default)
    {
        return await GetFollowersPageAsync(accessToken, userId, login, 1, cancellationToken);
    }

    public Task<DashboardSectionResult<GitHubUser[]>> GetFollowersPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default) =>
        GetPeoplePageAsync(accessToken, userId, login, RequirePage(page), "followers", "profile-followers", cancellationToken);

    public async Task<DashboardSectionResult<GitHubUser[]>> GetFollowingAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default)
    {
        return await GetFollowingPageAsync(accessToken, userId, login, 1, cancellationToken);
    }

    public Task<DashboardSectionResult<GitHubUser[]>> GetFollowingPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default) =>
        GetPeoplePageAsync(accessToken, userId, login, RequirePage(page), "following", "profile-following", cancellationToken);

    public async Task<DashboardSectionResult<GitHubActivityEvent[]>> GetPublicActivityAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default)
    {
        return await GetPublicActivityPageAsync(accessToken, userId, login, 1, cancellationToken);
    }

    public async Task<DashboardSectionResult<GitHubActivityEvent[]>> GetPublicActivityPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default)
    {
        page = RequirePage(page);
        int maximumPageCount = MaximumActivityCount / ActivityPageSize;
        if (page > maximumPageCount)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new DashboardSectionResult<GitHubActivityEvent[]>(
                [],
                CacheState.Fresh,
                now,
                now.AddMinutes(5),
                Completeness: PagedDataCompleteness.ApiLimited,
                LoadedItemCount: MaximumActivityCount,
                LoadedPageCount: maximumPageCount);
        }

        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubActivityEvent[] items = page == 1 ? CreatePreviewActivity(login) : [];
            return ApplyPublicActivityScope(
                new DashboardSectionResult<GitHubActivityEvent[]>(items, CacheState.Fresh, now, now.AddMinutes(5)),
                page);
        }

        string escapedLogin = Uri.EscapeDataString(login);
        DashboardSectionResult<GitHubActivityEvent[]> result = await GetSectionAsync(
            () => _queryService.GetAsync(
                CreateQuery(
                    accessToken,
                    GitHubAccountPartition.Require(userId),
                    $"users/{escapedLogin}/events/public?per_page={ActivityPageSize}&page={page}",
                    GitHubCachePolicy.MutableResource,
                    GitHubJsonSerializerContext.Default.GitHubActivityEventArray,
                    ["profile", "profile-activity", $"profile-activity-page-{page}"]),
                QueryFetchPolicy.StaleFirst,
                cancellationToken),
            Array.Empty<GitHubActivityEvent>());
        return ApplyPublicActivityScope(result, page);
    }

    private static DashboardSectionResult<GitHubActivityEvent[]> ApplyPublicActivityScope(
        DashboardSectionResult<GitHubActivityEvent[]> result,
        int page)
    {
        bool reachedApiLimit = !result.HasError
            && page == MaximumActivityCount / ActivityPageSize
            && result.Value.Length == ActivityPageSize;
        return result with
        {
            Completeness = result.HasError
                ? PagedDataCompleteness.Partial
                : reachedApiLimit
                    ? PagedDataCompleteness.ApiLimited
                    : result.Completeness,
            LoadedItemCount = reachedApiLimit ? MaximumActivityCount : result.Value.Length,
            LoadedPageCount = page
        };
    }

    public async Task FollowUserAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default)
    {
        await SendFollowMutationAsync(accessToken, userId, login, HttpMethod.Put, cancellationToken);
    }

    public async Task UnfollowUserAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default)
    {
        await SendFollowMutationAsync(accessToken, userId, login, HttpMethod.Delete, cancellationToken);
    }

    private async Task<CachedResult<GitHubUser>> GetUserAsync(
        string accessToken,
        string userId,
        string? login,
        bool forceAuthenticatedUser,
        CancellationToken cancellationToken)
    {
        string path = forceAuthenticatedUser || string.IsNullOrWhiteSpace(login)
            ? "user"
            : $"users/{Uri.EscapeDataString(login)}";
        return await _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                path,
                GitHubCachePolicy.RepositoryMetadataResource,
                GitHubJsonSerializerContext.Default.GitHubUser,
                ["profile", "profile-user"]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    private async Task<CachedResult<GitHubRepository[]>> LoadRepositoriesAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken) =>
        await _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"users/{Uri.EscapeDataString(login)}/repos?sort=updated&direction=desc&per_page={RepositoryPageSize}&page={page}",
                GitHubCachePolicy.RepositoryResource,
                GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
                ["profile", "profile-repositories", "repo", $"profile-repositories-page-{page}"]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);

    private async Task<CachedResult<GitHubRepository[]>> LoadStarredAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken) =>
        await _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"users/{Uri.EscapeDataString(login)}/starred?sort=updated&direction=desc&per_page={StarredPageSize}&page={page}",
                GitHubCachePolicy.RepositoryResource,
                GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
                ["profile", "profile-stars", "repo", $"profile-stars-page-{page}"]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);

    private async Task<DashboardSectionResult<GitHubUser[]>> GetPeoplePageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        string endpoint,
        string tag,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubUser[] items = page == 1 ? CreatePreviewPeople(login, endpoint).ToArray() : [];
            return new DashboardSectionResult<GitHubUser[]>(items, CacheState.Fresh, now, now.AddMinutes(5));
        }

        string escapedLogin = Uri.EscapeDataString(login);
        return await GetSectionAsync(
            () => _queryService.GetAsync(
                CreateQuery(
                    accessToken,
                    GitHubAccountPartition.Require(userId),
                    $"users/{escapedLogin}/{endpoint}?per_page={PeoplePageSize}&page={page}",
                    GitHubCachePolicy.LookupResource,
                    GitHubJsonSerializerContext.Default.GitHubUserArray,
                    ["profile", tag, $"{tag}-page-{page}"]),
                QueryFetchPolicy.StaleFirst,
                cancellationToken),
            Array.Empty<GitHubUser>());
    }

    private static int RequirePage(int page)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Profile pages are one-based.");
        }

        return page;
    }

    private async Task SendFollowMutationAsync(
        string accessToken,
        string userId,
        string login,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return;
        }

        string partition = GitHubAccountPartition.Require(userId);
        string escapedLogin = Uri.EscapeDataString(login);
        string operation = method == HttpMethod.Put ? "follow" : "unfollow";
        _ = await RunTrackedMutationAsync(
            partition,
            $"profile.mutation.{operation}",
            async ownedToken => await _requestQueue.EnqueueForAccountAsync(
                partition,
                $"{partition}:profile:{operation}:{escapedLogin}",
                GitHubRequestPriority.Mutation,
                async mutationToken =>
                {
                    using HttpRequestMessage message = new(method, $"user/following/{escapedLogin}");
                    AddGitHubHeaders(message, accessToken);
                    using HttpResponseMessage response = await _httpClient.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        mutationToken);
                    await EnsureSuccessAsync(response, mutationToken);
                    await _cacheStore.InvalidateTagsAsync(
                        partition,
                        ["profile", "profile-user", "profile-graphql", "profile-followers", "profile-following"],
                        mutationToken);
                    return true;
                },
                ownedToken),
            cancellationToken);
    }

    private async Task<DashboardSectionResult<GitHubOrganization[]>> GetOrganizationsSectionAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken)
    {
        SortedDictionary<int, GitHubOrganization[]> pages = [];
        GitHubOrganization[] items = [];
        CachedResult<GitHubOrganization[]>? lastResult = null;
        int loadedPages = 0;
        PagedDataCompleteness completeness = PagedDataCompleteness.Partial;
        int maximumPages = MaximumOrganizationCount / OrganizationPageSize;

        for (int page = 1; page <= maximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int countBeforePage = items.Length;
            GitHubQuery<GitHubOrganization[]> query = CreateQuery(
                accessToken,
                userId,
                $"users/{Uri.EscapeDataString(login)}/orgs?per_page={OrganizationPageSize}&page={page}",
                GitHubCachePolicy.LookupResource,
                GitHubJsonSerializerContext.Default.GitHubOrganizationArray,
                ["profile", "profile-organizations"],
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);

            CachedResult<GitHubOrganization[]> result;
            try
            {
                result = await _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
                pages[page] = result.Value ?? [];
                items = FlattenOrganizations(pages);
                lastResult = result;
                loadedPages = page;

                if (GitHubPagedReconciler.RequiresAuthoritativeRefresh(result))
                {
                    try
                    {
                        result = await _queryService.RefreshAsync(query, cancellationToken);
                        pages[page] = result.Value ?? [];
                        items = FlattenOrganizations(pages);
                        lastResult = result;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return CreateOrganizationSection(
                            items,
                            lastResult,
                            PagedDataCompleteness.Partial,
                            loadedPages,
                            JitHub.WinUI.Helpers.UserFacingError.For(
                                ex,
                                JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                                "profile-organizations"));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return CreateOrganizationSection(
                    items,
                    lastResult,
                    PagedDataCompleteness.Partial,
                    loadedPages,
                    JitHub.WinUI.Helpers.UserFacingError.For(
                        ex,
                        JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                        "profile-organizations"));
            }

            GitHubOrganization[] pageItems = result.Value ?? [];
            if (pageItems.Length < OrganizationPageSize)
            {
                completeness = PagedDataCompleteness.Complete;
                break;
            }

            if (items.Length == countBeforePage)
            {
                completeness = PagedDataCompleteness.Partial;
                break;
            }

            if (items.Length >= MaximumOrganizationCount)
            {
                completeness = PagedDataCompleteness.ApiLimited;
                break;
            }
        }

        return CreateOrganizationSection(items, lastResult, completeness, loadedPages, errorMessage: null);
    }

    private static DashboardSectionResult<GitHubOrganization[]> CreateOrganizationSection(
        GitHubOrganization[] items,
        CachedResult<GitHubOrganization[]>? result,
        PagedDataCompleteness completeness,
        int loadedPages,
        string? errorMessage) =>
        new(
            items,
            result?.CacheState ?? (errorMessage is null ? CacheState.Miss : CacheState.Error),
            result?.FetchedAt,
            result?.StaleAfter,
            result?.IsRefreshInProgress ?? false,
            errorMessage ?? (result?.RefreshError is Exception refreshError
                ? JitHub.WinUI.Helpers.UserFacingError.For(
                    refreshError,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "profile-organizations")
                : null),
            RequiresReconnect: false,
            completeness,
            items.Length,
            loadedPages);

    private static GitHubOrganization[] FlattenOrganizations(
        IEnumerable<KeyValuePair<int, GitHubOrganization[]>> pages) =>
        pages
            .SelectMany(static page => page.Value)
            .DistinctBy(
                static organization => organization.Id > 0
                    ? organization.Id.ToString(CultureInfo.InvariantCulture)
                    : organization.Login,
                StringComparer.OrdinalIgnoreCase)
            .Take(MaximumOrganizationCount)
            .ToArray();

    private async Task<CachedResult<GitHubProfileReadme>> GetReadmeAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken)
    {
        try
        {
            CachedResult<GitHubRepositoryContent> content = await _queryService.GetAsync(
                CreateQuery(
                    accessToken,
                    userId,
                    $"repos/{Uri.EscapeDataString(login)}/{Uri.EscapeDataString(login)}/readme",
                    GitHubCachePolicy.RepositoryMetadataResource,
                    GitHubJsonSerializerContext.Default.GitHubRepositoryContent,
                    ["profile", "profile-readme"]),
                QueryFetchPolicy.StaleFirst,
                cancellationToken);
            GitHubProfileReadme readme = DecodeReadme(login, content.Value);
            return new CachedResult<GitHubProfileReadme>(
                readme,
                content.CacheState,
                content.FetchedAt,
                content.StaleAfter,
                content.IsRefreshInProgress,
                content.RefreshError,
                content.ETag,
                content.LastModified);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new CachedResult<GitHubProfileReadme>(
                GitHubProfileReadme.Missing(login),
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1));
        }
    }

    private async Task<CachedResult<GitHubProfileGraphQlData>> GetGraphQlDataAsync(
        string accessToken,
        string userId,
        string login,
        bool forceAuthenticatedUser,
        CancellationToken cancellationToken)
    {
        GitHubQuery<GitHubProfileGraphQlData> query = CreateQuery(
            accessToken,
            userId,
            forceAuthenticatedUser
                ? "graphql/profile?target=viewer"
                : $"graphql/profile?login={Uri.EscapeDataString(login)}",
            GitHubCachePolicy.RepositoryMetadataResource,
            GitHubJsonSerializerContext.Default.GitHubProfileGraphQlData,
            ["profile", "profile-graphql"]);

        return await _graphQlQueryService.GetAsync(
            new GitHubGraphQlQuery<GitHubProfileGraphQlData>(
                query,
                new GitHubGraphQlRequest
                {
                    Query = forceAuthenticatedUser ? ViewerProfileGraphQlQuery : UserProfileGraphQlQuery,
                    Variables = forceAuthenticatedUser
                        ? null
                        : new Dictionary<string, string?> { ["login"] = login }
                },
                GitHubJsonSerializerContext.Default.GitHubProfileGraphQlResponse),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    private static async Task<DashboardSectionResult<T>> GetSectionAsync<T>(
        Func<Task<CachedResult<T>>> loadAsync,
        T fallback)
        where T : class
    {
        try
        {
            CachedResult<T> result = await loadAsync();
            return new DashboardSectionResult<T>(
                result.Value ?? fallback,
                result.CacheState,
                result.FetchedAt,
                result.StaleAfter,
                result.IsRefreshInProgress,
                result.RefreshError is null
                    ? null
                    : JitHub.WinUI.Helpers.UserFacingError.For(
                        result.RefreshError,
                        JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                        "profile-section"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DashboardSectionResult<T>(
                fallback,
                CacheState.Error,
                null,
                null,
                IsRefreshInProgress: false,
                ErrorMessage: JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Loading,
                    "profile-section"));
        }
    }

    private static DashboardSectionResult<TTarget> ProjectSection<TSource, TTarget>(
        DashboardSectionResult<TSource> source,
        TTarget value)
        where TSource : class
        where TTarget : class =>
        new(
            value,
            source.CacheState,
            source.FetchedAt,
            source.StaleAfter,
            source.IsRefreshInProgress,
            source.ErrorMessage,
            source.RequiresReconnect,
            source.Completeness,
            source.LoadedItemCount,
            source.LoadedPageCount);

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible)
        where T : class
    {
        string normalizedUserId = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            normalizedUserId,
            relativePath.StartsWith("graphql/", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(normalizedUserId, relativePath.StartsWith("graphql/", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            priority);
    }

    private static GitHubProfileReadme DecodeReadme(string login, GitHubRepositoryContent? content)
    {
        if (content is null || string.IsNullOrWhiteSpace(content.Content))
        {
            return GitHubProfileReadme.Missing(login);
        }

        string markdown = content.Content;
        if (string.Equals(content.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                markdown = Encoding.UTF8.GetString(Convert.FromBase64String(content.Content.Replace("\n", string.Empty, StringComparison.Ordinal)));
            }
            catch (FormatException)
            {
                markdown = string.Empty;
            }
        }

        return string.IsNullOrWhiteSpace(markdown)
            ? GitHubProfileReadme.Missing(login)
            : new GitHubProfileReadme(
                markdown,
                content.HtmlUrl ?? string.Empty,
                $"{login}/{login}",
                Exists: true);
    }

    private static GitHubContributionCalendar MapContributionCalendar(GitHubProfileGraphQlUser? user)
    {
        GitHubProfileContributionCalendarPayload? calendar = user?.ContributionsCollection?.ContributionCalendar;
        if (calendar is null)
        {
            return CreateEmptyCalendar();
        }

        return new GitHubContributionCalendar(
            calendar.TotalContributions,
            calendar.Weeks
                .Select(static week => new GitHubContributionWeek(
                    week.ContributionDays.Select(static day => new GitHubContributionDay(
                        day.Date,
                        day.ContributionCount,
                        day.Color,
                        day.Weekday)).ToArray()))
                .ToArray());
    }

    private static GitHubContributionCalendar CreateEmptyCalendar() =>
        new(0, Array.Empty<GitHubContributionWeek>());

    private static GitHubPinnedProfileItem[] MapPinnedItems(GitHubProfileGraphQlUser? user) =>
        user?.PinnedItems?.Nodes?
            .Where(static node => node is not null)
            .Select(static node => new GitHubPinnedProfileItem(
                node.TypeName,
                node.Name ?? node.NameWithOwner ?? "Pinned item",
                node.NameWithOwner ?? node.Name ?? string.Empty,
                node.Description ?? string.Empty,
                node.Url ?? string.Empty,
                node.PrimaryLanguage?.Name ?? string.Empty,
                node.PrimaryLanguage?.Color ?? string.Empty,
                node.StargazerCount,
                node.ForkCount,
                node.UpdatedAt,
                node.IsPrivate,
                node.IsFork))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray()
        ?? [];

    private static GitHubProfileViewerState MapViewerState(GitHubProfileGraphQlUser? user) =>
        user is null
            ? new(false, false, false, string.Empty, string.Empty)
            : new(
                user.IsViewer,
                user.ViewerCanFollow,
                user.ViewerIsFollowing,
                user.Status?.Message ?? string.Empty,
                user.Status?.Emoji ?? string.Empty);

    private static GitHubProfileHighlight[] MapHighlights(GitHubUser user, GitHubProfileGraphQlUser? graphQlUser)
    {
        List<GitHubProfileHighlight> highlights = [];
        if (graphQlUser?.IsGitHubStar == true)
        {
            highlights.Add(new("github-star", "GitHub Star", "\uE735", "accent"));
        }

        if (graphQlUser?.IsDeveloperProgramMember == true)
        {
            highlights.Add(new("developer-program", "Developer Program", "\uE943", "accent"));
        }

        if (graphQlUser?.IsEmployee == true)
        {
            highlights.Add(new("github-employee", "GitHub Staff", "\uE77B", "accent"));
        }

        if (graphQlUser?.IsCampusExpert == true)
        {
            highlights.Add(new("campus-expert", "Campus Expert", "\uE7BE", "accent"));
        }

        if (graphQlUser?.IsBountyHunter == true)
        {
            highlights.Add(new("bounty-hunter", "Security contributor", "\uE72E", "accent"));
        }

        if (graphQlUser?.IsHireable == true || user.Hireable == true)
        {
            highlights.Add(new("hireable", "Available for hire", "\uE8F2", "success"));
        }

        if (graphQlUser?.IsSiteAdmin == true || user.SiteAdmin)
        {
            highlights.Add(new("site-admin", "Site admin", "\uE713", "warning"));
        }

        if (!string.IsNullOrWhiteSpace(user.Type))
        {
            highlights.Add(new("account-type", user.Type!, "\uE77B", "muted"));
        }

        return highlights
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(8)
            .ToArray();
    }

    private static GitHubUserProfileSnapshot CreatePreviewSnapshot(string? login)
    {
        GitHubUser user = CreatePreviewUser(login);
        GitHubProfileGraphQlUser graphQlUser = CreatePreviewGraphQlUser(user.Login);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new GitHubUserProfileSnapshot(
            new DashboardSectionResult<GitHubUser>(user, CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubProfileReadme>(
                new GitHubProfileReadme(
                    "## Building JitHub\n\nNative GitHub workflows for Windows developers. I care about fast tools, clean UI, and tiny details that make daily work feel calm.",
                    string.Empty,
                    $"{user.Login}/{user.Login}",
                    true),
                CacheState.Fresh,
                now,
                now.AddMinutes(5)),
            new DashboardSectionResult<GitHubContributionCalendar>(
                ProductPerformanceLargeAccountFixture.IsEnabled
                    ? ProductPerformanceLargeAccountFixture.CreateContributionCalendar()
                    : MapContributionCalendar(graphQlUser),
                CacheState.Fresh,
                now,
                now.AddMinutes(5)),
            new DashboardSectionResult<GitHubPinnedProfileItem[]>(MapPinnedItems(graphQlUser), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubRepository[]>(CreatePreviewRepositories(user.Login), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubRepository[]>(CreatePreviewRepositories(user.Login).Take(4).ToArray(), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubUser[]>(CreatePreviewPeople(user.Login, "follower").ToArray(), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubUser[]>(CreatePreviewPeople(user.Login, "following").ToArray(), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubActivityEvent[]>(CreatePreviewActivity(user.Login), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubOrganization[]>(CreatePreviewOrganizations(), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubProfileViewerState>(MapViewerState(graphQlUser), CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubProfileHighlight[]>(MapHighlights(user, graphQlUser), CacheState.Fresh, now, now.AddMinutes(5)));
    }

    private static GitHubUser CreatePreviewUser(string? login = null) => new()
    {
        Id = 170190931,
        Login = string.IsNullOrWhiteSpace(login) ? "renanyoy" : login,
        Name = "Renan Yoy",
        AvatarUrl = "https://avatars.githubusercontent.com/u/170190931",
        Bio = "Building careful native developer tools.",
        Company = "@JitHubApp",
        Location = "Seattle, WA",
        Blog = "https://github.com/JitHubApp",
        HtmlUrl = "https://github.com/JitHubApp",
        PublicRepos = ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.RepositoryCount)
            : 111,
        Followers = ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.PeopleCount)
            : 39,
        Following = ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.PeopleCount)
            : 18,
        PublicGists = 4,
        Hireable = true,
        Type = "User",
        CreatedAt = DateTimeOffset.UtcNow.AddYears(-5),
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static GitHubProfileGraphQlUser CreatePreviewGraphQlUser(string login)
    {
        const int previewWeekCount = 53;
        DateTimeOffset start = DateTimeOffset.UtcNow.Date.AddDays(-((previewWeekCount * 7) - 1));
        List<GitHubProfileContributionWeekPayload> weeks = [];
        int totalContributions = 0;
        for (int week = 0; week < previewWeekCount; week++)
        {
            List<GitHubProfileContributionDayPayload> days = [];
            for (int day = 0; day < 7; day++)
            {
                int count = (week * 3 + day * 5) % 18;
                totalContributions += count;
                days.Add(new GitHubProfileContributionDayPayload
                {
                    Date = start.AddDays((week * 7) + day),
                    ContributionCount = count,
                    Color = count switch
                    {
                        0 => "#1f2a22",
                        < 4 => "#24563b",
                        < 9 => "#2f8154",
                        < 14 => "#56b87d",
                        _ => "#8ee6a8"
                    },
                    Weekday = day
                });
            }

            weeks.Add(new GitHubProfileContributionWeekPayload { ContributionDays = [.. days] });
        }

        return new GitHubProfileGraphQlUser
        {
            Login = login,
            IsViewer = true,
            ViewerCanFollow = false,
            ViewerIsFollowing = false,
            IsDeveloperProgramMember = true,
            IsGitHubStar = false,
            IsHireable = true,
            Status = new GitHubProfileStatus { Message = "Designing native GitHub workflows", Emoji = "💻" },
            ContributionsCollection = new GitHubProfileContributionsCollection
            {
                ContributionCalendar = new GitHubProfileContributionCalendarPayload
                {
                    TotalContributions = totalContributions,
                    Weeks = [.. weeks]
                }
            },
            PinnedItems = new GitHubProfilePinnedItemsConnection
            {
                Nodes =
                [
                    new GitHubProfilePinnedItemNode
                    {
                        TypeName = "Repository",
                        Name = "JitHubV2",
                        NameWithOwner = "JitHubApp/JitHubV2",
                        Description = "Native Windows GitHub client built with WinUI.",
                        Url = "https://github.com/JitHubApp/JitHubV2",
                        StargazerCount = 146,
                        ForkCount = 15,
                        UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                        PrimaryLanguage = new GitHubProfileLanguage { Name = "C#", Color = "#178600" }
                    },
                    new GitHubProfilePinnedItemNode
                    {
                        TypeName = "Repository",
                        Name = "open-ui",
                        NameWithOwner = "JitHubApp/open-ui",
                        Description = "Small WinUI primitives for dense desktop workflows.",
                        Url = "https://github.com/JitHubApp/open-ui",
                        StargazerCount = 42,
                        ForkCount = 8,
                        UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                        PrimaryLanguage = new GitHubProfileLanguage { Name = "XAML", Color = "#0060ac" }
                    }
                ]
            }
        };
    }

    private static GitHubRepository[] CreatePreviewRepositories(string login) =>
        ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.CreateRepositories(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.RepositoryCount),
                login)
            :
            [
                CreatePreviewRepository(login, "JitHubV2", "Native Windows GitHub client.", "C#", 146, 15),
                CreatePreviewRepository(login, "open-ui", "Composable WinUI primitives.", "XAML", 42, 8),
                CreatePreviewRepository(login, "agentic-company", "AI-powered company management platform.", "TypeScript", 31, 6),
                CreatePreviewRepository(login, "JustCode", "AI assistant for developers.", "Python", 25, 4)
            ];

    private static GitHubRepository CreatePreviewRepository(
        string owner,
        string name,
        string description,
        string language,
        int stars,
        int forks) => new()
    {
        Id = Math.Abs(HashCode.Combine(owner, name)),
        Name = name,
        FullName = $"{owner}/{name}",
        Description = description,
        HtmlUrl = $"https://github.com/{owner}/{name}",
        Language = language,
        StargazersCount = stars,
        ForksCount = forks,
        UpdatedAt = DateTimeOffset.UtcNow.AddHours(-stars % 48),
        Owner = new GitHubRepositoryOwner
        {
            Login = owner,
            AvatarUrl = "https://avatars.githubusercontent.com/u/170190931"
        }
    };

    private static GitHubOrganization[] CreatePreviewOrganizations() =>
    [
        new()
        {
            Id = 1,
            Login = "JitHubApp",
            AvatarUrl = "https://avatars.githubusercontent.com/u/583231",
            Description = "Native developer tools."
        }
    ];

    private static IEnumerable<GitHubUser> CreatePreviewPeople(string login, string prefix)
    {
        if (ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled)
        {
            return ProductPerformanceLargeAccountFixture.CreatePeople(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.PeopleCount));
        }

        return CreateDefaultPreviewPeople(login, prefix);
    }

    private static IEnumerable<GitHubUser> CreateDefaultPreviewPeople(string login, string prefix)
    {
        for (int index = 1; index <= 6; index++)
        {
            yield return new GitHubUser
            {
                Id = Math.Abs(HashCode.Combine(login, prefix, index)),
                Login = $"{prefix}-{index}",
                Name = $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(prefix)} {index}",
                AvatarUrl = "https://avatars.githubusercontent.com/u/583231",
                Bio = index % 2 == 0 ? "Builds native developer workflows." : "Open source maintainer.",
                HtmlUrl = $"https://github.com/{prefix}-{index}",
                Type = "User"
            };
        }
    }

    private static GitHubActivityEvent[] CreatePreviewActivity(string login)
    {
        if (ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled)
        {
            return ProductPerformanceLargeAccountFixture.CreateActivity(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.ActivityCount));
        }

        GitHubActivityRepository repo = new()
        {
            Id = 1,
            Name = $"{login}/JitHubV2",
            Url = $"https://api.github.com/repos/{login}/JitHubV2"
        };

        return
        [
            CreatePreviewActivityEvent("profile-activity-1", "PushEvent", repo, DateTimeOffset.UtcNow.AddMinutes(-22)),
            CreatePreviewActivityEvent("profile-activity-2", "WatchEvent", repo, DateTimeOffset.UtcNow.AddHours(-3)),
            CreatePreviewActivityEvent("profile-activity-3", "IssuesEvent", repo, DateTimeOffset.UtcNow.AddHours(-7))
        ];
    }

    private static GitHubActivityEvent CreatePreviewActivityEvent(
        string id,
        string type,
        GitHubActivityRepository repo,
        DateTimeOffset createdAt) => new()
    {
        Id = id,
        Type = type,
        Public = true,
        CreatedAt = createdAt,
        Actor = new GitHubActor
        {
            Login = repo.Name.Split('/')[0],
            AvatarUrl = "https://avatars.githubusercontent.com/u/170190931"
        },
        Repo = repo
    };

    private async Task<T> RunTrackedMutationAsync<T>(
        string accountPartition,
        string taskName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task trackedTask = _taskCoordinator.RunAsync(
            async ownedToken =>
            {
                try
                {
                    result.TrySetResult(await operation(ownedToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (ownedToken.IsCancellationRequested)
                {
                    result.TrySetCanceled(ownedToken);
                    throw;
                }
                catch (Exception exception)
                {
                    result.TrySetException(exception);
                    throw;
                }
            },
            new ApplicationTaskOptions(taskName, accountPartition),
            cancellationToken);

        try
        {
            await trackedTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.TrySetCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }

        return await result.Task.ConfigureAwait(false);
    }

    private static void AddGitHubHeaders(HttpRequestMessage message, string accessToken)
    {
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("JitHub", "1.0"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        message.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string messageText = await ReadErrorMessageAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubAuthenticationException(messageText);
        }

        throw new GitHubApiException(response.StatusCode, messageText);
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            GitHubApiError? error = await response.Content.ReadFromJsonAsync(
                GitHubJsonSerializerContext.Default.GitHubApiError,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                return JitHub.WinUI.Helpers.UserFacingError.ForInternalMessage(
                    error.Message,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                    "profile-api");
            }
        }
        catch
        {
        }

        return $"GitHub request failed with status code {(int)response.StatusCode}.";
    }

    private static HttpClient CreateDefaultHttpClient() => new()
    {
        BaseAddress = new Uri("https://api.github.com/")
    };

    private const string UserProfileGraphQlQuery = """
query JitHubUserProfile($login: String!) {
  user(login: $login) {
    ...JitHubProfileUser
  }
}

fragment JitHubProfileUser on User {
  login
  isViewer
  viewerCanFollow
  viewerIsFollowing
  isDeveloperProgramMember
  isEmployee
  isGitHubStar
  isHireable
  isBountyHunter
  isCampusExpert
  isSiteAdmin
  status {
    message
    emoji
  }
  contributionsCollection {
    contributionCalendar {
      totalContributions
      weeks {
        contributionDays {
          date
          contributionCount
          color
          weekday
        }
      }
    }
  }
  pinnedItems(first: 6, types: [REPOSITORY, GIST]) {
    nodes {
      __typename
      ... on Repository {
        name
        nameWithOwner
        description
        url
        stargazerCount
        forkCount
        updatedAt
        isPrivate
        isFork
        primaryLanguage {
          name
          color
        }
      }
      ... on Gist {
        name
        description
        url
        updatedAt
      }
    }
  }
}
""";

    private const string ViewerProfileGraphQlQuery = """
query JitHubViewerProfile {
  viewer {
    ...JitHubProfileUser
  }
}

fragment JitHubProfileUser on User {
  login
  isViewer
  viewerCanFollow
  viewerIsFollowing
  isDeveloperProgramMember
  isEmployee
  isGitHubStar
  isHireable
  isBountyHunter
  isCampusExpert
  isSiteAdmin
  status {
    message
    emoji
  }
  contributionsCollection {
    contributionCalendar {
      totalContributions
      weeks {
        contributionDays {
          date
          contributionCount
          color
          weekday
        }
      }
    }
  }
  pinnedItems(first: 6, types: [REPOSITORY, GIST]) {
    nodes {
      __typename
      ... on Repository {
        name
        nameWithOwner
        description
        url
        stargazerCount
        forkCount
        updatedAt
        isPrivate
        isFork
        primaryLanguage {
          name
          color
        }
      }
      ... on Gist {
        name
        description
        url
        updatedAt
      }
    }
  }
}
""";
}
