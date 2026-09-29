using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubClientPublicReadmeTests
{
    [Fact]
    public async Task PublicReadmeSource_ProbesCanonicalNamesAndComputesGitBlobIdentity()
    {
        RecordingHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/deadbeef/README", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("# Hello"))
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        GitHubRepositoryContent? readme = await client.GetPublicReadmeSourceAsync(
            "octo",
            "app",
            "deadbeef",
            CancellationToken.None);

        Assert.NotNull(readme);
        Assert.Equal("README", readme.Name);
        Assert.Equal("README", readme.Path);
        Assert.Equal("file", readme.Type);
        Assert.Equal("base64", readme.Encoding);
        Assert.Equal("# Hello", Encoding.UTF8.GetString(Convert.FromBase64String(readme.Content!)));
        Assert.Equal("5570c7ff58e717dad46ce7b0691ccf2bdb55f187", readme.Sha);
        Assert.Equal(16, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("raw.githubusercontent.com", request.Uri.Host);
            Assert.False(request.HadAuthorization);
        });
    }

    [Fact]
    public async Task PublicReadmeSource_RejectsDeclaredBodyAboveBudgetBeforeReading()
    {
        RecordingHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
            response.Content.Headers.ContentLength = (8L * 1024 * 1024) + 1;
            return response;
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() =>
            client.GetPublicReadmeSourceAsync("octo", "app", "main", CancellationToken.None));
    }

    [Fact]
    public async Task PublicReadmeSource_UsesGithubThenRootThenDocsPrecedence()
    {
        RecordingHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            bool exists = path.EndsWith("/deadbeef/.github/README.rst", StringComparison.Ordinal) ||
                path.EndsWith("/deadbeef/README.md", StringComparison.Ordinal) ||
                path.EndsWith("/deadbeef/docs/README.md", StringComparison.Ordinal);
            return new HttpResponseMessage(exists ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(path))
            };
        });
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        GitHubRepositoryContent? readme = await client.GetPublicReadmeSourceAsync(
            "octo",
            "app",
            "deadbeef",
            CancellationToken.None);

        Assert.Equal(".github/README.rst", readme?.Path);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<(Uri Uri, bool HadAuthorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!, request.Headers.Authorization is not null));
            return Task.FromResult(responseFactory(request));
        }
    }
}
