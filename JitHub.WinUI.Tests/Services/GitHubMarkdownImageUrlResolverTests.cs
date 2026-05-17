using System;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public class GitHubMarkdownImageUrlResolverTests
{
    [Fact]
    public void TryResolve_RelativeImage_UsesMarkdownFileDirectory()
    {
        var baseUri = new Uri("https://github.com/octo/repo/blob/main/docs/readme.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "images/logo.png",
            baseUri,
            "docs/readme.md",
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("octo", reference.Owner);
        Assert.Equal("repo", reference.Repository);
        Assert.Equal("main", reference.Ref);
        Assert.Equal("docs/images/logo.png", reference.Path);
    }

    [Fact]
    public void TryResolve_RelativeParentSegment_NormalizesRepositoryPath()
    {
        var baseUri = new Uri("https://github.com/octo/repo/blob/main/docs/guides/readme.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "../images/logo.svg",
            baseUri,
            "docs/guides/readme.md",
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("docs/images/logo.svg", reference.Path);
    }

    [Fact]
    public void TryResolve_RootRelativeImage_UsesRepositoryRoot()
    {
        var baseUri = new Uri("https://github.com/octo/repo/blob/main/docs/readme.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "/assets/logo.png",
            baseUri,
            "docs/readme.md",
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("assets/logo.png", reference.Path);
    }

    [Fact]
    public void TryResolve_BranchNameWithSlash_UsesKnownDocumentPath()
    {
        var baseUri = new Uri("https://github.com/octo/repo/blob/feature/docs-refresh/docs/readme.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "logo.png",
            baseUri,
            "docs/readme.md",
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("feature/docs-refresh", reference.Ref);
        Assert.Equal("docs/logo.png", reference.Path);
    }

    [Fact]
    public void TryResolve_RawGitHubUrl_ParsesRepositoryReference()
    {
        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "https://raw.githubusercontent.com/octo/repo/main/assets/logo.png",
            null,
            null,
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("octo", reference.Owner);
        Assert.Equal("repo", reference.Repository);
        Assert.Equal("main", reference.Ref);
        Assert.Equal("assets/logo.png", reference.Path);
    }

    [Fact]
    public void TryResolve_NonGitHubAbsoluteUrl_ReturnsFalse()
    {
        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "https://example.com/logo.png",
            null,
            null,
            out _);

        Assert.False(resolved);
    }
}
