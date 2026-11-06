using System;
using System.Collections.Generic;
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
/// <remarks>
/// This file is longer than 500 lines because every scenario compares the fused
/// constructor indexes with the same legacy per-index walks, and those shared
/// legacy comparison helpers belong with the scenarios that use them.
/// </remarks>
public sealed class LayoutSnapshotIndexConstructionTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Theory]
    [InlineData(FlowDirection.LeftToRight, 0)]
    [InlineData(FlowDirection.RightToLeft, 0)]
    [InlineData(FlowDirection.LeftToRight, 199)]
    [InlineData(FlowDirection.RightToLeft, 199)]
    public void FusedIndexesAndFocusablePlanMatchLegacyPreorder(
        FlowDirection flowDirection, int generatedBlockIndexUpperBound)
    {
        SnapshotIndexFixture fixture = SnapshotIndexFixture.Create(_renderResources, flowDirection);
        using LayoutSnapshot snapshot = new(
            fixture.Roots,
            fixture.Context.SourceMap,
            width: 360,
            height: 640,
            generatedBlockIndexUpperBound: generatedBlockIndexUpperBound);

        LegacySnapshotIndexes expected = LegacySnapshotIndexes.Build(fixture.Roots);
        AssertFusedIndexesMatchLegacy(snapshot, expected);
        AssertDuplicateOwnershipRules(snapshot, fixture);
        AssertLegacyFocusPlanShape(expected, fixture);

        // Dynamic overflow entries are resolved at collection time; the
        // constructor must not allocate and then discard a fixed item array.
        Assert.Null(snapshot.Focusables.FixedItems);
    }

    private static void AssertFusedIndexesMatchLegacy(LayoutSnapshot snapshot, LegacySnapshotIndexes expected)
    {
        AssertDictionaryEquals(expected.BlockOrdinals, ReadField<Dictionary<BlockBox, int>>(snapshot.Geometry.BlockOrdinals));
        AssertDictionaryEquals(
            expected.TopLevelOrdinalByBlockIndex,
            ReadAssignableField<IReadOnlyDictionary<int, int>>(snapshot.Geometry.TopLevelOrdinalByBlockIndex));
        AssertDictionaryEquals(
            expected.ImageTopLevelOrdinalByIdentity,
            ReadField<Dictionary<ImageBox, int>>(snapshot.ImageTopLevelOrdinals));
        AssertReferenceSetEquals(
            expected.HorizontalOverflowBoxes,
            ReadField<HashSet<IHorizontalOverflowBox>>(snapshot.HorizontalOverflow.Boxes));
        AssertDictionaryEquals(
            expected.HorizontalOverflowByBlockIndex,
            ReadField<Dictionary<int, IHorizontalOverflowBox>>(snapshot.HorizontalOverflow.ByBlockIndex));
        object focusPlanValue = snapshot.Focusables.Entries;
        AssertFocusablePlanEquals(expected.FocusablePlan, Assert.IsAssignableFrom<Array>(focusPlanValue));
    }

    private static void AssertDuplicateOwnershipRules(LayoutSnapshot snapshot, SnapshotIndexFixture fixture)
    {
        // Shared image identity is stable within one root but deliberately
        // becomes ambiguous when the same image is encountered under another
        // top-level root. Duplicate block indexes remain first-wins for root
        // ownership and last-wins for the horizontal-overflow alias map.
        IReadOnlyDictionary<int, int> topOrdinals = ReadAssignableField<IReadOnlyDictionary<int, int>>(snapshot.Geometry.TopLevelOrdinalByBlockIndex);
        Assert.Equal(0, topOrdinals[fixture.DuplicateTopLevelBlockIndex]);
        Dictionary<ImageBox, int> imageOrdinals = ReadField<Dictionary<ImageBox, int>>(snapshot.ImageTopLevelOrdinals);
        Assert.Equal(-1, imageOrdinals[fixture.SharedImage]);
        Assert.Equal(0, imageOrdinals[fixture.SameRootImage]);
        Assert.Equal(0, imageOrdinals[fixture.CodeChunkImage]);
        Assert.Equal(0, imageOrdinals[fixture.TableCellImage]);
        Assert.Equal(2, imageOrdinals[fixture.DecorativeImage]);
        Dictionary<int, IHorizontalOverflowBox> horizontalByIndex = ReadField<Dictionary<int, IHorizontalOverflowBox>>(snapshot.HorizontalOverflow.ByBlockIndex);
        Assert.Same(fixture.OuterOverflowOwner, horizontalByIndex[fixture.NestedOverflowDescendantBlockIndex]);
        Assert.Same(fixture.Table, horizontalByIndex[fixture.TableCellBlockIndex]);
        Assert.Same(fixture.LastDuplicateOverflowOwner, horizontalByIndex[fixture.DuplicateOverflowBlockIndex]);
    }

    private static void AssertLegacyFocusPlanShape(LegacySnapshotIndexes expected, SnapshotIndexFixture fixture)
    {
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
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void LayoutPlanCandidateIndexMatchesLegacyRealizationTraversal(FlowDirection flowDirection)
    {
        SnapshotIndexFixture fixture = SnapshotIndexFixture.Create(_renderResources, flowDirection);
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
        BlockBox[] roots = CreateFixedEmbedRoots(blockCount);

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
            AssertFinalizedMeasuredOrdinals(
                snapshot, roots, firstMeasuredOrdinal, secondMeasuredOrdinal, thirdMeasuredOrdinal);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static BlockBox[] CreateFixedEmbedRoots(int blockCount)
    {
        Block sourceBlock = Markdig.Markdown.Parse("embedded")[0];
        var roots = new BlockBox[blockCount];
        for (int ordinal = 0; ordinal < roots.Length; ordinal++)
        {
            roots[ordinal] = new EmbedBox(sourceBlock, SnapshotIndexEmbedFactory.Instance)
            {
                BlockIndex = ordinal,
            };
        }

        return roots;
    }

    private static void AssertFinalizedMeasuredOrdinals(
        LayoutSnapshot snapshot,
        BlockBox[] roots,
        int firstMeasuredOrdinal,
        int secondMeasuredOrdinal,
        int thirdMeasuredOrdinal)
    {
        bool[] measuredMask = ReadField<bool[]>(snapshot.Geometry.MeasuredTopLevelBlocks);
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

        List<int> finalizedOrdinals = ReadField<List<int>>(snapshot.PlanCandidates.MeasuredRootOrdinals);
        Assert.Equal(expectedOrdinals, finalizedOrdinals);
        Assert.True(ReadField<bool>(snapshot.PlanCandidates.MeasuredRootOrdinalsAreSorted));
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
        Assert.Same(finalizedOrdinals, ReadField<List<int>>(snapshot.PlanCandidates.MeasuredRootOrdinals));
        Assert.Equal(ordinalsBeforeEnumeration, finalizedOrdinals);
        Assert.True(ReadField<bool>(snapshot.PlanCandidates.MeasuredRootOrdinalsAreSorted));
        Assert.True(snapshot.TryGetMeasuredBlockByOrdinalUnderPaint(
            secondMeasuredOrdinal,
            out BlockBox secondMeasuredRoot));
        Assert.Same(roots[secondMeasuredOrdinal], secondMeasuredRoot);
        Assert.True(snapshot.TryGetMeasuredBlockByOrdinalUnderPaint(
            firstMeasuredOrdinal,
            out BlockBox firstMeasuredRoot));
        Assert.Same(roots[firstMeasuredOrdinal], firstMeasuredRoot);
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

        List<int> priorOrdinals = ReadField<List<int>>(snapshot.PlanCandidates.MeasuredRootOrdinals);
        int[] priorOrdinalValues = priorOrdinals.ToArray();
        Assert.Contains(initiallyMeasuredOrdinal, priorOrdinalValues);
        Assert.Equal(priorOrdinalValues.Order(), priorOrdinalValues);
        Assert.True(ReadField<bool>(snapshot.PlanCandidates.MeasuredRootOrdinalsAreSorted));
        bool[] priorMeasuredMask = ReadField<bool[]>(snapshot.Geometry.MeasuredTopLevelBlocks);
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
        Assert.Same(priorMeasuredMask, ReadField<bool[]>(snapshot.Geometry.MeasuredTopLevelBlocks));
        Assert.Equal(priorMask, priorMeasuredMask);
        Assert.Same(priorOrdinals, ReadField<List<int>>(snapshot.PlanCandidates.MeasuredRootOrdinals));
        Assert.Equal(priorOrdinalValues, priorOrdinals);
        Assert.True(ReadField<bool>(snapshot.PlanCandidates.MeasuredRootOrdinalsAreSorted));
        Assert.False(priorMeasuredMask[canceledOrdinal]);
    }

    [Fact]
    public void FailedLazyResidencyCommitRollsBackMeasuredCandidateRootIndex()
    {
        Block sourceBlock = Markdig.Markdown.Parse("embedded")[0];
        BlockBox[] roots =
        [
            new EmbedBox(sourceBlock, SnapshotIndexEmbedFactory.Instance) { BlockIndex = 0 },
            new EmbedBox(sourceBlock, SnapshotIndexEmbedFactory.Instance) { BlockIndex = 1 },
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
        Assert.Equal(1, ReadField<int>(snapshot.Geometry.MeasuredTopLevelBlockCount));
        Assert.Equal(new[] { 0 }, ReadField<List<int>>(snapshot.PlanCandidates.MeasuredRootOrdinals));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(roots[1].Bounds.Top + 0.125d, roots[1].Bounds.Top + 0.5d),
                CancellationToken.None));
        Assert.Equal(ThrowingVectorResidencyPolicy.FailureMessage, failure.Message);
        Assert.Equal(1, ReadField<int>(snapshot.Geometry.MeasuredTopLevelBlockCount));
        bool[] measuredMask = ReadField<bool[]>(snapshot.Geometry.MeasuredTopLevelBlocks);
        Assert.True(measuredMask[0]);
        Assert.False(measuredMask[1]);
        Assert.Equal(new[] { 0 }, ReadField<List<int>>(snapshot.PlanCandidates.MeasuredRootOrdinals));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void FixedFocusableItemsMatchLegacyPlanWhenNoDynamicOverflow(FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = SnapshotIndexFixture.CreateContext(_renderResources, flowDirection);
        var paragraph = SnapshotIndexFixture.NewParagraph(context, blockIndex: 801);
        var link = new LinkRun("fixed link", "https://example.test/fixed-link");
        paragraph.Add(link);
        var inlineAction = new InlineEmbedRun(16, 16, static () => null!);
        paragraph.Add(inlineAction);

        var root = new StackBox { BlockIndex = 800 };
        root.Add(paragraph);
        var blockEmbed = new EmbedBox(
            Markdig.Markdown.Parse("fixed block embed")[0],
            SnapshotIndexEmbedFactory.Instance)
        {
            BlockIndex = 802,
        };
        root.Add(blockEmbed);

        using LayoutSnapshot snapshot = new(
            new BlockBox[] { root },
            context.SourceMap,
            width: 360,
            height: 640);

        LegacySnapshotIndexes expected = LegacySnapshotIndexes.Build(new BlockBox[] { root });
        Assert.All(expected.FocusablePlan, entry => Assert.Null(entry.Overflow));
        FocusableItem[] actual = ReadField<FocusableItem[]>(snapshot.Focusables.FixedItems);
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
        MarkdownLayoutContext context = SnapshotIndexFixture.CreateContext(_renderResources, FlowDirection.LeftToRight);
        var paragraph = SnapshotIndexFixture.NewParagraph(context, blockIndex);
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


    private static T ReadField<T>(object? value)
        => Assert.IsType<T>(value);

    private static T ReadAssignableField<T>(object? value)
        where T : class
        => Assert.IsAssignableFrom<T>(value);

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
        FocusablePlanEntry[] entries = Assert.IsType<FocusablePlanEntry[]>(actual);
        for (int index = 0; index < expected.Count; index++)
        {
            FocusablePlanEntry entry = entries[index];
            FocusableItem item = entry.Item;
            IHorizontalOverflowBox? overflow = entry.HorizontalOverflow;
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
}
