using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Protects the constructor's fused snapshot-index traversal against the
/// independent, former per-index preorder walks.
/// </summary>
public sealed class LayoutSnapshotIndexConstructionTests
{
    [Theory]
    [InlineData(FlowDirection.LeftToRight, 0)]
    [InlineData(FlowDirection.RightToLeft, 0)]
    [InlineData(FlowDirection.LeftToRight, 199)]
    [InlineData(FlowDirection.RightToLeft, 199)]
    public void FusedIndexesAndFocusablePlanMatchLegacyPreorder(
        FlowDirection flowDirection, int generatedBlockIndexUpperBound)
    {
        Fixture fixture = CreateFixture(flowDirection);
        using LayoutSnapshot snapshot = new(
            fixture.Roots,
            fixture.Context.SourceMap,
            width: 360,
            height: 640,
            generatedBlockIndexUpperBound: generatedBlockIndexUpperBound);

        LegacyIndexes expected = LegacyIndexes.Build(fixture.Roots);
        AssertDictionaryEquals(expected.BlockOrdinals, ReadField<Dictionary<BlockBox, int>>(snapshot, "_blockOrdinals"));
        AssertDictionaryEquals(
            expected.TopLevelOrdinalByBlockIndex,
            ReadAssignableField<IReadOnlyDictionary<int, int>>(snapshot, "_topLevelOrdinalByBlockIndex"));
        AssertDictionaryEquals(
            expected.ImageTopLevelOrdinalByIdentity,
            ReadField<Dictionary<ImageBox, int>>(snapshot, "_imageTopLevelOrdinalByIdentity"));
        AssertReferenceSetEquals(
            expected.HorizontalOverflowBoxes,
            ReadField<HashSet<IHorizontalOverflowBox>>(snapshot, "_horizontalOverflowBoxes"));
        AssertDictionaryEquals(
            expected.HorizontalOverflowByBlockIndex,
            ReadField<Dictionary<int, IHorizontalOverflowBox>>(snapshot, "_horizontalOverflowByBlockIndex"));
        object focusPlanValue = typeof(LayoutSnapshot).GetField(
            "_focusablePlan",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(snapshot)!;
        AssertFocusablePlanEquals(expected.FocusablePlan, Assert.IsAssignableFrom<Array>(focusPlanValue));

        // Shared image identity is stable within one root but deliberately
        // becomes ambiguous when the same image is encountered under another
        // top-level root. Duplicate block indexes remain first-wins for root
        // ownership and last-wins for the horizontal-overflow alias map.
        IReadOnlyDictionary<int, int> topOrdinals = ReadAssignableField<IReadOnlyDictionary<int, int>>(
            snapshot,
            "_topLevelOrdinalByBlockIndex");
        Assert.Equal(0, topOrdinals[fixture.DuplicateTopLevelBlockIndex]);
        Dictionary<ImageBox, int> imageOrdinals = ReadField<Dictionary<ImageBox, int>>(
            snapshot,
            "_imageTopLevelOrdinalByIdentity");
        Assert.Equal(-1, imageOrdinals[fixture.SharedImage]);
        Assert.Equal(0, imageOrdinals[fixture.SameRootImage]);
        Assert.Equal(0, imageOrdinals[fixture.CodeChunkImage]);
        Assert.Equal(0, imageOrdinals[fixture.TableCellImage]);
        Assert.Equal(2, imageOrdinals[fixture.DecorativeImage]);
        Dictionary<int, IHorizontalOverflowBox> horizontalByIndex = ReadField<Dictionary<int, IHorizontalOverflowBox>>(
            snapshot,
            "_horizontalOverflowByBlockIndex");
        Assert.Same(fixture.OuterOverflowOwner, horizontalByIndex[fixture.NestedOverflowDescendantBlockIndex]);
        Assert.Same(fixture.Table, horizontalByIndex[fixture.TableCellBlockIndex]);
        Assert.Same(fixture.LastDuplicateOverflowOwner, horizontalByIndex[fixture.DuplicateOverflowBlockIndex]);

        // Code chunks still contribute image/block/overflow indexing, but the
        // legacy focus walk never descended into them, even when they contain
        // dense syntax runs, links, and images.
        Assert.DoesNotContain(
            expected.FocusablePlan,
            entry => entry.Item.BlockIndex == fixture.CodeChunk.BlockIndex);
        Assert.Contains(expected.FocusablePlan, entry =>
            entry.Item.Kind == FocusableItemKind.CodeBlockCopy &&
            entry.Item.BlockIndex == fixture.CopyCodeBlock.BlockIndex);
        Assert.DoesNotContain(expected.FocusablePlan, entry =>
            entry.Item.Kind == FocusableItemKind.CodeBlockCopy &&
            entry.Item.BlockIndex == fixture.PlainMarkdownCodeBlock.BlockIndex);
        Assert.Contains(expected.FocusablePlan, entry =>
            ReferenceEquals(entry.Overflow, fixture.FallbackVector));
        Assert.Contains(expected.FocusablePlan, entry =>
            entry.Item.Kind == FocusableItemKind.VectorSemantic &&
            entry.Item.BlockIndex == fixture.FocusableVector.BlockIndex &&
            entry.Item.InlineIndex == 0);
        Assert.DoesNotContain(expected.FocusablePlan, entry =>
            entry.Item.Kind == FocusableItemKind.VectorSemantic &&
            entry.Item.BlockIndex == fixture.FocusableVector.BlockIndex &&
            entry.Item.InlineIndex == 1);
        Assert.Contains(expected.FocusablePlan, entry =>
            entry.Item.Kind == FocusableItemKind.DeclarativeHostedElement &&
            entry.Item.BlockIndex == fixture.HostedElement.BlockIndex);
        Assert.Contains(expected.FocusablePlan, entry =>
            entry.Item.Kind == FocusableItemKind.BlockEmbed &&
            entry.Item.BlockIndex == fixture.BlockEmbed.BlockIndex);
        Assert.DoesNotContain(expected.FocusablePlan, entry =>
            entry.Item.BlockIndex == fixture.ReadOnlyTaskParagraph.BlockIndex &&
            entry.Item.InlineIndex == fixture.ReadOnlyTaskRun.InlineIndex);
        Assert.DoesNotContain(expected.FocusablePlan, entry =>
            entry.Item.BlockIndex == fixture.DecorativeImage.BlockIndex);

        // Dynamic overflow entries are resolved at collection time; the
        // constructor must not allocate and then discard a fixed item array.
        FieldInfo fixedItemsField = typeof(LayoutSnapshot).GetField(
            "_fixedFocusableItems",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Snapshot fixed-focus field was not found.");
        Assert.Null(fixedItemsField.GetValue(snapshot));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void LayoutPlanCandidateIndexMatchesLegacyRealizationTraversal(FlowDirection flowDirection)
    {
        Fixture fixture = CreateFixture(flowDirection);
        using LayoutSnapshot snapshot = new(
            fixture.Roots,
            fixture.Context.SourceMap,
            width: 360,
            height: 640);

        var expected = new List<LayoutPlanCandidate>();
        for (int ordinal = 0; ordinal < fixture.Roots.Count; ordinal++)
            CollectLegacyPlanCandidates(fixture.Roots[ordinal], ordinal, expected);

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            ReadOnlySpan<LayoutPlanCandidate> actual = snapshot.GetLayoutPlanCandidatesUnderPaint();
            Assert.Equal(expected.Count, actual.Length);
            for (int index = 0; index < actual.Length; index++)
            {
                Assert.Same(expected[index].Box, actual[index].Box);
                Assert.Equal(expected[index].TopLevelOrdinal, actual[index].TopLevelOrdinal);
            }

            var measuredCandidates = snapshot.GetMeasuredLayoutPlanCandidatesUnderPaint();
            var measured = new List<LayoutPlanCandidate>();
            while (measuredCandidates.MoveNext())
                measured.Add(measuredCandidates.Current);
            Assert.Equal(expected.Count, measured.Count);
            for (int index = 0; index < measured.Count; index++)
            {
                Assert.Same(expected[index].Box, measured[index].Box);
                Assert.Equal(expected[index].TopLevelOrdinal, measured[index].TopLevelOrdinal);
            }

            // Drawing chunks remain atomic beneath CodeBlockBox: the old
            // realization pass did not descend into them, even when a chunk
            // contains inline images or embeds.
            Assert.DoesNotContain(actual.ToArray(), candidate =>
                ReferenceEquals(candidate.Box, fixture.CodeChunk));
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    [Fact]
    public void LazyCandidateEnumerationIsFinalizedInDocumentOrderAcrossOutOfOrderBands()
    {
        const int blockCount = 10_000;
        const int firstMeasuredOrdinal = 8_000;
        const int secondMeasuredOrdinal = 5_000;
        const int thirdMeasuredOrdinal = 7_000;
        const float width = 360;
        Block sourceBlock = Markdig.Markdown.Parse("embedded")[0];
        var roots = new BlockBox[blockCount];
        for (int ordinal = 0; ordinal < roots.Length; ordinal++)
        {
            roots[ordinal] = new EmbedBox(sourceBlock, TestEmbedFactory.Instance)
            {
                BlockIndex = ordinal,
            };
        }

        using var snapshot = new LayoutSnapshot(
            roots,
            new MarkdownSourceMap(string.Empty),
            width,
            height: 0,
            blockSpacing: 0);

        // EmbedBox estimates are exactly 64px; the test factory measures them
        // to 20px. Starting at a distant root proves ordinal stability across
        // suffix reflow, then measuring an earlier root makes arrival order
        // differ from the document-order plan contract.
        snapshot.EnableLazyLayout(
            width,
            viewportTop: firstMeasuredOrdinal * 64d,
            viewportHeight: 0.5d,
            overscan: 0,
            cancellationToken: CancellationToken.None);
        Assert.InRange(snapshot.MeasuredTopLevelBlockCount, 1, 99);

        BlockBox secondRoot = roots[secondMeasuredOrdinal];
        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(secondRoot.Bounds.Top + 0.125d, secondRoot.Bounds.Top + 0.5d),
            CancellationToken.None);
        Assert.InRange(snapshot.MeasuredTopLevelBlockCount, 2, 99);

        // A third, intermediate band proves the list remains in document
        // order after multiple out-of-order arrivals, rather than merely
        // sorting one pair as part of candidate enumeration.
        BlockBox thirdRoot = roots[thirdMeasuredOrdinal];
        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(thirdRoot.Bounds.Top + 0.125d, thirdRoot.Bounds.Top + 0.5d),
            CancellationToken.None);
        Assert.InRange(snapshot.MeasuredTopLevelBlockCount, 3, 99);

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.Equal(blockCount, snapshot.GetLayoutPlanCandidatesUnderPaint().Length);

            bool[] measuredMask = ReadField<bool[]>(snapshot, "_measuredTopLevelBlocks");
            var expectedOrdinals = new List<int>();
            for (int ordinal = 0; ordinal < measuredMask.Length; ordinal++)
            {
                if (measuredMask[ordinal])
                    expectedOrdinals.Add(ordinal);
            }
            Assert.InRange(expectedOrdinals.Count, 3, 99);
            Assert.Contains(firstMeasuredOrdinal, expectedOrdinals);
            Assert.Contains(secondMeasuredOrdinal, expectedOrdinals);
            Assert.Contains(thirdMeasuredOrdinal, expectedOrdinals);

            List<int> finalizedOrdinals = ReadField<List<int>>(
                snapshot,
                "_measuredLayoutPlanRootOrdinals");
            Assert.Equal(expectedOrdinals, finalizedOrdinals);
            Assert.True(ReadField<bool>(
                snapshot,
                "_measuredLayoutPlanRootOrdinalsAreSorted"));
            int[] ordinalsBeforeEnumeration = finalizedOrdinals.ToArray();

            var candidates = snapshot.GetMeasuredLayoutPlanCandidatesUnderPaint();
            var actual = new List<LayoutPlanCandidate>();
            while (candidates.MoveNext())
                actual.Add(candidates.Current);

            Assert.Equal(expectedOrdinals.Count, actual.Count);
            for (int index = 0; index < actual.Count; index++)
            {
                Assert.Equal(expectedOrdinals[index], actual[index].TopLevelOrdinal);
                Assert.Same(roots[expectedOrdinals[index]], actual[index].Box);
            }
            Assert.Same(finalizedOrdinals, ReadField<List<int>>(
                snapshot, "_measuredLayoutPlanRootOrdinals"));
            Assert.Equal(ordinalsBeforeEnumeration, finalizedOrdinals);
            Assert.True(ReadField<bool>(
                snapshot,
                "_measuredLayoutPlanRootOrdinalsAreSorted"));
            Assert.True(snapshot.TryGetMeasuredBlockByOrdinalUnderPaint(
                secondMeasuredOrdinal,
                out BlockBox secondMeasuredRoot));
            Assert.Same(roots[secondMeasuredOrdinal], secondMeasuredRoot);
            Assert.True(snapshot.TryGetMeasuredBlockByOrdinalUnderPaint(
                firstMeasuredOrdinal,
                out BlockBox firstMeasuredRoot));
            Assert.Same(roots[firstMeasuredOrdinal], firstMeasuredRoot);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    [Fact]
    public void CanceledOutOfOrderBandRestoresPriorMeasuredOrdinalOrderAndPublication()
    {
        const int blockCount = 10;
        const int initiallyMeasuredOrdinal = 8;
        const int canceledOrdinal = 5;
        Block sourceBlock = Markdig.Markdown.Parse("embedded")[0];
        using var cancellation = new CancellationTokenSource();
        var factory = new CancelAfterMeasureEmbedFactory();
        var roots = new BlockBox[blockCount];
        for (int ordinal = 0; ordinal < roots.Length; ordinal++)
        {
            roots[ordinal] = new EmbedBox(sourceBlock, factory)
            {
                BlockIndex = ordinal,
            };
        }

        using var snapshot = new LayoutSnapshot(
            roots,
            new MarkdownSourceMap(string.Empty),
            width: 360,
            height: 0);
        snapshot.EnableLazyLayout(
            availableWidth: 360,
            viewportTop: initiallyMeasuredOrdinal * 64d,
            viewportHeight: 0.5d,
            overscan: 0,
            cancellationToken: CancellationToken.None);

        List<int> priorOrdinals = ReadField<List<int>>(
            snapshot, "_measuredLayoutPlanRootOrdinals");
        int[] priorOrdinalValues = priorOrdinals.ToArray();
        Assert.Contains(initiallyMeasuredOrdinal, priorOrdinalValues);
        Assert.Equal(priorOrdinalValues.Order(), priorOrdinalValues);
        Assert.True(ReadField<bool>(snapshot, "_measuredLayoutPlanRootOrdinalsAreSorted"));
        bool[] priorMeasuredMask = ReadField<bool[]>(snapshot, "_measuredTopLevelBlocks");
        bool[] priorMask = priorMeasuredMask.ToArray();
        int priorMeasuredCount = snapshot.MeasuredTopLevelBlockCount;
        Size priorSize = snapshot.Size;
        long priorRevision = snapshot.LayoutRevision;
        LayoutPlacementGeneration priorPlacements = snapshot.PublishedPlacementGeneration;

        factory.AfterMeasure = cancellation.Cancel;
        BlockBox canceledRoot = roots[canceledOrdinal];
        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(canceledRoot.Bounds.Top + 0.125d, canceledRoot.Bounds.Top + 0.5d),
            cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.Equal(priorMeasuredCount, snapshot.MeasuredTopLevelBlockCount);
        Assert.Equal(priorSize, snapshot.Size);
        Assert.Equal(priorRevision, snapshot.LayoutRevision);
        Assert.Same(priorPlacements, snapshot.PublishedPlacementGeneration);
        Assert.Same(priorMeasuredMask, ReadField<bool[]>(snapshot, "_measuredTopLevelBlocks"));
        Assert.Equal(priorMask, priorMeasuredMask);
        Assert.Same(priorOrdinals, ReadField<List<int>>(
            snapshot, "_measuredLayoutPlanRootOrdinals"));
        Assert.Equal(priorOrdinalValues, priorOrdinals);
        Assert.True(ReadField<bool>(snapshot, "_measuredLayoutPlanRootOrdinalsAreSorted"));
        Assert.False(priorMeasuredMask[canceledOrdinal]);
    }

    [Fact]
    public void FailedLazyResidencyCommitRollsBackMeasuredCandidateRootIndex()
    {
        Block sourceBlock = Markdig.Markdown.Parse("embedded")[0];
        BlockBox[] roots =
        [
            new EmbedBox(sourceBlock, TestEmbedFactory.Instance) { BlockIndex = 0 },
            new EmbedBox(sourceBlock, TestEmbedFactory.Instance) { BlockIndex = 1 },
        ];
        var residencyPolicy = new ThrowingVectorResidencyPolicy(throwOnCall: 2);
        using var snapshot = new LayoutSnapshot(
            roots,
            new MarkdownSourceMap(string.Empty),
            width: 360,
            height: 0,
            blockSpacing: 0,
            vectorSceneResidencyPolicy: residencyPolicy);

        snapshot.EnableLazyLayout(
            availableWidth: 360,
            viewportTop: 0,
            viewportHeight: 0.5d,
            overscan: 0,
            cancellationToken: CancellationToken.None);
        Assert.Equal(1, ReadField<int>(snapshot, "_measuredTopLevelBlockCount"));
        Assert.Equal(new[] { 0 }, ReadField<List<int>>(
            snapshot,
            "_measuredLayoutPlanRootOrdinals"));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(roots[1].Bounds.Top + 0.125d, roots[1].Bounds.Top + 0.5d),
                CancellationToken.None));
        Assert.Equal(ThrowingVectorResidencyPolicy.FailureMessage, failure.Message);
        Assert.Equal(1, ReadField<int>(snapshot, "_measuredTopLevelBlockCount"));
        bool[] measuredMask = ReadField<bool[]>(snapshot, "_measuredTopLevelBlocks");
        Assert.True(measuredMask[0]);
        Assert.False(measuredMask[1]);
        Assert.Equal(new[] { 0 }, ReadField<List<int>>(
            snapshot,
            "_measuredLayoutPlanRootOrdinals"));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void FixedFocusableItemsMatchLegacyPlanWhenNoDynamicOverflow(FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = CreateContext(flowDirection);
        var paragraph = NewParagraph(context, blockIndex: 801);
        var link = new LinkRun("fixed link", "https://example.test/fixed-link");
        paragraph.Add(link);
        var inlineAction = new InlineEmbedRun(16, 16, static () => null!);
        paragraph.Add(inlineAction);

        var root = new StackBox { BlockIndex = 800 };
        root.Add(paragraph);
        var blockEmbed = new EmbedBox(
            Markdig.Markdown.Parse("fixed block embed")[0],
            TestEmbedFactory.Instance)
        {
            BlockIndex = 802,
        };
        root.Add(blockEmbed);

        using LayoutSnapshot snapshot = new(
            new BlockBox[] { root },
            context.SourceMap,
            width: 360,
            height: 640);

        LegacyIndexes expected = LegacyIndexes.Build(new BlockBox[] { root });
        Assert.All(expected.FocusablePlan, entry => Assert.Null(entry.Overflow));
        FocusableItem[] actual = ReadField<FocusableItem[]>(snapshot, "_fixedFocusableItems");
        Assert.Equal(expected.FocusablePlan.Count, actual.Length);
        for (int index = 0; index < actual.Length; index++)
            Assert.Equal(expected.FocusablePlan[index].Item, actual[index]);

        Assert.Collection(
            actual,
            item => Assert.Equal(new FocusableItem(801, link.InlineIndex, FocusableItemKind.Link), item),
            item => Assert.Equal(new FocusableItem(801, inlineAction.InlineIndex, FocusableItemKind.InlineEmbed), item),
            item => Assert.Equal(new FocusableItem(802, 0, FocusableItemKind.BlockEmbed), item));
    }

    [Fact]
    public void LinkedEmptyAltInlineImageIsFocusableButDecorativeImagesAreNot()
    {
        const int blockIndex = 803;
        const string linkedImageTarget = "https://example.test/image-target";
        MarkdownLayoutContext context = CreateContext(FlowDirection.LeftToRight);
        var paragraph = NewParagraph(context, blockIndex);
        var precedingLink = new LinkRun("before", "https://example.test/before");
        var linkedEmptyAltImage = new InlineImageRun(
            context,
            altText: string.Empty,
            url: string.Empty,
            linkUrl: linkedImageTarget);
        var decorativeEmptyAltImage = new InlineImageRun(
            context,
            altText: string.Empty,
            url: string.Empty);
        var ordinaryUnlinkedAltImage = new InlineImageRun(
            context,
            altText: "ordinary unlinked image",
            url: string.Empty);
        var followingLink = new LinkRun("after", "https://example.test/after");
        paragraph.Add(precedingLink);
        paragraph.Add(linkedEmptyAltImage);
        paragraph.Add(decorativeEmptyAltImage);
        paragraph.Add(ordinaryUnlinkedAltImage);
        paragraph.Add(followingLink);

        using LayoutSnapshot snapshot = new(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 360,
            height: 640);

        Assert.Equal(
            new[]
            {
                new FocusableItem(blockIndex, precedingLink.InlineIndex, FocusableItemKind.Link),
                new FocusableItem(blockIndex, linkedEmptyAltImage.InlineIndex, FocusableItemKind.Link),
                new FocusableItem(blockIndex, followingLink.InlineIndex, FocusableItemKind.Link),
            },
            snapshot.CollectFocusableItems());

        MarkdownSemanticDocument semanticDocument = snapshot.SemanticDocument;
        MarkdownSemanticNode? linkedImageNode = null;
        MarkdownSemanticNode? ordinaryImageNode = null;
        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(semanticDocument.Root))
        {
            if (ReferenceEquals(node.InlineRun, linkedEmptyAltImage))
                linkedImageNode = node;
            else if (ReferenceEquals(node.InlineRun, ordinaryUnlinkedAltImage))
                ordinaryImageNode = node;

            Assert.NotSame(decorativeEmptyAltImage, node.InlineRun);
        }

        Assert.NotNull(linkedImageNode);
        Assert.Equal(MarkdownSemanticRole.Image, linkedImageNode!.Role);
        Assert.Same(linkedEmptyAltImage.Image, linkedImageNode.ImageBox);
        Assert.Equal(linkedImageTarget, linkedImageNode.HelpText);
        Assert.False(string.IsNullOrWhiteSpace(semanticDocument.GetText(linkedImageNode)));

        Assert.NotNull(ordinaryImageNode);
        Assert.Equal(MarkdownSemanticRole.Image, ordinaryImageNode!.Role);
        Assert.Equal("ordinary unlinked image", semanticDocument.GetText(ordinaryImageNode));
        Assert.DoesNotContain(
            snapshot.CollectFocusableItems(),
            item => item.InlineIndex == ordinaryUnlinkedAltImage.InlineIndex);
    }

    private static void CollectLegacyPlanCandidates(
        BlockBox box,
        int topLevelOrdinal,
        List<LayoutPlanCandidate> candidates)
    {
        switch (box)
        {
            case InlineContainerBox inline:
                if (inline.HasInlineImages || inline.HasInlineEmbeds)
                    candidates.Add(new LayoutPlanCandidate(box, topLevelOrdinal));
                break;
            case EmbedBox:
            case DeclarativeHostedElementBox:
            case ImageBox:
            case CodeBlockBox:
                candidates.Add(new LayoutPlanCandidate(box, topLevelOrdinal));
                break;
            case ListItemBox listItem:
                CollectLegacyPlanCandidates(listItem.Marker, topLevelOrdinal, candidates);
                CollectLegacyPlanCandidates(listItem.Content, topLevelOrdinal, candidates);
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    CollectLegacyPlanCandidates(cell, topLevelOrdinal, candidates);
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    CollectLegacyPlanCandidates(child, topLevelOrdinal, candidates);
                break;
        }
    }

    private static Fixture CreateFixture(FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = CreateContext(flowDirection);
        var root = new StackBox { BlockIndex = 100 };

        var inline = NewParagraph(context, blockIndex: 101);
        inline.Add(new TextRun("text before"));
        inline.Add(new LinkRun("linked text", "https://example.test/link"));
        var sharedInlineImageRun = new InlineImageRun(context, "shared", "https://example.test/shared.png");
        inline.Add(sharedInlineImageRun);
        var sameRootImageRun = new InlineImageRun(context, "same root", "https://example.test/same-root.png");
        inline.Add(sameRootImageRun);
        inline.Add(new InlineImageRun(context, string.Empty, "https://example.test/decorative-inline.png"));
        var readOnlyTask = new InlineEmbedRun(18, 18, static () => null!)
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Complete",
                "Incomplete",
                "Read only",
                "Toggle",
                "task-readonly",
                isChecked: false),
        };
        inline.Add(readOnlyTask);
        inline.Add(new InlineEmbedRun(18, 18, static () => null!));
        var linkedInlineVector = new InlineVectorSceneRun(
            context,
            CreateVectorScene(Array.Empty<MarkdownVectorSemanticItem>()),
            "math",
            "math",
            accessibilityDescription: null,
            elementKey: MarkdownElementKeys.Math)
        {
            LinkUrl = "https://example.test/math",
        };
        inline.Add(linkedInlineVector);
        root.Add(inline);

        // Repeat the same image identity within root zero, then under a
        // different root below. These aliases exercise the old identity index
        // ambiguity rule without loading or retaining a bitmap.
        root.Add(sharedInlineImageRun.Image);
        root.Add(sharedInlineImageRun.Image);
        root.Add(sameRootImageRun.Image);
        root.Add(sameRootImageRun.Image);

        var tableHeaderLink = NewParagraph(context, 201, MarkdownElementKeys.TableCell);
        tableHeaderLink.Add(new LinkRun("table link", "https://example.test/table"));
        var secondTableHeaderLink = NewParagraph(context, 211, MarkdownElementKeys.TableCell);
        secondTableHeaderLink.Add(new LinkRun("second header link", "https://example.test/table-header-2"));
        var tableBodyTask = NewParagraph(context, 202, MarkdownElementKeys.TableCell);
        tableBodyTask.Add(new InlineEmbedRun(16, 16, static () => null!)
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Done",
                "Not done",
                "Read only task",
                "Toggle",
                "task-table",
                isChecked: true),
        });
        var secondTableBodyLink = NewParagraph(context, 212, MarkdownElementKeys.TableCell);
        secondTableBodyLink.Add(new LinkRun("body link", "https://example.test/table-body"));
        var laterTableCell = NewParagraph(context, 213, MarkdownElementKeys.TableCell);
        laterTableCell.Add(new TextRun("later row"));
        var tableCellImageRun = new InlineImageRun(context, "table cell image", "https://example.test/table-cell.png");
        laterTableCell.Add(tableCellImageRun);
        var table = new TableBox(
            context,
            new[] { new[] { tableHeaderLink, secondTableHeaderLink } },
            new[] { new[] { tableBodyTask, secondTableBodyLink }, new[] { laterTableCell } })
        {
            BlockIndex = 200,
        };
        var outerOverflowOwner = new TestOverflowStackBox { BlockIndex = 190 };
        outerOverflowOwner.Add(NewParagraph(context, 199));
        outerOverflowOwner.Add(table);
        root.Add(outerOverflowOwner);

        CodeBlockBox copyCodeBlock = CreateCodeBlock(context, 200, isCopyButtonEnabled: true);
        var codeChunk = NewParagraph(context, 202, MarkdownElementKeys.CodeBlock);
        for (int index = 0; index < 128; index++)
            codeChunk.Add(new TextRun($"syntax-{index}"));
        codeChunk.Add(new LinkRun("chunk link must not focus", "https://example.test/chunk"));
        var codeChunkImageRun = new InlineImageRun(context, "code image", "https://example.test/code.png");
        codeChunk.Add(codeChunkImageRun);
        copyCodeBlock.AddChunk(codeChunk);
        root.Add(copyCodeBlock);

        MarkdownLayoutContext plainContext = CreateContext(flowDirection, CodeBlockChromeMode.PlainMarkdown);
        CodeBlockBox plainMarkdownCodeBlock = CreateCodeBlock(
            plainContext,
            203,
            isCopyButtonEnabled: true);
        root.Add(plainMarkdownCodeBlock);

        VectorSceneBox fallbackVector = CreateVectorBox(context, 200, Array.Empty<MarkdownVectorSemanticItem>());
        root.Add(fallbackVector);

        MarkdownVectorSemanticItem[] vectorSemantics =
        [
            CreateSemantic(1, MarkdownVectorSemanticFlags.Focusable),
            CreateSemantic(2, MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Decorative),
        ];
        VectorSceneBox focusableVector = CreateVectorBox(context, 200, vectorSemantics);
        root.Add(focusableVector);

        var blockEmbed = new EmbedBox(
            Markdig.Markdown.Parse("embedded block")[0],
            TestEmbedFactory.Instance)
        {
            BlockIndex = 205,
        };
        root.Add(blockEmbed);

        var hostedBuilder = new MarkdownContentBuilder();
        hostedBuilder.AddHostedElement(
            "test.hosted",
            new SourceSpan(0, 1),
            MarkdownStyleRole.Body,
            MarkdownAccessibilityRole.Group,
            accessibilityName: "Hosted region");
        MarkdownContentFragment hostedFragment = hostedBuilder.Build();
        var hosted = new DeclarativeHostedElementBox(
            context,
            Assert.Single(hostedFragment.Items),
            new HostedElementFallbackKey(hostedFragment))
        {
            BlockIndex = 206,
        };
        root.Add(hosted);

        var marker = NewParagraph(context, 208, MarkdownElementKeys.ListMarker);
        marker.Add(new InlineEmbedRun(14, 14, static () => null!)
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Checked",
                "Unchecked",
                "Task state",
                "Toggle task",
                "task-list-marker",
                isChecked: true),
        });
        var listContent = new StackBox { BlockIndex = 209 };
        var listParagraph = NewParagraph(context, 210);
        listParagraph.Add(new LinkRun("list link", "https://example.test/list"));
        listContent.Add(listParagraph);
        var listItem = new ListItemBox(marker, listContent, markerWidth: 22)
        {
            FlowDirection = flowDirection,
            BlockIndex = 207,
        };
        root.Add(listItem);

        var secondRoot = new StackBox { BlockIndex = 100 };
        secondRoot.Add(sharedInlineImageRun.Image);
        var decorativeImage = new ImageBox(context, "https://example.test/decorative.png", string.Empty)
        {
            BlockIndex = 220,
        };

        return new Fixture(
            context,
            new BlockBox[] { root, secondRoot, decorativeImage },
            sharedInlineImageRun.Image,
            sameRootImageRun.Image,
            codeChunkImageRun.Image,
            tableCellImageRun.Image,
            decorativeImage,
            codeChunk,
            table,
            outerOverflowOwner,
            copyCodeBlock,
            plainMarkdownCodeBlock,
            fallbackVector,
            focusableVector,
            hosted,
            blockEmbed,
            inline,
            readOnlyTask,
            DuplicateTopLevelBlockIndex: 100,
            DuplicateOverflowBlockIndex: 200,
            NestedOverflowDescendantBlockIndex: 199,
            TableCellBlockIndex: 201,
            LastDuplicateOverflowOwner: focusableVector);
    }

    private static CodeBlockBox CreateCodeBlock(
        MarkdownLayoutContext context,
        int blockIndex,
        bool isCopyButtonEnabled)
    {
        const string code = "var answer = 42;";
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, code.Length),
            code,
            "csharp",
            new Dictionary<string, string>());
        return new CodeBlockBox(context, metadata, code, isCopyButtonEnabled, showLineNumbers: false)
        {
            BlockIndex = blockIndex,
        };
    }

    private static VectorSceneBox CreateVectorBox(
        MarkdownLayoutContext context,
        int blockIndex,
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
    {
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            CreateVectorScene(semantics),
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Test diagram");
        MarkdownContentFragment fragment = builder.Build();
        return new VectorSceneBox(context, Assert.Single(fragment.Items), MarkdownElementKeys.Diagram)
        {
            BlockIndex = blockIndex,
        };
    }

    private static MarkdownVectorScene CreateVectorScene(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
        => new(
            width: 24,
            height: 12,
            baseline: 10,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 12,
            viewport: new MarkdownVectorRectangle(0, 0, 24, 12),
            semantics: semantics,
            links: Array.Empty<MarkdownVectorLinkAction>());

    private static MarkdownVectorSemanticItem CreateSemantic(
        uint sourceId,
        MarkdownVectorSemanticFlags flags)
        => new(
            sourceId: sourceId,
            role: MarkdownVectorSemanticRole.Node,
            name: $"node {sourceId}",
            description: null,
            parentIndex: -1,
            sourceSpan: new SourceSpan(checked((int)sourceId), 1),
            bounds: new MarkdownVectorRectangle(0, 0, 8, 6),
            flags: flags);

    private static InlineContainerBox NewParagraph(
        MarkdownLayoutContext context,
        int blockIndex,
        string? elementKey = null)
        => new(context, elementKey ?? MarkdownElementKeys.Body)
        {
            BlockIndex = blockIndex,
        };

    private static MarkdownLayoutContext CreateContext(
        FlowDirection flowDirection,
        CodeBlockChromeMode codeBlockChromeMode = CodeBlockChromeMode.Enhanced)
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ListMarker] = style,
            [MarkdownElementKeys.TableCell] = style,
            [MarkdownElementKeys.CodeBlock] = style,
            [MarkdownElementKeys.Math] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Link] = style,
        };
        Color black = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            black,
            black,
            black,
            black,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string('x', 4096)),
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            CodeBlockChromeMode = codeBlockChromeMode,
        };
    }

    private static T ReadField<T>(LayoutSnapshot snapshot, string name)
    {
        FieldInfo field = typeof(LayoutSnapshot).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Snapshot index field '{name}' was not found.");
        return Assert.IsType<T>(field.GetValue(snapshot));
    }

    private static T ReadAssignableField<T>(LayoutSnapshot snapshot, string name)
        where T : class
    {
        FieldInfo field = typeof(LayoutSnapshot).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Snapshot index field '{name}' was not found.");
        return Assert.IsAssignableFrom<T>(field.GetValue(snapshot));
    }

    private static void AssertDictionaryEquals<TKey, TValue>(
        Dictionary<TKey, TValue> expected,
        Dictionary<TKey, TValue> actual)
        where TKey : notnull
        where TValue : class
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<TKey, TValue> entry in expected)
        {
            Assert.True(actual.TryGetValue(entry.Key, out TValue? actualValue));
            Assert.Same(entry.Value, actualValue);
        }
    }

    private static void AssertReferenceSetEquals<T>(HashSet<T> expected, HashSet<T> actual)
        where T : class
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (T value in expected)
            Assert.Contains(value, actual);
    }

    private static void AssertFocusablePlanEquals(IReadOnlyList<LegacyFocusableEntry> expected, Array actual)
    {
        Assert.Equal(expected.Count, actual.Length);
        for (int index = 0; index < expected.Count; index++)
        {
            object entry = actual.GetValue(index)!;
            Type entryType = entry.GetType();
            var item = Assert.IsType<FocusableItem>(
                entryType.GetField("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(entry));
            var overflow = (IHorizontalOverflowBox?)entryType
                .GetField("HorizontalOverflow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(entry);
            Assert.Equal(expected[index].Item.BlockIndex, item.BlockIndex);
            Assert.Equal(expected[index].Item.InlineIndex, item.InlineIndex);
            Assert.Equal(expected[index].Item.Kind, item.Kind);
            Assert.Same(expected[index].Overflow, overflow);
        }
    }

    private static void AssertDictionaryEquals<TKey>(
        Dictionary<TKey, int> expected,
        IReadOnlyDictionary<TKey, int> actual)
        where TKey : notnull
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<TKey, int> entry in expected)
        {
            Assert.True(actual.TryGetValue(entry.Key, out int actualValue));
            Assert.Equal(entry.Value, actualValue);
        }
    }

    private sealed class LegacyIndexes
    {
        internal readonly Dictionary<BlockBox, int> BlockOrdinals = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<int, int> TopLevelOrdinalByBlockIndex = new();
        internal readonly Dictionary<ImageBox, int> ImageTopLevelOrdinalByIdentity = new(ReferenceEqualityComparer.Instance);
        internal readonly HashSet<IHorizontalOverflowBox> HorizontalOverflowBoxes = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<int, IHorizontalOverflowBox> HorizontalOverflowByBlockIndex = new();
        internal readonly List<LegacyFocusableEntry> FocusablePlan = new();

        internal static LegacyIndexes Build(IReadOnlyList<BlockBox> roots)
        {
            var indexes = new LegacyIndexes();
            for (int ordinal = 0; ordinal < roots.Count; ordinal++)
            {
                BlockBox root = roots[ordinal];
                indexes.BlockOrdinals[root] = ordinal;
                IndexBlockTree(root, ordinal, indexes.TopLevelOrdinalByBlockIndex);
                IndexImageTree(root, ordinal, indexes.ImageTopLevelOrdinalByIdentity);
                BuildLegacyFocusablePlan(root, indexes.FocusablePlan);
                CollectLegacyHorizontalOverflow(
                    root,
                    indexes.HorizontalOverflowBoxes,
                    indexes.HorizontalOverflowByBlockIndex,
                    ancestor: null);
            }
            return indexes;
        }

        private static void IndexBlockTree(BlockBox box, int ordinal, Dictionary<int, int> byBlockIndex)
        {
            byBlockIndex.TryAdd(box.BlockIndex, ordinal);
            switch (box)
            {
                case ListItemBox item:
                    IndexBlockTree(item.Marker, ordinal, byBlockIndex);
                    IndexBlockTree(item.Content, ordinal, byBlockIndex);
                    break;
                case StackBox stack:
                    foreach (BlockBox child in stack.Children)
                        IndexBlockTree(child, ordinal, byBlockIndex);
                    break;
                case CodeBlockBox code:
                    foreach (InlineContainerBox chunk in code.Chunks)
                        IndexBlockTree(chunk, ordinal, byBlockIndex);
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        IndexBlockTree(cell, ordinal, byBlockIndex);
                    break;
            }
        }

        private static void IndexImageTree(
            BlockBox box,
            int ordinal,
            Dictionary<ImageBox, int> byImageIdentity)
        {
            switch (box)
            {
                case ImageBox image:
                    AddImageOwner(image, ordinal, byImageIdentity);
                    break;
                case InlineContainerBox inline:
                    foreach ((InlineImageRun run, _) in inline.InlineImageRuns)
                        AddImageOwner(run.Image, ordinal, byImageIdentity);
                    break;
                case ListItemBox item:
                    IndexImageTree(item.Marker, ordinal, byImageIdentity);
                    IndexImageTree(item.Content, ordinal, byImageIdentity);
                    break;
                case StackBox stack:
                    foreach (BlockBox child in stack.Children)
                        IndexImageTree(child, ordinal, byImageIdentity);
                    break;
                case CodeBlockBox code:
                    foreach (InlineContainerBox chunk in code.Chunks)
                        IndexImageTree(chunk, ordinal, byImageIdentity);
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        IndexImageTree(cell, ordinal, byImageIdentity);
                    break;
            }
        }

        private static void AddImageOwner(
            ImageBox image,
            int ordinal,
            Dictionary<ImageBox, int> byImageIdentity)
        {
            if (byImageIdentity.TryGetValue(image, out int existing) && existing != ordinal)
                byImageIdentity[image] = -1;
            else
                byImageIdentity[image] = ordinal;
        }

        private static void CollectLegacyHorizontalOverflow(
            BlockBox box,
            HashSet<IHorizontalOverflowBox> result,
            Dictionary<int, IHorizontalOverflowBox> byBlockIndex,
            IHorizontalOverflowBox? ancestor)
        {
            if (box is IHorizontalOverflowBox overflow)
            {
                result.Add(overflow);
                ancestor = overflow;
            }
            if (ancestor is not null)
                byBlockIndex[box.BlockIndex] = ancestor;

            switch (box)
            {
                case ListItemBox item:
                    CollectLegacyHorizontalOverflow(item.Marker, result, byBlockIndex, ancestor);
                    CollectLegacyHorizontalOverflow(item.Content, result, byBlockIndex, ancestor);
                    break;
                case StackBox stack:
                    foreach (BlockBox child in stack.Children)
                        CollectLegacyHorizontalOverflow(child, result, byBlockIndex, ancestor);
                    break;
                case CodeBlockBox code:
                    foreach (InlineContainerBox chunk in code.Chunks)
                        CollectLegacyHorizontalOverflow(chunk, result, byBlockIndex, ancestor);
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        CollectLegacyHorizontalOverflow(cell, result, byBlockIndex, ancestor);
                    break;
            }
        }

        private static void BuildLegacyFocusablePlan(BlockBox box, List<LegacyFocusableEntry> plan)
        {
            int descendantStart = plan.Count;
            switch (box)
            {
                case InlineContainerBox inline:
                    foreach (InlineRun run in inline.Runs)
                    {
                        if (run is LinkRun or InlineImageRun { IsLinked: true } or
                            InlineVectorSceneRun { IsLinked: true })
                        {
                            plan.Add(new LegacyFocusableEntry(
                                new FocusableItem(inline.BlockIndex, run.InlineIndex, FocusableItemKind.Link),
                                null));
                        }
                        else if (run is InlineEmbedRun embed && LayoutSnapshot.IsInlineEmbedKeyboardFocusable(embed))
                        {
                            plan.Add(new LegacyFocusableEntry(
                                new FocusableItem(inline.BlockIndex, run.InlineIndex, FocusableItemKind.InlineEmbed),
                                null));
                        }
                    }
                    break;
                case EmbedBox embed:
                    plan.Add(new LegacyFocusableEntry(
                        new FocusableItem(embed.BlockIndex, 0, FocusableItemKind.BlockEmbed),
                        null));
                    break;
                case DeclarativeHostedElementBox hosted:
                    plan.Add(new LegacyFocusableEntry(
                        new FocusableItem(hosted.BlockIndex, 0, FocusableItemKind.DeclarativeHostedElement),
                        null));
                    break;
                case CodeBlockBox code when code.HasCopyButtonAction:
                    plan.Add(new LegacyFocusableEntry(
                        new FocusableItem(code.BlockIndex, 0, FocusableItemKind.CodeBlockCopy),
                        null));
                    break;
                case VectorSceneBox vector:
                    for (int semanticIndex = 0; semanticIndex < vector.SemanticItems.Count; semanticIndex++)
                    {
                        if (MarkdownVectorSemanticPolicy.IsKeyboardFocusable(vector.SemanticItems[semanticIndex].Flags))
                        {
                            plan.Add(new LegacyFocusableEntry(
                                new FocusableItem(vector.BlockIndex, semanticIndex, FocusableItemKind.VectorSemantic),
                                null));
                        }
                    }
                    break;
                case ListItemBox item:
                    BuildLegacyFocusablePlan(item.Marker, plan);
                    BuildLegacyFocusablePlan(item.Content, plan);
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        BuildLegacyFocusablePlan(cell, plan);
                    break;
                case StackBox stack:
                    foreach (BlockBox child in stack.Children)
                        BuildLegacyFocusablePlan(child, plan);
                    break;
            }

            if (plan.Count == descendantStart && box is IHorizontalOverflowBox overflow)
            {
                plan.Add(new LegacyFocusableEntry(
                    new FocusableItem(box.BlockIndex, 0, FocusableItemKind.HorizontalOverflow),
                    overflow));
            }
        }
    }

    private sealed record Fixture(
        MarkdownLayoutContext Context,
        IReadOnlyList<BlockBox> Roots,
        ImageBox SharedImage,
        ImageBox SameRootImage,
        ImageBox CodeChunkImage,
        ImageBox TableCellImage,
        ImageBox DecorativeImage,
        InlineContainerBox CodeChunk,
        TableBox Table,
        IHorizontalOverflowBox OuterOverflowOwner,
        CodeBlockBox CopyCodeBlock,
        CodeBlockBox PlainMarkdownCodeBlock,
        VectorSceneBox FallbackVector,
        VectorSceneBox FocusableVector,
        DeclarativeHostedElementBox HostedElement,
        EmbedBox BlockEmbed,
        InlineContainerBox ReadOnlyTaskParagraph,
        InlineEmbedRun ReadOnlyTaskRun,
        int DuplicateTopLevelBlockIndex,
        int DuplicateOverflowBlockIndex,
        int NestedOverflowDescendantBlockIndex,
        int TableCellBlockIndex,
        IHorizontalOverflowBox LastDuplicateOverflowOwner);

    private sealed record LegacyFocusableEntry(FocusableItem Item, IHorizontalOverflowBox? Overflow);

    private sealed class TestOverflowStackBox : StackBox, IHorizontalOverflowBox
    {
        public double HorizontalOffset => 0;
        public double HorizontalExtent => 200;
        public double HorizontalViewport => 100;
        public bool CanScrollHorizontally => true;
        public bool IsRightToLeft => false;
        public Windows.Foundation.Rect HorizontalViewportBounds => default;
        public Windows.Foundation.Rect HorizontalScrollTrackBounds => default;
        public Windows.Foundation.Rect HorizontalScrollThumbBounds => default;
        public bool SetHorizontalOffset(double offset) => false;
        public bool ScrollHorizontal(double delta) => false;
    }

    private sealed class TestEmbedFactory : IMarkdownEmbedFactory
    {
        internal static readonly TestEmbedFactory Instance = new();
        public bool CanCreate(Block block) => true;
        public float MeasureHeight(Block block, float availableWidth) => 20;
        public FrameworkElement CreateBlock(Block block) => null!;
    }

    private sealed class CancelAfterMeasureEmbedFactory : IMarkdownEmbedFactory
    {
        internal Action? AfterMeasure { get; set; }

        public bool CanCreate(Block block) => true;

        public float MeasureHeight(Block block, float availableWidth)
        {
            AfterMeasure?.Invoke();
            return 20;
        }

        public FrameworkElement CreateBlock(Block block) => null!;
    }

    private sealed class ThrowingVectorResidencyPolicy : IMarkdownVectorSceneResidencyPolicy
    {
        internal const string FailureMessage = "test residency preparation failure";
        private readonly int _throwOnCall;
        private int _replaceCallCount;

        internal ThrowingVectorResidencyPolicy(int throwOnCall)
            => _throwOnCall = throwOnCall;

        public int ActiveSceneCount => 0;
        public long ActiveEstimatedBytes => 0;
        public int InlineGeometryIndexBuildCount => 0;
        public int CommandGraphEstimateCount => 0;
        public int ActiveInlineSceneGeometryIndexCount => 0;
        public List<string> VisibleDeferredSceneMarkerScratch { get; } = new();

        public bool AreNativeResourcesPrepared(
            IReadOnlyList<int> topLevelOrdinals,
            LazyLayoutBand band,
            LayoutPlacementGeneration placements,
            IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
            => false;

        public bool ReplaceActiveBand(
            IReadOnlyList<int> topLevelOrdinals,
            LazyLayoutBand band,
            LayoutPlacementGeneration placements,
            IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
            CancellationToken cancellationToken)
        {
            if (++_replaceCallCount == _throwOnCall)
                throw new InvalidOperationException(FailureMessage);
            return false;
        }

        public bool TryAppendVisibleInlineSceneMarkers(
            InlineContainerBox inline,
            double viewportTop,
            double viewportBottom,
            Windows.Foundation.Rect? viewport,
            List<string> markerKeys,
            ref bool visibleSceneResourcesReady)
            => false;

        public void ReleaseUnpinnedResources(
            IReadOnlyList<int> topLevelOrdinals,
            LazyLayoutBand band,
            LayoutPlacementGeneration placements,
            IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
        {
        }

        public void Dispose()
        {
        }
    }
}
