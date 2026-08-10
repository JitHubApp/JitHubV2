using System;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GitHubStarQueryService : IGitHubStarQueryService
{
    public const int PageSize = 100;
    public const string StarMediaType = "application/vnd.github.star+json";
    private readonly IGitHubQueryService _queryService;

    public GitHubStarQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public Task<CachedResult<GitHubStarredRepository[]>> GetPageAsync(
        string accessToken,
        string userId,
        int page,
        QueryFetchPolicy fetchPolicy,
        GitHubRequestPriority priority,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            GitHubStarredRepository[] preview = ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
                ? ProductPerformanceLargeAccountFixture.CreateStars(
                        ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.StarCount))
                    .Skip((Math.Max(1, page) - 1) * PageSize)
                    .Take(PageSize)
                    .ToArray()
                : page == 1 ? CreatePreviewStars() : [];
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<GitHubStarredRepository[]>(preview, CacheState.Fresh, now, now.AddMinutes(5)));
        }

        string normalizedUserId = GitHubAccountPartition.Require(userId);
        string path = $"user/starred?sort=created&direction=desc&per_page={PageSize}&page={Math.Max(1, page)}";
        GitHubQuery<GitHubStarredRepository[]> query = new(
            accessToken,
            normalizedUserId,
            HttpMethod.Get,
            path,
            GitHubQueryKeys.Create(normalizedUserId, HttpMethod.Get, path) + ":star-media-v1",
            GitHubCachePolicy.MutableResource,
            GitHubCachePolicy.TtlForResource(GitHubCachePolicy.MutableResource),
            Phase0GitHubJsonSerializerContext.Default.GitHubStarredRepositoryArray,
            ["me-stars", "star-library", "repo"],
            priority,
            StarMediaType);
        return _queryService.GetAsync(query, fetchPolicy, cancellationToken);
    }

    private static GitHubStarredRepository[] CreatePreviewStars()
    {
        string[] names = ["JitHubApp/JitHubV2", "microsoft/WinUI-Gallery", "microsoft/WindowsAppSDK", "dotnet/runtime"];
        return names.Select((fullName, index) =>
        {
            string[] parts = fullName.Split('/', 2);
            return new GitHubStarredRepository
            {
                StarredAt = DateTimeOffset.UtcNow.AddDays(-index * 3),
                Repository = new GitHubRepository
                {
                    Id = index + 1,
                    Name = parts[1],
                    FullName = fullName,
                    Description = index == 0 ? "A native Windows GitHub client." : "A useful developer repository.",
                    DefaultBranch = "main",
                    HtmlUrl = $"https://github.com/{fullName}",
                    Language = index % 2 == 0 ? "C#" : "C++",
                    StargazersCount = 420 + index * 1200,
                    ForksCount = 20 + index * 10,
                    UpdatedAt = DateTimeOffset.UtcNow.AddHours(-index),
                    PushedAt = DateTimeOffset.UtcNow.AddHours(-index),
                    Topics = ["windows", "developer-tools"],
                    Owner = new GitHubRepositoryOwner { Login = parts[0], AvatarUrl = "ms-appx:///Assets/Octocat.png" }
                }
            };
        }).ToArray();
    }
}
