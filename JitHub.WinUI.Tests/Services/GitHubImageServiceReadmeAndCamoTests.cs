using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceHttpDoubles;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceAdmissionDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Rendered README fetches and GitHub Camo image map parsing.
/// </summary>
public sealed class GitHubImageServiceReadmeAndCamoTests
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

    [Fact]
    public void ParseGitHubCamoImageMap_AdmitsHttpOrHttpsOriginalOnlyThroughCanonicalHttpsCamo()
    {
        const string source = "https://assets.example.test/image.png?one=1&two=2";
        const string camo = "https://camo.githubusercontent.com/hash/encoded";
        string html =
            $"<img data-canonical-src='https://assets.example.test/image.png?one=1&amp;two=2' src='{camo}'>" +
            $"<img data-canonical-src='http://legacy.example.test/image.gif' src='{camo}/legacy'>" +
            "<img src=\"https://evil.example.test/image.png\" data-canonical-src=\"https://assets.example.test/evil.png\">";

        IReadOnlyDictionary<string, string> map = GitHubCamoImageMapParser.Parse(html);

        Assert.Equal(2, map.Count);
        Assert.Equal(camo, map[source]);
        Assert.Equal($"{camo}/legacy", map["http://legacy.example.test/image.gif"]);
    }

    [Fact]
    public void DecodeGitHubCamoCanonicalSource_RecoversBoundedHttpsOrigin()
    {
        const string origin = "https://asciinema.org/a/736339.svg?download=1";
        string encoded = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(origin)).ToLowerInvariant();
        var camo = new Uri(
            "https://camo.githubusercontent.com/" + new string('a', 64) + "/" + encoded);

        Assert.True(GitHubCamoImageMapParser.TryDecodeCanonicalSource(camo, out Uri decoded));
        Assert.Equal(origin, decoded.AbsoluteUri);
    }

    [Fact]
    public void DecodeGitHubCamoCanonicalSource_UpgradesLegacyHttpOriginToHttps()
    {
        const string origin = "http://hits.dwyl.com/996icu/996ICU.svg";
        string encoded = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(origin)).ToLowerInvariant();
        var camo = new Uri(
            "https://camo.githubusercontent.com/" + new string('a', 64) + "/" + encoded);

        Assert.True(GitHubCamoImageMapParser.TryDecodeCanonicalSource(camo, out Uri decoded));
        Assert.Equal("https://hits.dwyl.com/996icu/996ICU.svg", decoded.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://camo.githubusercontent.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/68747470733a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://evil.example/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/68747470733a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://camo.githubusercontent.com/short/68747470733a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://camo.githubusercontent.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/6674703a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://camo.githubusercontent.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/not-hex")]
    public void DecodeGitHubCamoCanonicalSource_RejectsNonCanonicalOrInsecureValues(string value)
    {
        Assert.False(GitHubCamoImageMapParser.TryDecodeCanonicalSource(new Uri(value), out _));
    }
}
