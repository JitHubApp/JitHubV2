using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicRepositoryData_RetriesAnonymouslyWhenAuthenticatedAccessCannotReadPublicData(
        bool quotaExhausted)
    {
        GitHubRateLimitException policyFailure = quotaExhausted
            ? CreateQuotaFailure()
            : CreatePolicyFailure();
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
                GitHubAuthenticationConstants.PublicAccessToken,
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

    [Fact]
    public async Task RenderedReadme_RetriesAnonymouslyWhenAuthenticatedQuotaExpiresAfterSourceLoad()
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetReadmeAsync(
                "authenticated-token",
                "42",
                "xai-org",
                "grok-1",
                "main",
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(new CachedResult<GitHubRepositoryContent>(
                CreateReadmeContent(),
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1)));

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetRenderedReadmeHtmlAsync(
                "authenticated-token",
                "xai-org",
                "grok-1",
                "main",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(CreateQuotaFailure()));
        client.GetRenderedReadmeHtmlAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "main",
                Arg.Any<CancellationToken>())
            .Returns("<h1>Public fallback</h1>");

        RepoTreeService service = CreateService(query, client);

        RepoCodeLoadResult<RepoReadmeFile>? readme =
            await service.LoadReadmeAsync("xai-org", "grok-1", "main", CancellationToken.None);

        Assert.Equal("<h1>Public fallback</h1>", readme?.Value.RenderedHtml);
        await client.Received(1).GetRenderedReadmeHtmlAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "xai-org",
            "grok-1",
            "main",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Readme_UsesBoundedRawSourceWhenAuthenticatedAndAnonymousApiAccessFail()
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetReadmeAsync(
                "authenticated-token",
                "42",
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubRepositoryContent>>(CreatePolicyFailure()));

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetReadmeAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubRepositoryContent>(CreateQuotaFailure()));
        client.GetPublicReadmeSourceAsync(
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<CancellationToken>())
            .Returns(CreateReadmeContent());

        RepoTreeService service = CreateService(query, client);

        RepoCodeLoadResult<RepoReadmeFile>? readme =
            await service.LoadReadmeAsync("xai-org", "grok-1", "commit-sha", CancellationToken.None);

        Assert.Equal("# Hello", readme?.Value.Blob.Text);
        Assert.Null(readme?.Value.RenderedHtml);
        await client.Received(1).GetPublicReadmeSourceAsync(
            "xai-org",
            "grok-1",
            "commit-sha",
            Arg.Any<CancellationToken>());
        await client.DidNotReceiveWithAnyArgs().GetRenderedReadmeHtmlAsync(
            default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Readme_UsesRawSourceWhenAnonymousApiTransportTimesOut()
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetReadmeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubRepositoryContent>>(CreatePolicyFailure()));

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetReadmeAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubRepositoryContent>(new TaskCanceledException("transport timeout")));
        client.GetPublicReadmeSourceAsync(
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<CancellationToken>())
            .Returns(CreateReadmeContent());

        RepoTreeService service = CreateService(query, client);

        RepoCodeLoadResult<RepoReadmeFile>? readme =
            await service.LoadReadmeAsync("xai-org", "grok-1", "commit-sha", CancellationToken.None);

        Assert.Equal("# Hello", readme?.Value.Blob.Text);
    }

    [Fact]
    public async Task Readme_DoesNotMaskUnexpectedAnonymousApiFailure()
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetReadmeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubRepositoryContent>>(CreatePolicyFailure()));

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetReadmeAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubRepositoryContent>(new InvalidOperationException("invariant")));
        RepoTreeService service = CreateService(query, client);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoadReadmeAsync("xai-org", "grok-1", "commit-sha", CancellationToken.None));

        Assert.Equal("invariant", error.Message);
        await client.DidNotReceiveWithAnyArgs().GetPublicReadmeSourceAsync(
            default!, default!, default!, default);
    }

    [Fact]
    public async Task Readme_DoesNotStartRawFallbackAfterCallerCancellation()
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetReadmeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubRepositoryContent>>(CreatePolicyFailure()));

        using CancellationTokenSource cancellation = new();
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetReadmeAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "xai-org",
                "grok-1",
                "commit-sha",
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<GitHubRepositoryContent>(cancellation.Token);
            });

        RepoTreeService service = CreateService(query, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LoadReadmeAsync("xai-org", "grok-1", "commit-sha", cancellation.Token));
        await client.DidNotReceiveWithAnyArgs().GetPublicReadmeSourceAsync(
            default!, default!, default!, default);
    }

    [Fact]
    public async Task Readme_UsesRawSourceWhenAlreadyAnonymousApiQuotaIsExhausted()
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetReadmeAsync(
                GitHubAuthenticationConstants.PublicAccessToken,
                "current",
                "octo",
                "app",
                "main",
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CachedResult<GitHubRepositoryContent>>(CreateQuotaFailure()));

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetPublicReadmeSourceAsync(
                "octo",
                "app",
                "main",
                Arg.Any<CancellationToken>())
            .Returns(CreateReadmeContent());
        IAuthService auth = Substitute.For<IAuthService>();
        auth.AuthenticatedUser.Returns((GitHubUser?)null);
        auth.GetToken(0).Returns((string?)null);
        IAccountService account = Substitute.For<IAccountService>();
        account.GetUser().Returns(0);
        RepoTreeService service = new(query, auth, account, client);

        RepoCodeLoadResult<RepoReadmeFile>? readme =
            await service.LoadReadmeAsync("octo", "app", "main", CancellationToken.None);

        Assert.Equal("# Hello", readme?.Value.Blob.Text);
        await client.DidNotReceiveWithAnyArgs().GetReadmeAsync(
            default!, default!, default!, default, default);
        await client.Received(1).GetPublicReadmeSourceAsync(
            "octo",
            "app",
            "main",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Readme_KeepsSourceWhenRenderedHtmlEnhancementFails()
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        query.GetReadmeAsync(
                "authenticated-token",
                "42",
                "octo",
                "app",
                "main",
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(new CachedResult<GitHubRepositoryContent>(
                CreateReadmeContent(),
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1)));

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetRenderedReadmeHtmlAsync(
                "authenticated-token",
                "octo",
                "app",
                "main",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new HttpRequestException("offline")));

        RepoTreeService service = CreateService(query, client);

        RepoCodeLoadResult<RepoReadmeFile>? readme =
            await service.LoadReadmeAsync("octo", "app", "main", CancellationToken.None);

        Assert.Equal("# Hello", readme?.Value.Blob.Text);
        Assert.Null(readme?.Value.RenderedHtml);
    }

    [Theory]
    [InlineData("IP allow list enabled", null, null, true)]
    [InlineData("Resource not accessible by integration", 42, null, true)]
    [InlineData("API rate limit exceeded for installation", 0, null, true)]
    [InlineData("API rate limit exceeded", null, null, true)]
    [InlineData("API rate limit exceeded", 0, 30, false)]
    [InlineData("Resource not accessible by integration", 42, 30, false)]
    [InlineData("Some other forbidden response", 42, null, false)]
    public void AnonymousFallback_OnlyAdmitsSafeAuthenticatedPublicReadFailures(
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

        Assert.Equal(expected, RepoTreeService.IsAnonymousPublicDataFallbackCandidate(exception));
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

    private static GitHubRateLimitException CreateQuotaFailure() =>
        new(
            HttpStatusCode.Forbidden,
            "API rate limit exceeded for installation.",
            TimeSpan.FromMinutes(1),
            0,
            DateTimeOffset.UtcNow.AddMinutes(1),
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
