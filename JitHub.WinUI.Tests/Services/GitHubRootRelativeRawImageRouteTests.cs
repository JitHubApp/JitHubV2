using System;
using System.Linq;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRootRelativeRawImageRouteTests
{
    [Theory]
    [InlineData(
        "sherlock-project",
        "sherlock",
        "e40a45ec2a074b90703b3b4b842c8a3adbd6ada3",
        "docs/README.md",
        "images/sherlock-logo.png",
        "docs/images/sherlock-logo.png")]
    [InlineData(
        "coder",
        "code-server",
        "96e468eaa666f86bed3367bb1cf6e1348e15612b",
        "docs/README.md",
        "./assets/screenshot-1.png",
        "docs/assets/screenshot-1.png")]
    public void TryResolve_AuthoredRelativeImageFromNestedReadme_UsesFactoryDocumentContext(
        string owner,
        string repository,
        string gitRef,
        string documentPath,
        string source,
        string expectedPath)
    {
        string blobUrl =
            $"https://github.com/{owner}/{repository}/blob/{gitRef}/{documentPath}";
        MarkdownDocumentSource? documentSource = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            $"{owner}/{repository}:{gitRef}:{documentPath}",
            blobUrl,
            documentPath);

        Assert.NotNull(documentSource);
        Assert.Equal(documentPath, documentSource.Path);
        Assert.Equal(gitRef, documentSource.Ref);
        Assert.True(GitHubMarkdownImageUrlResolver.TryResolve(
            source,
            documentSource,
            out GitHubMarkdownImageReference reference));
        Assert.Equal(expectedPath, reference.Path);
        Assert.Equal(gitRef, reference.Ref);
    }

    [Theory]
    [InlineData(
        "sherlock-project",
        "sherlock",
        "e40a45ec2a074b90703b3b4b842c8a3adbd6ada3",
        "docs/README.md",
        "/sherlock-project/sherlock/raw/e40a45ec2a074b90703b3b4b842c8a3adbd6ada3/docs/images/sherlock-logo.png",
        "docs/images/sherlock-logo.png")]
    [InlineData(
        "coder",
        "code-server",
        "96e468eaa666f86bed3367bb1cf6e1348e15612b",
        "docs/README.md",
        "/coder/code-server/raw/96e468eaa666f86bed3367bb1cf6e1348e15612b/docs/assets/screenshot-1.png",
        "docs/assets/screenshot-1.png")]
    public void TryResolve_GitHubRootRelativeRawImageRoute_UsesPinnedRepositoryAndDocumentContext(
        string owner,
        string repository,
        string gitRef,
        string documentPath,
        string source,
        string expectedPath)
    {
        var documentSource = new MarkdownDocumentSource(
            $"repository-file:{owner}/{repository}:{gitRef}:{documentPath}",
            owner,
            repository,
            gitRef,
            documentPath);

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            source,
            documentSource,
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal(owner, reference.Owner);
        Assert.Equal(repository, reference.Repository);
        Assert.Equal(gitRef, reference.Ref);
        Assert.Equal(expectedPath, reference.Path);
        Assert.Equal(
            $"https://raw.githubusercontent.com/{owner}/{repository}/{gitRef}/{expectedPath}",
            GitHubMarkdownImageUrlResolver.CreateRawUri(reference).AbsoluteUri);
    }

    [Fact]
    public void TryResolve_RootRelativeRepositoryAsset_StillUsesRepositoryRoot()
    {
        var documentSource = new MarkdownDocumentSource(
            "repository-file:octo/repo:main:docs/README.md",
            "octo",
            "repo",
            "main",
            "docs/README.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "/assets/logo.png",
            documentSource,
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("assets/logo.png", reference.Path);
    }

    [Fact]
    public void TryResolve_GitHubRootRelativeRawRouteForAnotherRepository_IsRejected()
    {
        var documentSource = new MarkdownDocumentSource(
            "repository-file:octo/repo:main:README.md",
            "octo",
            "repo",
            "main",
            "README.md");

        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(
            "/other/project/raw/main/assets/logo.png",
            documentSource,
            out _));
    }

    [Theory]
    [InlineData("/octo/repo/raw/other/assets/logo.png")]
    [InlineData("/octo/repo/raw/main/assets/logo.png?raw=true")]
    [InlineData("/octo/repo/raw/main/assets/logo.png#preview")]
    [InlineData("/octo/repo/raw%2Fmain/assets/logo.png")]
    [InlineData("/octo/repo/raw%5Cmain/assets/logo.png")]
    [InlineData("/octo/repo/raw/main/assets%2Fsecret/logo.png")]
    [InlineData("/octo/repo/raw/main/assets%5Csecret/logo.png")]
    [InlineData("/octo/repo/raw/main/assets/../secret.png")]
    [InlineData("/octo/repo/raw/main/assets/%2e%2e/secret.png")]
    public void TryResolve_GitHubRootRelativeRawRouteWithAmbiguousOrUnsafePath_IsRejected(
        string source)
    {
        var documentSource = new MarkdownDocumentSource(
            "repository-file:octo/repo:main:docs/README.md",
            "octo",
            "repo",
            "main",
            "docs/README.md");

        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(
            source,
            documentSource,
            out _));
    }

    [Fact]
    public void TryResolve_RawRouteSlashStorm_IsRejectedWithinBoundedSegmentBudget()
    {
        var documentSource = new MarkdownDocumentSource(
            "repository-file:octo/repo:main:README.md",
            "octo",
            "repo",
            "main",
            "README.md");
        string source = "/octo/repo/raw/main/" + string.Join("/", Enumerable.Repeat("segment", 520));

        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(source, documentSource, out _));
    }

    [Fact]
    public void TryResolve_OversizedRootRelativeSlashStorm_FailsClosedBeforePathNormalization()
    {
        var documentSource = new MarkdownDocumentSource(
            "repository-file:octo/repo:main:README.md",
            "octo",
            "repo",
            "main",
            "README.md");
        string source = "/assets/" + new string('/', 32 * 1024);

        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(source, documentSource, out _));
    }

    [Fact]
    public void TryResolve_GitHubRootRelativeRawImageRoute_UsesBaseUriContextToo()
    {
        const string commit = "e40a45ec2a074b90703b3b4b842c8a3adbd6ada3";
        var baseUri = new Uri(
            $"https://github.com/sherlock-project/sherlock/blob/{commit}/docs/README.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            $"/sherlock-project/sherlock/raw/{commit}/docs/images/sherlock-logo.png",
            baseUri,
            "docs/README.md",
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("docs/images/sherlock-logo.png", reference.Path);
        Assert.Equal(commit, reference.Ref);
    }
}
