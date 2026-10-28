using System.Reflection;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RawSemanticTextProjectionReuseTests
{
    private const float Width = 320f;

    [Fact]
    public async Task RasterCandidateReusesVerifiedTextProjectionButBindsFreshSemanticTree()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 211);
        MarkdownSemanticDocument sourceSemantic = source.SemanticDocument;
        MarkdownSemanticNode sourceImageNode = MarkdownSemanticDocument
            .EnumerateDepthFirst(sourceSemantic.Root)
            .Single(node => node.Role == MarkdownSemanticRole.Image);
        MarkdownTextSpan sourceImageSpan = sourceSemantic.TextSpans
            .Single(span => span.InlineRun is InlineImageRun);

        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);
        try
        {
            SingleRasterLayoutCandidate candidateOwner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            Assert.IsType<MarkdownLayoutContext>(constructionContext);
            LayoutSnapshot replacement = candidateOwner.Snapshot;
            MarkdownSemanticDocument replacementSemantic = replacement.SemanticDocument;
            InlineContainerBox replacementRoot = Assert.IsType<InlineContainerBox>(
                Assert.Single(replacement.Blocks));
            InlineImageRun replacementRun = replacementRoot.Runs.OfType<InlineImageRun>().Single();
            MarkdownSemanticNode replacementImageNode = MarkdownSemanticDocument
                .EnumerateDepthFirst(replacementSemantic.Root)
                .Single(node => node.Role == MarkdownSemanticRole.Image);
            MarkdownTextSpan replacementImageSpan = replacementSemantic.TextSpans
                .Single(span => span.InlineRun is InlineImageRun);

            Assert.NotSame(sourceSemantic, replacementSemantic);
            Assert.NotSame(sourceSemantic.Root, replacementSemantic.Root);
            Assert.NotSame(sourceImageNode, replacementImageNode);
            Assert.NotSame(sourceSemantic.TextSpans, replacementSemantic.TextSpans);
            Assert.NotSame(sourceImageSpan.InlineBox, replacementImageSpan.InlineBox);
            Assert.NotSame(sourceImageSpan.InlineRun, replacementImageSpan.InlineRun);
            Assert.Same(sourceSemantic.Text, replacementSemantic.Text);
            Assert.Same(sourceSemantic.TextElementBoundaries, replacementSemantic.TextElementBoundaries);
            Assert.Equal(sourceSemantic.TextProjectionFingerprint, replacementSemantic.TextProjectionFingerprint);
            Assert.Equal(source.SourceFingerprint, replacement.SourceFingerprint);
            Assert.Same(source.SourceMap, replacement.SourceMap);
            Assert.Equal(sourceSemantic.SemanticRangeFingerprint, replacementSemantic.SemanticRangeFingerprint);

            Assert.NotSame(fixture.Source, replacementRoot);
            Assert.NotSame(fixture.ImageRun, replacementRun);
            Assert.NotSame(fixture.ImageRun.Image, replacementRun.Image);
            Assert.Same(replacementRoot, replacementImageNode.InlineBox);
            Assert.Same(replacementRun, replacementImageNode.InlineRun);
            Assert.Same(replacementRun.Image, replacementImageNode.ImageBox);
            MarkdownSemanticNode replacementHeading = Assert.IsType<MarkdownSemanticNode>(replacementImageNode.Parent);
            Assert.Equal(MarkdownSemanticRole.Heading, replacementHeading.Role);
            Assert.Equal(3, replacementHeading.HeadingLevel);
            Assert.NotSame(sourceImageNode.Parent, replacementHeading);
            Assert.Same(replacementRoot, replacementHeading.InlineBox);
            Assert.Same(replacementRun, replacementHeading.InlineRun);
            Assert.Same(replacementSemantic.Root.Children.Single(), replacementHeading.Parent);
            Assert.Equal(fixture.RasterSource, replacementImageNode.HelpText);
            Assert.Equal(sourceImageNode.HelpText, replacementImageNode.HelpText);
            Assert.Equal(sourceImageNode.AccessibilityRole, replacementImageNode.AccessibilityRole);
            Assert.Same(replacementRoot, replacementImageSpan.InlineBox);
            Assert.Same(replacementRun, replacementImageSpan.InlineRun);
            Assert.Null(replacementImageSpan.ImageBox);

            Assert.Equal(fixture.ImageRun.InlineIndex, replacementRun.InlineIndex);
            Assert.Equal(fixture.ImageRun.SourceSpan, replacementRun.SourceSpan);
            Assert.Equal(fixture.ImageRun.ElementKey, replacementRun.ElementKey);
            Assert.Equal(fixture.ImageRun.StyleAliases, replacementRun.StyleAliases);
            Assert.Equal(fixture.ImageRun.StyleModifierKeys, replacementRun.StyleModifierKeys);
            Assert.Equal(sourceImageSpan.TextStart, replacementImageSpan.TextStart);
            Assert.Equal(sourceImageSpan.TextEnd, replacementImageSpan.TextEnd);
            Assert.Equal(sourceSemantic.Text, replacementSemantic.Text);
            Assert.Equal(211, replacement.AutomationDocumentGeneration);
        }
        finally
        {
            try
            {
                candidate?.Dispose();
            }
            finally
            {
                if (constructionContext is not null)
                    await constructionContext.DrainOwnedRetirementsAsync();
            }
        }
    }

    [Fact]
    public void ExistingConstructorAndBuildReflectionShapesRemainAvailable()
    {
        Type[] snapshotParameters =
        [
            typeof(IReadOnlyList<BlockBox>),
            typeof(MarkdownSourceMap),
            typeof(float),
            typeof(float),
            typeof(IReadOnlyDictionary<int, int>),
            typeof(IReadOnlyDictionary<int, int>),
            typeof(IReadOnlyDictionary<string, int>),
            typeof(SharedLayoutMetrics),
            typeof(Microsoft.UI.Xaml.Thickness),
            typeof(double),
            typeof(IMarkdownVectorSceneResidencyPolicy),
        ];
        ConstructorInfo? snapshotConstructor = typeof(LayoutSnapshot).GetConstructor(snapshotParameters);
        Assert.NotNull(snapshotConstructor);
        Assert.Equal(11, snapshotConstructor!.GetParameters().Length);

        Type[] semanticParameters =
        [
            typeof(MarkdownSemanticNode),
            typeof(string),
            typeof(IReadOnlyList<MarkdownTextSpan>),
        ];
        ConstructorInfo? semanticConstructor = typeof(MarkdownSemanticDocument).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: semanticParameters,
            modifiers: null);
        Assert.NotNull(semanticConstructor);
        Assert.Equal(3, semanticConstructor!.GetParameters().Length);
        Assert.NotNull(typeof(MarkdownSemanticDocument).GetMethod(
            nameof(MarkdownSemanticDocument.Build),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(LayoutSnapshot)],
            modifiers: null));

        ConstructorInfo projectionConstructor = typeof(MarkdownSemanticTextProjectionReuse)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        Assert.True(projectionConstructor.IsPrivate);
        Assert.NotNull(typeof(MarkdownSemanticTextProjectionReuse).GetMethod(
            "Capture",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(LayoutSnapshot)],
            modifiers: null));
    }

    [Fact]
    public void BuilderOrdinalComparisonIsExactAcrossChunkedUnicodeSequences()
    {
        AssertAccumulatorExactAndMismatch("combining-mark", builder =>
        {
            builder.Append('x', 15);
            builder.Append('e');
            builder.Append('\u0301');
        }, expected => expected[..^1] + '\u0300');

        AssertAccumulatorExactAndMismatch("rtl-combining-mark", builder =>
        {
            builder.Append('x', 15);
            builder.Append('\u05D0');
            builder.Append('\u05B0');
        }, expected => expected[..^1] + '\u05B1');

        AssertAccumulatorExactAndMismatch("zwj-emoji", builder =>
        {
            builder.Append('x', 13);
            builder.Append("👨‍");
            builder.Append("👩");
        }, expected => expected[..^2] + "👧");
    }

    [Fact]
    public void BuilderOrdinalComparisonObservesCanceledLayoutPassBeforeLengthCheck()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var accumulator = new SemanticTextProjectionAccumulator("matching text");
            Assert.Throws<OperationCanceledException>(() => accumulator.GetText());
            Assert.Throws<OperationCanceledException>(() => accumulator.Append("different-length"));
        }
    }

    [Fact]
    public async Task SameTextFromDifferentSourceMapCannotReuseCapturedProjection()
    {
        await using var sourceFixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        await using var targetFixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(sourceFixture, automationGeneration: 401);
        using var target = CreateSourceSnapshot(targetFixture, automationGeneration: 402);
        Assert.NotSame(source.SourceMap, target.SourceMap);
        Assert.Equal(source.SourceMap.SourceText, target.SourceMap.SourceText);
        Assert.Equal(source.SemanticDocument.Text, target.SemanticDocument.Text);

        MarkdownSemanticTextProjectionReuse captured = MarkdownSemanticTextProjectionReuse.Capture(source);
        MarkdownSemanticDocument rebuilt = MarkdownSemanticDocument.Build(target, captured);

        Assert.Equal(target.SemanticDocument.Text, rebuilt.Text);
        Assert.NotSame(source.SemanticDocument.Text, rebuilt.Text);
        Assert.NotSame(source.SemanticDocument.TextElementBoundaries, rebuilt.TextElementBoundaries);
        Assert.Equal(target.SemanticDocument.TextProjectionFingerprint, rebuilt.TextProjectionFingerprint);
        Assert.Equal(target.SemanticDocument.SemanticRangeFingerprint, rebuilt.SemanticRangeFingerprint);
    }

    [Fact]
    public async Task SameMapWithDifferentBuilderTextFallsBackToFreshProjection()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 503);
        MarkdownSemanticTextProjectionReuse captured = MarkdownSemanticTextProjectionReuse.Capture(source);

        var changedRoot = new InlineContainerBox(fixture.SourceContext, MarkdownElementKeys.Body)
        {
            BlockIndex = fixture.SourceContext.NextBlockIndex(),
        };
        const string changedText = "different";
        var changedRun = new TextRun(changedText) { SourceSpan = new SourceSpan(0, changedText.Length) };
        changedRoot.Add(changedRun);
        fixture.SourceContext.SourceMap.Add(
            changedRoot.BlockIndex,
            changedRun.InlineIndex,
            changedRun.RenderedLength,
            changedRun.SourceSpan,
            changedRun.Text);
        float height = changedRoot.Measure(Width);
        changedRoot.Arrange(0, 0, Width);
        using var target = new LayoutSnapshot(
            new BlockBox[] { changedRoot },
            fixture.SourceContext.SourceMap,
            Width,
            height);
        Assert.Same(source.SourceMap, target.SourceMap);
        Assert.NotEqual(source.SemanticDocument.Text, target.SemanticDocument.Text);

        MarkdownSemanticDocument rebuilt = MarkdownSemanticDocument.Build(target, captured);

        Assert.Equal(changedText, rebuilt.Text);
        Assert.NotSame(captured.Text, rebuilt.Text);
        Assert.NotSame(captured.TextElementBoundaries, rebuilt.TextElementBoundaries);
        Assert.Equal(target.SemanticDocument.TextProjectionFingerprint, rebuilt.TextProjectionFingerprint);
        Assert.Equal(target.SemanticDocument.SemanticRangeFingerprint, rebuilt.SemanticRangeFingerprint);
    }

    [Fact]
    public async Task ReusedBoundaryIndexHasStableConcurrentGraphemeAndWordReads()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 307);
        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);

        try
        {
            SingleRasterLayoutCandidate candidateOwner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            Assert.IsType<MarkdownLayoutContext>(constructionContext);
            TextElementBoundaryIndex index = candidateOwner.Snapshot.SemanticDocument.TextElementBoundaries;
            Assert.Same(source.SemanticDocument.TextElementBoundaries, index);
            TextElementBoundaryIndex unicodeIndex = new("e\u0301 אבג 👨‍👩‍👧");
            TextElementBoundaryIndex[] indexes = [index, unicodeIndex];
            BoundaryRead[][] expectedByIndex = indexes.Select(ReadAll).ToArray();

            Task<bool>[] workers = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() =>
                {
                    for (int indexNumber = 0; indexNumber < indexes.Length; indexNumber++)
                    {
                        TextElementBoundaryIndex current = indexes[indexNumber];
                        BoundaryRead[] expected = expectedByIndex[indexNumber];
                        for (int offset = 0; offset < expected.Length; offset++)
                        {
                            BoundaryRead actual = new(
                                current.FindBoundaries(offset),
                                current.FindWordBoundaries(offset),
                                current.Move(offset, 1, out int moved),
                                moved);
                            if (actual != expected[offset])
                                return false;
                        }
                    }

                    return true;
                }))
                .ToArray();
            bool[] results = await Task.WhenAll(workers);
            Assert.All(results, result => Assert.True(result));

            static BoundaryRead[] ReadAll(TextElementBoundaryIndex current) =>
                Enumerable.Range(0, current.TextLength + 1)
                    .Select(offset =>
                    {
                        int next = current.Move(offset, 1, out int moved);
                        return new BoundaryRead(
                            current.FindBoundaries(offset),
                            current.FindWordBoundaries(offset),
                            next,
                            moved);
                    })
                    .ToArray();
        }
        finally
        {
            try
            {
                candidate?.Dispose();
            }
            finally
            {
                if (constructionContext is not null)
                    await constructionContext.DrainOwnedRetirementsAsync();
            }
        }
    }

    private static LayoutSnapshot CreateSourceSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        long automationGeneration)
    {
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { fixture.Source },
            fixture.SourceContext.SourceMap,
            Width,
            (float)fixture.Source.Bounds.Height);
        fixture.TransferSourceOwnershipToSnapshot(snapshot);
        snapshot.AssignAutomationDocumentGeneration(automationGeneration);
        return snapshot;
    }

    private static void AssertAccumulatorExactAndMismatch(
        string scenario,
        Action<StringBuilder> append,
        Func<string, string> differentSameLength)
    {
        var builder = new StringBuilder(capacity: 16);
        append(builder);
        string expected = builder.ToString();
        var fragments = new List<string>();
        foreach (ReadOnlyMemory<char> chunk in builder.GetChunks())
            fragments.Add(new string(chunk.Span));
        Assert.True(fragments.Count > 1, $"{scenario} should cross a StringBuilder chunk boundary.");

        var exact = new SemanticTextProjectionAccumulator(expected);
        foreach (string fragment in fragments)
            exact.Append(fragment);
        Assert.Same(expected, exact.GetText());

        string different = differentSameLength(expected);
        Assert.Equal(expected.Length, different.Length);
        Assert.NotEqual(expected, different);
        var mismatch = new SemanticTextProjectionAccumulator(expected);
        int offset = 0;
        foreach (string fragment in fragments)
        {
            mismatch.Append(different.Substring(offset, fragment.Length));
            offset += fragment.Length;
        }

        string actual = mismatch.GetText();
        Assert.NotSame(expected, actual);
        Assert.Equal(different, actual);
    }

    private readonly record struct BoundaryRead(
        (int Start, int End) Grapheme,
        (int Start, int End) Word,
        int NextStart,
        int Moved);
}
