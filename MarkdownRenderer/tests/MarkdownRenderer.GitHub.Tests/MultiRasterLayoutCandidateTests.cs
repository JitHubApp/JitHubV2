using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MultiRasterLayoutCandidateTests
{
    private const float Width = 320f;

    [Fact]
    public async Task MultiRasterCandidatePreservesSourceSemanticGraphemeAndAutomationIdentity()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, automationGeneration: 907);
        MarkdownSemanticDocument sourceSemantic = source.SemanticDocument;
        MarkdownAutomationTextFingerprint sourceFingerprint = source.SourceFingerprint;
        MarkdownSourceMap sourceMap = source.SourceMap;
        InlineContainerBox sourceRoot = fixture.Source;
        InlineRun[] sourceRuns = sourceRoot.Runs.ToArray();
        DocumentRange sourceRange = new(
            new DocumentPosition(sourceRoot.BlockIndex, 0, 0),
            new DocumentPosition(sourceRoot.BlockIndex, sourceRuns.Length - 1, sourceRuns[^1].Text.Length));
        string sourceSlice = sourceMap.Slice(sourceRange);
        Assert.NotEmpty(sourceSlice);
        Assert.Contains("e\u0301", sourceSemantic.Text, StringComparison.Ordinal);
        Assert.Contains("👩‍👩‍👧", sourceSemantic.Text, StringComparison.Ordinal);

        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext buildContext) =
            CreateCandidate(source, fixture);
        try
        {
            LayoutSnapshot replacement = candidate.Snapshot;
            InlineContainerBox replacementRoot = Assert.IsType<InlineContainerBox>(
                Assert.Single(replacement.Blocks));
            InlineRun[] replacementRuns = replacementRoot.Runs.ToArray();
            InlineImageRun[] replacementImages = replacementRuns.OfType<InlineImageRun>().ToArray();
            MarkdownSemanticDocument replacementSemantic = replacement.SemanticDocument;

            Assert.Equal(sourceRuns.Length, replacementRuns.Length);
            for (int index = 0; index < sourceRuns.Length; index++)
            {
                Assert.Equal(sourceRuns[index].InlineIndex, replacementRuns[index].InlineIndex);
                Assert.Equal(sourceRuns[index].RenderedLength, replacementRuns[index].RenderedLength);
                Assert.Equal(sourceRuns[index].SourceSpan, replacementRuns[index].SourceSpan);
                Assert.Equal(sourceRuns[index].ElementKey, replacementRuns[index].ElementKey);
                Assert.Equal(sourceRuns[index].StyleAliases, replacementRuns[index].StyleAliases);
                Assert.Equal(sourceRuns[index].StyleModifierKeys, replacementRuns[index].StyleModifierKeys);
                Assert.Equal(sourceRuns[index].SemanticHeadingKey, replacementRuns[index].SemanticHeadingKey);
            }

            Assert.Equal(fixture.ImageRuns.Count, replacementImages.Length);
            Assert.NotSame(sourceSemantic, replacementSemantic);
            Assert.NotSame(sourceSemantic.Root, replacementSemantic.Root);
            Assert.Same(sourceMap, replacement.SourceMap);
            Assert.Equal(sourceFingerprint, replacement.SourceFingerprint);
            Assert.Equal(sourceSemantic.TextProjectionFingerprint, replacementSemantic.TextProjectionFingerprint);
            Assert.Equal(sourceSemantic.SemanticRangeFingerprint, replacementSemantic.SemanticRangeFingerprint);
            Assert.Same(sourceSemantic.Text, replacementSemantic.Text);
            Assert.Same(sourceSemantic.TextElementBoundaries, replacementSemantic.TextElementBoundaries);
            Assert.Equal(sourceSlice, replacement.SourceMap.Slice(sourceRange));
            Assert.Equal(907, source.AutomationDocumentGeneration);
            Assert.Equal(907, replacement.AutomationDocumentGeneration);
            Assert.Equal(source.LayoutRevision + 1, replacement.LayoutRevision);

            TextElementBoundaryIndex sourceBoundaries = sourceSemantic.TextElementBoundaries;
            TextElementBoundaryIndex replacementBoundaries = replacementSemantic.TextElementBoundaries;
            for (int offset = 0; offset <= sourceBoundaries.TextLength; offset++)
            {
                Assert.Equal(sourceBoundaries.FindBoundaries(offset), replacementBoundaries.FindBoundaries(offset));
                Assert.Equal(sourceBoundaries.FindWordBoundaries(offset), replacementBoundaries.FindWordBoundaries(offset));
                int sourceNext = sourceBoundaries.Move(offset, 1, out int sourceMoved);
                int replacementNext = replacementBoundaries.Move(offset, 1, out int replacementMoved);
                Assert.Equal(sourceNext, replacementNext);
                Assert.Equal(sourceMoved, replacementMoved);
            }

            MarkdownSemanticNode[] sourceImageNodes = MarkdownSemanticDocument
                .EnumerateDepthFirst(sourceSemantic.Root)
                .Where(node => node.Role == MarkdownSemanticRole.Image)
                .ToArray();
            MarkdownSemanticNode[] replacementImageNodes = MarkdownSemanticDocument
                .EnumerateDepthFirst(replacementSemantic.Root)
                .Where(node => node.Role == MarkdownSemanticRole.Image)
                .ToArray();
            Assert.Equal(replacementImages.Length, sourceImageNodes.Length);
            Assert.Equal(sourceImageNodes.Length, replacementImageNodes.Length);
            for (int index = 0; index < replacementImages.Length; index++)
            {
                Assert.NotSame(fixture.ImageRuns[index], replacementImages[index]);
                Assert.NotSame(fixture.ImageBoxes[index], replacementImages[index].Image);
                Assert.Same(replacementRoot, replacementImageNodes[index].InlineBox);
                Assert.Same(replacementImages[index], replacementImageNodes[index].InlineRun);
                Assert.Same(replacementImages[index].Image, replacementImageNodes[index].ImageBox);
                Assert.Equal(sourceImageNodes[index].HelpText, replacementImageNodes[index].HelpText);
                Assert.Equal(sourceImageNodes[index].AccessibilityRole, replacementImageNodes[index].AccessibilityRole);
                Assert.Equal(fixture.ImageRuns[index].AltText, replacementImages[index].AltText);
            }
        }
        finally
        {
            try
            {
                candidate.Dispose();
            }
            finally
            {
                await buildContext.DrainOwnedRetirementsAsync();
            }
        }
    }

    [Fact]
    public async Task SourceMembershipFenceChecksEveryOrderedIdentityWithoutAllocating()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, automationGeneration: 911);
        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext buildContext) =
            CreateCandidate(source, fixture);
        try
        {
            ImageBox[] sourceImages = fixture.ImageBoxes.ToArray();
            var subscriptions = new HashSet<ImageBox>(sourceImages, ReferenceEqualityComparer.Instance);
            Assert.True(candidate.MatchesSourceImages(sourceImages, subscriptions));

            ImageBox[] reordered = sourceImages.Reverse().ToArray();
            Assert.False(candidate.MatchesSourceImages(reordered, subscriptions));

            ImageBox[] wrongIdentity = (ImageBox[])sourceImages.Clone();
            wrongIdentity[0] = Assert.IsType<InlineContainerBox>(Assert.Single(candidate.Snapshot.Blocks))
                .Runs.OfType<InlineImageRun>().First().Image;
            Assert.False(candidate.MatchesSourceImages(wrongIdentity, subscriptions));

            ImageBox[] shortList = sourceImages[..^1];
            Assert.False(candidate.MatchesSourceImages(shortList, subscriptions));

            var wrongSubscriptions = new HashSet<ImageBox>(sourceImages.Skip(1), ReferenceEqualityComparer.Instance);
            wrongSubscriptions.Add(wrongIdentity[0]);
            Assert.False(candidate.MatchesSourceImages(sourceImages, wrongSubscriptions));

            for (int warmup = 0; warmup < 32; warmup++)
                Assert.True(candidate.MatchesSourceImages(sourceImages, subscriptions));
            bool lastResult = false;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 512; iteration++)
                lastResult = candidate.MatchesSourceImages(sourceImages, subscriptions);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.True(lastResult);
            Assert.Equal(0, allocated);
            Assert.True(candidate.HasCurrentSourceRaster());
        }
        finally
        {
            try
            {
                candidate.Dispose();
            }
            finally
            {
                await buildContext.DrainOwnedRetirementsAsync();
            }
        }
    }

    [Fact]
    public async Task EverySourceLeaseIsRecheckedBeforeCandidatePublication()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, automationGeneration: 919);
        ImageBox[] sourceImages = fixture.ImageBoxes.ToArray();

        // Mutate each source slot in turn. A check that fences only the first
        // (or last) lease would incorrectly leave one of these candidates valid.
        for (int index = 0; index < sourceImages.Length; index++)
        {
            (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext buildContext) =
                CreateCandidate(source, fixture);
            try
            {
                Assert.True(candidate.HasCurrentSourceRaster());
                InlineImageRun[] candidateRuns = Assert.IsType<InlineContainerBox>(
                    Assert.Single(candidate.Snapshot.Blocks)).Runs.OfType<InlineImageRun>().ToArray();
                CanvasBitmap retainedCandidateBitmap = Assert.IsType<CanvasBitmap>(candidateRuns[index].Image.Bitmap);
                ReplaceBitmap(sourceImages[index], fixture.Device, width: 17 + index, height: 9 + index);

                Assert.False(candidate.HasCurrentSourceRaster());
                Assert.Same(retainedCandidateBitmap, candidateRuns[index].Image.Bitmap);
            }
            finally
            {
                try
                {
                    candidate.Dispose();
                }
                finally
                {
                    await buildContext.DrainOwnedRetirementsAsync();
                }
            }
        }
    }

    [Fact]
    public async Task ImagePlanUsesImageOrdinalsAndRejectsIncompleteMembership()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, automationGeneration: 929);
        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext buildContext) =
            CreateCandidate(source, fixture);
        try
        {
            InlineContainerBox root = Assert.IsType<InlineContainerBox>(Assert.Single(candidate.Snapshot.Blocks));
            InlineImageRun[] runs = root.Runs.OfType<InlineImageRun>().ToArray();
            var plan = new RasterPlacementImagePlan(root, candidate.SourceImageCount);

            Assert.Equal(runs.Length, candidate.SourceImageCount);
            Assert.Equal(runs.Select(run => run.Image), plan.Images);
            Assert.Equal(runs.Select(run => run.Image), plan.PublicationImages);
            Assert.Equal(runs.Length, plan.Subscriptions.Count);
            Assert.Equal(runs.Length, plan.Index.Count);
            var seenOrdinals = new bool[runs.Length];
            for (int index = 0; index < plan.Index.Count; index++)
            {
                int ordinal = plan.Index.GetBlockOrdinal(index);
                Assert.InRange(ordinal, 0, runs.Length - 1);
                Assert.False(seenOrdinals[ordinal]);
                seenOrdinals[ordinal] = true;

                ImageBox image = plan.Images[ordinal];
                Assert.Same(runs[ordinal].Image, image);
                Assert.Contains(image, plan.Subscriptions);
                (double top, double bottom) = plan.Index.GetBounds(index);
                Assert.Equal(image.Bounds.Top, top);
                Assert.Equal(image.Bounds.Bottom, bottom);
                ViewportRange range = plan.Index.Find(top, bottom);
                Assert.InRange(index, range.Start, range.End - 1);
            }
            Assert.All(seenOrdinals, static seen => Assert.True(seen));

            Assert.Throws<InvalidOperationException>(
                () => new RasterPlacementImagePlan(root, candidate.SourceImageCount + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RasterPlacementImagePlan(root, expectedCount: 0));
        }
        finally
        {
            try
            {
                candidate.Dispose();
            }
            finally
            {
                await buildContext.DrainOwnedRetirementsAsync();
            }
        }
    }

    [Fact]
    public async Task SourceRetirementDoesNotReleaseClonesAndTransferredCandidateRetiresOnceDrained()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, automationGeneration: 937);
        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext buildContext) =
            CreateCandidate(source, fixture);
        LayoutSnapshot? transferred = null;
        try
        {
            InlineImageRun[] sourceRuns = fixture.Source.Runs.OfType<InlineImageRun>().ToArray();
            InlineImageRun[] candidateRuns = Assert.IsType<InlineContainerBox>(
                Assert.Single(candidate.Snapshot.Blocks)).Runs.OfType<InlineImageRun>().ToArray();
            CanvasBitmap[] retained = candidateRuns.Select(run => Assert.IsType<CanvasBitmap>(run.Image.Bitmap)).ToArray();
            Assert.Equal(sourceRuns.Length, candidateRuns.Length);
            for (int index = 0; index < sourceRuns.Length; index++)
                Assert.Same(sourceRuns[index].Image.Bitmap, retained[index]);

            transferred = candidate.TransferToControl();
            candidate.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = candidate.Snapshot);
            Assert.Equal(937, transferred.AutomationDocumentGeneration);
            Assert.All(candidateRuns, run => Assert.NotNull(run.Image.Bitmap));

            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(sourceRuns, run => Assert.Null(run.Image.Bitmap));
            for (int index = 0; index < candidateRuns.Length; index++)
                Assert.Same(retained[index], candidateRuns[index].Image.Bitmap);

            Task candidateRetirement = transferred.Retire();
            buildContext.RegisterRetirement(candidateRetirement);
            await buildContext.DrainOwnedRetirementsAsync();
            Assert.All(candidateRuns, run => Assert.Null(run.Image.Bitmap));
            Assert.True(transferred.IsRetiringOrDisposed);
        }
        finally
        {
            try
            {
                candidate.Dispose();
                if (transferred is not null)
                    buildContext.RegisterRetirement(transferred.Retire());
            }
            finally
            {
                await buildContext.DrainOwnedRetirementsAsync();
            }
        }
    }

    [Fact]
    public async Task CanceledImageGenerationDrainsAllPrivateImageClonesWithoutRetiringSources()
    {
        await using var fixture = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        using LayoutSnapshot source = CreateSourceSnapshot(fixture, automationGeneration: 941);
        CanvasBitmap[] sourceBitmaps = fixture.ImageBoxes
            .Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap))
            .ToArray();
        using var canceledImageLifetime = new CancellationTokenSource();
        canceledImageLifetime.Cancel();

        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            canceledImageLifetime.Token,
            Assert.IsAssignableFrom<object>(fixture.SourceContext.ImagePreparationOwner),
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);
        Assert.Null(candidate);
        MarkdownLayoutContext buildContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
        await buildContext.DrainOwnedRetirementsAsync();

        Assert.Same(source.Blocks[0], fixture.Source);
        Assert.Equal(0, source.LayoutRevision);
        Assert.Equal(941, source.AutomationDocumentGeneration);
        for (int index = 0; index < fixture.ImageBoxes.Count; index++)
            Assert.Same(sourceBitmaps[index], fixture.ImageBoxes[index].Bitmap);

        await buildContext.DrainOwnedRetirementsAsync();
    }

    private static LayoutSnapshot CreateSourceSnapshot(
        MultiRasterPlacementCloneTests.MultiRasterFixture fixture,
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

    private static (SingleRasterLayoutCandidate Candidate, MarkdownLayoutContext ConstructionContext)
        CreateCandidate(LayoutSnapshot source, MultiRasterPlacementCloneTests.MultiRasterFixture fixture)
    {
        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            Assert.IsAssignableFrom<object>(fixture.SourceContext.ImagePreparationOwner),
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);
        return (
            Assert.IsType<SingleRasterLayoutCandidate>(candidate),
            Assert.IsType<MarkdownLayoutContext>(constructionContext));
    }

    private static void ReplaceBitmap(ImageBox image, CanvasDevice device, int width, int height)
    {
        var bitmap = CanvasBitmap.CreateFromBytes(
            device,
            new byte[checked(width * height * 4)],
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        SharedCanvasBitmapCache.Lease lease = SharedCanvasBitmapCache.CreateUncachedLease(bitmap);
        try
        {
            MethodInfo replaceBitmap = typeof(ImageBox).GetMethod(
                "ReplaceBitmap",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = replaceBitmap.Invoke(image, [bitmap, lease, null]);
            lease = null!;
        }
        finally
        {
            lease?.Dispose();
        }
    }
}
