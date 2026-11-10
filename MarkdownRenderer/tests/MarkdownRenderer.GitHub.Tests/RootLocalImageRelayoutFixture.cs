using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ImageRelayoutTestSupport;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Sources, steps and oracles for <see cref="RootLocalImageRelayoutTests"/>:
/// standalone raster roots that relayout through the completed-image path
/// and are compared with a protected full-reflow fallback snapshot.
/// </summary>
internal static class RootLocalImageRelayoutFixture
{
    private const float LayoutWidth = RootLocalImageDocument.LayoutWidth;

    internal static string CreateDocumentSource(ControlledRasterResolver resolver, bool includePendingSibling) =>
        "# Image relayout\n\n" +
        $"![target image]({resolver.TargetUrl})\n\n" +
        "This paragraph has text, a [link](https://example.test/path), and enough words to paint after the image.\n\n" +
        (includePendingSibling ? $"![pending sibling]({resolver.PendingUrl})\n" : string.Empty);

    internal static string CreateBatchDocumentSource(
        ControlledRasterResolver resolver,
        string firstAlt = "first completed image",
        string secondAlt = "second completed image") =>
        "# Batch image relayout\n\n" +
        $"![{firstAlt}]({resolver.TargetUrl})\n\n" +
        "A stable paragraph separates the two standalone raster roots.\n\n" +
        $"![{secondAlt}]({resolver.SecondTargetUrl})\n\n" +
        "A trailing paragraph keeps suffix arrangement observable.\n";

    internal static string CreateLongLazySource(ControlledRasterResolver resolver) =>
        $"![target image]({resolver.TargetUrl})\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 80).Select(static index =>
            $"Lazy paragraph {index}: preserve the estimated tail and do not realize distant roots.")) +
        $"\n\n![far image]({resolver.FarUrl})\n";

    internal static string CreateLongLazySourceWithPendingSibling(ControlledRasterResolver resolver) =>
        $"![target image]({resolver.TargetUrl})\n\n" +
        $"![second target image]({resolver.SecondTargetUrl})\n\n" +
        $"![pending sibling]({resolver.PendingUrl})\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 80).Select(static index =>
            $"Lazy paragraph {index}: keep this distant root estimated during target completion.")) +
        $"\n\n![far image]({resolver.FarUrl})\n";

    internal static ImageBox FindSecondTarget(RootLocalImageDocument document) =>
        Assert.Single(document.Snapshot.Blocks.OfType<ImageBox>(),
            image => image.Url == document.Resolver.SecondTargetUrl);

    internal static async Task<long> AssertCompletedPlacementTransferPredicatesAsync(
        ImageBox source,
        float availableWidth)
    {
        MarkdownLayoutContext context = source.Context.CreateRasterPlacementContext(
            source.Context.ImageCancellationToken,
            source.Context.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);
        ImageBox? prepared = null;
        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                context,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedLease,
                out CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            _ = prepared.Measure(availableWidth);
            prepared.Arrange(0, 0, availableWidth);

            ImageBitmapOutputState sourceWaiter = source.OutputDeferral.GetState();
            string waiterState = sourceWaiter.ToString();
            bool sourcePristineWaiterState = sourceWaiter.IsPristine;
            bool sourceHasNoLiveWaiterWork = sourceWaiter.HasNoLiveWork;
            bool preparedWaiterStateIsClear = prepared.OutputDeferral.GetState().HasNoLiveWork;
            bool sourcePlacementIsCoherent =
                source.MeasuredPlacement.HasCoherentMeasuredPlacementNoLock(availableWidth, false);
            bool preparedPlacementIsCoherent =
                prepared.MeasuredPlacement.HasCoherentMeasuredPlacementNoLock(availableWidth, false);
            bool inputsMatch = source.MeasuredPlacement.HasMatchingPlacementInputs(prepared);
            bool transferEligible = source.MeasuredPlacement.IsTransferSourceEligibleNoLock(
                prepared, expectedLease, svgFence, 0L, availableWidth, false);
            long sourceWaiterEpoch = sourceWaiter.WaiterEpoch;

            Assert.True(sourceWaiterEpoch > 0,
                $"The completed source did not exercise historical admission state: {waiterState}");
            Assert.False(sourcePristineWaiterState,
                "The old virgin-image predicate unexpectedly classified a completed source as pristine.");
            Assert.True(sourceHasNoLiveWaiterWork,
                $"Completed source still has live bitmap-admission work: {waiterState}");
            Assert.True(preparedWaiterStateIsClear,
                "Fresh placement unexpectedly owns live bitmap-admission work.");
            Assert.True(sourcePlacementIsCoherent,
                "The source placement is not internally coherent before candidate transfer.");
            Assert.True(preparedPlacementIsCoherent,
                "The privately measured placement is not internally coherent.");
            Assert.True(inputsMatch,
                "The source and private candidate have different placement inputs.");
            Assert.True(transferEligible,
                "The transfer eligibility predicate rejected an otherwise prepared raster candidate.");
            return sourceWaiterEpoch;
        }
        finally
        {
            if (prepared is not null)
            {
                context.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    internal static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    internal static void InvokeReplaceBitmap(ImageBox image, SharedCanvasBitmapCache.Lease lease)
    {
        image.Presentation.Replace(lease.Bitmap, lease);
    }

    internal static async Task StartPendingSiblingAsync(RootLocalImageDocument document)
    {
        if (document.PendingImage is null)
            return;
        document.PendingImage.EnsureLoading();
        await document.Resolver.WaitForPendingResolveAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static async Task WaitForPendingWorkAndResolverStabilityAsync(
        RootLocalImageDocument first,
        RootLocalImageDocument second)
    {
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => first.PendingImage?.GetWorkState().ActiveWorkCount > 0 &&
                  second.PendingImage?.GetWorkState().ActiveWorkCount > 0 &&
                  first.Resolver.ResolveCountFor(first.Resolver.PendingUrl) > 0);

        int previousCount = first.Resolver.ResolveCountFor(first.Resolver.PendingUrl);
        int stableSamples = 0;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
            int currentCount = first.Resolver.ResolveCountFor(first.Resolver.PendingUrl);
            bool bothWorkItemsRemainActive =
                first.PendingImage?.GetWorkState().ActiveWorkCount > 0 &&
                second.PendingImage?.GetWorkState().ActiveWorkCount > 0;
            stableSamples = bothWorkItemsRemainActive && currentCount == previousCount
                ? stableSamples + 1
                : 0;
            previousCount = currentCount;
            if (stableSamples >= 5)
                return;
        }

        throw new TimeoutException("Pending image resolution did not reach a stable, active state.");
    }

    internal static Color[] Paint(LayoutSnapshot snapshot, CanvasDevice device, float rasterizationScale) =>
        ImageRelayoutTestSupport.Paint(snapshot, device, rasterizationScale, LayoutWidth);

    internal static Color[] PaintViewport(LayoutSnapshot snapshot, CanvasDevice device, float height, float dpi)
    {
        using var target = new CanvasRenderTarget(device, LayoutWidth, height, dpi);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, new Rect(0, 0, LayoutWidth, height));
        }
        return target.GetPixelColors();
    }

    internal static void AssertViewportIndexMatchesFullRebuild(LayoutSnapshot snapshot)
    {
        LayoutPlacementGeneration actual = snapshot.PublishedPlacementGeneration;
        var expected = new ViewportBandIndex(snapshot.Blocks.Count);
        var queryBands = new List<(double Top, double Bottom)>(snapshot.Blocks.Count * 4 + 1);

        for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++)
        {
            Rect bounds = actual.GetPlacement(ordinal).LogicalBounds;
            expected.SetEntry(ordinal, ordinal, bounds.Top, bounds.Bottom);
            queryBands.Add((bounds.Top, bounds.Top));
            queryBands.Add((bounds.Bottom, bounds.Bottom));
            queryBands.Add((bounds.Top, bounds.Bottom));
            if (ordinal + 1 < snapshot.Blocks.Count)
                queryBands.Add((bounds.Bottom, actual.GetPlacement(ordinal + 1).Top));
        }

        expected.Commit();
        queryBands.Add((0, snapshot.Size.Height));
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.IsDocumentOrder, actual.IsDocumentOrder);
        for (int entry = 0; entry < expected.Count; entry++)
        {
            Assert.Equal(expected.GetBlockOrdinal(entry), actual.GetBlockOrdinal(entry));
            Assert.Equal(expected.GetBounds(entry), actual.GetIndexedBounds(entry));
        }

        foreach ((double top, double bottom) in queryBands)
        {
            ViewportRange expectedRange = expected.Find(top, bottom);
            ViewportRange actualRange = actual.Find(top, bottom);
            Assert.Equal(expectedRange, actualRange);
            for (int entry = expectedRange.Start; entry < expectedRange.End; entry++)
                Assert.Equal(expected.GetBlockOrdinal(entry), actual.GetBlockOrdinal(entry));
        }
    }

    /// <summary>
    /// Asserts that the snapshot's semantic fingerprints are unchanged from
    /// <paramref name="before"/> after a placement-only relayout.
    /// </summary>
    internal static void AssertSemanticFingerprintsPreserved(
        MarkdownSemanticDocument before,
        LayoutSnapshot snapshot)
    {
        Assert.Equal(before.TextProjectionFingerprint,
            snapshot.SemanticDocument.TextProjectionFingerprint);
        Assert.Equal(before.SemanticRangeFingerprint,
            snapshot.SemanticDocument.SemanticRangeFingerprint);
    }

    internal static SharedCanvasBitmapCache.Lease GetImageLease(ImageBox image)
    {
        return Assert.IsType<SharedCanvasBitmapCache.Lease>(image.Presentation.Lease);
    }

    internal static int GetEntryReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        int? count = lease.EntryReferenceCount;
        Assert.True(count.HasValue, "The lease no longer holds a cache entry.");
        return count.Value;
    }

    internal static (MarkdownSemanticRole Role, int Start, int End, int HeadingLevel,
        MarkdownAccessibilityRole AccessibilityRole, string? Name, string? Description,
        string? AutomationId, Rect Bounds)[] GetSemanticProjection(LayoutSnapshot snapshot)
    {
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        var result = new List<(MarkdownSemanticRole, int, int, int, MarkdownAccessibilityRole,
            string?, string?, string?, Rect)>();
        var pending = new Stack<MarkdownSemanticNode>();
        pending.Push(document.Root);
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            while (pending.Count > 0)
            {
                MarkdownSemanticNode node = pending.Pop();
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(node.LayoutBlockIndex);
                Rect physicalBounds = node.VectorSceneBox is { } vector
                    ? node.VectorSemanticIndex >= 0
                        ? vector.GetVisibleSemanticBounds(node.VectorSemanticIndex)
                        : vector.Bounds
                    : node.Bounds;
                Rect logicalBounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                    node.LayoutBlockIndex,
                    physicalBounds);
                result.Add((node.Role, node.TextStart, node.TextEnd, node.HeadingLevel,
                    node.AccessibilityRole, node.AccessibilityName, node.AccessibilityDescription,
                    node.AutomationId, logicalBounds));
                for (int child = node.Children.Count - 1; child >= 0; child--)
                    pending.Push(node.Children[child]);
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    internal static async Task AssertBatchUnavailableWithoutLeaseChangeAsync(
        LayoutSnapshot snapshot,
        IReadOnlyCollection<int> indices,
        SharedCanvasBitmapCache.Lease firstLease,
        int firstReferences,
        SharedCanvasBitmapCache.Lease secondLease,
        int secondReferences)
    {
        MarkdownLayoutContext? rejectedContext = null;
        try
        {
            PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
                indices, LayoutWidth, CancellationToken.None, out rejectedContext,
                expectedPointerInputEpoch: snapshot.PointerInputEpoch);
            Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
            Assert.Null(rejectedContext);
            Assert.Equal(firstReferences, GetEntryReferenceCount(firstLease));
            Assert.Equal(secondReferences, GetEntryReferenceCount(secondLease));
        }
        finally
        {
            await DrainAsync(rejectedContext);
        }
    }

    internal static async Task AssertOverBudgetAltBatchUnavailableAsync(
        RootLocalImageDocument overBudget,
        int firstAltLength,
        int secondAltLength)
    {
        ImageBox overBudgetFirst = overBudget.TargetImage;
        ImageBox overBudgetSecond = FindSecondTarget(overBudget);
        Assert.Equal(firstAltLength, overBudgetFirst.Alt.Length);
        Assert.Equal(secondAltLength, overBudgetSecond.Alt.Length);
        Assert.True(overBudgetFirst.Alt.Length + overBudgetSecond.Alt.Length >
                    LayoutSnapshot.MaximumPrivateRasterPlacementTextLength);
        await LoadImageAsync(overBudgetFirst);
        await LoadImageAsync(overBudgetSecond);
        SharedCanvasBitmapCache.Lease overBudgetFirstLease = GetImageLease(overBudgetFirst);
        SharedCanvasBitmapCache.Lease overBudgetSecondLease = GetImageLease(overBudgetSecond);
        int overBudgetFirstReferences = GetEntryReferenceCount(overBudgetFirstLease);
        int overBudgetSecondReferences = GetEntryReferenceCount(overBudgetSecondLease);
        LayoutSnapshot overBudgetSnapshot = overBudget.Snapshot;
        BlockBox[] overBudgetRoots = overBudgetSnapshot.Blocks.ToArray();
        Rect[] overBudgetBounds = overBudgetRoots.Select(static block => block.Bounds).ToArray();
        long overBudgetRevision = overBudgetSnapshot.LayoutRevision;
        Size overBudgetSize = overBudgetSnapshot.Size;
        MarkdownLayoutContext? overBudgetContext = null;

        try
        {
            PreparedImageRelayoutResult overBudgetResult = overBudgetSnapshot.TryRelayoutCompletedImageRoots(
                [overBudgetFirst.BlockIndex, overBudgetSecond.BlockIndex], LayoutWidth, CancellationToken.None,
                out overBudgetContext,
                expectedPointerInputEpoch: overBudgetSnapshot.PointerInputEpoch);

            Assert.Equal(PreparedImageRelayoutResult.Unavailable, overBudgetResult);
            Assert.Null(overBudgetContext);
            Assert.Equal(overBudgetRevision, overBudgetSnapshot.LayoutRevision);
            Assert.Equal(overBudgetSize, overBudgetSnapshot.Size);
            AssertRootsUnchanged(overBudgetSnapshot, overBudgetRoots, overBudgetBounds);
            Assert.Equal(overBudgetFirstReferences, GetEntryReferenceCount(overBudgetFirstLease));
            Assert.Equal(overBudgetSecondReferences, GetEntryReferenceCount(overBudgetSecondLease));
        }
        finally
        {
            await DrainAsync(overBudgetContext);
        }
    }

    internal static void AssertSemanticTextAndSpansPreserved(
        MarkdownSemanticDocument before,
        LayoutSnapshot snapshot)
    {
        Assert.Equal(before.Text, snapshot.SemanticDocument.Text);
        AssertSemanticFingerprintsPreserved(before, snapshot);
        Assert.Equal(before.TextSpans.Select(static span => (span.TextStart, span.TextEnd)),
            snapshot.SemanticDocument.TextSpans.Select(static span => (span.TextStart, span.TextEnd)));
    }

    internal static void AssertPendingSiblingsStillUnloaded(
        RootLocalImageDocument optimized,
        RootLocalImageDocument fallback)
    {
        Assert.NotNull(optimized.PendingImage);
        Assert.NotNull(fallback.PendingImage);
        Assert.Null(optimized.PendingImage!.Bitmap);
        Assert.Null(fallback.PendingImage!.Bitmap);
    }

    internal static void AssertViewportIndexAndPixelsMatchFallback(
        LayoutSnapshot snapshot,
        LayoutSnapshot fallback,
        CanvasDevice device,
        float rasterizationScale)
    {
        AssertViewportIndexMatchesFullRebuild(snapshot);
        AssertViewportIndexMatchesFullRebuild(fallback);
        Assert.Equal(Paint(snapshot, device, rasterizationScale),
            Paint(fallback, device, rasterizationScale));
    }

    internal static void AssertMeasured(LayoutSnapshot snapshot, params BlockBox[] roots)
    {
        foreach (BlockBox root in roots)
            Assert.True(snapshot.IsTopLevelBlockMeasured(root));
    }

    internal static void AssertRootsMatchFallbackWithMeasuredMask(
        LayoutSnapshot fallback,
        LayoutSnapshot snapshot,
        BlockBox[] rootsBefore,
        bool[] measuredBefore)
    {
        for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
        {
            Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
            Assert.Equal(measuredBefore[ordinal], snapshot.IsTopLevelBlockMeasured(snapshot.Blocks[ordinal]));
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallback.Blocks[ordinal]),
                LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]));
        }
    }

    internal static void AssertPendingWorkAndFarRootUntouched(
        LayoutSnapshot snapshot,
        ImageBox pending,
        int pendingWorkBefore,
        ImageBox far)
    {
        Assert.True(pending.GetWorkState().ActiveWorkCount > 0);
        Assert.Equal(pendingWorkBefore, pending.GetWorkState().ActiveWorkCount);
        Assert.Null(pending.Bitmap);
        Assert.False(snapshot.IsTopLevelBlockMeasured(far));
        Assert.Null(far.Bitmap);
    }

    /// <summary>Carries a layout context out of a <c>Task.Run</c> lambda's <c>out</c> argument.</summary>
    internal sealed class ContextCapture
    {
        internal MarkdownLayoutContext? Value;
    }
}
