using System.Threading;
using Markdig.Helpers;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig;
using Markdig.Syntax.Inlines;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MarkdownSourceProjectionTests
{
    [Fact]
    public void RangeEndingAtNextSegmentStartUsesPreviousSegmentEndpoint()
    {
        MarkdownSourceProjection projection = CreateProjectionWithTrimmedSourceGap();

        Assert.True(projection.TryMapRange(0, 4, out SourceSpan wholePrefix));
        Assert.Equal(new SourceSpan(10, 5), wholePrefix);
    }

    [Fact]
    public void RangeStartingAtPreviousSegmentEndUsesFollowingSegmentStart()
    {
        MarkdownSourceProjection projection = CreateProjectionWithTrimmedSourceGap();

        Assert.True(projection.TryMapRange(3, 1, out SourceSpan newline));
        Assert.Equal(new SourceSpan(13, 2), newline);
    }

    [Fact]
    public void RangeBeginningAtNextSegmentStartSkipsTrimmedSourceGap()
    {
        MarkdownSourceProjection projection = CreateProjectionWithTrimmedSourceGap();

        Assert.True(projection.TryMapRange(4, 1, out SourceSpan followingText));
        Assert.Equal(new SourceSpan(19, 1), followingText);
    }

    [Fact]
    public void ApplyingProjectionMapsLinkDestinationAndTitleSpans()
    {
        const string source = "[label](https://example.test \"title\")";
        var pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, pipeline);
        Markdig.Syntax.ParagraphBlock paragraph =
            Assert.IsType<Markdig.Syntax.ParagraphBlock>(Assert.Single(document));
        LinkInline link = Assert.IsType<LinkInline>(Assert.Single(paragraph.Inline!));
        Markdig.Syntax.SourceSpan originalUrlSpan = link.UrlSpan;
        Markdig.Syntax.SourceSpan originalTitleSpan = link.TitleSpan;
        Assert.False(originalUrlSpan.IsEmpty);
        Assert.False(originalTitleSpan.IsEmpty);

        var builder = new MarkdownSourceProjectionBuilder();
        builder.AppendMapped(source, new SourceSpan(100, source.Length));
        builder.Build().ApplyTo(document);

        Assert.Equal(originalUrlSpan.Start + 100, link.UrlSpan.Start);
        Assert.Equal(originalUrlSpan.End + 100, link.UrlSpan.End);
        Assert.Equal(originalTitleSpan.Start + 100, link.TitleSpan.Start);
        Assert.Equal(originalTitleSpan.End + 100, link.TitleSpan.End);
    }

    [Fact]
    public void HtmlLineProjectionPreservesInternalSourceGaps()
    {
        const string source = "<div>abcd</div>";
        var pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, pipeline);
        Markdig.Syntax.HtmlBlock htmlBlock =
            Assert.IsType<Markdig.Syntax.HtmlBlock>(Assert.Single(document));

        var builder = new MarkdownSourceProjectionBuilder();
        builder.AppendMapped("<div>ab", new SourceSpan(10, 7));
        builder.AppendMapped("cd</div>", new SourceSpan(30, 8));
        MarkdownSourceProjection projection = builder.Build();
        projection.ApplyTo(document);

        Assert.True(projection.TryGetHtmlBlockProjection(
            htmlBlock,
            htmlBlock.Lines.ToString(),
            CancellationToken.None,
            out MarkdownSourceProjection htmlProjection));
        Assert.True(htmlProjection.TryMapRange(7, 2, out SourceSpan imageLabel));
        Assert.Equal(new SourceSpan(30, 2), imageLabel);
    }

    [Fact]
    public void HtmlLineProjectionIgnoresUnusedLineGroupBufferEntries()
    {
        const string source = "<div>\n</div>";
        var pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, pipeline);
        Markdig.Syntax.HtmlBlock htmlBlock =
            Assert.IsType<Markdig.Syntax.HtmlBlock>(Assert.Single(document));
        Markdig.Syntax.MarkdownDocument extraDocument = Markdown.Parse("<aside>unused</aside>", pipeline);
        Markdig.Syntax.HtmlBlock extraBlock =
            Assert.IsType<Markdig.Syntax.HtmlBlock>(Assert.Single(extraDocument));

        StringLine[] lineBuffer = Assert.IsType<StringLine[]>(htmlBlock.Lines.Lines);
        StringLine[] extraLineBuffer = Assert.IsType<StringLine[]>(extraBlock.Lines.Lines);
        Assert.True(lineBuffer.Length > htmlBlock.Lines.Count);
        Assert.True(extraBlock.Lines.Count > 0);
        lineBuffer[htmlBlock.Lines.Count] = extraLineBuffer[0];

        var builder = new MarkdownSourceProjectionBuilder();
        builder.AppendMapped(source, new SourceSpan(40, source.Length));
        MarkdownSourceProjection projection = builder.Build();
        projection.ApplyTo(document);

        Assert.True(projection.TryGetHtmlBlockProjection(
            htmlBlock,
            htmlBlock.Lines.ToString(),
            CancellationToken.None,
            out MarkdownSourceProjection htmlProjection));
        Assert.True(htmlProjection.TryMapRange(0, source.Length, out SourceSpan mapped));
        Assert.Equal(new SourceSpan(40, source.Length), mapped);
    }

    [Fact]
    public async Task SummaryProjectionMapsCarriageReturnToItsAuthoredSourceUnit()
    {
        const string source = "<details>\r<summary>first\rsecond</summary>\r</details>";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();

        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed =
            Assert.IsType<Markdig.Syntax.MarkdownDocument>(document.ParsedDocument);
        GitHubDetailsBlock details = Assert.Single(parsed.OfType<GitHubDetailsBlock>());
        Assert.Equal("first\nsecond", details.SummaryMarkup);

        Assert.True(details.CreateSummarySourceProjection().TryMapRange(5, 1, out SourceSpan mapped));
        int authoredCarriageReturn = source.IndexOf('\r', source.IndexOf("first", StringComparison.Ordinal) + 5);
        Assert.Equal(new SourceSpan(authoredCarriageReturn, 1), mapped);
    }

    [Fact]
    public async Task SummaryProjectionUsesNearestMixedLineBreak()
    {
        const string source = "<details>\n<summary>first\nsecond\rthird</summary>\n</details>";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();

        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed =
            Assert.IsType<Markdig.Syntax.MarkdownDocument>(document.ParsedDocument);
        GitHubDetailsBlock details = Assert.Single(parsed.OfType<GitHubDetailsBlock>());
        Assert.Equal("first\nsecond\nthird", details.SummaryMarkup);

        Assert.True(details.CreateSummarySourceProjection().TryMapRange(12, 1, out SourceSpan mapped));
        int authoredCarriageReturn = source.IndexOf('\r', source.IndexOf("second", StringComparison.Ordinal) + 6);
        Assert.Equal(new SourceSpan(authoredCarriageReturn, 1), mapped);
    }

    [Fact]
    public void ApplyingProjectionHonorsPreCanceledToken()
    {
        var pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse("text", pipeline);
        var builder = new MarkdownSourceProjectionBuilder();
        builder.AppendMapped("text", new SourceSpan(20, 4));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            builder.Build().ApplyTo(document, cancellation.Token));
    }

    [Fact]
    public void NestedSafeHtmlProjectionScopesClearAndRestoreAcrossFailure()
    {
        MarkdownLayoutContext context = CreateContext();
        MarkdownSourceProjection outer = CreateOffsetProjection(30);
        MarkdownSourceProjection inner = CreateOffsetProjection(70);

        Assert.Null(context.SafeHtmlSourceProjection);
        Action exerciseScopes = () =>
        {
            using (context.PushSafeHtmlSourceProjection(outer))
            {
                Assert.Same(outer, context.SafeHtmlSourceProjection);
                Assert.Equal(new SourceSpan(30, 1), context.MapSafeHtmlSourceRange(0, 1));

                using (context.PushSafeHtmlSourceProjection(inner))
                {
                    Assert.Same(inner, context.SafeHtmlSourceProjection);
                    Assert.Equal(new SourceSpan(70, 1), context.MapSafeHtmlSourceRange(0, 1));

                    using (context.PushSafeHtmlSourceProjection(null))
                    {
                        Assert.Null(context.SafeHtmlSourceProjection);
                        Assert.Equal(new SourceSpan(0, 1), context.MapSafeHtmlSourceRange(0, 1));
                    }

                    Assert.Same(inner, context.SafeHtmlSourceProjection);
                }

                Assert.Same(outer, context.SafeHtmlSourceProjection);
                throw new InvalidOperationException("Exercise exceptional scope restoration.");
            }
        };
        Assert.Throws<InvalidOperationException>(exerciseScopes);

        Assert.Null(context.SafeHtmlSourceProjection);
        Assert.Equal(new SourceSpan(0, 1), context.MapSafeHtmlSourceRange(0, 1));
    }

    private static MarkdownSourceProjection CreateProjectionWithTrimmedSourceGap()
    {
        var builder = new MarkdownSourceProjectionBuilder();
        builder.AppendMapped("abc", new SourceSpan(10, 3));
        builder.AppendMapped("\n", new SourceSpan(13, 2));
        builder.AppendMapped("defgh", new SourceSpan(19, 5));
        return builder.Build();
    }

    private static MarkdownSourceProjection CreateOffsetProjection(int sourceStart)
    {
        var builder = new MarkdownSourceProjectionBuilder();
        builder.AppendMapped("x", new SourceSpan(sourceStart, 1));
        return builder.Build();
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 256)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
