using System.Text;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;
using static MarkdownRenderer.GitHub.Tests.RootLocalContainerImageRelayoutHarness;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Budget fixtures and the fail-closed oracle for root-local container
/// relayout: a request over an image, run, node or depth cap must be rejected
/// before any private clone, lease or measurement work starts.
/// </summary>
internal static class ContainerRelayoutBudgetAssertions
{
    internal static string CreateBudgetListSource(ContainerResolver resolver, int itemCount, int imageCount)
    {
        Assert.InRange(imageCount, 0, itemCount);
        var source = new StringBuilder(itemCount * 48);
        for (int index = 0; index < itemCount; index++)
        {
            source.Append("- ");
            if (index < imageCount)
                source.Append("![budget image ").Append(index).Append("](").Append(resolver.FirstUrl).Append(')');
            else
                source.Append("ordinary supported list item ").Append(index);
            source.Append('\n');
        }
        return source.ToString();
    }

    internal static string CreateRunBudgetQuoteSource(ContainerResolver resolver, int formattedParagraphCount)
    {
        var source = new StringBuilder(formattedParagraphCount * 96);
        source.Append("> ![run budget image](").Append(resolver.FirstUrl).Append(")\n>\n");
        for (int index = 0; index < formattedParagraphCount; index++)
        {
            if (index > 0)
                source.Append(">\n");
            source.Append("> alpha **strong** beta *emphasis* gamma ~~deleted~~ delta\n");
        }
        return source.ToString();
    }

    internal static string CreateSingleLeafRunBudgetListSource(ContainerResolver resolver, int repeatedPairs)
    {
        var source = new StringBuilder(repeatedPairs * 32);
        source.Append("- ![single leaf run budget image](").Append(resolver.FirstUrl).Append(") ");
        for (int index = 0; index < repeatedPairs; index++)
            source.Append("plain").Append(index).Append(" **bold").Append(index).Append("** ");
        source.Append('\n');
        return source.ToString();
    }

    internal static string CreateDeepListSource(ContainerResolver resolver, int nestingDepth)
    {
        var source = new StringBuilder(nestingDepth * 24);
        for (int depth = 0; depth < nestingDepth; depth++)
        {
            source.Append(' ', depth * 2).Append("- ");
            if (depth == nestingDepth - 1)
                source.Append("![deep budget image](").Append(resolver.FirstUrl).Append(')');
            else
                source.Append("nested level ").Append(depth).Append('\n');
        }
        return source.ToString();
    }

    internal static void AssertContainerShapeIsSupportedAndMeasured(IEnumerable<BlockBox> roots)
    {
        BlockBox[] boxes = EnumerateBoxes(roots).ToArray();
        Assert.NotEmpty(boxes);
        foreach (BlockBox box in boxes)
            AssertSupportedMeasuredBudgetNode(box);

        var inlineImages = new HashSet<ImageBox>(ReferenceEqualityComparer.Instance);
        foreach (InlineContainerBox inline in boxes.OfType<InlineContainerBox>())
        {
            Assert.True(inline.HasMeasuredLayout,
                "Every supported source leaf must own its completed measured native layout.");
            Assert.False(inline.IsDirty,
                "Every supported source leaf must have completed, current layout before cap rejection.");
            foreach (var entry in inline.InlineImageRuns)
            {
                InlineImageRun run = entry.Run;
                Assert.True(run.Image.TryGetCompletedInlinePlacementState(
                        out float availableWidth, out float imageWidth, out float imageHeight),
                    "Inline image dirtiness is permitted, but completed bitmap placement must be coherent.");
                Assert.True(float.IsFinite(availableWidth) && availableWidth > 0);
                Assert.True(float.IsFinite(imageWidth) && imageWidth > 0 && imageWidth <= run.DesiredWidth);
                Assert.Equal(run.DesiredHeight, imageHeight);
                Assert.True(inlineImages.Add(run.Image),
                    "The fixture must not alias one image identity across multiple inline placements.");
            }
        }

        ImageBox[] images = EnumerateImages(roots).ToArray();
        Assert.NotEmpty(images);
        foreach (ImageBox image in images)
        {
            Assert.NotNull(image.Bitmap);
            Assert.Equal(0, image.GetWorkState().ActiveWorkCount);
            if (!inlineImages.Contains(image))
                Assert.False(image.IsDirty,
                    "Non-inline image boxes must have a completed measured source placement.");
            _ = GetBitmapLease(image);
        }
    }

    private static void AssertSupportedMeasuredBudgetNode(BlockBox box)
    {
        Assert.False(box.IsDirty, $"Budget fixture unexpectedly contains a dirty {box.GetType().Name}.");
        Assert.True(double.IsFinite(box.Bounds.X) && double.IsFinite(box.Bounds.Y) &&
                    double.IsFinite(box.Bounds.Width) && box.Bounds.Width > 0 &&
                    double.IsFinite(box.Bounds.Height) && box.Bounds.Height >= 0,
            $"Budget fixture contains incomplete measured bounds for {box.GetType().Name}.");
        switch (box)
        {
            case StackBox stack:
                Assert.Equal(typeof(StackBox), stack.GetType());
                Assert.True(stack.IsPrivateRasterPlacementCloneEligible);
                Assert.Null(stack.SafeHtmlInlineFlowScopeId);
                Assert.False(stack.HasResolvedSafeHtmlAlignment);
                break;
            case ListItemBox item:
                Assert.Equal(typeof(ListItemBox), item.GetType());
                Assert.Null(item.SafeHtmlInlineFlowScopeId);
                break;
            case InlineContainerBox inline:
                Assert.Equal(typeof(InlineContainerBox), inline.GetType());
                Assert.Contains(inline.ElementKey, new[]
                {
                    MarkdownElementKeys.Body,
                    MarkdownElementKeys.Heading1,
                    MarkdownElementKeys.Heading2,
                    MarkdownElementKeys.Heading3,
                    MarkdownElementKeys.Heading4,
                    MarkdownElementKeys.Heading5,
                    MarkdownElementKeys.Heading6,
                    MarkdownElementKeys.ListMarker,
                });
                Assert.Null(inline.SafeHtmlInlineFlowScopeId);
                Assert.Null(inline.CodeLanguage);
                Assert.Null(inline.StyleState);
                Assert.False(inline.HasInlineEmbeds);
                Assert.Equal(0, inline.PrivateRasterPlacementForegroundSpanCount);
                break;
            case ImageBox image:
                Assert.Equal(typeof(ImageBox), image.GetType());
                Assert.True(image.BlockIndex > 0);
                break;
            default:
                throw new Xunit.Sdk.XunitException(
                    $"Budget fixture reached unsupported node {box.GetType().Name} instead of its intended cap.");
        }
    }

    internal static int GetMaximumBoxDepth(IEnumerable<BlockBox> roots)
    {
        int maximum = 0;
        foreach (BlockBox root in roots)
            maximum = System.Math.Max(maximum, GetMaximumBoxDepth(root, depth: 1));
        return maximum;
    }

    private static int GetMaximumBoxDepth(BlockBox box, int depth)
    {
        int maximum = depth;
        switch (box)
        {
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    maximum = System.Math.Max(maximum, GetMaximumBoxDepth(child, depth + 1));
                break;
            case ListItemBox item:
                maximum = System.Math.Max(maximum, GetMaximumBoxDepth(item.Marker, depth + 1));
                maximum = System.Math.Max(maximum, GetMaximumBoxDepth(item.Content, depth + 1));
                break;
        }
        return maximum;
    }

    internal static async Task AssertContainerBudgetRejectedBeforeCloneAsync(
        ContainerImageDocument document,
        IReadOnlyCollection<int> changedBlockIndices,
        ContainerResolver resolver)
    {
        LayoutSnapshot snapshot = document.Snapshot;
        SourceTreeState before = SourceTreeState.Capture(snapshot, resolver);
        bool privateMeasurementStarted = false;
        bool constructionContextCreated = false;
        MarkdownLayoutContext? constructionContext = null;
        PreparedImageRelayoutResult result = default;

        try
        {
            result = snapshot.TryRelayoutCompletedImageRoots(
                changedBlockIndices, LayoutWidth, CancellationToken.None, out constructionContext,
                phaseHooks: CompletedImageRelayoutPhaseHooks.BeforeMeasure(() => privateMeasurementStarted = true),
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            constructionContextCreated = constructionContext is not null;
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(AsyncDeadline);
        }

        Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
        Assert.False(constructionContextCreated,
            "Budget rejection must not expose a candidate context or acquire any private bitmap leases.");
        Assert.False(privateMeasurementStarted,
            "Budget rejection must precede native/private measurement.");
        before.AssertDocumentUnchanged(snapshot, resolver);
        before.AssertBoxesAndRunsUnchanged(snapshot);
        before.AssertImagesUnchanged(snapshot);
    }

    /// <summary>The complete source tree, image and document identity before a rejected request.</summary>
    private sealed class SourceTreeState
    {
        private BlockBox[] _roots = [];
        private Rect[] _rootBounds = [];
        private BlockBox[] _boxes = [];
        private Rect[] _boxBounds = [];
        private InlineContainerBox[] _inlines = [];
        private InlineRun[][] _runs = [];
        private ImageBox[] _images = [];
        private CanvasBitmap[] _bitmaps = [];
        private Rect[] _imageBounds = [];
        private SharedCanvasBitmapCache.Lease[] _leases = [];
        private int[] _leaseCounts = [];
        private int[] _resolveCounts = [];
        private Size _size;
        private long _revision;
        private MarkdownSourceMap? _sourceMap;
        private MarkdownSemanticDocument? _semantic;
        private long _automationGeneration;

        internal static SourceTreeState Capture(LayoutSnapshot snapshot, ContainerResolver resolver)
        {
            var state = new SourceTreeState();
            state._roots = snapshot.Blocks.ToArray();
            state._rootBounds = state._roots.Select(static root => root.Bounds).ToArray();
            state._boxes = EnumerateBoxes(state._roots).ToArray();
            state._boxBounds = state._boxes.Select(static box => box.Bounds).ToArray();
            state._inlines = state._boxes.OfType<InlineContainerBox>().ToArray();
            state._runs = state._inlines.Select(static inline => inline.Runs.ToArray()).ToArray();
            state._images = EnumerateImages(state._roots).ToArray();
            state._bitmaps = state._images
                .Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
            state._imageBounds = state._images.Select(static image => image.Bounds).ToArray();
            state._leases = state._images.Select(GetBitmapLease).ToArray();
            state._leaseCounts = state._leases.Select(GetLeaseReferenceCount).ToArray();
            state._resolveCounts = resolver.CaptureResolveCounts();
            state._size = snapshot.Size;
            state._revision = snapshot.LayoutRevision;
            state._sourceMap = snapshot.SourceMap;
            state._semantic = snapshot.SemanticDocument;
            state._automationGeneration = snapshot.AutomationDocumentGeneration;
            return state;
        }

        internal void AssertDocumentUnchanged(LayoutSnapshot snapshot, ContainerResolver resolver)
        {
            Assert.Equal(_revision, snapshot.LayoutRevision);
            Assert.Equal(_size, snapshot.Size);
            Assert.Equal(_automationGeneration, snapshot.AutomationDocumentGeneration);
            Assert.Same(_sourceMap, snapshot.SourceMap);
            Assert.Same(_semantic, snapshot.SemanticDocument);
            Assert.Equal(_resolveCounts, resolver.CaptureResolveCounts());
            Assert.Equal(_roots.Length, snapshot.Blocks.Count);
            for (int index = 0; index < _roots.Length; index++)
            {
                Assert.Same(_roots[index], snapshot.Blocks[index]);
                Assert.Equal(_rootBounds[index], snapshot.Blocks[index].Bounds);
            }
        }

        internal void AssertBoxesAndRunsUnchanged(LayoutSnapshot snapshot)
        {
            BlockBox[] boxesAfter = EnumerateBoxes(snapshot.Blocks).ToArray();
            Assert.Equal(_boxes.Length, boxesAfter.Length);
            for (int index = 0; index < _boxes.Length; index++)
            {
                Assert.Same(_boxes[index], boxesAfter[index]);
                Assert.Equal(_boxBounds[index], boxesAfter[index].Bounds);
            }

            InlineContainerBox[] inlineAfter = boxesAfter.OfType<InlineContainerBox>().ToArray();
            Assert.Equal(_inlines.Length, inlineAfter.Length);
            for (int index = 0; index < _inlines.Length; index++)
            {
                Assert.Same(_inlines[index], inlineAfter[index]);
                Assert.Equal(_runs[index].Length, inlineAfter[index].Runs.Count);
                for (int run = 0; run < _runs[index].Length; run++)
                    Assert.Same(_runs[index][run], inlineAfter[index].Runs[run]);
            }
        }

        internal void AssertImagesUnchanged(LayoutSnapshot snapshot)
        {
            ImageBox[] imagesAfter = EnumerateImages(snapshot.Blocks).ToArray();
            Assert.Equal(_images.Length, imagesAfter.Length);
            for (int index = 0; index < _images.Length; index++)
            {
                Assert.Same(_images[index], imagesAfter[index]);
                Assert.Same(_bitmaps[index], imagesAfter[index].Bitmap);
                Assert.Same(_leases[index], GetBitmapLease(imagesAfter[index]));
                Assert.Equal(_imageBounds[index], imagesAfter[index].Bounds);
                Assert.Equal(_leaseCounts[index], GetLeaseReferenceCount(_leases[index]));
            }
        }
    }
}
