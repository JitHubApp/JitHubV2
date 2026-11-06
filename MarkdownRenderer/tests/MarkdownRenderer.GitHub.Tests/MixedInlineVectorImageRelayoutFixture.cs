using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ImageRelayoutTestSupport;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Steps and oracles for <see cref="MixedInlineVectorImageRelayoutTests"/>:
/// inline roots that mix image runs with materialized math vector runs and
/// relayout through the completed-image path against a protected reflow.
/// </summary>
internal static class MixedInlineVectorImageRelayoutFixture
{
    private const float LayoutWidth = MixedInlineVectorImageDocument.LayoutWidth;

    internal static MarkdownProgressiveScenePlan GetRegisteredPlan(MarkdownPerformanceSession session, object owner)
    {
        Assert.True(session.ScenePlans.TryGetPlan(owner, out MarkdownProgressiveScenePlan? plan));
        return plan;
    }

    internal static async Task PublishAsync(MarkdownProgressiveScenePlan plan, string marker)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(plan.TrySchedule(
            marker,
            static action => { action(); return true; },
            () => published.TrySetResult()));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static Rect[] SelectionFor(LayoutSnapshot snapshot, InlineContainerBox inline, InlineRun run)
    {
        int runIndex = Enumerable.Range(0, inline.Runs.Count)
            .Single(index => ReferenceEquals(inline.Runs[index], run));
        var range = new DocumentRange(
            new DocumentPosition(inline.BlockIndex, runIndex, 0),
            new DocumentPosition(inline.BlockIndex, runIndex, run.RenderedLength));
        return LayoutPlacementTestGeometry.ToLogical(
            snapshot, inline.BlockIndex, inline.GetSelectionRects(range));
    }

    internal static SemanticSpanGeometry[] CaptureSemanticSpanGeometry(
        LayoutSnapshot snapshot,
        InlineContainerBox inline)
    {
        return snapshot.SemanticDocument.TextSpans
            .Where(span => ReferenceEquals(span.InlineBox, inline))
            .Select(span => new SemanticSpanGeometry(
                span.TextStart,
                span.TextEnd,
                span.InlineRun?.GetType(),
                snapshot.SemanticDocument.Text.Substring(span.TextStart, span.TextEnd - span.TextStart),
                snapshot.SemanticDocument.GetDocumentRects(span.TextStart, span.TextEnd).ToArray()))
            .ToArray();
    }

    internal static InlineSceneGeometrySnapshot[] CaptureSceneGeometry(InlineSceneGeometry[] geometry) =>
        geometry.Select(item => new InlineSceneGeometrySnapshot(
            item.RegionCount,
            Enumerable.Range(0, item.RegionCount).Select(item.GetRegionBounds).ToArray())).ToArray();

    internal static void AssertSceneGeometryEqual(
        InlineSceneGeometrySnapshot[] expected,
        InlineSceneGeometrySnapshot[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].RegionCount, actual[index].RegionCount);
            Assert.Equal(expected[index].Regions, actual[index].Regions);
        }
    }

    internal static void AssertSemanticSpanGeometryEqual(
        SemanticSpanGeometry[] expected,
        SemanticSpanGeometry[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Start, actual[index].Start);
            Assert.Equal(expected[index].End, actual[index].End);
            Assert.Equal(expected[index].RunType, actual[index].RunType);
            Assert.Equal(expected[index].Text, actual[index].Text);
            Assert.Equal(expected[index].DocumentRects, actual[index].DocumentRects);
        }
    }

    internal static void AssertGeometryBoundToSource(
        InlineSceneGeometry[] geometry,
        InlineVectorSceneRun source)
    {
        Assert.NotEmpty(geometry);
        Assert.All(geometry, item => Assert.Same(source, item.Run));
    }

    internal static Color[] Paint(LayoutSnapshot snapshot, CanvasDevice device, float rasterizationScale) =>
        ImageRelayoutTestSupport.Paint(snapshot, device, rasterizationScale, LayoutWidth);

    internal static void AssertSemanticSpansAndPixelsMatchFallback(
        LayoutSnapshot snapshot,
        InlineContainerBox inline,
        LayoutSnapshot fallback,
        InlineContainerBox fallbackInline,
        CanvasDevice device,
        float rasterizationScale)
    {
        AssertSemanticSpanGeometryEqual(
            CaptureSemanticSpanGeometry(snapshot, inline),
            CaptureSemanticSpanGeometry(fallback, fallbackInline));
        Assert.Equal(Paint(snapshot, device, rasterizationScale),
            Paint(fallback, device, rasterizationScale));
    }

    /// <summary>
    /// Captures the root's current scene geometry from its live layout and
    /// checks it covers <paramref name="vector"/>; checks the optional pending
    /// cache too when it still exists; then explicitly releases the pending
    /// cache so a later reflow must recapture candidate geometry.
    /// </summary>
    /// <returns>The scene geometry generation that was captured and released.</returns>
    internal static long CaptureThenReleasePendingSceneGeometry(
        InlineContainerBox inline,
        InlineVectorSceneRun vector)
    {
        Assert.True(inline.TryCaptureCurrentSceneGeometry(
            CancellationToken.None, out long generation, out InlineSceneGeometry[] initialGeometry));
        Assert.Contains(initialGeometry, item => ReferenceEquals(item.Run, vector) && item.RegionCount > 0);
        if (inline.TryGetCurrentSceneGeometry(
                out long pendingGeneration, out InlineSceneGeometry[] pendingGeometry))
        {
            Assert.Equal(generation, pendingGeneration);
            Assert.Contains(pendingGeometry, item => ReferenceEquals(item.Run, vector) && item.RegionCount > 0);
        }
        inline.ReleasePendingSceneGeometry(generation);
        Assert.False(inline.TryGetCurrentSceneGeometry(out _, out _));
        return generation;
    }

    internal static void AssertPendingGeometryCoversVectors(
        InlineContainerBox inline,
        InlineVectorSceneRun[] vectors)
    {
        Assert.True(inline.TryGetCurrentSceneGeometry(
            out long pendingGeneration, out InlineSceneGeometry[] pendingGeometry));
        Assert.Equal(inline.SceneGeometryGeneration, pendingGeneration);
        foreach (InlineVectorSceneRun vector in vectors)
            Assert.Contains(pendingGeometry, geometry => ReferenceEquals(geometry.Run, vector) && geometry.RegionCount > 0);
    }

    /// <summary>
    /// Asserts that the inline root contributes no visible inline scene
    /// markers in <paramref name="band"/> and that its resources are ready.
    /// </summary>
    /// <returns>The snapshot's vector scene residency policy.</returns>
    internal static MarkdownVectorSceneResidencyPolicy AssertNoVisibleInlineSceneMarkers(
        LayoutSnapshot snapshot,
        InlineContainerBox inline,
        LazyLayoutBand band)
    {
        MarkdownVectorSceneResidencyPolicy policy = Assert.IsType<MarkdownVectorSceneResidencyPolicy>(
            snapshot.VectorSceneResidencyPolicy);
        var inlineMarkers = new List<string>();
        bool inlineResourcesReady = true;
        Assert.True(policy.TryAppendVisibleInlineSceneMarkers(
            inline, band.Top, band.Bottom, viewport: null,
            inlineMarkers, ref inlineResourcesReady));
        Assert.True(inlineResourcesReady);
        Assert.Empty(inlineMarkers);
        return policy;
    }

    /// <summary>
    /// Under one paint scope, probes the measured top-level blocks in the
    /// band, collects their visible deferred scene markers through the
    /// published placement generation, and hands them to
    /// <paramref name="assertMarkers"/> before the paint scope ends.
    /// </summary>
    internal static void ProbeVisibleDeferredMarkersUnderPaint(
        LayoutSnapshot snapshot,
        MarkdownVectorSceneResidencyPolicy policy,
        double top,
        double bottom,
        Action<IReadOnlyList<string>> assertMarkers)
    {
        Assert.True(snapshot.TryBeginPaint());
        try
        {
            Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                top,
                bottom,
                out IReadOnlyList<BlockBox> measuredBlocks,
                out IReadOnlyList<int> measuredOrdinals,
                out LayoutPlacementGeneration placements,
                out IReadOnlyDictionary<int, int> ordinalByBlockIndex));
            Assert.True(MarkdownProgressiveScenePlan.TryCollectVisibleDeferredSceneMarkers(
                measuredBlocks,
                measuredOrdinals,
                placements,
                snapshot.PlacementTransform,
                ordinalByBlockIndex,
                top,
                bottom,
                viewport: null,
                residencyPolicy: policy,
                out IReadOnlyList<string> visibleMarkers,
                out _));
            assertMarkers(visibleMarkers);
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    internal readonly record struct SemanticSpanGeometry(
        int Start,
        int End,
        Type? RunType,
        string Text,
        Rect[] DocumentRects);

    internal readonly record struct InlineSceneGeometrySnapshot(int RegionCount, Rect[] Regions);

    /// <summary>
    /// The source inline root's published state captured before a completed
    /// image relayout: its runs, text layout, image presentation, root and run
    /// geometry, and the semantic document it must keep.
    /// </summary>
    internal sealed class InlineRootBefore
    {
        private InlineRootBefore(
            LayoutSnapshot snapshot,
            InlineContainerBox inline,
            InlineVectorSceneRun vector,
            ImageBox image,
            InlineVectorRasterResolver resolver)
        {
            Runs = inline.Runs.ToArray();
            Layout = GetInlineLayout(inline);
            Lease = GetBitmapLease(image);
            Bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
            RootBounds = snapshot.Blocks.Select(static block => block.Bounds).ToArray();
            Size = snapshot.Size;
            Revision = snapshot.LayoutRevision;
            SemanticDocument = snapshot.SemanticDocument;
            SourceMap = snapshot.SourceMap;
            SemanticText = SemanticDocument.Text;
            SemanticProjection = SemanticDocument.TextProjectionFingerprint;
            SemanticRanges = SemanticDocument.SemanticRangeFingerprint;
            VectorBounds = vector.LocalBounds;
            Selection = SelectionFor(snapshot, inline, vector);
            ImageBounds = image.Bounds;
            ResolveCount = resolver.ResolveCount;
        }

        internal InlineRun[] Runs { get; }
        internal CanvasTextLayout Layout { get; }
        internal SharedCanvasBitmapCache.Lease Lease { get; }
        internal CanvasBitmap Bitmap { get; }
        internal Rect[] RootBounds { get; }
        internal Size Size { get; }
        internal long Revision { get; }
        internal MarkdownSemanticDocument SemanticDocument { get; }
        internal MarkdownSourceMap SourceMap { get; }
        internal string SemanticText { get; }
        internal MarkdownAutomationTextFingerprint SemanticProjection { get; }
        internal MarkdownAutomationTextFingerprint SemanticRanges { get; }
        internal Rect VectorBounds { get; }
        internal Rect[] Selection { get; }
        internal Rect ImageBounds { get; }
        internal int ResolveCount { get; }

        internal static InlineRootBefore Capture(
            LayoutSnapshot snapshot,
            InlineContainerBox inline,
            InlineVectorSceneRun vector,
            ImageBox image,
            InlineVectorRasterResolver resolver) =>
            new(snapshot, inline, vector, image, resolver);

        /// <summary>
        /// Asserts one published revision whose size matches the fallback,
        /// over the same semantic document and source map, without another
        /// image-provider pass.
        /// </summary>
        internal void AssertPublishedOverSameDocument(
            LayoutSnapshot snapshot,
            LayoutSnapshot fallback,
            InlineVectorRasterResolver resolver)
        {
            Assert.Equal(Revision + 1, snapshot.LayoutRevision);
            Assert.NotEqual(Size, snapshot.Size);
            Assert.Equal(fallback.Size, snapshot.Size);
            Assert.Same(SemanticDocument, snapshot.SemanticDocument);
            Assert.Same(SourceMap, snapshot.SourceMap);
            Assert.Equal(SemanticText, snapshot.SemanticDocument.Text);
            Assert.Equal(SemanticProjection, snapshot.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(SemanticRanges, snapshot.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(ResolveCount, resolver.ResolveCount);
        }

        /// <summary>
        /// Asserts every root now sits at the fallback's logical placement and
        /// that each moved away from its captured bounds.
        /// </summary>
        internal void AssertRootsMovedToFallbackPlacement(LayoutSnapshot snapshot, LayoutSnapshot fallback)
        {
            Assert.Equal(RootBounds.Length, snapshot.Blocks.Count);
            for (int index = 0; index < RootBounds.Length; index++)
            {
                Rect expectedBounds = LayoutPlacementTestGeometry.ToLogical(
                    fallback, fallback.Blocks[index]);
                Rect actualBounds = LayoutPlacementTestGeometry.ToLogical(
                    snapshot, snapshot.Blocks[index]);
                Assert.Equal(expectedBounds, actualBounds);
                Assert.NotEqual(RootBounds[index], actualBounds);
            }
        }

        /// <summary>
        /// Asserts a new text layout over the same run objects, and that the
        /// image run kept its image, bitmap and lease while taking the
        /// fallback's image placement and desired size.
        /// </summary>
        internal void AssertRunsAndImageRetainedWithFallbackPlacement(
            LayoutSnapshot snapshot,
            InlineContainerBox inline,
            InlineVectorSceneRun vector,
            InlineImageRun imageRun,
            LayoutSnapshot fallback,
            InlineImageRun fallbackImageRun,
            ImageBox fallbackImage)
        {
            ImageBox image = imageRun.Image;
            Assert.NotSame(Layout, GetInlineLayout(inline));
            AssertRunsRetained(inline, Runs);
            Assert.Same(vector, inline.Runs.OfType<InlineVectorSceneRun>().Single());
            Assert.Same(imageRun, inline.Runs.OfType<InlineImageRun>().Single());
            Assert.Same(image, imageRun.Image);
            Assert.Same(Bitmap, image.Bitmap);
            Assert.Same(Lease, GetBitmapLease(image));
            Rect expectedImageBounds = LayoutPlacementTestGeometry.ToLogical(
                fallback, inline.BlockIndex, [fallbackImage.Bounds])[0];
            Rect actualImageBounds = LayoutPlacementTestGeometry.ToLogical(
                snapshot, inline.BlockIndex, [image.Bounds])[0];
            Assert.Equal(expectedImageBounds, actualImageBounds);
            Assert.NotEqual(ImageBounds, actualImageBounds);
            Assert.Equal(fallbackImageRun.DesiredWidth, imageRun.DesiredWidth);
            Assert.Equal(fallbackImageRun.DesiredHeight, imageRun.DesiredHeight);
        }
    }
}
