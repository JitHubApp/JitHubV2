using System.Reflection;
using System.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MultiRasterPlacementCloneTests
{
    [Fact]
    public async Task MultipleRasterRunsCloneWithFreshLayoutsAndExactLogicalMetadata()
    {
        await using var fixture = new MultiRasterFixture(imageCount: 3);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();
        SourceSpan[] sourceSpans = fixture.Source.Runs.Select(static run => run.SourceSpan).ToArray();
        string sourceSlice = GetWholeBlockSourceSlice(fixture.Source);
        CanvasTextLayout sourceLayout = GetField<CanvasTextLayout>(fixture.Source, "_layout");
        Rect sourceBounds = fixture.Source.Bounds;

        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));
        fixture.SetCandidate(candidateContext, candidate);

        InlineContainerBox cloned = Assert.IsType<InlineContainerBox>(candidate);
        Assert.False(cloned.HasMeasuredLayout);
        Assert.Same(fixture.SourceContext.SourceMap, candidateContext.SourceMap);
        Assert.Equal(fixture.SourceContext.SourceMap.Slice(WholeBlockRange(fixture.Source)), sourceSlice);
        Assert.Equal(sourceSpans, cloned.Runs.Select(static run => run.SourceSpan));
        Assert.Equal(fixture.Source.Runs.Count, cloned.Runs.Count);

        InlineRun[] sourceRuns = fixture.Source.Runs.ToArray();
        InlineRun[] clonedRuns = cloned.Runs.ToArray();
        for (int index = 0; index < sourceRuns.Length; index++)
        {
            Assert.NotSame(sourceRuns[index], clonedRuns[index]);
            Assert.Equal(sourceRuns[index].InlineIndex, clonedRuns[index].InlineIndex);
            Assert.Equal(sourceRuns[index].RenderedLength, clonedRuns[index].RenderedLength);
            Assert.Equal(sourceRuns[index].ElementKey, clonedRuns[index].ElementKey);
            Assert.Equal(sourceRuns[index].StyleAliases, clonedRuns[index].StyleAliases);
            Assert.NotSame(sourceRuns[index].StyleAliases, clonedRuns[index].StyleAliases);
            Assert.Equal(sourceRuns[index].StyleModifierKeys, clonedRuns[index].StyleModifierKeys);
            Assert.Equal(sourceRuns[index].SemanticHeadingKey, clonedRuns[index].SemanticHeadingKey);
        }

        InlineImageRun[] clonedImageRuns = clonedRuns.OfType<InlineImageRun>().ToArray();
        Assert.Equal(fixture.ImageRuns.Count, clonedImageRuns.Length);
        Assert.Equal(fixture.ImageRuns.Count, expectedSources!.Length);
        for (int index = 0; index < clonedImageRuns.Length; index++)
        {
            InlineImageRun sourceRun = fixture.ImageRuns[index];
            InlineImageRun cloneRun = clonedImageRuns[index];
            ImageBox sourceImage = sourceRun.Image;
            ImageBox cloneImage = cloneRun.Image;
            SharedCanvasBitmapCache.Lease sourceLease = GetImageLease(sourceImage);
            SharedCanvasBitmapCache.Lease cloneLease = GetImageLease(cloneImage);

            Assert.Same(sourceImage, expectedSources[index].Image);
            Assert.Same(sourceLease, expectedSources[index].Lease);
            Assert.NotSame(sourceImage, cloneImage);
            Assert.NotSame(sourceLease, cloneLease);
            Assert.Same(sourceImage.Bitmap, cloneImage.Bitmap);
            Assert.Equal(sourceLease.IntrinsicSize, cloneLease.IntrinsicSize);
            Assert.Equal(
                GetField<Size>(sourceImage, "_rasterIntrinsicSize"),
                GetField<Size>(cloneImage, "_rasterIntrinsicSize"));
            Assert.NotSame(
                GetField<CancellationTokenSource>(sourceImage, "_imageLifetimeCancellation"),
                GetField<CancellationTokenSource>(cloneImage, "_imageLifetimeCancellation"));
            Assert.Equal(sourceRun.Url, cloneRun.Url);
            Assert.Equal(sourceRun.AltText, cloneRun.AltText);
            Assert.Equal(sourceRun.LinkUrl, cloneRun.LinkUrl);
        }

        Assert.Equal(
            fixture.Source.InlineImageRuns.Select(static entry => entry.Offset),
            cloned.InlineImageRuns.Select(static entry => entry.Offset));
        Assert.Equal(sourceSlice, fixture.SourceContext.SourceMap.Slice(WholeBlockRange(fixture.Source)));

        float candidateHeight = cloned.Measure(180f);
        cloned.Arrange(0f, 0f, 180f);
        Assert.True(float.IsFinite(candidateHeight));
        Assert.True(candidateHeight > 0);
        Assert.True(cloned.HasMeasuredLayout);
        Assert.NotSame(sourceLayout, GetField<CanvasTextLayout>(cloned, "_layout"));
        Assert.Equal(sourceBounds, fixture.Source.Bounds);
        Assert.Same(sourceLayout, GetField<CanvasTextLayout>(fixture.Source, "_layout"));
    }

    [Fact]
    public async Task EveryClonedRasterLeaseSurvivesSourceRetirementIndependently()
    {
        await using var fixture = new MultiRasterFixture(imageCount: 3);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();
        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out _));
        fixture.SetCandidate(candidateContext, candidate);
        InlineContainerBox cloned = Assert.IsType<InlineContainerBox>(candidate);
        CanvasBitmap[] originalBitmaps = fixture.ImageBoxes
            .Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap))
            .ToArray();
        ImageBox[] clonedImages = cloned.Runs.OfType<InlineImageRun>()
            .Select(static run => run.Image)
            .ToArray();

        fixture.ReleaseCacheEntries();
        await fixture.RetireSourceAsync();

        Assert.All(fixture.ImageBoxes, static image => Assert.Null(image.Bitmap));
        for (int index = 0; index < clonedImages.Length; index++)
            Assert.Same(originalBitmaps[index], clonedImages[index].Bitmap);

        await fixture.RetireCandidateAsync(candidateContext, cloned);
        Assert.All(clonedImages, static image => Assert.Null(image.Bitmap));
    }

    [Fact]
    public async Task LateUnsupportedImageRejectsAndRetiresEarlierPrivateRasterClones()
    {
        await using var fixture = new MultiRasterFixture(imageCount: 2, finalImageIsSvg: true);
        ImageBox earlierSource = fixture.ImageBoxes[0];
        SharedCanvasBitmapCache.Lease earlierLease = GetImageLease(earlierSource);
        int referencesBefore = GetEntryReferenceCount(earlierLease);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();

        Assert.False(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));

        Assert.Null(candidate);
        Assert.Null(expectedSources);
        await candidateContext.DrainOwnedRetirementsAsync();
        Assert.Equal(referencesBefore, GetEntryReferenceCount(earlierLease));
        Assert.Same(earlierLease, GetImageLease(earlierSource));
        Assert.NotNull(earlierSource.Bitmap);
    }

    [Fact]
    public async Task CanceledPassRejectsBeforeAcquiringAnyImageCloneLeases()
    {
        await using var fixture = new MultiRasterFixture(imageCount: 3);
        int[] referencesBefore = fixture.ImageBoxes
            .Select(static image => GetEntryReferenceCount(GetImageLease(image)))
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext(cancellation.Token);

        Assert.Throws<OperationCanceledException>(() => fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));

        Assert.Equal(referencesBefore, fixture.ImageBoxes
            .Select(static image => GetEntryReferenceCount(GetImageLease(image))));
        Assert.All(fixture.ImageBoxes, static image => Assert.NotNull(image.Bitmap));
    }

    [Fact]
    public async Task DuplicateSourceImageIdentityRejectsBeforeCloningAnyRaster()
    {
        await using var fixture = new MultiRasterFixture(imageCount: 2);
        InlineImageRun secondRun = fixture.ImageRuns[1];
        ImageBox originalSecondImage = secondRun.Image;
        ImageBox firstImage = fixture.ImageRuns[0].Image;
        FieldInfo imageField = typeof(InlineImageRun).GetField(
            nameof(InlineImageRun.Image),
            BindingFlags.Public | BindingFlags.Instance)!;
        int referencesBefore = GetEntryReferenceCount(GetImageLease(firstImage));

        try
        {
            imageField.SetValue(secondRun, firstImage);
            MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();

            Assert.False(fixture.Source.TryCreatePrivateRasterImageCandidates(
                candidateContext,
                320f,
                out InlineContainerBox? candidate,
                out RasterPlacementSourceFence[]? expectedSources));

            Assert.Null(candidate);
            Assert.Null(expectedSources);
            Assert.Equal(referencesBefore, GetEntryReferenceCount(GetImageLease(firstImage)));
        }
        finally
        {
            imageField.SetValue(secondRun, originalSecondImage);
        }
    }

    [Fact]
    public async Task MalformedInlineImageOffsetOrRunIndexRejectsBeforeCloneLeaseAcquisition()
    {
        await using var fixture = new MultiRasterFixture(imageCount: 2);
        FieldInfo entriesField = typeof(InlineContainerBox).GetField(
            "_inlineImageRuns",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entries = Assert.IsType<List<(InlineImageRun Run, int Offset)>>(
            entriesField.GetValue(fixture.Source));
        var secondEntry = entries[1];
        int[] referencesBefore = fixture.ImageBoxes
            .Select(static image => GetEntryReferenceCount(GetImageLease(image)))
            .ToArray();

        try
        {
            entries[1] = (secondEntry.Run, secondEntry.Offset + 1);
            MarkdownLayoutContext badOffsetContext = fixture.CreateCandidateContext();
            Assert.False(fixture.Source.TryCreatePrivateRasterImageCandidates(
                badOffsetContext,
                320f,
                out InlineContainerBox? offsetCandidate,
                out RasterPlacementSourceFence[]? offsetFences));
            Assert.Null(offsetCandidate);
            Assert.Null(offsetFences);
            Assert.Equal(referencesBefore, fixture.ImageBoxes
                .Select(static image => GetEntryReferenceCount(GetImageLease(image))));
        }
        finally
        {
            entries[1] = secondEntry;
        }

        int originalIndex = secondEntry.Run.InlineIndex;
        try
        {
            secondEntry.Run.InlineIndex = originalIndex + 1;
            MarkdownLayoutContext badIndexContext = fixture.CreateCandidateContext();
            Assert.False(fixture.Source.TryCreatePrivateRasterImageCandidates(
                badIndexContext,
                320f,
                out InlineContainerBox? indexCandidate,
                out RasterPlacementSourceFence[]? indexFences));
            Assert.Null(indexCandidate);
            Assert.Null(indexFences);
            Assert.Equal(referencesBefore, fixture.ImageBoxes
                .Select(static image => GetEntryReferenceCount(GetImageLease(image))));
        }
        finally
        {
            secondEntry.Run.InlineIndex = originalIndex;
        }
    }

    [Fact]
    public async Task RasterCandidateRejectsRunAndImageBudgetsBeforeCloning()
    {
        await using var tooManyImages = new MultiRasterFixture(
            imageCount: InlineContainerBox.MaximumPrivateRasterPlacementImages + 1);
        int[] referencesBefore = tooManyImages.ImageBoxes
            .Select(static image => GetEntryReferenceCount(GetImageLease(image)))
            .ToArray();
        MarkdownLayoutContext imageContext = tooManyImages.CreateCandidateContext();

        Assert.False(tooManyImages.Source.TryCreatePrivateRasterImageCandidates(
            imageContext,
            320f,
            out InlineContainerBox? imageCandidate,
            out RasterPlacementSourceFence[]? imageFences));

        Assert.Null(imageCandidate);
        Assert.Null(imageFences);
        Assert.Equal(referencesBefore, tooManyImages.ImageBoxes
            .Select(static image => GetEntryReferenceCount(GetImageLease(image))));

        await using var tooManyRuns = new MultiRasterFixture(imageCount: 1, trailingTextRunCount: 256);
        int runReferenceCount = GetEntryReferenceCount(GetImageLease(tooManyRuns.ImageBoxes[0]));
        MarkdownLayoutContext runContext = tooManyRuns.CreateCandidateContext();

        Assert.False(tooManyRuns.Source.TryCreatePrivateRasterImageCandidates(
            runContext,
            320f,
            out InlineContainerBox? runCandidate,
            out RasterPlacementSourceFence[]? runFences));

        Assert.Null(runCandidate);
        Assert.Null(runFences);
        Assert.Equal(runReferenceCount, GetEntryReferenceCount(GetImageLease(tooManyRuns.ImageBoxes[0])));
    }

    [Fact]
    public async Task MaximumSupportedRasterAndRunCountsAreAccepted()
    {
        await using var fixture = new MultiRasterFixture(
            imageCount: InlineContainerBox.MaximumPrivateRasterPlacementImages,
            trailingTextRunCount: 127);
        int[] referenceCountsBefore = fixture.ImageBoxes
            .Select(static image => GetEntryReferenceCount(GetImageLease(image)))
            .ToArray();
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();

        Assert.Equal(InlineContainerBox.MaximumPrivateRasterPlacementRuns, fixture.Source.Runs.Count);
        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));
        fixture.SetCandidate(candidateContext, candidate);

        InlineContainerBox cloned = Assert.IsType<InlineContainerBox>(candidate);
        Assert.Equal(InlineContainerBox.MaximumPrivateRasterPlacementImages, expectedSources!.Length);
        InlineImageRun[] clonedImageRuns = cloned.Runs.OfType<InlineImageRun>().ToArray();
        Assert.Equal(InlineContainerBox.MaximumPrivateRasterPlacementImages, clonedImageRuns.Length);
        for (int index = 0; index < clonedImageRuns.Length; index++)
        {
            Assert.NotSame(fixture.ImageBoxes[index], clonedImageRuns[index].Image);
            Assert.Same(fixture.ImageBoxes[index].Bitmap, clonedImageRuns[index].Image.Bitmap);
            Assert.Equal(
                referenceCountsBefore[index] + 1,
                GetEntryReferenceCount(GetImageLease(fixture.ImageBoxes[index])));
        }

        float measuredHeight = cloned.Measure(320f);
        cloned.Arrange(0f, 0f, 320f);
        Assert.True(float.IsFinite(measuredHeight));
        Assert.True(measuredHeight > 0);
        Assert.True(cloned.HasMeasuredLayout);
        Assert.True(fixture.Source.HasMeasuredLayout);
    }

    [Theory]
    [InlineData(1.0, FlowDirection.LeftToRight)]
    [InlineData(1.25, FlowDirection.RightToLeft)]
    [InlineData(1.5, FlowDirection.LeftToRight)]
    [InlineData(1.75, FlowDirection.RightToLeft)]
    [InlineData(2.0, FlowDirection.LeftToRight)]
    [InlineData(2.25, FlowDirection.RightToLeft)]
    [InlineData(3.0, FlowDirection.LeftToRight)]
    [InlineData(4.0, FlowDirection.RightToLeft)]
    public async Task MultiRasterClonePreservesIntrinsicMetricsAtSimulatedScaleAndFlow(
        double rasterizationScale,
        FlowDirection flowDirection)
    {
        await using var fixture = new MultiRasterFixture(
            imageCount: 2,
            rasterizationScale: rasterizationScale,
            flowDirection: flowDirection);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();

        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));
        fixture.SetCandidate(candidateContext, candidate);
        InlineContainerBox cloned = Assert.IsType<InlineContainerBox>(candidate);
        Assert.Equal(rasterizationScale, cloned.Context.RasterizationScale);
        Assert.Equal(flowDirection, cloned.Context.FlowDirection);
        Assert.Equal(fixture.ImageBoxes.Count, expectedSources!.Length);
        Assert.Equal(
            fixture.ImageBoxes.Select(static image => GetField<Size>(image, "_rasterIntrinsicSize")),
            cloned.Runs.OfType<InlineImageRun>()
                .Select(static run => GetField<Size>(run.Image, "_rasterIntrinsicSize")));

        float measuredHeight = cloned.Measure(280f);
        cloned.Arrange(0f, 0f, 280f);
        Assert.True(float.IsFinite(measuredHeight));
        Assert.True(measuredHeight > 0);
        Assert.All(cloned.Runs.OfType<InlineImageRun>(), static run =>
        {
            Assert.True(double.IsFinite(run.Image.Bounds.X));
            Assert.True(double.IsFinite(run.Image.Bounds.Y));
            Assert.True(double.IsFinite(run.Image.Bounds.Width));
            Assert.True(double.IsFinite(run.Image.Bounds.Height));
            Assert.True(run.Image.Bounds.Width > 0);
            Assert.True(run.Image.Bounds.Height > 0);
        });
    }

    [Fact]
    public async Task RasterCloneRejectsChangedDpiOrFlowContext()
    {
        await using var fixture = new MultiRasterFixture(
            imageCount: 2,
            rasterizationScale: 1.5,
            flowDirection: FlowDirection.LeftToRight);

        MarkdownLayoutContext changedScale = fixture.CreateCandidateContext(rasterizationScale: 2.0);
        Assert.False(fixture.Source.TryCreatePrivateRasterImageCandidates(
            changedScale,
            320f,
            out InlineContainerBox? scaleCandidate,
            out RasterPlacementSourceFence[]? scaleFences));
        Assert.Null(scaleCandidate);
        Assert.Null(scaleFences);

        MarkdownLayoutContext changedFlow = fixture.CreateCandidateContext(flowDirection: FlowDirection.RightToLeft);
        Assert.False(fixture.Source.TryCreatePrivateRasterImageCandidates(
            changedFlow,
            320f,
            out InlineContainerBox? flowCandidate,
            out RasterPlacementSourceFence[]? flowFences));
        Assert.Null(flowCandidate);
        Assert.Null(flowFences);
    }

    private static DocumentRange WholeBlockRange(InlineContainerBox block)
    {
        InlineRun last = block.Runs[^1];
        return new DocumentRange(
            new DocumentPosition(block.BlockIndex, 0, 0),
            new DocumentPosition(block.BlockIndex, last.InlineIndex, last.Text.Length));
    }

    private static string GetWholeBlockSourceSlice(InlineContainerBox block)
        => block.Context.SourceMap.Slice(WholeBlockRange(block));

    private static SharedCanvasBitmapCache.Lease GetImageLease(ImageBox image)
    {
        FieldInfo field = typeof(ImageBox).GetField("_bitmapLease", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<SharedCanvasBitmapCache.Lease>(field.GetValue(image));
    }

    private static int GetEntryReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        FieldInfo entryField = typeof(SharedCanvasBitmapCache.Lease).GetField(
            "_entry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object entry = Assert.IsAssignableFrom<SharedCanvasBitmapCache.Entry>(entryField.GetValue(lease));
        FieldInfo countField = typeof(SharedCanvasBitmapCache.Entry).GetField(
            "_referenceCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<int>(countField.GetValue(entry));
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    internal sealed class MultiRasterFixture : IAsyncDisposable
    {
        private const string SvgDataUri = "data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2710%27%20height%3D%275%27%2F%3E";
        private readonly CanvasDevice _device = new();
        private readonly ThemeSnapshot _theme;
        private readonly MarkdownExtensionRegistry _registry = new();
        private readonly MarkdownSourceMap _sourceMap;
        private readonly object _imagePreparationOwner = new();
        private bool _sourceRetired;
        private bool _candidateRetired;
        private LayoutSnapshot? _sourceSnapshot;

        internal MultiRasterFixture(
            int imageCount = 3,
            bool finalImageIsSvg = false,
            int trailingTextRunCount = 0,
            double rasterizationScale = 1.0,
            FlowDirection flowDirection = FlowDirection.LeftToRight)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(imageCount);
            ArgumentOutOfRangeException.ThrowIfNegative(trailingTextRunCount);
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.Strong] = body,
                [MarkdownElementKeys.Emphasis] = body,
                [MarkdownElementKeys.Link] = body,
                [MarkdownElementKeys.Heading2] = body,
                [MarkdownElementKeys.Heading3] = body,
            };
            Color transparent = Color.FromArgb(0, 0, 0, 0);
            _theme = new ThemeSnapshot(
                styles,
                new Dictionary<string, ElementStyleOverride>(),
                transparent,
                transparent,
                transparent,
                transparent,
                isDark: false,
                isHighContrast: false,
                textScaleFactor: 1f);

            (List<(string? Text, int ImageIndex)> segments, string sourceText) = CreateSegments(imageCount, trailingTextRunCount);
            _sourceMap = new MarkdownSourceMap(sourceText);
            SourceContext = CreateContext(CancellationToken.None, rasterizationScale, flowDirection);
            Source = new InlineContainerBox(SourceContext, MarkdownElementKeys.Body)
            {
                BlockIndex = SourceContext.NextBlockIndex(),
            };
            ImageRuns = new List<InlineImageRun>(imageCount);
            ImageBoxes = new List<ImageBox>(imageCount);

            int sourceOffset = 0;
            int imageOrdinal = 0;
            foreach ((string? text, int segmentImageIndex) in segments)
            {
                if (text is { } value)
                {
                    Source.Add(new TextRun(value)
                    {
                        ElementKey = imageOrdinal % 2 == 0 ? MarkdownElementKeys.Strong : string.Empty,
                        SourceSpan = new SourceSpan(sourceOffset, value.Length),
                        StyleAliases = new[] { $"text-run-{Source.Runs.Count}" },
                        StyleModifierKeys = imageOrdinal % 2 == 0
                            ? new[] { MarkdownElementKeys.Emphasis }
                            : Array.Empty<string>(),
                        SemanticHeadingKey = MarkdownElementKeys.Heading2,
                    });
                    sourceOffset += value.Length;
                }
                else
                {
                    string imageUrl;
                    if (finalImageIsSvg && segmentImageIndex == imageCount - 1)
                    {
                        imageUrl = SvgDataUri;
                    }
                    else
                    {
                        imageUrl = $"ms-appx:///multi-raster-{Guid.NewGuid():N}-{segmentImageIndex}.png";
                        StoreRaster(imageUrl, segmentImageIndex);
                    }

                    var imageRun = new InlineImageRun(
                        SourceContext,
                        $"Raster alt {segmentImageIndex}",
                        imageUrl,
                        linkUrl: $"https://example.test/image-{segmentImageIndex}")
                    {
                        ElementKey = MarkdownElementKeys.Link,
                        SourceSpan = new SourceSpan(sourceOffset, 1),
                        StyleAliases = new[] { $"image-run-{segmentImageIndex}" },
                        StyleModifierKeys = new[] { MarkdownElementKeys.Strong },
                        SemanticHeadingKey = MarkdownElementKeys.Heading3,
                    };
                    Source.Add(imageRun);
                    ImageRuns.Add(imageRun);
                    ImageBoxes.Add(imageRun.Image);
                    sourceOffset++;
                    imageOrdinal++;
                }
            }

            if (sourceOffset != sourceText.Length)
                throw new InvalidOperationException("The multi-raster source fixture produced inconsistent source spans.");
            _ = Source.Measure(320f);
            Source.Arrange(0f, 0f, 320f);
            Assert.Equal(imageCount, ImageRuns.Count);
            if (!finalImageIsSvg)
                Assert.All(ImageBoxes, static image => Assert.NotNull(image.Bitmap));
        }

        internal CanvasDevice Device => _device;
        internal string SourceText => _sourceMap.SourceText;
        internal MarkdownLayoutContext SourceContext { get; }
        internal InlineContainerBox Source { get; }
        internal List<InlineImageRun> ImageRuns { get; }
        internal List<ImageBox> ImageBoxes { get; }

        internal MarkdownLayoutContext CreateCandidateContext(
            CancellationToken cancellationToken = default,
            double? rasterizationScale = null,
            FlowDirection? flowDirection = null)
        {
            MarkdownLayoutContext candidateContext = new(
                SourceContext.ResourceCreator,
                SourceContext.ThemeSnapshot,
                SourceContext.SourceMap,
                SourceContext.Registry,
                flowDirection ?? SourceContext.FlowDirection,
                SourceContext.Dispatcher,
                SourceContext.Language)
            {
                CancellationToken = CancellationToken.None,
                ImageCancellationToken = SourceContext.ImageCancellationToken,
                RetirementSink = SourceContext.RetirementSink,
                RasterizationScale = rasterizationScale ?? SourceContext.RasterizationScale,
                IsCodeBlockCopyEnabled = SourceContext.IsCodeBlockCopyEnabled,
                CodeBlockLineNumberMode = SourceContext.CodeBlockLineNumberMode,
                CodeBlockWrappingMode = SourceContext.CodeBlockWrappingMode,
                CodeBlockChromeMode = SourceContext.CodeBlockChromeMode,
                IsTaskListEditingEnabled = SourceContext.IsTaskListEditingEnabled,
                CommandProvider = SourceContext.CommandProvider,
                StringProvider = SourceContext.StringProvider,
                ImageResolver = SourceContext.ImageResolver,
                PerformanceSession = SourceContext.PerformanceSession,
                PerformanceDocumentOwner = null,
                ImagePreparationOwner = _imagePreparationOwner,
                SvgRenderer = SourceContext.SvgRenderer,
                ImageBaseUri = SourceContext.ImageBaseUri,
                ImageDocumentPath = SourceContext.ImageDocumentPath,
                ImageDocumentSource = SourceContext.ImageDocumentSource,
                AllowThirdPartyRemoteImages = SourceContext.AllowThirdPartyRemoteImages,
                DisclosureStates = SourceContext.DisclosureStates,
            };
            candidateContext.SetCancellationToken(cancellationToken);
            return candidateContext;
        }

        internal void TransferSourceOwnershipToSnapshot(LayoutSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (_sourceRetired || _sourceSnapshot is not null)
                throw new InvalidOperationException("The fixture source already has a retirement owner.");
            if (!snapshot.Blocks.Any(block => ReferenceEquals(block, Source)))
                throw new InvalidOperationException("The snapshot does not own this fixture's source root.");

            // The snapshot now owns the source tree, but its retirement is
            // asynchronous. Keep the snapshot identity so fixture teardown
            // joins the actual native-resource drain before disposing device.
            _sourceSnapshot = snapshot;
        }

        internal void SetCandidate(MarkdownLayoutContext context, InlineContainerBox? candidate)
        {
            CandidateContext = context;
            Candidate = candidate;
        }

        private MarkdownLayoutContext? CandidateContext { get; set; }
        private InlineContainerBox? Candidate { get; set; }

        internal void ReleaseCacheEntries() => SharedCanvasBitmapCache.ReleaseDevice(_device);

        internal async Task RetireSourceAsync()
        {
            if (_sourceRetired)
                return;
            _sourceRetired = true;
            if (_sourceSnapshot is { } snapshot)
            {
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                SourceContext.DiscardConstructionBlock(Source);
                await Task.WhenAll(ImageBoxes.Select(static image => image.RetirementTask))
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        internal async Task RetireCandidateAsync(MarkdownLayoutContext context, InlineContainerBox candidate)
        {
            if (_candidateRetired)
                return;
            _candidateRetired = true;
            ImageBox[] images = candidate.Runs.OfType<InlineImageRun>()
                .Select(static run => run.Image)
                .ToArray();
            context.DiscardConstructionBlock(candidate);
            await Task.WhenAll(images.Select(static image => image.RetirementTask))
                .WaitAsync(TimeSpan.FromSeconds(10));
            await context.DrainOwnedRetirementsAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Candidate is not null && CandidateContext is not null)
                await RetireCandidateAsync(CandidateContext, Candidate);
            await RetireSourceAsync();
            SharedCanvasBitmapCache.ReleaseDevice(_device);
            _device.Dispose();
        }

        private MarkdownLayoutContext CreateContext(
            CancellationToken cancellationToken,
            double rasterizationScale,
            FlowDirection flowDirection)
            => new(
                _device,
                _theme,
                _sourceMap,
                _registry,
                flowDirection,
                language: "en-US")
            {
                CancellationToken = cancellationToken,
                ImageCancellationToken = CancellationToken.None,
                ImagePreparationOwner = _imagePreparationOwner,
                DisclosureStates = new Dictionary<string, bool>(),
                RasterizationScale = rasterizationScale,
            };

        private void StoreRaster(string source, int imageOrdinal)
        {
            Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(source, null, out MarkdownBuiltInImageSource resolved));
            int width = 64 + imageOrdinal * 8;
            int height = 32 + imageOrdinal * 4;
            using SharedCanvasBitmapCache.Lease lease = SharedCanvasBitmapCache.StoreAndAcquire(
                _device,
                resolved.CacheKey,
                CreateBitmap(_device, width, height),
                new Size(width * 1.5, height * 1.5));
        }

        private static (List<(string? Text, int ImageIndex)> Segments, string SourceText) CreateSegments(
            int imageCount,
            int trailingTextRunCount)
        {
            var segments = new List<(string? Text, int ImageIndex)>(imageCount * 2 + trailingTextRunCount + 1)
            {
                ("before e\u0301 👩‍👩‍👧 ", -1),
            };
            for (int index = 0; index < imageCount; index++)
            {
                segments.Add((null, index));
                string nextText = index == imageCount - 1
                    ? " after אבג"
                    : $" middle-{index} אבג ";
                segments.Add((nextText, -1));
            }
            for (int index = 0; index < trailingTextRunCount; index++)
                segments.Add(($" tail-{index}", -1));

            var source = new StringBuilder();
            foreach ((string? text, _) in segments)
                source.Append(text ?? "!");
            return (segments, source.ToString());
        }

        private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
        {
            byte[] pixels = new byte[checked(width * height * 4)];
            return CanvasBitmap.CreateFromBytes(
                device,
                pixels,
                width,
                height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
    }
}
