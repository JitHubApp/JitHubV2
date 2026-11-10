using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.GitHub.Caching;
using JitHub.Core.Services.Images;
using JitHub.Core.Services.Images.Caching;
using JitHub.Markdown.Images.Caching;
using JitHub.Markdown.Images.Repository;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// How the Markdown raw-image pipeline in JitHub.Markdown reports outcomes when
/// the app's real <see cref="GitHubImageService"/> serves a repeat read from cache.
/// </summary>
public sealed class GitHubRawImageCacheOutcomeTests
{
    [Fact]
    public async Task RawImageSuccessOutcomeDoesNotImplyAnHttpRequestWhenSecondOperationUsesCache()
    {
        const string source = "https://raw.githubusercontent.com/owner/repository/main/image.png";
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "JitHubRawImageFetchPipelineTests",
            Guid.NewGuid().ToString("N"));
        var handler = new CountingPngHandler(pngBytes);
        var outcomes = new List<GitHubRawImageFetchOutcome>();

        try
        {
            var store = new GitHubImageCacheStore(cacheRoot, GitHubCachePolicy.Default);
            using HttpClient client = new(handler);
            using var service = new GitHubImageService(store, client);

            GitHubCachedImage? first = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                service,
                new Uri(source),
                _ => throw new Xunit.Sdk.XunitException("Unexpected raw image failure"),
                CancellationToken.None,
                outcomes.Add);
            GitHubCachedImage? second = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                service,
                new Uri(source),
                _ => throw new Xunit.Sdk.XunitException("Unexpected raw image failure"),
                CancellationToken.None,
                outcomes.Add);

            Assert.NotNull(first);
            Assert.False(first!.IsFromCache);
            Assert.NotNull(second);
            Assert.True(second!.IsFromCache);
            Assert.Equal(pngBytes, first.Bytes);
            Assert.Equal(pngBytes, second.Bytes);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(
                new[]
                {
                    new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.Success),
                    new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.Success),
                },
                outcomes);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private sealed class CountingPngHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
