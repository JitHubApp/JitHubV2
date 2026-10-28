using System.Reflection;
using System.Text;
using Markdig;
using MarkdownRenderer.Accessibility;
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

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class DirectLiteralTextRunTests
{
    public static IEnumerable<object[]> DifferentialCorpus
    {
        get
        {
            yield return new object[]
            {
                "ordinary 😀 e\u0301 RTL אבג\nsoft continuation",
                false,
                // Markdig joins this multiline paragraph into an equal but
                // distinct backing string; the exact-identity guard must fall back.
                false,
            };
            yield return new object[]
            {
                "escaped \\*literal\\* &amp; **strong** `code`",
                false,
                true,
            };
            yield return new object[]
            {
                "soft\nbreak and hard  \nbreak\n\nnext paragraph",
                false,
                true,
            };
            yield return new object[]
            {
                "~~strike~~ ++inserted++ ==marked==",
                true,
                true,
            };
            yield return new object[]
            {
                "emoji :rocket: and 🧪",
                true,
                true,
            };
            yield return new object[]
            {
                "<p>prefix <strong>bold</strong> and " +
                "<strong>strong <a href='https://example.test'>linked</a> tail</strong> suffix</p>",
                true,
                false,
            };
        }
    }

    [Fact]
    public void BuilderUsesOnlyExactDirectSourceSlicesAndPreservesTextAndSelectionProjection()
    {
        const string source = @"literal &amp; \*escaped\* **strong** tail";
        using BuildFixture fixture = Build(source);
        InlineContainerBox paragraph = Assert.Single(fixture.Snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun[] runs = paragraph.Runs.ToArray();
        SourceSliceTextRun[] directSlices = runs.OfType<SourceSliceTextRun>().ToArray();

        Assert.NotEmpty(directSlices);
        Assert.All(directSlices, static slice => Assert.Equal(string.Empty, ReadTextCache(slice)));
        foreach (SourceSliceTextRun slice in directSlices)
        {
            Assert.Same(source, slice.SourceText);
            Assert.Equal(slice.SourceStart, slice.SourceSpan.Start);
            Assert.Equal(slice.SliceLength, slice.SourceSpan.Length);
            Assert.Equal(source.AsSpan(slice.SourceStart, slice.SliceLength).ToString(), slice.Text);
        }

        Assert.Contains(runs, run => run is StrongRun { Text: "strong" });
        TextRun entity = Assert.IsType<TextRun>(Assert.Single(
            runs,
            static run => run.GetType() == typeof(TextRun) && run.Text == "&"));
        Assert.Equal(string.Concat(runs.Select(static run => run.AccessibleText)), fixture.Snapshot.SemanticDocument.Text);

        var entitySelection = new DocumentRange(
            new DocumentPosition(paragraph.BlockIndex, entity.InlineIndex, 0),
            new DocumentPosition(paragraph.BlockIndex, entity.InlineIndex, 1));
        Assert.True(fixture.Snapshot.SourceMap.TryMapRange(entitySelection, out SourceSpan entitySource));
        Assert.Equal(entity.SourceSpan, entitySource);
        Assert.Equal("&amp;", fixture.Snapshot.SourceMap.Slice(entitySelection));

        SourceSliceTextRun selected = Assert.IsType<SourceSliceTextRun>(
            directSlices.First(static run => run.SliceLength >= 2));
        var selection = new DocumentRange(
            new DocumentPosition(paragraph.BlockIndex, selected.InlineIndex, 1),
            new DocumentPosition(paragraph.BlockIndex, selected.InlineIndex, selected.SliceLength));
        Assert.True(fixture.Snapshot.SourceMap.TryMapRange(selection, out SourceSpan selectedSource));
        Assert.Equal(
            new SourceSpan(selected.SourceStart + 1, selected.SliceLength - 1),
            selectedSource);
    }

    [Fact]
    public void EqualButDistinctParserAndSourceMapStringsDoNotQualifyForSlicing()
    {
        const string source = "ordinary literal with enough characters";
        string equalCopy = new(source.ToCharArray());
        Assert.NotSame(source, equalCopy);

        using BuildFixture fixture = Build(source, equalCopy);
        InlineRun[] runs = fixture.Snapshot.Blocks
            .OfType<InlineContainerBox>()
            .SelectMany(static box => box.Runs)
            .ToArray();

        Assert.DoesNotContain(runs, static run => run is SourceSliceTextRun);
        Assert.Contains(runs, static run => run.GetType() == typeof(TextRun));
        Assert.Equal(source, fixture.Snapshot.SemanticDocument.Text);
    }

    [Fact]
    public void ImpreciseParserSpansKeepTheLegacyStringFallback()
    {
        const string source = "ordinary literal without precise source ranges";
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(device, source);
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source);
        using var snapshot = new LayoutBuilder(context).Build(document, availableWidth: 480f);

        Assert.DoesNotContain(
            snapshot.Blocks.OfType<InlineContainerBox>().SelectMany(static box => box.Runs),
            static run => run is SourceSliceTextRun);
        Assert.Equal(source, snapshot.SemanticDocument.Text);
    }

    [Fact]
    public void SourceSliceLayoutAppendAndPrivateCloneStayLazyAndPublishOneConcurrentString()
    {
        Assert.Equal(
            new[] { "_text" },
            typeof(TextRun)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(static field => field.Name));

        const string source = "prefix:A\uD83D\uDE00B:suffix";
        var run = new SourceSliceTextRun(source, 7, 4)
        {
            InlineIndex = 3,
            SourceSpan = new SourceSpan(7, 4),
            ElementKey = MarkdownElementKeys.Strong,
            StyleAliases = ["literal"],
            StyleModifierKeys = [MarkdownElementKeys.Emphasis],
            SemanticHeadingKey = MarkdownElementKeys.Heading2,
        };
        Assert.True(PrivateTextRunPlacement.IsSupported(run));
        Assert.Equal(4, run.TextLength);
        Assert.Equal(4, run.LayoutTextLength);
        Assert.Equal(string.Empty, ReadTextCache(run));

        var layout = new StringBuilder();
        run.AppendLayoutTextTo(layout);
        Assert.Equal("A\uD83D\uDE00B", layout.ToString());
        Assert.Equal(string.Empty, ReadTextCache(run));

        var clone = Assert.IsType<SourceSliceTextRun>(PrivateTextRunPlacement.Clone(run));
        Assert.NotSame(run, clone);
        Assert.Same(source, clone.SourceText);
        Assert.Equal(run.SourceStart, clone.SourceStart);
        Assert.Equal(run.SourceSpan, clone.SourceSpan);
        Assert.Equal(run.InlineIndex, clone.InlineIndex);
        Assert.Equal(run.ElementKey, clone.ElementKey);
        Assert.Equal(run.StyleAliases, clone.StyleAliases);
        Assert.Equal(run.StyleModifierKeys, clone.StyleModifierKeys);
        Assert.Equal(run.SemanticHeadingKey, clone.SemanticHeadingKey);
        Assert.Equal(string.Empty, ReadTextCache(clone));

        var results = new string[64];
        Parallel.For(0, results.Length, index => results[index] = clone.Text);
        Assert.All(results, value => Assert.Same(results[0], value));
        Assert.Equal("A\uD83D\uDE00B", results[0]);
        Assert.Same(results[0], clone.Text);

        var cachedClone = Assert.IsType<SourceSliceTextRun>(PrivateTextRunPlacement.Clone(clone));
        Assert.Same(results[0], cachedClone.Text);
    }

    [Fact]
    public void PrivateTextCloneClassifierRejectsUnreviewedTextRunSubclasses()
    {
        var custom = new CustomTextRun("stored");

        Assert.False(PrivateTextRunPlacement.IsSupported(custom));
        Assert.Null(PrivateTextRunPlacement.Clone(custom));
    }

    [Fact]
    public void PrivateTextCloneClassifierRetainsItsNullFailClosedContract()
    {
        Assert.False(PrivateTextRunPlacement.IsSupported(null!));
        Assert.Null(PrivateTextRunPlacement.Clone(null!));
    }

    [Fact]
    public void SourceSliceConstructorRejectsEmptyAndOutOfBoundsRanges()
    {
        const string source = "abc";

        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSliceTextRun(source, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSliceTextRun(source, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSliceTextRun(source, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSliceTextRun(source, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSliceTextRun(source, int.MaxValue, 1));
    }

    [Theory]
    [MemberData(nameof(DifferentialCorpus))]
    public void DirectSourceSlicesMatchForcedStringFallbackAcrossRenderedRunAndSelectionContracts(
        string source,
        bool useGitHubReadme,
        bool expectDirectSlice)
    {
        string copiedSource = new(source.ToCharArray());
        Assert.NotSame(source, copiedSource);

        using BuildFixture exact = Build(source, source, useGitHubReadme);
        using BuildFixture fallback = Build(source, copiedSource, useGitHubReadme);
        InlineContainerBox[] exactBoxes = EnumerateInlineBoxes(exact.Snapshot).ToArray();
        InlineContainerBox[] fallbackBoxes = EnumerateInlineBoxes(fallback.Snapshot).ToArray();
        InlineRun[] exactRuns = exactBoxes.SelectMany(static box => box.Runs).ToArray();
        InlineRun[] fallbackRuns = fallbackBoxes.SelectMany(static box => box.Runs).ToArray();

        Assert.Same(source, exact.Snapshot.SourceMap.SourceText);
        Assert.Same(copiedSource, fallback.Snapshot.SourceMap.SourceText);
        Assert.Equal(source, fallback.Snapshot.SourceMap.SourceText);
        Assert.Equal(expectDirectSlice, exactRuns.Any(static run => run is SourceSliceTextRun));
        Assert.DoesNotContain(fallbackRuns, static run => run is SourceSliceTextRun);
        Assert.Equal(exact.Snapshot.SemanticDocument.Text, fallback.Snapshot.SemanticDocument.Text);
        Assert.Equal(
            exactBoxes.Select(ToBoxSnapshot),
            fallbackBoxes.Select(ToBoxSnapshot));
        Assert.Equal(
            exactRuns.Select(ToRunSnapshot),
            fallbackRuns.Select(ToRunSnapshot));
        Assert.Equal(
            MapAllRunSubranges(exact.Snapshot, exactBoxes),
            MapAllRunSubranges(fallback.Snapshot, fallbackBoxes));

        if (source.Contains("<strong>", StringComparison.Ordinal))
        {
            Assert.Contains(exactRuns, static run => run is StrongRun { Text: "bold" });
            Assert.Contains(exactRuns, static run => run is LinkRun { Text: "linked" });
        }
    }

    [Fact]
    public void InlineAddUsesProjectionWhenSourceIdentityOrSpanDoesNotExactlyMatchSlice()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);

        const string escapedSource = "&amp;x &x";
        MarkdownSourceMap spanMismatchMap = new(escapedSource);
        MarkdownLayoutContext spanMismatchContext = CreateContext(device, spanMismatchMap);
        var spanMismatchBox = new InlineContainerBox(spanMismatchContext, MarkdownElementKeys.Body)
        {
            BlockIndex = spanMismatchContext.NextBlockIndex(),
        };
        var spanMismatchRun = new SourceSliceTextRun(escapedSource, 7, 2)
        {
            SourceSpan = new SourceSpan(0, 6),
        };
        spanMismatchBox.Add(spanMismatchRun);
        Assert.NotNull(Assert.Single(spanMismatchMap.Entries).Projection);
        AssertMappedCharacter(spanMismatchMap, spanMismatchBox, spanMismatchRun, 0,
            new SourceSpan(0, 5), "&amp;");
        AssertMappedCharacter(spanMismatchMap, spanMismatchBox, spanMismatchRun, 1,
            new SourceSpan(5, 1), "x");

        const string identityMismatchMapText = "&amp;x";
        const string sliceText = "&x";
        Assert.NotSame(identityMismatchMapText, sliceText);
        MarkdownSourceMap identityMismatchMap = new(identityMismatchMapText);
        MarkdownLayoutContext identityMismatchContext = CreateContext(device, identityMismatchMap);
        var identityMismatchBox = new InlineContainerBox(identityMismatchContext, MarkdownElementKeys.Body)
        {
            BlockIndex = identityMismatchContext.NextBlockIndex(),
        };
        var identityMismatchRun = new SourceSliceTextRun(sliceText, 0, 2)
        {
            SourceSpan = new SourceSpan(0, identityMismatchMapText.Length),
        };
        identityMismatchBox.Add(identityMismatchRun);
        Assert.NotNull(Assert.Single(identityMismatchMap.Entries).Projection);
        AssertMappedCharacter(identityMismatchMap, identityMismatchBox, identityMismatchRun, 0,
            new SourceSpan(0, 5), "&amp;");
        AssertMappedCharacter(identityMismatchMap, identityMismatchBox, identityMismatchRun, 1,
            new SourceSpan(5, 1), "x");
    }

    [Fact]
    public void SpanProjectionMatchesStringProjectionAcrossBoundariesAndCancellation()
    {
        string expected = new string('x', 4_097) + "\uD83D\uDE00tail\n\n";
        string actual = string.Concat("prefix", expected);
        var bySpan = new SemanticTextProjectionAccumulator(expected);
        bySpan.Append(actual.AsSpan("prefix".Length));
        Assert.Equal(expected.Length, bySpan.Length);
        bySpan.Length -= 2;
        Assert.Equal(expected[..^2], bySpan.GetText());

        var mismatchBySpan = new SemanticTextProjectionAccumulator("prefix!");
        mismatchBySpan.Append("prefix?".AsSpan());
        Assert.Equal("prefix?", mismatchBySpan.GetText());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var canceled = new SemanticTextProjectionAccumulator();
            Assert.Throws<OperationCanceledException>(() => canceled.Append(actual.AsSpan()));
            Assert.Equal(0, canceled.Length);
        }
    }

    [Fact]
    public void SpanProjectionPreservesEmptyMultiAppendTrailingNewlineAndChunkBoundaryBehavior()
    {
        string expected = new string('x', 4_095) + "\uD83D\uDE00tail\n\n";
        var matched = new SemanticTextProjectionAccumulator(expected);
        matched.Append(ReadOnlySpan<char>.Empty);
        matched.Append(expected.AsSpan(0, 4_096));
        matched.Append(ReadOnlySpan<char>.Empty);
        matched.Append(expected.AsSpan(4_096, 1));
        matched.Append(expected.AsSpan(4_097));
        Assert.Same(expected, matched.GetText());

        string boundaryExpected = new string('a', 4_096) + "expected tail";
        string boundaryActual = new string('a', 4_096) + "actual tail";
        var boundaryMismatch = new SemanticTextProjectionAccumulator(boundaryExpected);
        boundaryMismatch.Append(boundaryActual.AsSpan(0, 4_095));
        boundaryMismatch.Append(boundaryActual.AsSpan(4_095));
        Assert.Equal(boundaryActual, boundaryMismatch.GetText());
    }

    [Fact]
    public void AtomicImageBoundariesUseSliceCharactersWithoutMaterializingAdjacentLiterals()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        const string source = "AB";
        MarkdownLayoutContext context = CreateContext(device, source);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var left = new SourceSliceTextRun(source, 0, 1) { SourceSpan = new SourceSpan(0, 1) };
        var image = new InlineImageRun(context, "I", "https://invalid.invalid/image.png")
        {
            SourceSpan = new SourceSpan(1, 0),
        };
        var right = new SourceSliceTextRun(source, 1, 1) { SourceSpan = new SourceSpan(1, 1) };
        paragraph.Add(left);
        paragraph.Add(image);
        paragraph.Add(right);
        using var snapshot = new LayoutSnapshot(new BlockBox[] { paragraph }, context.SourceMap, 120f, 20f);

        Assert.Equal("A I B", snapshot.SemanticDocument.Text);
        Assert.Equal(string.Empty, ReadTextCache(left));
        Assert.Equal(string.Empty, ReadTextCache(right));
    }

    private static BuildFixture Build(
        string source,
        string? sourceMapText = null,
        bool useGitHubReadme = false)
    {
        var device = new CanvasDevice(forceSoftwareRenderer: true);
        try
        {
            MarkdownExtensionRegistry registry = new();
            registry.ConfigurePipeline(static pipeline => pipeline.UsePreciseSourceLocation());
            if (useGitHubReadme)
                registry.ConfigureGitHubReadmeRegistry();
            MarkdownSourceMap sourceMap = new(sourceMapText ?? source);
            MarkdownLayoutContext context = CreateContext(device, sourceMap, registry);
            Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());
            LayoutSnapshot snapshot = new LayoutBuilder(context).Build(document, availableWidth: 480f);
            return new BuildFixture(device, context, snapshot);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static MarkdownLayoutContext CreateContext(CanvasDevice device, string source)
        => CreateContext(device, new MarkdownSourceMap(source));

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry? registry = null)
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
        return new MarkdownLayoutContext(
            device,
            theme,
            sourceMap,
            registry ?? new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US");
    }

    private static IEnumerable<InlineContainerBox> EnumerateInlineBoxes(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
            foreach (InlineContainerBox inline in EnumerateInlineBoxes(block))
                yield return inline;
    }

    private static IEnumerable<InlineContainerBox> EnumerateInlineBoxes(BlockBox block)
    {
        if (block is InlineContainerBox inline)
        {
            yield return inline;
            yield break;
        }

        if (block is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
                foreach (InlineContainerBox nested in EnumerateInlineBoxes(child))
                    yield return nested;
            yield break;
        }

        if (block is ListItemBox listItem)
        {
            yield return listItem.Marker;
            foreach (InlineContainerBox nested in EnumerateInlineBoxes(listItem.Content))
                yield return nested;
            yield break;
        }

        if (block is TableBox table)
            foreach (InlineContainerBox cell in table.GetCellBoxes())
                yield return cell;
    }

    private static BoxSnapshot ToBoxSnapshot(InlineContainerBox box)
        => new(
            box.BlockIndex,
            box.SafeHtmlInlineFlowScopeId,
            box.ElementKey,
            box.Runs.Count);

    private static RunSnapshot ToRunSnapshot(InlineRun run)
        => new(
            run.InlineIndex,
            run.Text,
            run.LayoutText,
            run.AccessibleText,
            run.RenderedLength,
            run.SourceSpan,
            run.ElementKey,
            string.Join("\u001f", run.StyleAliases),
            string.Join("\u001f", run.StyleModifierKeys),
            run.SemanticHeadingKey);

    private static List<RunSubrangeMapping> MapAllRunSubranges(
        LayoutSnapshot snapshot,
        IReadOnlyList<InlineContainerBox> boxes)
    {
        var mappings = new List<RunSubrangeMapping>();
        foreach (InlineContainerBox box in boxes)
        {
            foreach (InlineRun run in box.Runs)
            {
                int textLength = run.Text.Length;
                for (int start = 0; start < textLength; start++)
                {
                    for (int end = start + 1; end <= textLength; end++)
                    {
                        var range = new DocumentRange(
                            new DocumentPosition(box.BlockIndex, run.InlineIndex, start),
                            new DocumentPosition(box.BlockIndex, run.InlineIndex, end));
                        bool mapped = snapshot.SourceMap.TryMapRange(range, out SourceSpan sourceSpan);
                        mappings.Add(new RunSubrangeMapping(
                            box.BlockIndex,
                            run.InlineIndex,
                            start,
                            end,
                            mapped,
                            sourceSpan,
                            snapshot.SourceMap.Slice(range)));
                    }
                }
            }
        }

        return mappings;
    }

    private static void AssertMappedCharacter(
        MarkdownSourceMap sourceMap,
        InlineContainerBox box,
        InlineRun run,
        int offset,
        SourceSpan expectedSpan,
        string expectedSlice)
    {
        var range = new DocumentRange(
            new DocumentPosition(box.BlockIndex, run.InlineIndex, offset),
            new DocumentPosition(box.BlockIndex, run.InlineIndex, offset + 1));
        Assert.True(sourceMap.TryMapRange(range, out SourceSpan actualSpan));
        Assert.Equal(expectedSpan, actualSpan);
        Assert.Equal(expectedSlice, sourceMap.Slice(range));
    }

    private readonly record struct BoxSnapshot(
        int BlockIndex,
        int? SafeHtmlInlineFlowScopeId,
        string ElementKey,
        int RunCount);

    private readonly record struct RunSnapshot(
        int InlineIndex,
        string Text,
        string LayoutText,
        string AccessibleText,
        int RenderedLength,
        SourceSpan SourceSpan,
        string ElementKey,
        string StyleAliases,
        string StyleModifierKeys,
        string SemanticHeadingKey);

    private readonly record struct RunSubrangeMapping(
        int BlockIndex,
        int InlineIndex,
        int Start,
        int End,
        bool Mapped,
        SourceSpan SourceSpan,
        string SourceSlice);

    private static string ReadTextCache(SourceSliceTextRun run)
        => Assert.IsType<string>(typeof(TextRun)
            .GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(run));

    private sealed class CustomTextRun(string text) : TextRun(text)
    {
        public override string Text => "custom";
    }

    private sealed class BuildFixture(CanvasDevice device, MarkdownLayoutContext context, LayoutSnapshot snapshot)
        : IDisposable
    {
        public MarkdownLayoutContext Context { get; } = context;
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
