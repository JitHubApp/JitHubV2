using System;
using System.Collections.Generic;
using JitHub.Markdown.Images;
using Xunit;

namespace JitHub.Markdown.Tests.Images;

/// <summary>
/// How GitHub's rendered README source-to-Camo map and Camo's encoded canonical
/// origin are parsed for Markdown image fallbacks.
/// </summary>
public sealed class GitHubCamoImageMapParserTests
{
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
