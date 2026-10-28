using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotRelayoutCancellationCommitTests
{
    [Fact]
    public void CancellationFromSingleChangedArrangeCompletesGeometryAndMetadataCommit()
    {
        var first = new ProbeBlock(24) { BlockIndex = 0 };
        var changed = new ProbeBlock(40) { BlockIndex = 1 };
        var trailing = new ProbeBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        using var cancellation = new CancellationTokenSource();
        changed.ObserveCancellationToken(cancellation.Token);
        changed.CancelOnNextArrange(cancellation);
        changed.SetHeight(55);
        double oldHeight = snapshot.Size.Height;
        double oldTrailingTop = trailing.Bounds.Top;
        long oldRevision = snapshot.LayoutRevision;

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(false, changed.EffectiveTokenCanBeCanceledAtCancellation);
        Assert.Equal(55, changed.Bounds.Height);
        Assert.Equal(oldTrailingTop + 15, trailing.Bounds.Top);
        Assert.Equal(oldHeight + 15, snapshot.Size.Height);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        AssertViewportIndex(snapshot, first, changed, trailing);
    }

    [Fact]
    public void MultipleChangedOwnersAreMeasuredBeforeTheNonCancelableSuffixCommit()
    {
        var first = new ProbeBlock(20) { BlockIndex = 0 };
        var firstChanged = new ProbeBlock(30) { BlockIndex = 1 };
        var middle = new ProbeBlock(25) { BlockIndex = 2 };
        var secondChanged = new ProbeBlock(35) { BlockIndex = 3 };
        var trailing = new ProbeBlock(40) { BlockIndex = 4 };
        using LayoutSnapshot snapshot = CreateSnapshot(
            [first, firstChanged, middle, secondChanged, trailing]);
        using var cancellation = new CancellationTokenSource();
        firstChanged.ObserveCancellationToken(cancellation.Token);
        firstChanged.BeforeArrange = () => Assert.Equal(2, secondChanged.MeasureCount);
        firstChanged.CancelOnNextArrange(cancellation);
        firstChanged.SetHeight(37);
        secondChanged.SetHeight(44);
        double oldTrailingTop = trailing.Bounds.Top;
        double oldHeight = snapshot.Size.Height;
        long oldRevision = snapshot.LayoutRevision;

        snapshot.RelayoutChangedBlocks(
            [firstChanged.BlockIndex, secondChanged.BlockIndex],
            200,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(false, firstChanged.EffectiveTokenCanBeCanceledAtCancellation);
        Assert.Equal(37, firstChanged.Bounds.Height);
        Assert.Equal(44, secondChanged.Bounds.Height);
        Assert.Equal(oldTrailingTop + 16, trailing.Bounds.Top);
        Assert.Equal(oldHeight + 16, snapshot.Size.Height);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        AssertViewportIndex(snapshot, first, firstChanged, middle, secondChanged, trailing);
    }

    [Fact]
    public void NestedStackArrangementIgnoresCancellationUntilSuffixCommitPublishes()
    {
        using var cancellation = new CancellationTokenSource();
        var firstChild = new ProbeBlock(10) { BlockIndex = 100 };
        var secondChild = new ProbeBlock(20) { BlockIndex = 101 };
        firstChild.ObserveCancellationToken(cancellation.Token);
        secondChild.ObserveCancellationToken(cancellation.Token);
        var stack = new StackBox { BlockIndex = 0 };
        stack.Add(firstChild);
        stack.Add(secondChild);
        var trailing = new ProbeBlock(30) { BlockIndex = 1 };
        using LayoutSnapshot snapshot = CreateSnapshot([stack, trailing]);
        firstChild.CancelOnArrangeNumber(cancellation, firstChild.ArrangeCount + 2);
        firstChild.SetHeight(18);
        double oldTrailingTop = trailing.Bounds.Top;
        long oldRevision = snapshot.LayoutRevision;

        snapshot.RelayoutChangedBlocks([stack.BlockIndex], 200, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(false, firstChild.EffectiveTokenCanBeCanceledAtCancellation);
        Assert.False(secondChild.LastEffectiveTokenCanBeCanceled);
        Assert.Equal(38, stack.Bounds.Height);
        Assert.Equal(firstChild.Bounds.Bottom, secondChild.Bounds.Top);
        Assert.Equal(oldTrailingTop + 8, trailing.Bounds.Top);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        AssertViewportIndex(snapshot, stack, trailing);
    }

    [Fact]
    public void WidthFallbackCompletesTableAndFollowingBlocksAfterCancellationDuringArrange()
    {
        using var cancellation = new CancellationTokenSource();
        var canceller = new ProbeBlock(18) { BlockIndex = 0 };
        MarkdownLayoutContext context = CreateContext();
        var header = CreateCell(context, MarkdownElementKeys.TableHeader, "header", 100);
        var body = CreateCell(context, MarkdownElementKeys.TableCell, "cell text", 101);
        var table = new TableBox(context, [[header]], [[body]]) { BlockIndex = 1 };
        var trailing = new ProbeBlock(26) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([canceller, table, trailing], context.SourceMap);
        canceller.ObserveCancellationToken(cancellation.Token);
        canceller.CancelOnNextArrange(cancellation);
        long oldRevision = snapshot.LayoutRevision;

        snapshot.RelayoutChangedBlocks([table.BlockIndex], 260, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(false, canceller.EffectiveTokenCanBeCanceledAtCancellation);
        Assert.Equal(260, canceller.Bounds.Width);
        Assert.Equal(260, table.Bounds.Width);
        Assert.True(header.Bounds.Width > 0);
        Assert.True(body.Bounds.Width > 0);
        Assert.True(header.Bounds.Top >= table.Bounds.Top);
        Assert.True(body.Bounds.Bottom <= table.Bounds.Bottom + 0.01);
        Assert.InRange(System.Math.Abs(trailing.Bounds.Bottom - snapshot.Size.Height), 0, 0.01);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        AssertViewportIndex(snapshot, canceller, table, trailing);
    }

    [Fact]
    public void UnknownOwnerFallbackCompletesFullTreeCommitAfterCancellation()
    {
        var first = new ProbeBlock(20) { BlockIndex = 0 };
        var changed = new ProbeBlock(30) { BlockIndex = 1 };
        var trailing = new ProbeBlock(40) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        using var cancellation = new CancellationTokenSource();
        first.ObserveCancellationToken(cancellation.Token);
        first.CancelOnNextArrange(cancellation);
        changed.BlockIndex = 99;
        changed.SetHeight(48);
        double oldTrailingTop = trailing.Bounds.Top;
        double oldHeight = snapshot.Size.Height;
        long oldRevision = snapshot.LayoutRevision;

        snapshot.RelayoutChangedBlocks([99], 200, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(false, first.EffectiveTokenCanBeCanceledAtCancellation);
        Assert.Equal(48, changed.Bounds.Height);
        Assert.Equal(oldTrailingTop + 18, trailing.Bounds.Top);
        Assert.Equal(oldHeight + 18, snapshot.Size.Height);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        AssertViewportIndex(snapshot, first, changed, trailing);
    }

    [Fact]
    public void PreCanceledRequestDoesNotBeginMeasurementOrChangeCommittedState()
    {
        var first = new ProbeBlock(24) { BlockIndex = 0 };
        var changed = new ProbeBlock(40) { BlockIndex = 1 };
        var trailing = new ProbeBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        Rect firstBounds = first.Bounds;
        Rect changedBounds = changed.Bounds;
        Rect trailingBounds = trailing.Bounds;
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        int firstMeasures = first.MeasureCount;
        int changedMeasures = changed.MeasureCount;
        int trailingMeasures = trailing.MeasureCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.RelayoutChangedBlocks(
            [changed.BlockIndex],
            200,
            cancellation.Token));

        Assert.Equal(firstMeasures, first.MeasureCount);
        Assert.Equal(changedMeasures, changed.MeasureCount);
        Assert.Equal(trailingMeasures, trailing.MeasureCount);
        Assert.Equal(firstBounds, first.Bounds);
        Assert.Equal(changedBounds, changed.Bounds);
        Assert.Equal(trailingBounds, trailing.Bounds);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        AssertViewportIndex(snapshot, first, changed, trailing);
    }

    [Fact]
    public void CancellationDuringSecondChangedMeasureRestoresEveryAttemptedOwner()
    {
        var first = new ProbeBlock(20) { BlockIndex = 0 };
        var firstChanged = new ProbeBlock(30) { BlockIndex = 1 };
        var middle = new ProbeBlock(25) { BlockIndex = 2 };
        var secondChanged = new ProbeBlock(35) { BlockIndex = 3 };
        var trailing = new ProbeBlock(40) { BlockIndex = 4 };
        using LayoutSnapshot snapshot = CreateSnapshot(
            [first, firstChanged, middle, secondChanged, trailing]);
        Rect firstChangedBounds = firstChanged.Bounds;
        Rect secondChangedBounds = secondChanged.Bounds;
        Rect trailingBounds = trailing.Bounds;
        Rect[] oldBlockBounds = new[] { first, firstChanged, middle, secondChanged, trailing }
            .Select(block => block.Bounds)
            .ToArray();
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        LayoutPlacementGeneration oldIndex = GetViewportIndex(snapshot);
        (double Top, double Bottom)[] oldIndexBounds = Enumerable.Range(0, oldIndex.Count)
            .Select(oldIndex.GetIndexedBounds)
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        firstChanged.ObserveCancellationToken(cancellation.Token);
        secondChanged.ObserveCancellationToken(cancellation.Token);
        secondChanged.AfterMeasure = cancellation.Cancel;
        firstChanged.SetHeight(37);
        secondChanged.SetHeight(44);

        Assert.ThrowsAny<OperationCanceledException>(() => snapshot.RelayoutChangedBlocks(
            [firstChanged.BlockIndex, secondChanged.BlockIndex],
            200,
            cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(firstChangedBounds, firstChanged.Bounds);
        Assert.Equal(secondChangedBounds, secondChanged.Bounds);
        Assert.Equal(trailingBounds, trailing.Bounds);
        Assert.Equal(oldBlockBounds, new[] { first, firstChanged, middle, secondChanged, trailing }
            .Select(block => block.Bounds).ToArray());
        Assert.True(firstChanged.IsDirty);
        Assert.True(secondChanged.IsDirty);
        Assert.False(trailing.IsDirty);
        Assert.Equal(1, firstChanged.ArrangeCount);
        Assert.Equal(1, secondChanged.ArrangeCount);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision, snapshot.LayoutRevision);
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.True(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryBeginInteraction());
        Assert.False(snapshot.TryBeginPaint());
        Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            0,
            snapshot.Size.Height,
            out IReadOnlyList<BlockBox> unavailable));
        Assert.Empty(unavailable);
        Assert.Same(oldIndex, GetViewportIndex(snapshot));
        Assert.Equal(oldIndexBounds.Length, oldIndex.Count);
        for (int slot = 0; slot < oldIndexBounds.Length; slot++)
        {
            Assert.Equal(slot, oldIndex.GetBlockOrdinal(slot));
            Assert.Equal(oldIndexBounds[slot], oldIndex.GetIndexedBounds(slot));
        }
    }

    [Fact]
    public void CancellationFromUnchangedGeometryArrangePublishesCompleteFastPathCommit()
    {
        var first = new ProbeBlock(24) { BlockIndex = 0 };
        var changed = new ProbeBlock(40) { BlockIndex = 1 };
        var trailing = new ProbeBlock(30) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = CreateSnapshot([first, changed, trailing]);
        Rect changedBounds = changed.Bounds;
        Rect trailingBounds = trailing.Bounds;
        Size oldSize = snapshot.Size;
        long oldRevision = snapshot.LayoutRevision;
        int trailingArranges = trailing.ArrangeCount;
        using var cancellation = new CancellationTokenSource();
        changed.ObserveCancellationToken(cancellation.Token);
        changed.CancelOnNextArrange(cancellation);

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(false, changed.EffectiveTokenCanBeCanceledAtCancellation);
        Assert.Equal(changedBounds, changed.Bounds);
        Assert.Equal(trailingBounds, trailing.Bounds);
        Assert.Equal(trailingArranges, trailing.ArrangeCount);
        Assert.Equal(oldSize, snapshot.Size);
        Assert.Equal(oldRevision + 1, snapshot.LayoutRevision);
        AssertViewportIndex(snapshot, first, changed, trailing);
    }

    private static LayoutSnapshot CreateSnapshot(
        BlockBox[] blocks,
        MarkdownSourceMap? sourceMap = null,
        float width = 200)
    {
        float y = 0;
        for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
        {
            BlockBox block = blocks[ordinal];
            float height = block.Measure(width);
            block.Arrange(0, y, width);
            y += height;
            if (ordinal < blocks.Length - 1 &&
                !StackBox.SharesSafeHtmlInlineFlow(block, blocks[ordinal + 1]))
            {
                y += 8;
            }
        }

        return new LayoutSnapshot(
            blocks,
            sourceMap ?? new MarkdownSourceMap(string.Empty),
            width,
            y,
            blockSpacing: 8);
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        Color color = Color.FromArgb(255, 0, 0, 0);
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Table] = style,
            [MarkdownElementKeys.TableHeader] = style,
            [MarkdownElementKeys.TableCell] = style,
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            color,
            color,
            color,
            color,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static InlineContainerBox CreateCell(
        MarkdownLayoutContext context,
        string styleKey,
        string text,
        int blockIndex)
    {
        var cell = new InlineContainerBox(context, styleKey) { BlockIndex = blockIndex };
        cell.Add(new TextRun(text));
        return cell;
    }

    private static void AssertViewportIndex(LayoutSnapshot snapshot, params BlockBox[] expected)
    {
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                0,
                snapshot.Size.Height,
                out IReadOnlyList<BlockBox> indexed));
            Assert.Equal(expected, indexed.ToArray());
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static LayoutPlacementGeneration GetViewportIndex(LayoutSnapshot snapshot)
        => snapshot.PublishedPlacementGeneration;

    private sealed class ProbeBlock(float height) : BlockBox
    {
        private float _height = height;
        private CancellationToken _tokenToObserve;
        private CancellationTokenSource? _cancelOnArrange;
        private int _cancelAtArrangeCount;

        internal int MeasureCount { get; private set; }
        internal int ArrangeCount { get; private set; }
        internal Action? BeforeArrange { get; set; }
        internal Action? AfterMeasure { get; set; }
        internal bool? EffectiveTokenCanBeCanceledAtCancellation { get; private set; }
        internal bool LastEffectiveTokenCanBeCanceled { get; private set; }

        internal void SetHeight(float value) => _height = value;

        internal void ObserveCancellationToken(CancellationToken token)
            => _tokenToObserve = token;

        internal void CancelOnNextArrange(CancellationTokenSource cancellation)
            => CancelOnArrangeNumber(cancellation, ArrangeCount + 1);

        internal void CancelOnArrangeNumber(CancellationTokenSource cancellation, int arrangeCount)
        {
            _cancelOnArrange = cancellation;
            _cancelAtArrangeCount = arrangeCount;
        }

        public override float Measure(float availableWidth)
        {
            MeasureCount++;
            Bounds = new Rect(0, 0, availableWidth, _height);
            AfterMeasure?.Invoke();
            return _height;
        }

        public override void Arrange(float x, float y, float width)
        {
            BeforeArrange?.Invoke();
            ArrangeCount++;
            base.Arrange(x, y, width);
            LastEffectiveTokenCanBeCanceled =
                LayoutPassCancellation.GetEffectiveToken(_tokenToObserve).CanBeCanceled;
            if (ArrangeCount == _cancelAtArrangeCount && _cancelOnArrange is { } cancellation)
            {
                _cancelOnArrange = null;
                cancellation.Cancel();
                EffectiveTokenCanBeCanceledAtCancellation =
                    LayoutPassCancellation.GetEffectiveToken(_tokenToObserve).CanBeCanceled;
            }
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }
}
