using System.Text;
using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using MarkdownDocument = Markdig.Syntax.MarkdownDocument;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class FlattenContainerParityTests
{
    public static IEnumerable<object[]> MarkdownCases
    {
        get
        {
            yield return
            [
                "prefix **bold *nested* e\u0301 😀 אבג &amp;** " +
                "[linked _inner_ first\nsecond](https://example.test) " +
                "![alt **bold** e\u0301 😀 אבג &amp;](https://example.test/image.png) " +
                "  \nhard break and soft\ncontinuation",
                false,
            ];
            yield return
            [
                "~~strike with `code`~~ and ==marked *nested*== " +
                "![emoji :rocket: and &amp;](https://example.test/emoji.png)",
                true,
            ];
        }
    }

    [Theory]
    [MemberData(nameof(MarkdownCases))]
    public void FlattenedWrappersMatchLegacyTextAndFullSourceMapRanges(
        string source,
        bool useGitHubReadme)
    {
        WrapperExpectation[] expectedWrappers = CaptureLegacyWrapperExpectations(source, useGitHubReadme);
        Assert.NotEmpty(expectedWrappers);

        string equalButDistinctSource = new(source.ToCharArray());
        Assert.NotSame(source, equalButDistinctSource);

        using BuildFixture exact = Build(source, source, useGitHubReadme);
        using BuildFixture fallback = Build(source, equalButDistinctSource, useGitHubReadme);

        InlineRun[] exactRuns = GetInlineRuns(exact.Snapshot);
        InlineRun[] fallbackRuns = GetInlineRuns(fallback.Snapshot);
        AssertWrappersMatchLegacy(exactRuns, expectedWrappers);
        AssertWrappersMatchLegacy(fallbackRuns, expectedWrappers);

        Assert.Equal(exact.Snapshot.SemanticDocument.Text, fallback.Snapshot.SemanticDocument.Text);
        Assert.Equal(
            exactRuns.Select(ToRunSnapshot),
            fallbackRuns.Select(ToRunSnapshot));
        Assert.Equal(
            CaptureSelectableMappings(exact.Snapshot, exactRuns),
            CaptureSelectableMappings(fallback.Snapshot, fallbackRuns));
    }

    [Fact]
    public void FlattenFallsBackForSyntheticLiteralContentOutsideTheSourceSlice()
    {
        const string source = "before **provider-owned literal** after";
        MarkdownExtensionRegistry registry = CreateRegistry(useGitHubReadme: false);
        MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());
        ParagraphBlock paragraph = Assert.Single(document.OfType<ParagraphBlock>());
        EmphasisInline emphasis = Assert.Single(paragraph.Inline!.OfType<EmphasisInline>());
        LiteralInline literal = Assert.Single(emphasis.OfType<LiteralInline>());

        string originalContent = literal.Content.ToString();
        string providerText = new(originalContent.ToCharArray());
        Assert.NotSame(literal.Content.Text, providerText);
        literal.Content = new StringSlice(providerText);

        string expectedLegacyText = LegacyFlatten(emphasis);
        Assert.Equal(originalContent, expectedLegacyText);

        using BuildFixture fixture = Build(source, source, registry, document);
        InlineRun[] runs = GetInlineRuns(fixture.Snapshot);
        StrongRun strong = Assert.IsType<StrongRun>(Assert.Single(
            runs,
            run => run.SourceSpan == new SourceSpan(emphasis.Span.Start, emphasis.Span.Length)));

        Assert.Equal(expectedLegacyText, strong.Text);
        Assert.Equal(source, fixture.Snapshot.SourceMap.SourceText);
        Assert.Equal("before provider-owned literal after", fixture.Snapshot.SemanticDocument.Text);
    }

    private static WrapperExpectation[] CaptureLegacyWrapperExpectations(
        string source,
        bool useGitHubReadme)
    {
        MarkdownExtensionRegistry registry = CreateRegistry(useGitHubReadme);
        MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());
        ParagraphBlock paragraph = Assert.Single(document.OfType<ParagraphBlock>());
        var result = new List<WrapperExpectation>();

        foreach (Inline inline in paragraph.Inline!)
        {
            switch (inline)
            {
                case EmphasisInline emphasis:
                    result.Add(new WrapperExpectation(
                        WrapperKind.Emphasis,
                        new SourceSpan(emphasis.Span.Start, emphasis.Span.Length),
                        LegacyFlatten(emphasis)));
                    break;
                case LinkInline { IsImage: true } image:
                    result.Add(new WrapperExpectation(
                        WrapperKind.Image,
                        new SourceSpan(image.Span.Start, image.Span.Length),
                        LegacyFlatten(image)));
                    break;
                case LinkInline link:
                    result.Add(new WrapperExpectation(
                        WrapperKind.Link,
                        new SourceSpan(link.Span.Start, link.Span.Length),
                        LegacyFlatten(link)));
                    break;
            }
        }

        return result.ToArray();
    }

    // This deliberately retains the pre-optimization recursive implementation
    // as an independent oracle for the rendered wrapper text.
    private static string LegacyFlatten(ContainerInline container)
    {
        var builder = new StringBuilder();
        LegacyFlatten(container, builder);
        return builder.ToString();
    }

    private static void LegacyFlatten(ContainerInline container, StringBuilder builder)
    {
        foreach (Inline child in container)
        {
            switch (child)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case AbbreviationInline abbreviation:
                    builder.Append(abbreviation.Abbreviation?.Label ?? string.Empty);
                    break;
                case LineBreakInline lineBreak:
                    builder.Append(lineBreak.IsHard ? '\n' : ' ');
                    break;
                // P3-008/P3-009: the original implementation dropped these,
                // which lost "&" from "Q &amp; A" and autolink text. The
                // oracle keeps the recursive shape with the corrected rule.
                case HtmlEntityInline entity:
                    builder.Append(entity.Transcoded.ToString());
                    break;
                case AutolinkInline autolink:
                    builder.Append(autolink.Url);
                    break;
                case LinkInline { IsImage: true } image:
                    LegacyFlatten(image, builder);
                    break;
                case ContainerInline nested:
                    LegacyFlatten(nested, builder);
                    break;
            }
        }
    }

    private static void AssertWrappersMatchLegacy(
        IReadOnlyList<InlineRun> runs,
        IReadOnlyList<WrapperExpectation> expectations)
    {
        foreach (WrapperExpectation expected in expectations)
        {
            InlineRun run = Assert.Single(runs, candidate =>
                candidate.SourceSpan == expected.Span && MatchesKind(candidate, expected.Kind));

            string actual = run is InlineImageRun image ? image.AltText : run.Text;
            Assert.Equal(expected.Text, actual);
        }
    }

    private static bool MatchesKind(InlineRun run, WrapperKind kind) => kind switch
    {
        WrapperKind.Emphasis => run is EmphasisRun or StrongRun or StrikethroughRun or
            SubscriptRun or SuperscriptRun or InsertedRun or MarkedRun,
        WrapperKind.Link => run is LinkRun,
        WrapperKind.Image => run is InlineImageRun,
        _ => false,
    };

    private static InlineRun[] GetInlineRuns(LayoutSnapshot snapshot) => snapshot.Blocks
        .OfType<InlineContainerBox>()
        .SelectMany(static box => box.Runs)
        .ToArray();

    private static RunSnapshot ToRunSnapshot(InlineRun run) => new(
        run.InlineIndex,
        run.Text,
        run.LayoutText,
        run.AccessibleText,
        run.RenderedLength,
        run.SourceSpan,
        run.ElementKey);

    private static List<SourceRangeSnapshot> CaptureSelectableMappings(
        LayoutSnapshot snapshot,
        IReadOnlyList<InlineRun> runs)
    {
        var mappings = new List<SourceRangeSnapshot>();
        foreach (InlineRun run in runs)
        {
            if (run.RenderedLength == 0)
                continue;

            InlineContainerBox owner = Assert.Single(
                snapshot.Blocks.OfType<InlineContainerBox>(),
                box => box.Runs.Contains(run));

            AddMapping(0, run.RenderedLength);
            for (int offset = 0; offset < run.RenderedLength; offset++)
                AddMapping(offset, offset + 1);

            void AddMapping(int start, int end)
            {
                var range = new DocumentRange(
                    new DocumentPosition(owner.BlockIndex, run.InlineIndex, start),
                    new DocumentPosition(owner.BlockIndex, run.InlineIndex, end));
                bool mapped = snapshot.SourceMap.TryMapRange(range, out SourceSpan sourceSpan);
                mappings.Add(new SourceRangeSnapshot(
                    owner.BlockIndex,
                    run.InlineIndex,
                    start,
                    end,
                    mapped,
                    sourceSpan,
                    snapshot.SourceMap.Slice(range)));
            }
        }

        return mappings;
    }

    private static BuildFixture Build(
        string source,
        string sourceMapText,
        bool useGitHubReadme)
    {
        MarkdownExtensionRegistry registry = CreateRegistry(useGitHubReadme);
        MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());
        return Build(source, sourceMapText, registry, document);
    }

    private static BuildFixture Build(
        string source,
        string sourceMapText,
        MarkdownExtensionRegistry registry,
        MarkdownDocument document)
    {
        var device = new CanvasDevice(forceSoftwareRenderer: true);
        try
        {
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.Strong] = body,
                [MarkdownElementKeys.Emphasis] = body,
                [MarkdownElementKeys.CodeInline] = body,
                [MarkdownElementKeys.Heading1] = body,
                [MarkdownElementKeys.Heading2] = body,
                [MarkdownElementKeys.Heading3] = body,
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
                textScaleFactor: 1f);
            MarkdownLayoutContext context = new(
                device,
                theme,
                new MarkdownSourceMap(sourceMapText),
                registry,
                FlowDirection.LeftToRight,
                language: "en-US");
            LayoutSnapshot snapshot = new LayoutBuilder(context).Build(document, availableWidth: 480f);
            return new BuildFixture(device, snapshot);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static MarkdownExtensionRegistry CreateRegistry(bool useGitHubReadme)
    {
        var registry = new MarkdownExtensionRegistry();
        registry.ConfigurePipeline(static pipeline => pipeline.UsePreciseSourceLocation());
        if (useGitHubReadme)
            registry.ConfigureGitHubReadmeRegistry();
        return registry;
    }

    private enum WrapperKind
    {
        Emphasis,
        Link,
        Image,
    }

    private sealed record WrapperExpectation(WrapperKind Kind, SourceSpan Span, string Text);

    private readonly record struct RunSnapshot(
        int InlineIndex,
        string Text,
        string LayoutText,
        string AccessibleText,
        int RenderedLength,
        SourceSpan SourceSpan,
        string ElementKey);

    private readonly record struct SourceRangeSnapshot(
        int BlockIndex,
        int InlineIndex,
        int Start,
        int End,
        bool Mapped,
        SourceSpan SourceSpan,
        string SourceSlice);

    private sealed class BuildFixture(CanvasDevice device, LayoutSnapshot snapshot) : IDisposable
    {
        public LayoutSnapshot Snapshot { get; } = snapshot;

        public void Dispose()
        {
            try
            {
                Snapshot.Dispose();
            }
            finally
            {
                device.Dispose();
            }
        }
    }
}
