using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum RepositorySearchVisibility
{
    Any,
    Public,
    Private
}

public enum RepositorySearchForkScope
{
    Any,
    Sources,
    Forks
}

public enum RepositorySearchArchiveScope
{
    Any,
    Active,
    Archived
}

public enum RepositorySearchSort
{
    BestMatch,
    RecentlyUpdated,
    MostStars,
    MostForks
}

public sealed record RepositorySearchQuery(
    string Text,
    string Owner = "",
    string Language = "",
    string Topic = "",
    RepositorySearchVisibility Visibility = RepositorySearchVisibility.Any,
    RepositorySearchForkScope ForkScope = RepositorySearchForkScope.Any,
    RepositorySearchArchiveScope ArchiveScope = RepositorySearchArchiveScope.Any,
    RepositorySearchSort Sort = RepositorySearchSort.BestMatch);

public interface IGitHubRepositorySearchQueryService
{
    Task<CachedResult<GitHubRepositorySearchResponse>> SearchAsync(
        string accessToken,
        string userId,
        RepositorySearchQuery query,
        int page,
        int pageSize,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubRepositorySearchQueryService : IGitHubRepositorySearchQueryService
{
    private readonly IGitHubQueryService _queryService;

    public GitHubRepositorySearchQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public Task<CachedResult<GitHubRepositorySearchResponse>> SearchAsync(
        string accessToken,
        string userId,
        RepositorySearchQuery query,
        int page,
        int pageSize,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(query);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (ProductPerformanceLargeAccountFixture.IsEnabled ||
            GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            bool isLargeAccount = ProductPerformanceLargeAccountFixture.IsEnabled;
            IEnumerable<GitHubRepository> matches = ProductPerformanceLargeAccountFixture.CreateRepositories(
                ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
                    ? ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.RepositoryCount)
                    : 12,
                isLargeAccount ? "performance-owner" : "JitHubApp");
            if (isLargeAccount && !string.IsNullOrWhiteSpace(query.Text))
            {
                string text = query.Text.Trim();
                matches = matches.Where(repository =>
                    repository.FullName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    (repository.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (!string.IsNullOrWhiteSpace(query.Owner))
            {
                matches = matches.Where(repository => string.Equals(
                    repository.Owner?.Login,
                    query.Owner.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Language))
            {
                matches = matches.Where(repository => string.Equals(
                    repository.Language,
                    query.Language.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            }

            matches = query.Visibility switch
            {
                RepositorySearchVisibility.Public => matches.Where(static repository => !repository.Private),
                RepositorySearchVisibility.Private => matches.Where(static repository => repository.Private),
                _ => matches
            };
            matches = query.ForkScope switch
            {
                RepositorySearchForkScope.Sources => matches.Where(static repository => !repository.Fork),
                RepositorySearchForkScope.Forks => matches.Where(static repository => repository.Fork),
                _ => matches
            };
            matches = query.ArchiveScope switch
            {
                RepositorySearchArchiveScope.Active => matches.Where(static repository => !repository.Archived),
                RepositorySearchArchiveScope.Archived => matches.Where(static repository => repository.Archived),
                _ => matches
            };
            matches = query.Sort switch
            {
                RepositorySearchSort.RecentlyUpdated => matches.OrderByDescending(static repository => repository.UpdatedAt),
                RepositorySearchSort.MostStars => matches.OrderByDescending(static repository => repository.StargazersCount),
                RepositorySearchSort.MostForks => matches.OrderByDescending(static repository => repository.ForksCount),
                _ => matches
            };
            GitHubRepository[] allMatches = matches.ToArray();
            GitHubRepositorySearchResponse response = new()
            {
                TotalCount = allMatches.Length,
                IncompleteResults = false,
                Items = allMatches.Skip((page - 1) * pageSize).Take(pageSize).ToArray()
            };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<GitHubRepositorySearchResponse>(
                response,
                CacheState.Fresh,
                now,
                now.AddMinutes(15)));
        }

        string normalizedUserId = GitHubAccountPartition.Resolve(accessToken, userId);
        string path = BuildRelativePath(query, page, pageSize);
        GitHubQuery<GitHubRepositorySearchResponse> githubQuery = new(
            accessToken,
            normalizedUserId,
            HttpMethod.Get,
            path,
            GitHubQueryKeys.Create(normalizedUserId, HttpMethod.Get, path),
            GitHubCachePolicy.SearchResource,
            GitHubCachePolicy.TtlForResource(GitHubCachePolicy.SearchResource),
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositorySearchResponse,
            ["repo-search", "repository-search-workspace"],
            page == 1 ? GitHubRequestPriority.UserInitiated : GitHubRequestPriority.Prefetch);

        return forceRefresh
            ? _queryService.RefreshAsync(githubQuery, cancellationToken)
            : _queryService.GetAsync(githubQuery, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    internal static string BuildRelativePath(RepositorySearchQuery query, int page, int pageSize)
    {
        List<string> terms = [];
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            terms.Add(query.Text.Trim());
        }

        AddQualifier(terms, "user", query.Owner);
        AddQualifier(terms, "language", query.Language);
        AddQualifier(terms, "topic", query.Topic);
        if (query.Visibility != RepositorySearchVisibility.Any)
        {
            terms.Add(query.Visibility == RepositorySearchVisibility.Private ? "is:private" : "is:public");
        }

        terms.Add(query.ForkScope switch
        {
            RepositorySearchForkScope.Any => "fork:true",
            RepositorySearchForkScope.Forks => "fork:only",
            _ => string.Empty
        });
        terms.Add(query.ArchiveScope switch
        {
            RepositorySearchArchiveScope.Active => "archived:false",
            RepositorySearchArchiveScope.Archived => "archived:true",
            _ => string.Empty
        });

        string qualifiedQuery = string.Join(' ', terms.FindAll(static term => !string.IsNullOrWhiteSpace(term)));
        StringBuilder path = new("search/repositories?q=");
        path.Append(Uri.EscapeDataString(qualifiedQuery));
        path.Append("&per_page=").Append(Math.Clamp(pageSize, 1, 100));
        path.Append("&page=").Append(Math.Max(1, page));

        (string? sort, string order) = query.Sort switch
        {
            RepositorySearchSort.RecentlyUpdated => ("updated", "desc"),
            RepositorySearchSort.MostStars => ("stars", "desc"),
            RepositorySearchSort.MostForks => ("forks", "desc"),
            _ => (null, "desc")
        };
        if (sort is not null)
        {
            path.Append("&sort=").Append(sort).Append("&order=").Append(order);
        }

        return path.ToString();
    }

    private static void AddQualifier(List<string> terms, string qualifier, string value)
    {
        string normalized = value.Trim();
        if (normalized.Length > 0)
        {
            terms.Add($"{qualifier}:{normalized}");
        }
    }
}
