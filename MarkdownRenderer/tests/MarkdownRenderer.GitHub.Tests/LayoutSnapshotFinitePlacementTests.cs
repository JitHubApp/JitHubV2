using System;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotFinitePlacementTests
{
    [Fact]
    public void NonFiniteLastBlockXForcesPlacementRepairInsteadOfFastPathPublication()
    {
        AssertNonFiniteLastBlockPlacementRepair(
            static (block, y) => block.SetRawPlacement(float.NaN, y, 200));
    }

    [Fact]
    public void NonFiniteLastBlockYForcesPlacementRepairInsteadOfFastPathPublication()
    {
        AssertNonFiniteLastBlockPlacementRepair(
            static (block, y) => block.SetRawPlacement(0, float.NaN, 200));
    }

    private static void AssertNonFiniteLastBlockPlacementRepair(
        Action<MeasuredBlock, float> corruptPlacement)
    {
        var first = new MeasuredBlock(24) { BlockIndex = 0 };
        var changed = new MeasuredBlock(40) { BlockIndex = 1 };
        first.Measure(200);
        first.Arrange(0, 0, 200);
        changed.Measure(200);
        float expectedChangedTop = (float)first.Bounds.Bottom + 8;
        changed.Arrange(0, expectedChangedTop, 200);
        using LayoutSnapshot snapshot = new(
            [first, changed],
            new MarkdownSourceMap(string.Empty),
            width: 200,
            height: expectedChangedTop + 40,
            blockSpacing: 8);
        corruptPlacement(changed, expectedChangedTop);

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.True(double.IsFinite(changed.Bounds.X));
        Assert.True(double.IsFinite(changed.Bounds.Y));
        Assert.Equal(0, changed.Bounds.X);
        Assert.Equal(expectedChangedTop, changed.Bounds.Y);
        Assert.Equal(2, changed.ArrangeCount);
        Assert.Equal(expectedChangedTop + changed.Height, snapshot.Size.Height);
    }

    private sealed class MeasuredBlock(float height) : BlockBox
    {
        internal float Height { get; } = height;
        internal int ArrangeCount { get; private set; }

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, Height);
            return Height;
        }

        public override void Arrange(float x, float y, float width)
        {
            ArrangeCount++;
            base.Arrange(x, y, width);
        }

        internal void SetRawPlacement(float x, float y, float width) =>
            base.Arrange(x, y, width);

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }
}
