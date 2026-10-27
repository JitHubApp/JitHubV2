using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class SourceSliceMeasuredPlacementTransferTests
{
    private const int DirectSliceLength = 8192;

    [Fact]
    public async Task CompletedImageTransferKeepsDirectSourceSlicesLazyThroughValidationAndSwap()
    {
        var device = new CanvasDevice(forceSoftwareRenderer: true);
        InlineContainerBox? source = null;
        InlineContainerBox? candidate = null;
        MarkdownLayoutContext? sourceContext = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            string sourceText = string.Concat(
                new string('a', DirectSliceLength),
                "!",
                new string('b', DirectSliceLength));
            var sourceMap = new MarkdownSourceMap(sourceText);
            ThemeSnapshot theme = CreateTheme();
            var registry = new MarkdownExtensionRegistry();
            IReadOnlyDictionary<string, bool> disclosures = new Dictionary<string, bool>();
            object documentOwner = new();
            object preparationOwner = new();
            string imageUri = new Uri($"ms-appx:///source-slice-placement-{Guid.NewGuid():N}.png").AbsoluteUri;
            Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
                imageUri, null, out MarkdownBuiltInImageSource resolved));
            using (SharedCanvasBitmapCache.StoreAndAcquire(
                       device,
                       resolved.CacheKey,
                       CreateBitmap(device, 64, 32),
                       new Size(64, 32)))
            {
            }

            sourceContext = CreateContext(device, theme, sourceMap, registry, documentOwner, preparationOwner, disclosures);
            source = new InlineContainerBox(sourceContext, MarkdownElementKeys.Body)
            {
                BlockIndex = sourceContext.NextBlockIndex(),
            };
            var left = new SourceSliceTextRun(sourceText, 0, DirectSliceLength)
            {
                SourceSpan = new SourceSpan(0, DirectSliceLength),
                StyleAliases = new[] { "source-literal" },
                StyleModifierKeys = new[] { MarkdownElementKeys.Strong },
            };
            var sourceImageRun = new InlineImageRun(sourceContext, "image", imageUri)
            {
                SourceSpan = new SourceSpan(DirectSliceLength, 1),
            };
            var right = new SourceSliceTextRun(
                sourceText, DirectSliceLength + 1, DirectSliceLength)
            {
                SourceSpan = new SourceSpan(DirectSliceLength + 1, DirectSliceLength),
            };
            source.Add(left);
            source.Add(sourceImageRun);
            source.Add(right);

            AssertUnmaterialized(left, right);
            _ = source.Measure(320f);
            source.Arrange(0f, 0f, 320f);
            Assert.NotNull(sourceImageRun.Image.Bitmap);
            AssertUnmaterialized(left, right);

            candidateContext = CreateContext(device, theme, sourceMap, registry, documentOwner, preparationOwner, disclosures);
            Assert.True(InlineContainerBox.ArePrivateRasterPlacementContextsCompatible(sourceContext, candidateContext));
            Assert.True(source.TryCreatePrivateRasterImageCandidates(
                candidateContext,
                320f,
                out candidate,
                out _));
            Assert.NotNull(candidate);
            _ = Assert.Single(candidate!.Runs.OfType<InlineImageRun>());
            SourceSliceTextRun[] candidateSlices = candidate.Runs.OfType<SourceSliceTextRun>().ToArray();
            Assert.Equal(2, candidateSlices.Length);
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);

            Assert.True(source.TryCaptureMeasuredPlacementTransfer(
                candidate,
                320f,
                out InlineContainerBox.MeasuredPlacementTransfer? transfer));
            Assert.NotNull(transfer);
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);
            Assert.False(transfer!.IsCurrentAndPrepared());

            _ = candidate.Measure(320f);
            candidate.Arrange(0f, 0f, 320f);
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);
            Assert.True(transfer!.IsCurrentAndPrepared());
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);

            SourceSpan sourceOriginalSpan = left.SourceSpan;
            left.SourceSpan = new SourceSpan(sourceOriginalSpan.Start + 1, sourceOriginalSpan.Length);
            Assert.False(transfer.IsCurrentAndPrepared());
            left.SourceSpan = sourceOriginalSpan;
            Assert.True(transfer.IsCurrentAndPrepared());

            SourceSliceTextRun spanFenceRun = candidateSlices[0];
            SourceSpan originalSpan = spanFenceRun.SourceSpan;
            spanFenceRun.SourceSpan = new SourceSpan(originalSpan.Start + 1, originalSpan.Length);
            Assert.False(transfer.IsCurrentAndPrepared());
            spanFenceRun.SourceSpan = originalSpan;
            Assert.True(transfer.IsCurrentAndPrepared());
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);

            SourceSliceTextRun indexFenceRun = candidateSlices[1];
            int originalInlineIndex = indexFenceRun.InlineIndex;
            indexFenceRun.InlineIndex = originalInlineIndex - 1;
            Assert.False(transfer.IsCurrentAndPrepared());
            indexFenceRun.InlineIndex = originalInlineIndex;
            Assert.True(transfer.IsCurrentAndPrepared());
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);

            SourceSliceTextRun styleFenceRun = candidateSlices[0];
            IReadOnlyList<string> originalAliases = styleFenceRun.StyleAliases;
            styleFenceRun.StyleAliases = new[] { "changed-alias" };
            Assert.False(transfer.IsCurrentAndPrepared());
            styleFenceRun.StyleAliases = originalAliases;
            Assert.True(transfer.IsCurrentAndPrepared());

            IReadOnlyList<string> originalModifiers = styleFenceRun.StyleModifierKeys;
            styleFenceRun.StyleModifierKeys = new[] { MarkdownElementKeys.Emphasis };
            Assert.False(transfer.IsCurrentAndPrepared());
            styleFenceRun.StyleModifierKeys = originalModifiers;
            Assert.True(transfer.IsCurrentAndPrepared());
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);

            transfer.SwapPlacementNoThrow();
            AssertUnmaterialized(left, right);
            AssertUnmaterialized(candidateSlices);
        }
        finally
        {
            if (candidate is not null && candidateContext is not null)
            {
                InlineImageRun[] imageRuns = candidate.Runs.OfType<InlineImageRun>().ToArray();
                candidateContext.DiscardConstructionBlock(candidate);
                foreach (InlineImageRun imageRun in imageRuns)
                    await imageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (source is not null && sourceContext is not null)
            {
                InlineImageRun[] imageRuns = source.Runs.OfType<InlineImageRun>().ToArray();
                sourceContext.DiscardConstructionBlock(source);
                foreach (InlineImageRun imageRun in imageRuns)
                    await imageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }

            SharedCanvasBitmapCache.ReleaseDevice(device);
            device.Dispose();
        }
    }

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        ThemeSnapshot theme,
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry registry,
        object documentOwner,
        object preparationOwner,
        IReadOnlyDictionary<string, bool> disclosures) =>
        new MarkdownLayoutContext(
            device,
            theme,
            sourceMap,
            registry,
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            PerformanceDocumentOwner = documentOwner,
            ImagePreparationOwner = preparationOwner,
            DisclosureStates = disclosures,
        };

    private static ThemeSnapshot CreateTheme()
    {
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    private static void AssertUnmaterialized(params SourceSliceTextRun[] runs)
    {
        FieldInfo cache = typeof(TextRun).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(cache);
        foreach (SourceSliceTextRun run in runs)
            Assert.Equal(string.Empty, cache.GetValue(run));
    }
}
