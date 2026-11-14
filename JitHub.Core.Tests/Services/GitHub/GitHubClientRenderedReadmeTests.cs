using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.GitHub;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.Core.Tests.Services.Images.GitHubImageServiceAdmissionDoubles;
using static JitHub.Core.Tests.Services.Images.GitHubImageServiceHttpDoubles;

namespace JitHub.Core.Tests.Services.GitHub;

/// <summary>
/// Rendered README fetches through the app's GitHub client. Parsing the rendered
/// HTML into a Camo image map is covered by GitHubCamoImageMapParserTests in
/// JitHub.Markdown.Tests.
/// </summary>
public sealed class GitHubClientRenderedReadmeTests
{
    [Fact]
    public async Task GetRenderedReadmeHtmlAsync_RequestsGitHubRenderedRepresentation()
    {
        const string html = "<article><img src=\"rendered.png\"></article>";
        var handler = new RenderedReadmeHandler(html);
        using HttpClient client = new(handler);
        var service = new GitHubClientService(client);

        string result = await service.GetRenderedReadmeHtmlAsync(
            "token",
            "owner",
            "repository",
            "immutable-sha");

        Assert.Equal(html, result);
        Assert.Equal(
            "https://api.github.com/repos/owner/repository/readme?ref=immutable-sha",
            handler.RequestUri);
        Assert.Contains("application/vnd.github.html+json", handler.Accept);
    }

    [Fact]
    public async Task GetRenderedReadmeHtmlAsync_PublicReadmeRetriesWithoutTokenAfterPolicyForbidden()
    {
        const string html = "<article><img src=\"camo.png\"></article>";
        var handler = new AuthPolicyRenderedReadmeHandler(html);
        using HttpClient client = new(handler);
        var service = new GitHubClientService(client);

        string result = await service.GetRenderedReadmeHtmlAsync(
            "installation-token",
            "public-owner",
            "public-repository",
            "immutable-sha");

        Assert.Equal(html, result);
        Assert.Equal(2, handler.AuthorizationSchemes.Count);
        Assert.Equal("Bearer", handler.AuthorizationSchemes[0]);
        Assert.Null(handler.AuthorizationSchemes[1]);
        Assert.All(handler.AcceptValues, static accept =>
            Assert.Contains("application/vnd.github.html+json", accept));
    }
}
