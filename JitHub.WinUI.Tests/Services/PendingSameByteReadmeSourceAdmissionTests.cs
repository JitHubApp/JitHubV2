using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PendingSameByteReadmeSourceAdmissionTests
{
    [Fact]
    public void PendingAdmissionMatchesOnlyTheSameCompletedDocumentIdentityAndPath()
    {
        var identity = new SameByteReadmeSourceIdentity(
            12, new string('a', 64), 12, new string('b', 64), new string('c', 40));
        MarkdownDocumentSource documentSource = new(
            $"repository-file:owner:repo:immutable-ref:README.md:{identity.ReadmeGitBlobSha1}",
            "owner",
            "repo",
            new string('d', 40),
            "README.md");
        var pending = new PendingSameByteReadmeSourceAdmission(
            "# README\n",
            "README.md",
            documentSource,
            identity,
            "matched",
            StopwatchTicks: 1_000_000,
            StopwatchFrequency: 10_000_000);

        Assert.True(pending.Matches("# README\n", "README.md", documentSource));
        Assert.False(pending.Matches("# changed\n", "README.md", documentSource));
        Assert.False(pending.Matches("# README\n", "docs/README.md", documentSource));
        Assert.True(pending.Matches(
            "# README\n",
            "README.md",
            documentSource with { Owner = "OWNER" }));
        Assert.False(pending.Matches("# README\n", "README.md", documentSource with { Ref = "main" }));
        Assert.False(pending.Matches("# README\n", "README.md", documentSource with { Path = "docs/README.md" }));
        Assert.False(pending.Matches("# README\n", "README.md", documentSource with { Repository = "other-repo" }));
        Assert.False(pending.Matches("# README\n", "README.md", documentSource with { DocumentId = "different-document" }));
        Assert.Equal(1_000_000, pending.StopwatchTicks);
        Assert.Same(identity, pending.Identity);
    }

    [Fact]
    public void PendingAdmissionBindsOnlyItsPositiveAssignmentGeneration()
    {
        var identity = new SameByteReadmeSourceIdentity(
            12, new string('a', 64), 12, new string('b', 64), new string('c', 40));
        var pending = new PendingSameByteReadmeSourceAdmission(
            "# README\n",
            "README.md",
            DocumentSource: null,
            Identity: identity,
            FailureReason: "matched",
            StopwatchTicks: 1_000_000,
            StopwatchFrequency: 10_000_000)
        {
            AssignmentPipelineGeneration = 42,
        };

        Assert.True(pending.IsBoundToPipelineGeneration(42));
        Assert.False(pending.IsBoundToPipelineGeneration(0));
        Assert.False(pending.IsBoundToPipelineGeneration(41));
        Assert.False(pending.IsBoundToPipelineGeneration(43));

        // An assignment made while the renderer is unloaded has no known
        // pipeline generation. It intentionally fails closed after loading;
        // a later restyle cannot claim the old pre-assignment tick.
        PendingSameByteReadmeSourceAdmission unloaded = pending with
        {
            AssignmentPipelineGeneration = 0,
            Identity = null,
            FailureReason = "assignment-generation-unavailable",
        };
        Assert.False(unloaded.IsBoundToPipelineGeneration(1));
    }

    [Fact]
    public void RepositoryFileIdentityIncludesRefAndPathAndRetainsBlobSuffix()
    {
        const string blobSha = "0123456789abcdef0123456789abcdef01234567";
        MarkdownDocumentSource branchReadme = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/main/README.md",
            "README.md") ?? throw new InvalidOperationException("Expected repository source.");
        MarkdownDocumentSource otherRef = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/release/README.md",
            "README.md") ?? throw new InvalidOperationException("Expected repository source.");
        MarkdownDocumentSource otherPath = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/main/docs/README.md",
            "docs/README.md") ?? throw new InvalidOperationException("Expected repository source.");

        Assert.NotEqual(branchReadme.DocumentId, otherRef.DocumentId);
        Assert.NotEqual(branchReadme.DocumentId, otherPath.DocumentId);
        Assert.EndsWith($":{blobSha}", branchReadme.DocumentId, StringComparison.Ordinal);
        Assert.Equal("main", branchReadme.Ref);
        Assert.Equal("README.md", branchReadme.Path);
        Assert.Equal("release", otherRef.Ref);
        Assert.Equal("docs/README.md", otherPath.Path);

        MarkdownDocumentSource repositoryDocument = MarkdownDocumentSourceFactory.CreateRepositoryDocument(
            "repository-file", blobSha, "owner", "repo", "main", "README.md");
        MarkdownDocumentSource sameBlobDifferentContext = MarkdownDocumentSourceFactory.CreateRepositoryDocument(
            "repository-file", blobSha, "owner", "repo", "release", "docs/README.md");
        Assert.NotEqual(repositoryDocument.DocumentId, sameBlobDifferentContext.DocumentId);
        Assert.EndsWith($":{blobSha}", repositoryDocument.DocumentId, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryFileIdentityTracksTreeSnapshotOrReloadWhenTreeShaIsUnavailable()
    {
        const string blobSha = "0123456789abcdef0123456789abcdef01234567";
        const string firstTreeSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string nextTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        MarkdownDocumentSource firstTree = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/main/README.md",
            "README.md",
            firstTreeSha) ?? throw new InvalidOperationException("Expected repository source.");
        MarkdownDocumentSource firstTreeReloaded = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/main/README.md",
            "README.md",
            firstTreeSha,
            reloadRevision: "11") ?? throw new InvalidOperationException("Expected repository source.");
        MarkdownDocumentSource nextTree = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/main/README.md",
            "README.md",
            nextTreeSha) ?? throw new InvalidOperationException("Expected repository source.");
        MarkdownDocumentSource firstReloadFallback = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/main/README.md",
            "README.md",
            repositoryTreeSha: null,
            reloadRevision: "10") ?? throw new InvalidOperationException("Expected repository source.");
        MarkdownDocumentSource nextReloadFallback = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/main/README.md",
            "README.md",
            repositoryTreeSha: null,
            reloadRevision: "11") ?? throw new InvalidOperationException("Expected repository source.");

        Assert.NotEqual(firstTree.DocumentId, nextTree.DocumentId);
        Assert.Equal(firstTree.DocumentId, firstTreeReloaded.DocumentId);
        Assert.Contains($"tree-{firstTreeSha}", firstTree.DocumentId, StringComparison.Ordinal);
        Assert.EndsWith($":{blobSha}", firstTree.DocumentId, StringComparison.Ordinal);
        Assert.NotEqual(firstReloadFallback.DocumentId, nextReloadFallback.DocumentId);
        Assert.Contains("reload-10", firstReloadFallback.DocumentId, StringComparison.Ordinal);
        Assert.EndsWith($":{blobSha}", firstReloadFallback.DocumentId, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryFileIdentityEncodesSlashContainingRefWithoutCollidingWithPath()
    {
        const string blobSha = "0123456789abcdef0123456789abcdef01234567";
        MarkdownDocumentSource source = MarkdownDocumentSourceFactory.TryCreateRepositoryFile(
            blobSha,
            "https://github.com/owner/repo/blob/feature%2Fpreview/README.md",
            "README.md") ?? throw new InvalidOperationException("Expected repository source.");

        Assert.Equal("feature/preview", source.Ref);
        Assert.EndsWith($":{blobSha}", source.DocumentId, StringComparison.Ordinal);
        Assert.Contains("feature%2Fpreview", source.DocumentId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    public void RepositoryFileAuditRequiresImmutableObjectIdRef(string gitRef, bool immutable)
    {
        Assert.Equal(immutable, JitHub.Services.CodeViewer.GitReferencePolicy.IsImmutableObjectId(gitRef));
    }
}
