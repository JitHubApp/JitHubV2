using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using RootSpecification = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RootSpecification;
using RunKind = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RunKind;
using RunSpecification = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RunSpecification;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Root specifications, clone oracles and fail-closed checks for
/// single-raster candidates built over several top-level inline roots.
/// </summary>
internal static class MultiRootCandidateAssertions
{
    private const float Width = MultiRootDocumentFixture.Width;
    private static readonly Thickness DocumentPadding = MultiRootDocumentFixture.DocumentPadding;

    internal static RootSpecification[] CreateMixedRoots()
        =>
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [
                    RunSpecification.PlainText("body before e\u0301 👩‍👩‍👧 "),
                    RunSpecification.Link("linked paragraph", "https://example.test/body") with
                    {
                        AccessibilityName = "body link",
                    },
                    RunSpecification.PlainText(" after אבג"),
                ]),
            new RootSpecification(MarkdownElementKeys.Heading2,
                [
                    RunSpecification.PlainText("heading two "),
                    RunSpecification.Image(0),
                    RunSpecification.Link("heading link", "https://example.test/heading") with
                    {
                        AccessibilityName = "heading resource",
                    },
                ]),
            new RootSpecification(MarkdownElementKeys.Heading3,
                [
                    RunSpecification.Image(1),
                    RunSpecification.PlainText(" between rasters "),
                    RunSpecification.Image(2),
                ]),
        ];

    internal static DocumentRange WholeBlockRange(InlineContainerBox block)
    {
        Assert.NotEmpty(block.Runs);
        InlineRun last = block.Runs[^1];
        return new DocumentRange(
            new DocumentPosition(block.BlockIndex, 0, 0),
            new DocumentPosition(block.BlockIndex, last.InlineIndex, last.Text.Length));
    }

    internal static async Task AssertRejectedWithoutLeaseLeakAsync(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        RootSpecification[] specs)
    {
        await using var document = new MultiRootDocumentFixture(seed, specs);
        Assert.NotEmpty(document.Images);
        ImageBox sourceImage = document.Images[0];
        int referencesBefore = GetEntryReferenceCount(GetImageLease(sourceImage));
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            candidate = document.Snapshot.CreateSingleRasterCandidate(
                document.ImageBearingBlockIndices,
                Width,
                CancellationToken.None,
                CancellationToken.None,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            Assert.Null(candidate);
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, constructionContext);
        }

        Assert.Equal(referencesBefore, GetEntryReferenceCount(GetImageLease(sourceImage)));
        Assert.Equal(0, document.Snapshot.LayoutRevision);
    }

    internal static async Task AssertUnsupportedLaterRootWasPreflightedAsync(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        RootSpecification[] specs)
    {
        await using var document = new MultiRootDocumentFixture(seed, specs);
        ImageBox firstImage = Assert.Single(document.Images);
        int referencesBefore = GetEntryReferenceCount(GetImageLease(firstImage));
        MarkdownLayoutContext probeContext = document.SourceContext.CreateRasterPlacementContext(
            CancellationToken.None,
            document.ImagePreparationOwner,
            retirementSink: null,
            imageUnavailable: null);
        try
        {
            Assert.True(document.Roots[0].TryGetPrivateRasterPlacementCounts(
                probeContext, Width - (float)(DocumentPadding.Left + DocumentPadding.Right),
                out _, out int firstRootImages));
            Assert.Equal(1, firstRootImages);
            Assert.False(document.Roots[1].TryGetPrivateRasterPlacementCounts(
                probeContext, Width - (float)(DocumentPadding.Left + DocumentPadding.Right),
                out _, out _));
        }
        finally
        {
            await probeContext.DrainOwnedRetirementsAsync();
        }

        await AssertRejectedWithoutLeaseLeakAsync(seed, specs);
        Assert.Equal(referencesBefore, GetEntryReferenceCount(GetImageLease(firstImage)));
    }

    internal static async Task DisposeCandidateAndDrainAsync(
        SingleRasterLayoutCandidate? candidate,
        MarkdownLayoutContext? constructionContext)
    {
        try
        {
            candidate?.Dispose();
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync();
        }
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

    internal static void AssertRootCloneMatchesSource(
        InlineContainerBox original,
        InlineContainerBox clone,
        CanvasTextLayout sourceLayout,
        Rect sourceBounds,
        string sourceSlice,
        MarkdownSourceMap replacementSourceMap)
    {
        Assert.NotSame(original, clone);
        Assert.Equal(original.BlockIndex, clone.BlockIndex);
        Assert.Equal(original.ElementKey, clone.ElementKey);
        Assert.True(clone.HasMeasuredLayout);
        Assert.False(clone.IsDirty);
        Assert.NotSame(sourceLayout, clone.TextLayout!);
        Assert.Equal(sourceBounds, original.Bounds);
        Assert.Equal(sourceLayout, original.TextLayout!);

        // The private tree keeps both the raw margin inputs and the
        // effective, measured margin state rather than reconstructing
        // a collapsed margin from its public getter.
        Assert.Equal(original.Margin, clone.Margin);
        Assert.Equal(original.CollapsedTopMargin, clone.CollapsedTopMargin);
        Assert.Equal(original.SuppressTopMargin, clone.SuppressTopMargin);
        Assert.Equal(original.SuppressBottomMargin, clone.SuppressBottomMargin);
        Assert.Equal(original.Bounds.Height, clone.Bounds.Height, precision: 3);
        Assert.True(double.IsFinite(clone.Bounds.X));
        Assert.True(double.IsFinite(clone.Bounds.Y));
        Assert.True(double.IsFinite(clone.Bounds.Width));
        Assert.True(double.IsFinite(clone.Bounds.Height));
        Assert.Equal(sourceSlice, replacementSourceMap.Slice(WholeBlockRange(clone)));

        InlineRun[] originalRuns = original.Runs.ToArray();
        InlineRun[] clonedRuns = clone.Runs.ToArray();
        Assert.Equal(originalRuns.Length, clonedRuns.Length);
        for (int runIndex = 0; runIndex < originalRuns.Length; runIndex++)
            AssertRunCloneMatchesSource(originalRuns[runIndex], clonedRuns[runIndex]);
    }

    internal static void AssertRunCloneMatchesSource(InlineRun originalRun, InlineRun clonedRun)
    {
        Assert.NotSame(originalRun, clonedRun);
        Assert.Equal(originalRun.InlineIndex, clonedRun.InlineIndex);
        Assert.Equal(originalRun.RenderedLength, clonedRun.RenderedLength);
        Assert.Equal(originalRun.SourceSpan, clonedRun.SourceSpan);
        Assert.Equal(originalRun.ElementKey, clonedRun.ElementKey);
        Assert.Equal(originalRun.StyleAliases, clonedRun.StyleAliases);
        Assert.Equal(originalRun.StyleModifierKeys, clonedRun.StyleModifierKeys);
        Assert.Equal(originalRun.SemanticHeadingKey, clonedRun.SemanticHeadingKey);
        if (originalRun is LinkRun originalLink)
        {
            LinkRun clonedLink = Assert.IsType<LinkRun>(clonedRun);
            Assert.Equal(originalLink.Url, clonedLink.Url);
            Assert.Equal(originalLink.Title, clonedLink.Title);
            Assert.Equal(originalLink.AccessibilityName, clonedLink.AccessibilityName);
            Assert.Equal(originalLink.IsSuperscript, clonedLink.IsSuperscript);
            Assert.False(clonedLink.IsDisclosure);
            Assert.False(clonedLink.IsExpanded);
        }
        else if (originalRun is InlineImageRun originalImageRun)
        {
            InlineImageRun clonedImageRun = Assert.IsType<InlineImageRun>(clonedRun);
            Assert.NotSame(originalImageRun.Image, clonedImageRun.Image);
            Assert.Same(originalImageRun.Image.Bitmap, clonedImageRun.Image.Bitmap);
            Assert.Equal(originalImageRun.Url, clonedImageRun.Url);
            Assert.Equal(originalImageRun.AltText, clonedImageRun.AltText);
            Assert.Equal(originalImageRun.LinkUrl, clonedImageRun.LinkUrl);
        }
    }

    internal static void AssertCandidateImagesAreDistinctClonesWithSemanticOwners(
        SingleRasterLayoutCandidate owner,
        MarkdownSemanticDocument sourceSemantic,
        InlineContainerBox[] sourceRoots,
        InlineContainerBox[] replacementRoots,
        ImageBox[] sourceImages)
    {
        InlineImageRun[] candidateImageRuns = replacementRoots
            .SelectMany(static root => root.Runs.OfType<InlineImageRun>())
            .ToArray();
        Assert.Equal(sourceImages.Length, candidateImageRuns.Length);
        ImageBox[] candidateImages = candidateImageRuns.Select(static run => run.Image).ToArray();
        Assert.Equal(
            sourceRoots.SelectMany(static root => root.Runs.OfType<InlineImageRun>())
                .Select(static run => run.Url),
            candidateImageRuns.Select(static run => run.Url));
        for (int index = 0; index < sourceImages.Length; index++)
            Assert.NotSame(sourceImages[index], candidateImages[index]);
        var subscriptions = new HashSet<ImageBox>(sourceImages, ReferenceEqualityComparer.Instance);
        Assert.True(owner.MatchesSourceImages(sourceImages, subscriptions));
        Assert.True(owner.HasCurrentSourceRaster());

        MarkdownSemanticNode[] sourceImageNodes = MarkdownSemanticDocument
            .EnumerateDepthFirst(sourceSemantic.Root)
            .Where(static node => node.Role == MarkdownSemanticRole.Image)
            .ToArray();
        MarkdownSemanticNode[] candidateImageNodes = MarkdownSemanticDocument
            .EnumerateDepthFirst(owner.Snapshot.SemanticDocument.Root)
            .Where(static node => node.Role == MarkdownSemanticRole.Image)
            .ToArray();
        Assert.Equal(3, sourceImageNodes.Length);
        Assert.Equal(3, candidateImageNodes.Length);
        for (int index = 0; index < candidateImageNodes.Length; index++)
        {
            Assert.Same(candidateImageRuns[index].Image, candidateImageNodes[index].ImageBox);
            Assert.Same(candidateImageRuns[index], candidateImageNodes[index].InlineRun);
            InlineContainerBox imageRoot = replacementRoots.Single(
                root => root.Runs.Contains(candidateImageRuns[index]));
            Assert.Same(imageRoot, candidateImageNodes[index].InlineBox);
            Assert.Equal(sourceImageNodes[index].HelpText, candidateImageNodes[index].HelpText);
        }
    }

    internal static void AssertMixedHeadingRootsAndSemantics(
        LayoutSnapshot replacement,
        InlineContainerBox[] replacementRoots)
    {
        Assert.Equal(MarkdownElementKeys.Body, replacementRoots[0].ElementKey);
        Assert.Equal(MarkdownElementKeys.Heading2, replacementRoots[1].ElementKey);
        Assert.Equal(MarkdownElementKeys.Heading3, replacementRoots[2].ElementKey);
        Assert.Equal(
            new[] { 2, 3 },
            MarkdownSemanticDocument.EnumerateDepthFirst(replacement.SemanticDocument.Root)
                .Where(static node => node.Role == MarkdownSemanticRole.Heading)
                .Select(static node => node.HeadingLevel));
    }

    internal static void AssertSourceRastersRetained(ImageBox[] sourceImages, CanvasBitmap[] sourceBitmaps)
    {
        Assert.All(sourceImages.Select((image, index) => (image, index)), entry =>
        {
            Assert.Same(sourceBitmaps[entry.index], entry.image.Bitmap);
            Assert.False(entry.image.GetWorkState().LifetimeDisposed);
        });
    }
}
