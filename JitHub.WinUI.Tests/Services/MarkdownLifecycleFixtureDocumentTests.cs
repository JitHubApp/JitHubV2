using System;
using System.IO;
using System.Text;
using JitHub.Services.Markdown.Automation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownLifecycleFixtureDocumentTests : IDisposable
{
    private const string HostMarkdown = "# Host README\n\nHost content.";
    private const string ReadmeHost = "MarkdownHost_RepositoryReadme";
    private const string ReadmeAutomationId = "MarkdownHost_RepositoryReadme_RepoCodeReadme";
    private const string LifecycleFinalMarker = "Lifecycle long document final marker.";
    private const string SecurityFinalMarker = "Lifecycle security fixture final marker.";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "JitHubMarkdownLifecycleFixture",
        Guid.NewGuid().ToString("N"));

    public MarkdownLifecycleFixtureDocumentTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void HostOutsideTheTargetPrefix_RendersItsOwnMarkdown()
    {
        var document = new MarkdownLifecycleFixtureDocument(ReadmeHost, corpusPath: null, includeSecurityFixture: true);

        Assert.Equal(HostMarkdown, document.Compose(HostMarkdown, "MarkdownHost_EditorPreview_IssueComposer"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(ReadmeHost)]
    public void TargetedHost_PrependsTheLifecycleFixture(string? targetHost)
    {
        var document = new MarkdownLifecycleFixtureDocument(targetHost, corpusPath: null, includeSecurityFixture: false);

        string composed = document.Compose(HostMarkdown, ReadmeAutomationId);

        Assert.Equal(MarkdownLifecycleFixtureText.BuildLifecycleFixtureMarkdown() + HostMarkdown, composed);
        Assert.Contains("Markdown host lifecycle fixture", composed, StringComparison.Ordinal);
        Assert.Contains(LifecycleFinalMarker, composed, StringComparison.Ordinal);
        Assert.DoesNotContain(SecurityFinalMarker, composed, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetPrefix_IsCaseSensitive()
    {
        var document = new MarkdownLifecycleFixtureDocument(
            ReadmeHost.ToLowerInvariant(),
            corpusPath: null,
            includeSecurityFixture: false);

        Assert.Equal(HostMarkdown, document.Compose(HostMarkdown, ReadmeAutomationId));
    }

    [Fact]
    public void MarkdownThatAlreadyCarriesTheFixture_IsNotWrappedAgain()
    {
        var document = new MarkdownLifecycleFixtureDocument(targetHost: null, corpusPath: null, includeSecurityFixture: true);

        string once = document.Compose(HostMarkdown, ReadmeAutomationId);

        Assert.Equal(once, document.Compose(once, ReadmeAutomationId));
    }

    [Fact]
    public void SecurityFixture_FollowsTheLifecycleFixtureAndPrecedesHostMarkdown()
    {
        var document = new MarkdownLifecycleFixtureDocument(targetHost: null, corpusPath: null, includeSecurityFixture: true);

        string composed = document.Compose(HostMarkdown, ReadmeAutomationId);

        Assert.Equal(
            MarkdownLifecycleFixtureText.BuildLifecycleFixtureMarkdown() +
                MarkdownLifecycleFixtureText.BuildSecurityLifecycleFixtureMarkdown() +
                HostMarkdown,
            composed);
        Assert.Contains("Lifecycle oversized SVG", composed, StringComparison.Ordinal);
        Assert.True(
            composed.IndexOf(LifecycleFinalMarker, StringComparison.Ordinal) <
            composed.IndexOf(SecurityFinalMarker, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("corpus.md")]
    [InlineData("corpus.MARKDOWN")]
    public void MarkdownCorpusFile_ReplacesHostMarkdownAndFixtures(string fileName)
    {
        string path = WriteCorpus(fileName, "# Corpus\n\nCorpus body.");
        var document = new MarkdownLifecycleFixtureDocument(targetHost: null, path, includeSecurityFixture: true);

        Assert.Equal("# Corpus\n\nCorpus body.", document.Compose(HostMarkdown, ReadmeAutomationId));
    }

    [Fact]
    public void CorpusFile_IsReadOnEveryCompose()
    {
        string path = WriteCorpus("corpus.md", "first");
        var document = new MarkdownLifecycleFixtureDocument(targetHost: null, path, includeSecurityFixture: false);
        Assert.Equal("first", document.Compose(HostMarkdown, ReadmeAutomationId));

        File.WriteAllText(path, "second", Encoding.UTF8);

        Assert.Equal("second", document.Compose(HostMarkdown, ReadmeAutomationId));
    }

    [Fact]
    public void CorpusFile_DoesNotReplaceHostsOutsideTheTarget()
    {
        string path = WriteCorpus("corpus.md", "# Corpus");
        var document = new MarkdownLifecycleFixtureDocument(ReadmeHost, path, includeSecurityFixture: false);

        Assert.Equal(HostMarkdown, document.Compose(HostMarkdown, "MarkdownHost_EditorPreview_IssueComposer"));
    }

    [Theory]
    [InlineData("corpus.txt", "# Not Markdown")]
    [InlineData("corpus.md", "")]
    [InlineData("missing.md", null)]
    public void UnusableCorpusFile_FallsBackToTheLifecycleFixture(string fileName, string? contents)
    {
        string path = contents is null
            ? Path.Combine(_directory, fileName)
            : WriteCorpus(fileName, contents);
        var document = new MarkdownLifecycleFixtureDocument(targetHost: null, path, includeSecurityFixture: false);

        Assert.Equal(
            MarkdownLifecycleFixtureText.BuildLifecycleFixtureMarkdown() + HostMarkdown,
            document.Compose(HostMarkdown, ReadmeAutomationId));
    }

    [Fact]
    public void CorpusFileOverFourMebibytes_FallsBackToTheLifecycleFixture()
    {
        string path = WriteCorpus("oversized.md", new string('a', (4 * 1024 * 1024) + 1));
        var document = new MarkdownLifecycleFixtureDocument(targetHost: null, path, includeSecurityFixture: false);

        Assert.Equal(
            MarkdownLifecycleFixtureText.BuildLifecycleFixtureMarkdown() + HostMarkdown,
            document.Compose(HostMarkdown, ReadmeAutomationId));
    }

    [Fact]
    public void CorpusFileAtExactlyFourMebibytes_IsAccepted()
    {
        string contents = new('a', 4 * 1024 * 1024);
        string path = WriteCorpus("limit.md", contents);
        var document = new MarkdownLifecycleFixtureDocument(targetHost: null, path, includeSecurityFixture: false);

        Assert.Equal(contents, document.Compose(HostMarkdown, ReadmeAutomationId));
    }

    private string WriteCorpus(string fileName, string contents)
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
