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
    public void TryResolve_ParentBeyondRepositoryRoot_UsesGitHubAssetBranchConvention()
    {
        var documentSource = new MarkdownRenderer.Images.MarkdownDocumentSource(
            "repository-file:jesseduffield/lazygit:README.md",
            "jesseduffield",
            "lazygit",
            "master",
            "README.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            "../assets/demo.gif",
            documentSource,
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal("assets", reference.Ref);
        Assert.Equal("demo.gif", reference.Path);
        Assert.Equal(
            "https://raw.githubusercontent.com/jesseduffield/lazygit/assets/demo.gif",
            GitHubMarkdownImageUrlResolver.CreateRawUri(reference).ToString());
    }

    [Fact]
    public void TryResolve_MultipleParentsBeyondRepositoryRoot_IsRejected()
    {
        var documentSource = new MarkdownRenderer.Images.MarkdownDocumentSource(
            "repository-file:octo/repo:README.md",
            "octo",
            "repo",
            "main",
            "README.md");

        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(
            "../../outside/image.png",
            documentSource,
            out _));
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

    [Theory]
    [InlineData("images/logo.png?raw=1", "docs/images/logo.png")]
    [InlineData("images/logo.svg#gh-dark-mode-only", "docs/images/logo.svg")]
    [InlineData("images/logo%20dark.png", "docs/images/logo dark.png")]
    public void TryResolve_RelativeImage_RemovesPresentationSuffixAndDecodesPath(
        string source,
        string expectedPath)
    {
        var documentSource = new MarkdownRenderer.Images.MarkdownDocumentSource(
            "repository-file:octo/repo:docs/readme.md",
            "octo",
            "repo",
            "main",
            "docs/readme.md");

        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            source,
            documentSource,
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal(expectedPath, reference.Path);
    }

    [Fact]
    public void TryResolve_NetworkPathReference_IsNotTreatedAsRepositoryContent()
    {
        var documentSource = new MarkdownRenderer.Images.MarkdownDocumentSource(
            "repository-file:octo/repo:README.md",
            "octo",
            "repo",
            "main",
            "README.md");

        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(
            "//example.com/logo.png",
            documentSource,
            out _));
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
    public void GitLfsPointer_RecognizesCanonicalPointer_AndBuildsTrustedMediaRoute()
    {
        byte[] pointer = System.Text.Encoding.UTF8.GetBytes(
            "version https://git-lfs.github.com/spec/v1\n" +
            "oid sha256:db80c1464c7cbde6ef77682431a3baec4ba27b94485dea0b341623da7e4d1fad\n" +
            "size 591868\n");
        var reference = new GitHubMarkdownImageReference(
            "microsoft",
            "autogen",
            "027ecf0a379bcc1d09956d46d12d44a3ad9cee14",
            "autogen-landing.jpg",
            new Uri("https://github.com/microsoft/autogen/blob/main/autogen-landing.jpg"));

        Assert.True(GitLfsPointer.IsPointer(pointer));
        Assert.Equal(
            "https://github.com/microsoft/autogen/raw/027ecf0a379bcc1d09956d46d12d44a3ad9cee14/autogen-landing.jpg",
            GitHubMarkdownImageUrlResolver.CreateGitHubRawRouteUri(reference).AbsoluteUri);
    }

    [Theory]
    [InlineData("version https://git-lfs.github.com/spec/v1\noid sha256:not-a-hash\nsize 1\n")]
    [InlineData("version https://git-lfs.github.com/spec/v1\noid sha256:db80c1464c7cbde6ef77682431a3baec4ba27b94485dea0b341623da7e4d1fad\nsize -1\n")]
    [InlineData("ordinary image bytes")]
    public void GitLfsPointer_RejectsMalformedOrUnrelatedContent(string value)
    {
        Assert.False(GitLfsPointer.IsPointer(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    [Theory]
    [InlineData("heads", "main")]
    [InlineData("tags", "v2.1.0")]
    public void TryResolve_CanonicalRawGitHubRef_KeepsTheQualifiedRef(
        string refKind,
        string refName)
    {
        bool resolved = GitHubMarkdownImageUrlResolver.TryResolve(
            $"https://raw.githubusercontent.com/octo/repo/refs/{refKind}/{refName}/assets/logo.png",
            null,
            null,
            out GitHubMarkdownImageReference reference);

        Assert.True(resolved);
        Assert.Equal($"refs/{refKind}/{refName}", reference.Ref);
        Assert.Equal("assets/logo.png", reference.Path);
        Assert.Equal(
            $"https://raw.githubusercontent.com/octo/repo/refs/{refKind}/{refName}/assets/logo.png",
            GitHubMarkdownImageUrlResolver.CreateRawUri(reference).ToString());
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
