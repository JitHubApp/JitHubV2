using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using System.Text;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Checks the engine contract: built-in profiles, immutable UTF-16
/// documents, strict raw HTML, cancellation and parse limits, the
/// extension registry, and GFM 0.29 source ranges.
/// </summary>
public sealed class MarkdownEngineTests
{
    [Fact]
    public void BuiltInProfiles_AreExplicitAndStandardsVersioned()
    {
        Assert.Equal("commonmark-0.31.2", MarkdownProfiles.CommonMark.Id);
        Assert.Equal("https://spec.commonmark.org/0.31.2/", MarkdownProfiles.CommonMark.Specification);
        Assert.Equal("gfm-0.29", MarkdownProfiles.GfmStrict.Id);
        Assert.Equal("https://github.github.com/gfm/", MarkdownProfiles.GfmStrict.Specification);
        Assert.Contains("task-lists", MarkdownProfiles.GfmStrict.Features);
        Assert.Contains("disallowed-raw-html", MarkdownProfiles.GfmStrict.Features);
        Assert.Contains("footnotes", MarkdownProfiles.GitHubReadme.Features);
        Assert.Contains("definition-lists", MarkdownProfiles.MarkdownExtra.Features);
    }

    [Fact]
    public async Task ParseAsync_ReturnsReusableImmutableUtf16Document()
    {
        const string source = "😀 prefix\n\n# Heading\n\n[link](https://example.test)";
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.CommonMark)
            .Build();

        var document = await engine.ParseAsync(source);

        Assert.Equal(source, document.Source);
        Assert.NotNull(document.ParsedDocument);
        Assert.Single(document.GetHeadings());
        var link = Assert.Single(document.GetLinks());
        Assert.Equal(source.IndexOf("[link]", StringComparison.Ordinal), link.SourceSpan.Start);
        Assert.Equal("[link](https://example.test)", source.Substring(link.SourceSpan.Start, link.SourceSpan.Length));
    }

    [Fact]
    public async Task StrictProfiles_DoNotExposeRawHtmlNodes()
    {
        const string source = "<div>unsafe</div>";
        var commonMark = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.CommonMark)
            .Build()
            .ParseAsync(source);
        var gfm = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .Build()
            .ParseAsync(source);

        Assert.DoesNotContain(commonMark.ParsedDocument!, static block => block is HtmlBlock);
        Assert.DoesNotContain(gfm.ParsedDocument!, static block => block is HtmlBlock);
    }

    [Fact]
    public async Task ParseAsync_ObservesPreCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MarkdownEngine.Default.ParseAsync("# ignored", cancellation.Token));
    }

    [Fact]
    public void ParseLimits_AreValidatedAndFrozenIntoEngine()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MarkdownParseLimits(maximumConcurrentParseCount: 2, maximumOutstandingParseCount: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MarkdownParseLimits(maximumSourceLength: 100, maximumOutstandingSourceBytes: 199));

        var limits = new MarkdownParseLimits(
            maximumSourceLength: 100,
            maximumConcurrentParseCount: 1,
            maximumOutstandingParseCount: 3,
            maximumOutstandingSourceBytes: 200);
        MarkdownEngine engine = new MarkdownEngineBuilder().WithParseLimits(limits).Build();

        Assert.Same(limits, engine.ParseLimits);
    }

    [Fact]
    public async Task ParseAsync_RejectsSourceBeyondTheConfiguredMaximum()
    {
        var engine = new MarkdownEngineBuilder()
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 4,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 1,
                maximumOutstandingSourceBytes: 8))
            .Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.ParseAsync("12345"));
        Assert.Equal(0, engine.OutstandingParseCount);
    }

    [Fact]
    public async Task Gfm029_TableContinuationAndFollowingSourceRangesRemainExact()
    {
        const string source = """
            | abc | def |
            | --- | --- |
            | one | two |
            three

            # Heading

            [link](https://example.test)
            """;
        var engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build();

        var document = await engine.ParseAsync(source);

        var table = Assert.IsType<Table>(document.ParsedDocument![0]);
        Assert.Equal(3, table.Count);
        var continuation = Assert.IsType<TableRow>(table[2]);
        var firstCell = Assert.IsType<TableCell>(continuation[0]);
        var paragraph = Assert.IsType<ParagraphBlock>(firstCell[0]);
        Assert.Equal("three", Assert.IsType<LiteralInline>(paragraph.Inline!.FirstChild).Content.ToString());
        var heading = Assert.Single(document.GetHeadings());
        var link = Assert.Single(document.GetLinks());
        Assert.Equal(source.IndexOf("# Heading", StringComparison.Ordinal), heading.SourceSpan.Start);
        Assert.Equal(source.IndexOf("[link]", StringComparison.Ordinal), link.SourceSpan.Start);
    }

    [Fact]
    public async Task Gfm029_MismatchedTableHeaderStaysLiteralWithoutMaskCharacters()
    {
        const string source = "| abc | def |\n| --- |\n| bar |";
        var document = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build()
            .ParseAsync(source);

        Assert.IsType<ParagraphBlock>(document.ParsedDocument![0]);
        Assert.DoesNotContain(document.ParsedDocument!, static block => block is Table);
        Assert.DoesNotContain('\uE000', CollectInlineText(
            Assert.IsType<ParagraphBlock>(document.ParsedDocument![0]).Inline!));
    }

    [Fact]
    public async Task Gfm029_AutolinkEmailPreservesPunctuationAndExactSourceSpan()
    {
        const string email = "foo@bar.baz";
        const string source = "Contact foo@bar.baz, please.";
        var document = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build()
            .ParseAsync(source);

        var link = Assert.Single(document.GetLinks());
        Assert.Equal(email, link.DisplayText);
        Assert.Equal("mailto:" + email, link.Url);
        Assert.Equal(source.IndexOf(email, StringComparison.Ordinal), link.SourceSpan.Start);
        Assert.Equal(email.Length, link.SourceSpan.Length);
        Assert.Equal(',', source[link.SourceSpan.Start + link.SourceSpan.Length]);
    }

    [Fact]
    public async Task Gfm029_NoEmailLiteralStaysUnlinkedAndUnchanged()
    {
        const string source = "Ordinary literal text has no email address.";
        var document = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build()
            .ParseAsync(source);

        Assert.Empty(document.GetLinks());
        Assert.Equal(
            source,
            CollectInlineText(Assert.IsType<ParagraphBlock>(document.ParsedDocument![0]).Inline!));
    }

    [Fact]
    public async Task Gfm029_AutolinkEmailRejectsTrailingDomainUnderscore()
    {
        const string source = "foo@bar.baz\n\nfoo@bar.baz_";
        var document = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build()
            .ParseAsync(source);

        var link = Assert.Single(document.GetLinks());
        Assert.Equal("foo@bar.baz", link.DisplayText);
        Assert.Equal("mailto:foo@bar.baz", link.Url);
        Assert.DoesNotContain(
            document.GetLinks(),
            value => value.SourceSpan.Start == source.LastIndexOf("foo", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionRegistry_IsImmutableAndUsesExactKindDispatch()
    {
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension())
            .Build();

        Assert.True(engine.Extensions.ContainsExtension("tests.extension"));
        Assert.True(engine.Extensions.TryGetBlockRenderer("tests.block", out var renderer));
        Assert.False(engine.Extensions.TryGetBlockRenderer("tests", out _));

        var content = new MarkdownContentBuilder();
        renderer!(
            new MarkdownExtensionContext(new MarkdownSyntaxNode("tests.block", new SourceSpan(0, 1), "x")),
            content);
        var fragment = content.Build();
        Assert.Single(fragment.Items);
        Assert.Equal(MarkdownContentKind.Text, fragment.Items[0].Kind);
    }

    [Fact]
    public void LegacySynchronousLookupNeverWrapsAsyncRegistrations()
    {
        var builder = new MarkdownExtensionBuilder();
        builder.RegisterBlockAsync("tests.async", static (_, _) =>
            new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan)));
        builder.RegisterInline("tests.mixed", static (_, _) => { });
        builder.RegisterInlineAsync("tests.mixed", static (_, _) => ValueTask.CompletedTask);
        MarkdownExtensionSet extensions = builder.Build();

        Assert.False(extensions.TryGetBlockRenderer("tests.async", out _));
        Assert.False(extensions.TryGetInlineRenderer("tests.mixed", out _));
        Assert.True(extensions.TryGetAsyncBlockRenderer("tests.async", out _));
        Assert.True(extensions.TryGetAsyncInlineRenderer("tests.mixed", out _));
    }

    private sealed class TestExtension : IMarkdownExtension
    {
        public string Id => "tests.extension";

        public void Configure(MarkdownExtensionBuilder builder)
        {
            builder.RegisterBlock("tests.block", static (context, content) =>
                content.AddText(
                    context.Node.Literal ?? string.Empty,
                    context.Node.SourceSpan,
                    MarkdownStyleRole.Body));
        }
    }

    private static string CollectInlineText(ContainerInline container)
    {
        var builder = new StringBuilder();
        for (Inline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (inline is LiteralInline literal)
                builder.Append(literal.Content.ToString());
            if (inline is ContainerInline nested)
                builder.Append(CollectInlineText(nested));
        }
        return builder.ToString();
    }
}
