using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public interface IGitHubPilotQueryService
{
    Task<CachedResult<GitHubRepository[]>> SearchRepositoriesAsync(
        string accessToken,
        string userId,
        string query,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubRepository[]>> GetRecentRepositoriesAsync(
        string accessToken,
        string userId,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubPilotQueryService : IGitHubPilotQueryService
{
    private readonly IGitHubQueryService _queryService;

    public GitHubPilotQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public async Task<CachedResult<GitHubRepository[]>> SearchRepositoriesAsync(
        string accessToken,
        string userId,
        string query,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        GitHubRepository? exactMatch = null;
        if (TryParseRepositoryFullName(query, out string owner, out string name))
        {
            try
            {
                GitHubQuery<GitHubRepository> exactQuery = CreateQuery(
                    accessToken,
                    userId,
                    $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}",
                    GitHubCachePolicy.RepositoryResource,
                    Phase0GitHubJsonSerializerContext.Default.GitHubRepository,
                    (string[])["repo", "repo-exact"],
                    GitHubRequestPriority.Visible);
                CachedResult<GitHubRepository> exactResult = await _queryService.GetAsync(
                    exactQuery,
                    QueryFetchPolicy.StaleFirst,
                    cancellationToken);
                exactMatch = exactResult.Value;
            }
            catch (GitHubApiException)
            {
            }
            catch (HttpRequestException)
            {
            }
        }

        string path =
            $"search/repositories?q={Uri.EscapeDataString(query)}&per_page={pageSize}&page={pageNumber}";
        GitHubQuery<GitHubRepositorySearchResponse> searchQuery = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.SearchResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositorySearchResponse,
            (string[])["repo-search"],
            GitHubRequestPriority.UserInitiated);
        CachedResult<GitHubRepositorySearchResponse> searchResult = await _queryService.GetAsync(
            searchQuery,
            QueryFetchPolicy.StaleFirst,
            cancellationToken);

        GitHubRepository[] items = searchResult.Value?.Items ?? [];
        if (exactMatch is not null)
        {
            items = items
                .Prepend(exactMatch)
                .GroupBy(static repository => repository.Id)
                .Select(static group => group.First())
                .Take(pageSize)
                .ToArray();
        }

        return new CachedResult<GitHubRepository[]>(
            items,
            searchResult.CacheState,
            searchResult.FetchedAt,
            searchResult.StaleAfter,
            searchResult.IsRefreshInProgress,
            searchResult.RefreshError,
            searchResult.ETag,
            searchResult.LastModified);
    }

    public async Task<CachedResult<GitHubRepository[]>> GetRecentRepositoriesAsync(
        string accessToken,
        string userId,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path = GitHubAuthenticationConstants.IsPublicAccessToken(accessToken)
            ? $"users/JitHubApp/repos?sort=updated&direction=desc&per_page={pageSize}&page={pageNumber}"
            : $"user/repos?sort=updated&direction=desc&per_page={pageSize}&page={pageNumber}";
        GitHubQuery<GitHubRepository[]> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.RepositoryResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            (string[])["user-repos", "repo"],
            GitHubRequestPriority.Visible);

        return await _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        IReadOnlyList<string> tags,
        GitHubRequestPriority priority)
        where T : class
    {
        string normalizedUserId = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            normalizedUserId,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(normalizedUserId, HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            priority);
    }

    private static bool TryParseRepositoryFullName(string query, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        string trimmed = query.Trim();
        if (trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = trimmed.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        owner = parts[0];
        name = parts[1];
        return owner.Length > 0 && name.Length > 0;
    }
}
