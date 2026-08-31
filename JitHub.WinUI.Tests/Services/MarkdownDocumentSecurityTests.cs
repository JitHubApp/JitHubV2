using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownDocumentSecurityTests
{
    [Fact]
    public void Consent_IsScopedToStableDocumentIdentity()
    {
        MarkdownRemoteContentConsent consent = new();
        MarkdownDocumentSource first = new("issue:100", "owner", "repo", "main", "README.md");
        MarkdownDocumentSource sameIdentity = new("issue:100", "owner", "repo", "main", "other.md");
        MarkdownDocumentSource second = new("issue:101", "owner", "repo", "main", "README.md");

        consent.Activate(first);
        consent.Grant();
        Assert.True(consent.IsGranted);

        consent.Activate(sameIdentity);
        Assert.True(consent.IsGranted);

        consent.Activate(second);
        Assert.False(consent.IsGranted);
        Assert.Equal("issue:101", consent.CurrentIdentity);
    }

    [Fact]
    public void Consent_AnonymousHostReuseAlwaysRevokesPermission()
    {
        MarkdownRemoteContentConsent consent = new();
        consent.Grant();

        consent.Activate(null);
        string firstAnonymous = consent.CurrentIdentity;
        Assert.False(consent.IsGranted);

        consent.Grant();
        consent.Activate(new MarkdownDocumentSource(string.Empty));
        Assert.False(consent.IsGranted);
        Assert.NotEqual(firstAnonymous, consent.CurrentIdentity);
    }

    [Fact]
    public void Consent_AnonymousDocumentA_Grant_ThenDocumentB_IsRevoked()
    {
        MarkdownRemoteContentConsent consent = new();

        consent.Activate(null);
        string documentA = consent.CurrentIdentity;
        consent.Grant();
        Assert.True(consent.IsGranted);

        consent.Activate(null);
        Assert.False(consent.IsGranted);
        Assert.NotEqual(documentA, consent.CurrentIdentity);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/logo.png")]
    [InlineData("ms-appx:///Assets/logo.png")]
    [InlineData("ms-appdata:///local/logo.png")]
    [InlineData("images/relative.png")]
    public async Task DenyAllResolver_HandlesEverySourceWithoutFallback(string source)
    {
        MarkdownImageResolution result = await DenyAllMarkdownImageResolver.Instance.ResolveAsync(
            source,
            new MarkdownImageResolveContext(
                new Uri("https://github.com/"),
                DocumentSource: new MarkdownDocumentSource("test:deny")),
            CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Null(result.Asset);
        Assert.NotEqual(MarkdownImageUnavailableReason.None, result.UnavailableReason);
    }

    [Theory]
    [InlineData("issue:12", "docs/issue.md")]
    [InlineData("pull-request:17", "docs/pull-request.md")]
    [InlineData("commit:abc123", "docs/commit.md")]
    [InlineData("comment:88", "docs/comment.md")]
    [InlineData("readme:main", "README.md")]
    [InlineData("preview:draft", "docs/draft.md")]
    public void RepositoryContext_ResolvesRelativeImagesWithoutGuessing(
        string documentId,
        string documentPath)
    {
        MarkdownDocumentSource source = new(documentId, "owner", "repository", "feature/branch", documentPath);

        Assert.True(GitHubMarkdownImageUrlResolver.TryResolve("images/screenshot.png", source, out GitHubMarkdownImageReference reference));
        Assert.Equal("owner", reference.Owner);
        Assert.Equal("repository", reference.Repository);
        Assert.Equal("feature/branch", reference.Ref);
        Assert.EndsWith("images/screenshot.png", reference.Path, StringComparison.Ordinal);
        Assert.Equal(
            $"https://raw.githubusercontent.com/owner/repository/feature/branch/{reference.Path}",
            GitHubMarkdownImageUrlResolver.CreateRawUri(reference).ToString());
    }

    [Fact]
    public void RelativeImage_WithoutRepositoryContext_IsNotGuessedFromGitHubRoot()
    {
        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(
            "images/screenshot.png",
            new MarkdownDocumentSource("issue:12"),
            out _));
        Assert.False(GitHubMarkdownImageUrlResolver.TryResolve(
            "images/screenshot.png",
            new Uri("https://github.com/"),
            documentPath: null,
            out _));
    }
}
