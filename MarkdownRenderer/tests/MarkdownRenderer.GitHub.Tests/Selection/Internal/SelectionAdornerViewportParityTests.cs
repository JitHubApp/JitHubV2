using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SelectionAdornerViewportFixture;

namespace MarkdownRenderer.GitHub.Tests.Selection.Internal;

/// <summary>
/// Compares viewport-clipped selection adorner geometry and pixels with
/// the legacy full-tree oracle across custom visual order, scale
/// directions, native tables, multichunk code, atomic images and safe
/// HTML stacks.
/// </summary>
public sealed class SelectionAdornerViewportParityTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void ViewportSelectionMatchesFullTreeOracleForCustomVisualOrderAndAllScaleDirections()
    {
        foreach (FlowDirection flowDirection in new[]
                 {
                     FlowDirection.LeftToRight,
                     FlowDirection.RightToLeft,
                 })
        {
            CountingSelectionBox[] blocks =
            [
                CreateBox(0, top: 40.25, x: 8.25, flowDirection),
                CreateBox(1, top: 0.25, x: 28.5, flowDirection),
                CreateBox(2, top: 20.5, x: 48.75, flowDirection),
                CreateBox(3, top: 80.75, x: 68.25, flowDirection),
                CreateBox(4, top: 60.25, x: 88.5, flowDirection),
            ];
            using var snapshot = new LayoutSnapshot(
                blocks,
                new MarkdownSourceMap(string.Empty),
                width: 200,
                height: 120);
            SelectionController selection = SelectEveryBlock(blocks.Length);
            var viewport = new Rect(0, 20.5, 180, 60);

            List<Rect> legacy = GetLegacyViewportRects(selection, snapshot, viewport);
            int[] expectedOrdinals = blocks
                .Where(block => Intersects(block.Bounds, viewport))
                .Select(block => block.BlockIndex)
                .ToArray();
            Assert.Equal(new[] { 0, 2, 4 }, expectedOrdinals);
            ResetCounts(blocks);

            List<Rect> actual = CollectVisible(selection, snapshot, viewport);
            Assert.Equal(legacy, actual);
            Assert.Equal(
                expectedOrdinals,
                actual.Select(rect => blocks.Single(block => block.SelectionRect == rect).BlockIndex));

            foreach (double scale in RasterizationScales)
            {
                Rect[] expectedPixels = legacy.Select(rect => SnapToPhysicalPixels(rect, scale)).ToArray();
                Rect[] actualPixels = actual.Select(rect => SnapToPhysicalPixels(rect, scale)).ToArray();
                Assert.Equal(expectedPixels, actualPixels);
            }

            // A viewport index sorts by visual Y. Selection handles and paint
            // order retain the old document-order sequence, including custom
            // boxes whose visual order differs from their ordinal.
        }
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight, 1f)]
    [InlineData(FlowDirection.RightToLeft, 1f)]
    [InlineData(FlowDirection.LeftToRight, 1.5f)]
    [InlineData(FlowDirection.RightToLeft, 1.5f)]
    [InlineData(FlowDirection.LeftToRight, 2f)]
    [InlineData(FlowDirection.RightToLeft, 2f)]
    public async Task NativeWarpTableAndMultichunkCodeSelectionMatchesLegacyAfterHorizontalScroll(
        FlowDirection flowDirection,
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using NativeOverflowFixture fixture = NativeOverflowFixture.Create(
            device,
            _renderResources,
            flowDirection,
            rasterizationScale);

        Assert.True(fixture.Table.CanScrollHorizontally);
        Assert.True(fixture.Code.CanScrollHorizontally);
        Assert.True(fixture.Table.SetHorizontalOffset(fixture.Table.HorizontalExtent * 0.37));
        Assert.True(fixture.Code.SetHorizontalOffset(fixture.Code.HorizontalExtent * 0.29));
        Assert.True(fixture.Table.HorizontalOffset > 0);
        Assert.True(fixture.Code.HorizontalOffset > 0);

        var tableViewport = new Rect(
            0,
            fixture.Table.Bounds.Top,
            fixture.Width,
            fixture.Table.Bounds.Height);
        var codeViewport = new Rect(
            0,
            fixture.Code.Bounds.Top + fixture.Code.Bounds.Height * 0.32,
            fixture.Width,
            fixture.Code.Bounds.Height * 0.45);

        Assert.True(fixture.Code.Chunks.Count >= 3);
        Assert.True(Intersects(fixture.CodeViewportBounds, codeViewport));
        Assert.Equal(flowDirection == FlowDirection.RightToLeft, fixture.Table.IsRightToLeft);
        Assert.Equal(flowDirection == FlowDirection.RightToLeft, fixture.Code.IsRightToLeft);

        AssertVisibleParityAndPixels(fixture, tableViewport, rasterizationScale);
        AssertVisibleParityAndPixels(fixture, codeViewport, rasterizationScale);
    }

    [Fact]
    public async Task OffscreenAtomicImageEndpointUsesItsIndexedOwnerNotFirstVisibleSelectionRect()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        MarkdownLayoutContext context = CreateEndpointContext(device);
        var image = new ImageBox(context, context.ImageResources, string.Empty, "offscreen image") { BlockIndex = 1 };
        image.Measure(176);
        image.Arrange(0, 0, 176);
        Assert.True(image.Bounds.Height > 0);

        var visible = new CountingSelectionBox(2, new Rect(0, 110, 176, 20));
        visible.Measure(176);
        visible.Arrange(0, 110, 176);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { image, visible },
            context.SourceMap,
            width: 176,
            height: 160);
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(1, 0, 0));
        selection.ExtendTo(new DocumentPosition(2, 0, 1));
        var viewport = new Rect(0, 100, 176, 44);

        Assert.False(Intersects(image.Bounds, viewport));
        List<Rect> visibleRects = CollectVisible(selection, snapshot, viewport);
        Assert.Equal(new[] { visible.SelectionRect }, visibleRects);

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            Assert.True(selection.TryGetSelectionEndpointGeometry(
                snapshot,
                new DocumentPosition(1, 0, 0),
                selection.Range,
                viewport,
                verticalSearchMargin: 64,
                rangeStart: true,
                out _,
                out Rect endpointRect,
                out bool hasCaret));
            Assert.False(hasCaret);
            Assert.Equal(image.Bounds, endpointRect);
            Assert.NotEqual(visibleRects[0], endpointRect);
        }
        finally
        {
            snapshot.EndPaint();
        }

        await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void SafeHtmlStackSelectionUsesChildViewportRangeAndMatchesFullTraversal(FlowDirection flowDirection)
    {
        const int childCount = 256;
        var children = new CountingSelectionBox[childCount];
        var stack = new StackBox
        {
            BlockIndex = 0,
            FlowDirection = flowDirection,
            SafeHtmlInlineFlowScopeId = 31,
            HasResolvedSafeHtmlAlignment = true,
        };

        for (int index = 0; index < childCount; index++)
        {
            var child = new CountingSelectionBox(
                index + 1,
                new Rect(0, 0, 240, 9.5));
            children[index] = child;
            stack.Add(child);
        }

        stack.Measure(240);
        stack.Arrange(0, 0, 240);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { stack },
            new MarkdownSourceMap(string.Empty),
            width: 240,
            height: (float)stack.Bounds.Height);
        SelectionController selection = SelectEveryBlock(childCount, firstBlockIndex: 1);
        double viewportTop = children[childCount / 2].Bounds.Top + 1;
        var viewport = new Rect(0, viewportTop, 240, 34);

        List<Rect> expected = GetLegacyViewportRects(selection, snapshot, viewport);
        ResetCounts(children);

        List<Rect> actual = CollectVisible(selection, snapshot, viewport);

        Assert.Equal(expected, actual);
        Assert.Equal(expected.Count, children.Sum(child => child.SelectionQueryCount));
        Assert.All(
            children.Where(child => !Intersects(child.Bounds, viewport)),
            child => Assert.Equal(0, child.SelectionQueryCount));
    }

    private static CountingSelectionBox CreateBox(int index, double top, double x, FlowDirection flowDirection)
    {
        if (flowDirection == FlowDirection.RightToLeft)
            x = 176 - x - 15.75;

        return new CountingSelectionBox(
            index,
            new Rect(x, top, 15.75, 7.25));
    }

    private static void AssertVisibleParityAndPixels(
        NativeOverflowFixture fixture,
        Rect viewport,
        float rasterizationScale)
    {
        List<Rect> legacy = GetLegacyViewportRects(fixture.Selection, fixture.Snapshot, viewport);
        List<Rect> actual = CollectVisible(fixture.Selection, fixture.Snapshot, viewport);
        Assert.True(
            legacy.Count > 0,
            legacy.Count == 0
                ? DescribeEmptyLegacyRectResult(fixture, viewport, rasterizationScale)
                : string.Empty);
        Assert.Equal(legacy, actual);

        Color[] expectedPixels = RasterizeSelectionRects(
            fixture.Device,
            legacy,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Color[] actualPixels = RasterizeSelectionRects(
            fixture.Device,
            actual,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Assert.Equal(expectedPixels, actualPixels);

        int checkedPoints = 0;
        foreach (Rect rectangle in legacy)
        {
            Rect snapped = SnapToPhysicalPixels(rectangle, rasterizationScale);
            Rect visible = IntersectForTest(snapped, viewport);
            if (visible.Width <= 0 || visible.Height <= 0)
                continue;

            Point point = new(visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
            bool expectedHit = LegacyContainsPoint(fixture.Selection, fixture.Snapshot, point, rasterizationScale);
            bool actualHit = ContainsPointUnderReadLease(
                fixture.Selection,
                fixture.Snapshot,
                point,
                viewport,
                rasterizationScale);
            Assert.Equal(expectedHit, actualHit);
            Assert.True(actualHit);
            checkedPoints++;
        }

        Assert.True(checkedPoints > 0);
    }

    private static string DescribeEmptyLegacyRectResult(
        NativeOverflowFixture fixture,
        Rect viewport,
        float rasterizationScale)
    {
        string cellDetails = string.Join(
            " || ",
            fixture.Table.GetCellBoxes().Select(cell =>
            {
                Rect[] raw = cell.GetSelectionRects(fixture.Selection.Range).ToArray();
                Rect[] clipped = raw
                    .Select(rectangle => IntersectForTest(rectangle, fixture.Table.HorizontalViewportBounds))
                    .ToArray();
                return $"block={cell.BlockIndex};bounds={cell.Bounds};raw=[{string.Join(",", raw)}];" +
                    $"tableClip=[{string.Join(",", clipped)}]";
            }));
        string codeChunkDetails = string.Join(
            " || ",
            fixture.Code.Chunks.Select(chunk =>
            {
                Rect[] raw = chunk.GetSelectionRects(fixture.Selection.Range).ToArray();
                Rect[] clipped = raw
                    .Select(rectangle => IntersectForTest(rectangle, fixture.Code.HorizontalViewportBounds))
                    .ToArray();
                string nativeRequestedSize = chunk.TextLayout is CanvasTextLayout nativeLayout
                        ? nativeLayout.RequestedSize.ToString()
                        : "<no-layout>";
                return $"block={chunk.BlockIndex};bounds={chunk.Bounds};offset={chunk.CodeBlockTextOffset};" +
                    $"length={chunk.CodeBlockTextLength};requested={nativeRequestedSize};" +
                    $"raw=[{string.Join(",", raw)}];codeClip=[{string.Join(",", clipped)}]";
            }));

        return $"Expected a visible legacy selection rectangle, but none intersected the viewport. " +
            $"scale={rasterizationScale};range={fixture.Selection.Range};viewport={viewport};" +
            $"tableBounds={fixture.Table.Bounds};tableClip={fixture.Table.HorizontalViewportBounds};" +
            $"rtl={fixture.Table.IsRightToLeft};offset={fixture.Table.HorizontalOffset};" +
            $"extent={fixture.Table.HorizontalExtent};viewportWidth={fixture.Table.HorizontalViewport};" +
            $"cells={cellDetails};codeBounds={fixture.Code.Bounds};" +
            $"codeClip={fixture.Code.HorizontalViewportBounds};rtl={fixture.Code.IsRightToLeft};" +
            $"codeOffset={fixture.Code.HorizontalOffset};codeExtent={fixture.Code.HorizontalExtent};" +
            $"codeViewportWidth={fixture.Code.HorizontalViewport};canonicalLength={fixture.Code.CodeText.Length};" +
            $"chunks={codeChunkDetails}";
    }

    private MarkdownLayoutContext CreateEndpointContext(CanvasDevice device)
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 24, 25, 26),
        };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.ImageCaption] = body,
            },
            new Dictionary<string, ElementStyleOverride>(),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("offscreen image endpoint"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US") { RenderResources = _renderResources };
    }
}
