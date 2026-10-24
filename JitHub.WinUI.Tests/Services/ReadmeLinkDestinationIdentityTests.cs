using JitHub.WinUI.Automation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReadmeLinkDestinationIdentityTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void RelativeDestinationsResolveAgainstThePinnedReadmePath()
    {
        Uri readmeBase = CreatePinnedBase("docs/README.md");

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "../scripts/readme.md",
            readmeBase,
            out Uri? normalized));
        Assert.Equal(
            $"https://github.com/owner/repo/blob/{Commit}/scripts/readme.md",
            normalized!.AbsoluteUri);

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "./contributing.md",
            CreatePinnedBase("README.md"),
            out Uri? rootRelative));
        Assert.Equal(
            $"https://github.com/owner/repo/blob/{Commit}/contributing.md",
            rootRelative!.AbsoluteUri);
    }

    [Fact]
    public void RootSlashIsCanonicalizedWithoutChangingExternalHost()
    {
        Uri readmeBase = CreatePinnedBase("README.md");

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "https://roadmap.sh",
            readmeBase,
            out Uri? normalized));
        Assert.Equal("https://roadmap.sh/", normalized!.AbsoluteUri);
    }

    [Fact]
    public void DuplicateRawAndRelativeTargetsShareOneCanonicalIdentityButRetainInstanceCounts()
    {
        Uri readmeBase = CreatePinnedBase("README.md");
        string pinnedTarget = $"https://github.com/owner/repo/blob/{Commit}/license";

        ReadmeLinkDestinationSummary summary = ReadmeLinkDestinationIdentity.Measure(
            ["./license", pinnedTarget, "./license", null, "  "],
            readmeBase);

        Assert.Equal(5, summary.LinkElementCount);
        Assert.Equal(3, summary.NonEmptyDestinationCount);
        Assert.Equal(2, summary.DistinctRawDestinationCount);
        Assert.Equal(0, summary.InvalidDestinationCount);
        Assert.Equal(1, summary.DistinctCanonicalDestinationCount);
    }

    [Fact]
    public void QueryAndFragmentArePreservedInCanonicalDestinationIdentity()
    {
        Uri readmeBase = CreatePinnedBase("README.md");

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "./guide?Tab=One#Part",
            readmeBase,
            out Uri? normalized));
        Assert.Equal(
            $"https://github.com/owner/repo/blob/{Commit}/guide?tab=one#part",
            normalized!.AbsoluteUri.ToLowerInvariant());
    }

    [Fact]
    public void InvalidSchemesAndCredentialedUrlsAreRejectedButHttpHttpsAndMailtoRemainExternal()
    {
        Uri readmeBase = CreatePinnedBase("README.md");

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "https://example.net/path",
            readmeBase,
            out Uri? https));
        Assert.Equal("https://example.net/path", https!.AbsoluteUri);

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "http://example.net/path",
            readmeBase,
            out Uri? http));
        Assert.Equal("http://example.net/path", http!.AbsoluteUri);

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "//example.net/path",
            readmeBase,
            out Uri? protocolRelative));
        Assert.Equal("https://example.net/path", protocolRelative!.AbsoluteUri);

        Assert.True(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "mailto:docs@example.net",
            readmeBase,
            out Uri? mailto));
        Assert.Equal("mailto:docs@example.net", mailto!.AbsoluteUri);

        Assert.False(ReadmeLinkDestinationIdentity.TryNormalizeDestination("javascript:alert(1)", readmeBase, out _));
        Assert.False(ReadmeLinkDestinationIdentity.TryNormalizeDestination("data:text/html,unsafe", readmeBase, out _));
        Assert.False(ReadmeLinkDestinationIdentity.TryNormalizeDestination("file:///secret", readmeBase, out _));
        Assert.False(ReadmeLinkDestinationIdentity.TryNormalizeDestination("https://user:pass@example.net/", readmeBase, out _));
        Assert.False(ReadmeLinkDestinationIdentity.TryNormalizeDestination("http://[invalid", readmeBase, out _));

        ReadmeLinkDestinationSummary summary = ReadmeLinkDestinationIdentity.Measure(
            ["https://example.net/path", "javascript:alert(1)", "file:///secret", ""],
            readmeBase);
        Assert.Equal(4, summary.LinkElementCount);
        Assert.Equal(3, summary.NonEmptyDestinationCount);
        Assert.Equal(2, summary.InvalidDestinationCount);
        Assert.Equal(1, summary.DistinctCanonicalDestinationCount);
    }

    [Fact]
    public void PinnedBaseRequiresRepositoryCommitAndSafeReadmePath()
    {
        Assert.False(ReadmeLinkDestinationIdentity.TryCreatePinnedReadmeBaseUri(
            "owner/repo/extra", Commit, "README.md", out _));
        Assert.False(ReadmeLinkDestinationIdentity.TryCreatePinnedReadmeBaseUri(
            "owner/repo", "not-a-commit", "README.md", out _));
        Assert.False(ReadmeLinkDestinationIdentity.TryCreatePinnedReadmeBaseUri(
            "owner/repo", Commit, "../README.md", out _));
        Assert.False(ReadmeLinkDestinationIdentity.TryNormalizeDestination(
            "./guide",
            new Uri("https://example.net/README.md"),
            out _));
    }

    private static Uri CreatePinnedBase(string path)
    {
        Assert.True(ReadmeLinkDestinationIdentity.TryCreatePinnedReadmeBaseUri(
            "owner/repo",
            Commit,
            path,
            out Uri? readmeBase));
        return readmeBase!;
    }
}
