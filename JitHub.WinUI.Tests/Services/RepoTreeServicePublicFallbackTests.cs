using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepoTreeServicePublicFallbackTests
{
    [Fact]
    public async Task PublicRepositoryData_RetriesAnonymouslyWhenIntegrationIsBlockedByOrganizationPolicy()
    {
        GitHubRateLimitException policyFailure = CreatePolicyFailure();
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetTreeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubTree>>(policyFailure));
        query.GetDirectoryAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubRepositoryContent[]>>(policyFailure));
        query.GetReadmeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubRepositoryContent>>(policyFailure));
        query.GetBlobAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<GitHubRequestPriority>(),
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubBlob>>(policyFailure));

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetTreeAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "main",
                true,
                Arg.Any<CancellationToken>())
            .Returns(new GitHubTree
            {
                Sha = "tree",
                Tree = [new GitHubTreeEntry { Path = "README.md", Type = "blob", Sha = "readme" }]
            });
        client.GetRepositoryContentsAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                string.Empty,
                "main",
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GitHubRepositoryContent>)
                [new GitHubRepositoryContent { Name = "README.md", Path = "README.md", Type = "file", Sha = "readme" }]);
        client.GetReadmeAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "main",
                Arg.Any<CancellationToken>())
            .Returns(CreateReadmeContent());
        client.GetRenderedReadmeHtmlAsync(
                "authenticated-token",
                "xai-org",
                "grok-1",
                "main",
                Arg.Any<CancellationToken>())
            .Returns("<h1>Hello</h1>");
        client.GetBlobAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "readme",
                Arg.Any<CancellationToken>())
            .Returns(new GitHubBlob
            {
                Sha = "readme",
                Encoding = "base64",
                Content = Convert.ToBase64String(Encoding.UTF8.GetBytes("# Hello"))
            });

        RepoTreeService service = CreateService(query, client);

        RepoCodeLoadResult<RepoTree> tree =
            await service.LoadTreeAsync("xai-org", "grok-1", "main", CancellationToken.None);
        RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> directory =
            await service.LoadDirectoryAsync("xai-org", "grok-1", string.Empty, "main", CancellationToken.None);
        RepoCodeLoadResult<RepoReadmeFile>? readme =
            await service.LoadReadmeAsync("xai-org", "grok-1", "main", CancellationToken.None);
        RepoCodeLoadResult<RepoFileBlob> blob =
            await service.LoadBlobAsync("xai-org", "grok-1", "readme", CancellationToken.None);

        Assert.Equal("tree", tree.Value.Sha);
        Assert.Single(directory.Value);
        Assert.Equal("# Hello", readme?.Value.Blob.Text);
        Assert.Equal("<h1>Hello</h1>", readme?.Value.RenderedHtml);
        Assert.Equal("# Hello", blob.Value.Text);
        Assert.Equal(CacheState.Fresh, tree.CacheState);
        Assert.Equal(CacheState.Fresh, directory.CacheState);
        Assert.Equal(CacheState.Fresh, readme?.CacheState);
        Assert.Equal(CacheState.Fresh, blob.CacheState);
    }

    [Theory]
    [InlineData("IP allow list enabled", null, null, true)]
    [InlineData("Resource not accessible by integration", 42, null, true)]
    [InlineData("API rate limit exceeded", 0, null, false)]
    [InlineData("Resource not accessible by integration", 42, 30, false)]
    [InlineData("Some other forbidden response", 42, null, false)]
    public void AnonymousFallback_OnlyAdmitsNonThrottledAuthenticationPolicyFailures(
        string message,
        int? remaining,
        int? retryAfterSeconds,
        bool expected)
    {
        GitHubRateLimitException exception = new(
            HttpStatusCode.Forbidden,
            message,
            TimeSpan.FromMinutes(1),
            remaining,
            null,
            retryAfterSeconds is int seconds ? TimeSpan.FromSeconds(seconds) : null,
            null);

        Assert.Equal(expected, RepoTreeService.IsPublicDataAuthenticationPolicyDenial(exception));
    }

    private static RepoTreeService CreateService(
        IGitHubRepoCodeQueryService query,
        IGitHubClientService client)
    {
        IAuthService auth = Substitute.For<IAuthService>();
        auth.AuthenticatedUser.Returns(new GitHubUser { Id = 42, Login = "octo" });
        auth.GetToken(42).Returns("authenticated-token");
        IAccountService account = Substitute.For<IAccountService>();
        account.GetUser().Returns(42);
        return new RepoTreeService(query, auth, account, client);
    }

    private static GitHubRateLimitException CreatePolicyFailure() =>
        new(
            HttpStatusCode.Forbidden,
            "Although you appear to have the correct authorization credentials, the xai-org organization has an IP allow list enabled.",
            TimeSpan.FromMinutes(1),
            42,
            null,
            null,
            "core");

    private static GitHubRepositoryContent CreateReadmeContent() => new()
    {
        Name = "README.md",
        Path = "README.md",
        Type = "file",
        Sha = "readme",
        Encoding = "base64",
        Content = Convert.ToBase64String(Encoding.UTF8.GetBytes("# Hello"))
    };
}
