using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class PrivateRasterPlacementTreeTests
{
    private const float Width = 320f;

    [Fact]
    public async Task NestedListTreeClonesEveryMutableWrapperAndPreservesSourceGeometry()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        MarkdownLayoutContext targetContext = fixture.CreateCandidateContext();
        StackBox? sourceRoot = null;
        StackBox? content = null;
        InlineContainerBox? sourceLeaf = null;
        TableBox? sourceTable = null;
        LayoutSnapshot? sourceSnapshot = null;
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            (sourceRoot, content, ListItemBox sourceItem, InlineContainerBox sourceMarker, sourceTable, sourceLeaf) =
                CreateNestedListRoot(fixture);
            _ = sourceRoot.Measure(Width);
            sourceRoot.Arrange(0, 0, Width);
            Rect sourceBounds = sourceRoot.Bounds;
            Rect sourceLeafBounds = sourceLeaf.Bounds;

            Assert.True(PrivateRasterPlacementTree.TryGetFirstInlineContext(sourceRoot, out MarkdownLayoutContext? firstContext));
            Assert.Same(fixture.SourceContext, firstContext);

            var capture = new PrivateRasterPlacementTree.CaptureState();
            Assert.True(capture.TryAddRoot(
                sourceRoot,
                fixture.SourceContext,
                targetContext,
                Width,
                out PrivateRasterPlacementTree.RootCounts counts));
            Assert.Equal(7, counts.NodeCount);
            Assert.Equal(4, counts.RunCount);
            Assert.Equal(1, counts.ImageCount);
            Assert.Equal(1, capture.RootCount);
            Assert.Equal(1, capture.ImageCount);

            sourceSnapshot = new LayoutSnapshot(
                new BlockBox[] { sourceRoot },
                fixture.SourceContext.SourceMap,
                Width,
                (float)sourceRoot.Bounds.Height);
            candidate = sourceSnapshot.CreateSingleRasterCandidate(
                [sourceLeaf.BlockIndex],
                Width,
                CancellationToken.None,
                CancellationToken.None,
                Assert.IsAssignableFrom<object>(fixture.SourceContext.ImagePreparationOwner),
                retirementSink: null,
                imageUnavailable: null,
                out candidateContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            Assert.Equal(1, owner.SourceImageCount);
            Assert.True(owner.HasCurrentSourceRaster());
            ImageBox[] sourceImages = sourceLeaf.InlineImageRuns
                .Select(static entry => entry.Run.Image)
                .ToArray();
            Assert.True(owner.MatchesSourceImages(
                sourceImages,
                new HashSet<ImageBox>(sourceImages, ReferenceEqualityComparer.Instance)));

            LayoutSnapshot replacement = owner.Snapshot;
            StackBox clonedRoot = Assert.IsType<StackBox>(Assert.Single(replacement.Blocks));
            ListItemBox clonedItem = Assert.IsType<ListItemBox>(Assert.Single(clonedRoot.Children));
            StackBox clonedContent = clonedItem.Content;
            TableBox clonedTable = Assert.IsType<TableBox>(Assert.Single(clonedContent.Children));
            InlineContainerBox[] clonedCells = clonedTable.GetCellBoxes().ToArray();
            InlineContainerBox clonedLeaf = clonedCells[0];
            InlineContainerBox clonedEmptyCell = clonedCells[1];
            InlineContainerBox clonedMarker = clonedItem.Marker;
            Assert.NotSame(sourceRoot, clonedRoot);
            Assert.NotSame(sourceItem, clonedItem);
            Assert.NotSame(content, clonedContent);
            Assert.NotSame(sourceMarker, clonedMarker);
            Assert.NotSame(sourceTable, clonedTable);
            Assert.NotSame(sourceLeaf, clonedLeaf);
            Assert.NotSame(sourceLeaf.Runs[0], clonedLeaf.Runs[0]);
            Assert.NotSame(Assert.Single(sourceTable.GetCellBoxes().Skip(1)), clonedEmptyCell);
            Assert.Same(fixture.SourceContext.SourceMap, targetContext.SourceMap);
            Assert.Same(fixture.SourceContext.SourceMap, replacement.SourceMap);
            Assert.Equal(sourceSnapshot.SemanticDocument.Text, replacement.SemanticDocument.Text);
            Assert.Equal(
                sourceSnapshot.SemanticDocument.SemanticRangeFingerprint,
                replacement.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(sourceRoot.Margin, clonedRoot.Margin);
            Assert.Equal(sourceItem.Margin, clonedItem.Margin);
            Assert.True(clonedLeaf.HasMeasuredLayout);
            Assert.Same(fixture.ImageBoxes[0].Bitmap, clonedLeaf.InlineImageRuns[0].Run.Image.Bitmap);
            Assert.Equal(sourceBounds, sourceRoot.Bounds);
            Assert.Equal(sourceLeafBounds, sourceLeaf.Bounds);
        }
        finally
        {
            try
            {
                if (candidate is not null)
                    candidate.Dispose();
            }
            finally
            {
                try
                {
                    if (candidateContext is not null)
                        await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    try
                    {
                        if (sourceRoot is not null)
                        {
                            // The fixture owns the original; this tree contains
                            // a separate private source-context clone.
                            if (sourceSnapshot is not null)
                                await sourceSnapshot.Retire().WaitAsync(TimeSpan.FromSeconds(30));
                            else
                                fixture.SourceContext.DiscardConstructionBlock(sourceRoot);
                        }
                    }
                    finally
                    {
                        try
                        {
                            await fixture.SourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                        }
                        finally
                        {
                            await targetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public async Task UnsupportedLaterNodeAndRepeatedBoxAliasFailClosedDuringPreflight()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        MarkdownLayoutContext targetContext = fixture.CreateCandidateContext();
        StackBox? unsupportedRoot = null;
        StackBox? aliasRoot = null;
        StackBox? duplicateIndexRoot = null;
        InlineContainerBox? duplicateIndexLeaf = null;
        StackBox? unsupportedContent = null;
        try
        {
            (unsupportedRoot, unsupportedContent, _, _, _, _) = CreateNestedListRoot(fixture);
            unsupportedContent.Add(new ThematicBreakBox(fixture.SourceContext) { BlockIndex = 0 });

            var unsupportedCapture = new PrivateRasterPlacementTree.CaptureState();
            Assert.False(unsupportedCapture.TryAddRoot(
                unsupportedRoot,
                fixture.SourceContext,
                targetContext,
                Width,
                out _));
            // A partial walk has already populated identity/count state. It
            // must be unusable after the later unsupported node is found.
            Assert.False(unsupportedCapture.TryAddRoot(
                fixture.Source,
                fixture.SourceContext,
                targetContext,
                Width,
                out _));
            BlockBox[] unsupportedChildren = unsupportedContent.DetachChildrenForTransfer();
            foreach (BlockBox child in unsupportedChildren)
            {
                if (!ReferenceEquals(child, fixture.Source))
                    fixture.SourceContext.DiscardConstructionBlock(child);
            }
            unsupportedContent = null;

            aliasRoot = new StackBox { BlockIndex = 0 };
            aliasRoot.Add(fixture.Source);
            aliasRoot.Add(fixture.Source);
            var aliasCapture = new PrivateRasterPlacementTree.CaptureState();
            Assert.False(aliasCapture.TryAddRoot(
                aliasRoot,
                fixture.SourceContext,
                targetContext,
                Width,
                out _));
            Assert.True(PrivateRasterPlacementTree.TryGetFirstInlineContext(aliasRoot, out _));

            duplicateIndexLeaf = new InlineContainerBox(fixture.SourceContext, MarkdownElementKeys.Body)
            {
                BlockIndex = fixture.Source.BlockIndex,
            };
            duplicateIndexLeaf.Add(new TextRun("duplicate selectable index"));
            _ = duplicateIndexLeaf.Measure(Width);
            duplicateIndexLeaf.Arrange(0, 0, Width);
            duplicateIndexRoot = new StackBox { BlockIndex = 0 };
            duplicateIndexRoot.Add(fixture.Source);
            duplicateIndexRoot.Add(duplicateIndexLeaf);
            Assert.False(new PrivateRasterPlacementTree.CaptureState().TryAddRoot(
                duplicateIndexRoot,
                fixture.SourceContext,
                targetContext,
                Width,
                out _));
        }
        finally
        {
            try
            {
                if (aliasRoot is not null)
                {
                    aliasRoot.DetachChildrenForTransfer();
                    fixture.SourceContext.DiscardConstructionBlock(aliasRoot);
                }
                if (duplicateIndexRoot is not null)
                {
                    BlockBox[] detached = duplicateIndexRoot.DetachChildrenForTransfer();
                    foreach (BlockBox child in detached)
                    {
                        if (!ReferenceEquals(child, fixture.Source))
                            fixture.SourceContext.DiscardConstructionBlock(child);
                    }
                    fixture.SourceContext.DiscardConstructionBlock(duplicateIndexRoot);
                }
                if (unsupportedRoot is not null)
                {
                    if (unsupportedContent is not null)
                    {
                        BlockBox[] detached = unsupportedContent.DetachChildrenForTransfer();
                        foreach (BlockBox child in detached)
                        {
                            if (!ReferenceEquals(child, fixture.Source))
                                fixture.SourceContext.DiscardConstructionBlock(child);
                        }
                    }
                    else
                    {
                        FindStackContainingSource(unsupportedRoot, fixture.Source)?.DetachChildrenForTransfer();
                    }
                    fixture.SourceContext.DiscardConstructionBlock(unsupportedRoot);
                }
            }
            finally
            {
                try
                {
                    await fixture.SourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    await targetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }
    }

    [Fact]
    public async Task DeepAndOverwideTreesRejectBeforeAnUnboundedContextQueueIsBuilt()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        MarkdownLayoutContext targetContext = fixture.CreateCandidateContext();
        StackBox? deepRoot = null;
        StackBox? wideRoot = null;
        try
        {
            deepRoot = new StackBox { BlockIndex = 0 };
            StackBox cursor = deepRoot;
            for (int depth = 0; depth < PrivateRasterPlacementTree.MaximumDepth; depth++)
            {
                var child = new StackBox { BlockIndex = 0 };
                cursor.Add(child);
                cursor = child;
            }
            cursor.Add(fixture.Source);

            Assert.False(PrivateRasterPlacementTree.TryGetFirstInlineContext(deepRoot, out _));
            Assert.False(new PrivateRasterPlacementTree.CaptureState().TryAddRoot(
                deepRoot,
                fixture.SourceContext,
                targetContext,
                Width,
                out _));

            wideRoot = new StackBox { BlockIndex = 0 };
            for (int index = 0; index < PrivateRasterPlacementTree.MaximumNodes; index++)
                wideRoot.Add(new StackBox { BlockIndex = 0 });
            wideRoot.Add(fixture.Source);
            Assert.False(PrivateRasterPlacementTree.TryGetFirstInlineContext(wideRoot, out _));
            Assert.False(new PrivateRasterPlacementTree.CaptureState().TryAddRoot(
                wideRoot,
                fixture.SourceContext,
                targetContext,
                Width,
                out _));
        }
        finally
        {
            try
            {
                if (deepRoot is not null)
                {
                    FindStackContainingSource(deepRoot, fixture.Source)?.DetachChildrenForTransfer();
                    fixture.SourceContext.DiscardConstructionBlock(deepRoot);
                }
                if (wideRoot is not null)
                {
                    FindStackContainingSource(wideRoot, fixture.Source)?.DetachChildrenForTransfer();
                    fixture.SourceContext.DiscardConstructionBlock(wideRoot);
                }
            }
            finally
            {
                try
                {
                    await fixture.SourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    await targetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }
    }

    [Fact]
    public async Task CanceledCapturePoisonsItsIdentityState()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        MarkdownLayoutContext canceledContext = fixture.CreateCandidateContext(cancellation.Token);
        MarkdownLayoutContext nextContext = fixture.CreateCandidateContext();
        StackBox? sourceRoot = null;
        StackBox? content = null;
        try
        {
            (sourceRoot, content, _, _, _, _) = CreateNestedListRoot(fixture);
            var capture = new PrivateRasterPlacementTree.CaptureState();
            Assert.Throws<OperationCanceledException>(() => capture.TryAddRoot(
                sourceRoot,
                fixture.SourceContext,
                canceledContext,
                Width,
                out _));
            Assert.False(capture.TryAddRoot(
                sourceRoot,
                fixture.SourceContext,
                nextContext,
                Width,
                out _));
        }
        finally
        {
            try
            {
                if (sourceRoot is not null)
                    fixture.SourceContext.DiscardConstructionBlock(sourceRoot);
            }
            finally
            {
                try
                {
                    await fixture.SourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    try
                    {
                        await canceledContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    }
                    finally
                    {
                        await nextContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    }
                }
            }
        }
    }

    private static (
        StackBox Root,
        StackBox Content,
        ListItemBox Item,
        InlineContainerBox Marker,
        TableBox Table,
        InlineContainerBox ImageLeaf)
        CreateNestedListRoot(MultiRasterPlacementCloneTests.MultiRasterFixture fixture)
    {
        var marker = new InlineContainerBox(fixture.SourceContext, MarkdownElementKeys.ListMarker)
        {
            BlockIndex = fixture.SourceContext.NextBlockIndex(),
            Margin = new Thickness(1, 2, 3, 4),
        };
        marker.Add(new TextRun("1.") { ElementKey = MarkdownElementKeys.ListMarker });
        _ = marker.Measure(24f);
        marker.Arrange(0, 0, 24f);

        if (!fixture.Source.TryCreatePrivateInlinePlacementClone(
                fixture.SourceContext,
                Width,
                out InlineContainerBox? imageLeaf,
                out _) || imageLeaf is null)
            throw new InvalidOperationException("The fixture's completed raster leaf could not be privately cloned.");

        var emptyCell = new InlineContainerBox(fixture.SourceContext, MarkdownElementKeys.TableCell)
        {
            BlockIndex = fixture.SourceContext.NextBlockIndex(),
        };
        _ = emptyCell.Measure(Width);
        emptyCell.Arrange(0, 0, Width);
        var table = new TableBox(
            fixture.SourceContext,
            Array.Empty<InlineContainerBox[]>(),
            new[] { new[] { imageLeaf, emptyCell } },
            new[] { TableBox.CellAlignment.Left, TableBox.CellAlignment.Right })
        {
            BlockIndex = 0,
        };

        var content = new StackBox
        {
            BlockIndex = 0,
            ContentPadding = new Thickness(2, 3, 4, 5),
            FlowDirection = FlowDirection.LeftToRight,
        };
        content.Add(table);

        var item = new ListItemBox(marker, content, 24f)
        {
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Thickness(5, 6, 7, 8),
        };
        var root = new StackBox
        {
            BlockIndex = 0,
            ContentPadding = new Thickness(7, 9, 11, 13),
            AccentBar = Windows.UI.Color.FromArgb(255, 1, 2, 3),
            InterChildSpacing = 4f,
        };
        root.Add(item);
        return (root, content, item, marker, table, imageLeaf);
    }

    private static StackBox? FindStackContainingSource(BlockBox root, InlineContainerBox source)
    {
        if (root is StackBox stack)
        {
            if (stack.Children.Any(child => ReferenceEquals(child, source)))
                return stack;
            foreach (BlockBox child in stack.Children)
            {
                StackBox? found = FindStackContainingSource(child, source);
                if (found is not null)
                    return found;
            }
        }
        else if (root is ListItemBox item)
        {
            return FindStackContainingSource(item.Content, source);
        }

        return null;
    }
}
