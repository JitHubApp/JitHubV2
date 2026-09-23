using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepositoryImageFetchPipelineTests
{
    private static readonly Uri RawUri = new(
        "https://raw.githubusercontent.com/owner/repository/main/!/tags/implemented.svg");

    [Fact]
    public async Task ValidRawImageUsesCredentialFreeCdnScope()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        GitHubCachedImage expected = new(
            "partitioned-cache-key", [1, 2, 3], "image/svg+xml", false);
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitHubCachedImage?>(expected));
        int reportedFailures = 0;

        GitHubCachedImage? result = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => reportedFailures++, CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(0, reportedFailures);
        await images.Received(1).GetAsync(
            RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PrivateOrMissingRawImagePermitsAuthenticatedFallback(HttpStatusCode status)
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(
                new HttpRequestException("Raw object unavailable", null, status)));
        int reportedFailures = 0;

        GitHubCachedImage? result = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => reportedFailures++, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, reportedFailures);
    }

    [Fact]
    public async Task LfsPointerPermitsAuthenticatedFallback()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(
                new InvalidDataException("The raw response was an LFS pointer.")));

        GitHubCachedImage? result = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CancellationDoesNotStartAuthenticatedFallback()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<GitHubCachedImage?>(canceled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                images, RawUri, _ => { }, canceled.Token));
    }

    [Fact]
    public async Task RawFetchCannotBeRedirectedByCallerToThirdPartyHost()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                images, new Uri("https://example.org/image.svg"), _ => { },
                CancellationToken.None));
        await images.DidNotReceiveWithAnyArgs().GetAsync(default!, default, default);
    }
}
