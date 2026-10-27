using System.Collections.Generic;
using System.Reflection;
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

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class MultiRootInlinePlacementCloneTests
{
    [Fact]
    public async Task AllocationFreePreflightAcceptsTextOnlyAndRasterOnlyRoots()
    {
        await using var textOnly = new InlinePlacementFixture(InlinePlacementKind.TextOnly);
        MarkdownLayoutContext textContext = textOnly.CreateCandidateContext();
        Assert.True(textOnly.Source.TryGetPrivateRasterPlacementCounts(
            textContext,
            320f,
            out int textRunCount,
            out int textImageCount));
        Assert.Equal(1, textRunCount);
        Assert.Equal(0, textImageCount);
        Assert.True(textOnly.Source.TryCreatePrivateInlinePlacementClone(
            textContext,
            320f,
            out InlineContainerBox? textCandidate,
            out RasterPlacementSourceFence[]? textSources));
        textOnly.SetCandidate(textContext, textCandidate);
        InlineContainerBox textClone = Assert.IsType<InlineContainerBox>(textCandidate);
        Assert.Empty(textSources!);
        Assert.False(GetField<bool>(textClone, "_hasAtomicInlineObject"));
        float textHeight = textClone.Measure(240f);
        textClone.Arrange(0f, 0f, 240f);
        Assert.True(float.IsFinite(textHeight));
        Assert.True(textHeight > 0f);
        Assert.False(GetField<bool>(textClone, "_hasAtomicInlineObject"));

        await using var rasterOnly = new InlinePlacementFixture(InlinePlacementKind.RasterOnly);
        MarkdownLayoutContext rasterContext = rasterOnly.CreateCandidateContext();
        Assert.True(rasterOnly.Source.TryGetPrivateRasterPlacementCounts(
            rasterContext,
            320f,
            out int rasterRunCount,
            out int rasterImageCount));
        Assert.Equal(1, rasterRunCount);
        Assert.Equal(1, rasterImageCount);

        // The snapshot builder invokes this under its source read gate. Warm
        // the exact path first, then assert the per-root descriptor scan stays
        // allocation-free (it acquires no clone lease and creates no sidecar).
        for (int index = 0; index < 8; index++)
            Assert.True(rasterOnly.Source.TryGetPrivateRasterPlacementCounts(
                rasterContext, 320f, out _, out _));

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = true;
        for (int index = 0; index < 32; index++)
            accepted &= rasterOnly.Source.TryGetPrivateRasterPlacementCounts(
                rasterContext, 320f, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(accepted);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task OrdinaryLinkClonePreservesLogicalAndStyleMetadataAndIndependentRuns()
    {
        await using var fixture = new InlinePlacementFixture(InlinePlacementKind.LinkAndRaster);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();
        InlineRun[] sourceRuns = fixture.Source.Runs.ToArray();
        Rect sourceBounds = fixture.Source.Bounds;
        CanvasTextLayout sourceLayout = GetField<CanvasTextLayout>(fixture.Source, "_layout");

        Assert.True(fixture.Source.TryCreatePrivateInlinePlacementClone(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));
        fixture.SetCandidate(candidateContext, candidate);

        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        Assert.Single(expectedSources!);
        Assert.False(clone.HasMeasuredLayout);
        InlineRun[] clonedRuns = clone.Runs.ToArray();
        Assert.Equal(sourceRuns.Length, clonedRuns.Length);
        for (int index = 0; index < sourceRuns.Length; index++)
        {
            Assert.NotSame(sourceRuns[index], clonedRuns[index]);
            Assert.Equal(sourceRuns[index].InlineIndex, clonedRuns[index].InlineIndex);
            Assert.Equal(sourceRuns[index].RenderedLength, clonedRuns[index].RenderedLength);
            Assert.Equal(sourceRuns[index].SourceSpan, clonedRuns[index].SourceSpan);
            Assert.Equal(sourceRuns[index].ElementKey, clonedRuns[index].ElementKey);
            Assert.Equal(sourceRuns[index].StyleAliases, clonedRuns[index].StyleAliases);
            Assert.NotSame(sourceRuns[index].StyleAliases, clonedRuns[index].StyleAliases);
            Assert.Equal(sourceRuns[index].StyleModifierKeys, clonedRuns[index].StyleModifierKeys);
            Assert.Equal(sourceRuns[index].SemanticHeadingKey, clonedRuns[index].SemanticHeadingKey);
            Assert.Equal(sourceRuns[index].Text, clonedRuns[index].Text);
            Assert.Equal(sourceRuns[index].AccessibleText, clonedRuns[index].AccessibleText);
        }

        LinkRun sourceLink = Assert.IsType<LinkRun>(sourceRuns[1]);
        LinkRun clonedLink = Assert.IsType<LinkRun>(clonedRuns[1]);
        Assert.Equal(sourceLink.Url, clonedLink.Url);
        Assert.Equal(sourceLink.Title, clonedLink.Title);
        Assert.Equal(sourceLink.AccessibilityName, clonedLink.AccessibilityName);
        Assert.Equal(sourceLink.DisclosureId, clonedLink.DisclosureId);
        Assert.Equal(sourceLink.IsExpanded, clonedLink.IsExpanded);
        Assert.Equal(sourceLink.IsSuperscript, clonedLink.IsSuperscript);
        Assert.Equal(sourceBounds, fixture.Source.Bounds);
        Assert.Same(sourceLayout, GetField<CanvasTextLayout>(fixture.Source, "_layout"));

        float measuredHeight = clone.Measure(240f);
        clone.Arrange(0f, 0f, 240f);
        Assert.True(float.IsFinite(measuredHeight));
        Assert.True(measuredHeight > 0);
        Assert.True(clone.HasMeasuredLayout);
        Assert.Same(sourceLayout, GetField<CanvasTextLayout>(fixture.Source, "_layout"));
    }

    [Fact]
    public async Task DisclosureLinkRejectsWholeRootBeforeAcquiringAnyRasterCloneLease()
    {
        await using var fixture = new InlinePlacementFixture(InlinePlacementKind.DisclosureLinkAndRaster);
        SharedCanvasBitmapCache.Lease originalLease = GetImageLease(fixture.ImageRuns.Single().Image);
        int referencesBefore = GetEntryReferenceCount(originalLease);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();

        Assert.False(fixture.Source.TryGetPrivateRasterPlacementCounts(
            candidateContext, 320f, out int runCount, out int imageCount));
        Assert.Equal(0, runCount);
        Assert.Equal(0, imageCount);
        Assert.False(fixture.Source.TryCreatePrivateInlinePlacementClone(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));

        Assert.Null(candidate);
        Assert.Null(expectedSources);
        Assert.Equal(referencesBefore, GetEntryReferenceCount(originalLease));
        Assert.NotNull(fixture.ImageRuns.Single().Image.Bitmap);
    }

    [Fact]
    public async Task UnsupportedInlineRunRejectsBeforeAcquiringAnyRasterCloneLease()
    {
        await using var fixture = new InlinePlacementFixture(InlinePlacementKind.UnsupportedCustomAndRaster);
        SharedCanvasBitmapCache.Lease originalLease = GetImageLease(fixture.ImageRuns.Single().Image);
        int referencesBefore = GetEntryReferenceCount(originalLease);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();

        Assert.False(fixture.Source.TryGetPrivateRasterPlacementCounts(
            candidateContext, 320f, out int runCount, out int imageCount));
        Assert.Equal(0, runCount);
        Assert.Equal(0, imageCount);
        Assert.False(fixture.Source.TryCreatePrivateInlinePlacementClone(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));

        Assert.Null(candidate);
        Assert.Null(expectedSources);
        Assert.Equal(referencesBefore, GetEntryReferenceCount(originalLease));
    }

    [Fact]
    public async Task PrivateInlineCloneCopiesRawAndCollapsedOuterMarginState()
    {
        await using var fixture = new InlinePlacementFixture(
            InlinePlacementKind.LinkAndRaster,
            collapsedTopMargin: 3,
            suppressTopMargin: true,
            suppressBottomMargin: true);
        MarkdownLayoutContext candidateContext = fixture.CreateCandidateContext();

        Assert.True(fixture.Source.TryCreatePrivateInlinePlacementClone(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out _));
        fixture.SetCandidate(candidateContext, candidate);

        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        Assert.Equal(fixture.Source.CollapsedTopMargin, clone.CollapsedTopMargin);
        Assert.Equal(fixture.Source.SuppressTopMargin, clone.SuppressTopMargin);
        Assert.Equal(fixture.Source.SuppressBottomMargin, clone.SuppressBottomMargin);
        Assert.Equal(GetBaseMargin(fixture.Source), GetBaseMargin(clone));
        Assert.Equal(fixture.Source.Margin, clone.Margin);

        float sourceHeight = fixture.Source.Measure(240f);
        float cloneHeight = clone.Measure(240f);
        Assert.Equal(sourceHeight, cloneHeight);
        Assert.Equal(fixture.Source.Margin, clone.Margin);
    }

    [Fact]
    public async Task InlineCodeAndRasterCloneKeepsNativeGeometryAndPrivateTextOwnership()
    {
        await using var fixture = new InlinePlacementFixture(InlinePlacementKind.CodeAndRaster);
        MarkdownLayoutContext context = fixture.CreateCandidateContext();
        Rect sourceBounds = fixture.Source.Bounds;
        CanvasTextLayout sourceLayout = GetField<CanvasTextLayout>(fixture.Source, "_layout");
        Assert.True(fixture.Source.TryCreatePrivateInlinePlacementClone(
            context, 320f, out InlineContainerBox? candidate, out RasterPlacementSourceFence[]? sources));
        fixture.SetCandidate(context, candidate);
        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        Assert.Single(sources!);
        CodeInlineRun sourceCode = Assert.IsType<CodeInlineRun>(fixture.Source.Runs[0]);
        CodeInlineRun clonedCode = Assert.IsType<CodeInlineRun>(clone.Runs[0]);
        Assert.NotSame(sourceCode, clonedCode);
        Assert.Equal(sourceCode.SourceSpan, clonedCode.SourceSpan);
        Assert.Equal(sourceCode.StyleAliases, clonedCode.StyleAliases);
        Assert.NotSame(sourceCode.StyleAliases, clonedCode.StyleAliases);
        _ = clone.Measure(320f);
        clone.Arrange(0f, 0f, 320f);
        Assert.Equal(sourceBounds, clone.Bounds);
        Assert.NotSame(sourceLayout, GetField<CanvasTextLayout>(clone, "_layout"));
        Assert.Same(sourceLayout, GetField<CanvasTextLayout>(fixture.Source, "_layout"));
        Assert.Equal(sourceBounds, fixture.Source.Bounds);
        Assert.Equal(fixture.Source.EnumerateInlineImageRects().Single().Rect,
            clone.EnumerateInlineImageRects().Single().Rect);
    }

    private sealed class OpaqueTestRun : InlineRun
    {
        private readonly string _text;
        internal OpaqueTestRun(string text)
        {
            _text = text;
            RenderedLength = text.Length;
        }
        public override string Text => _text;
    }

    private static Thickness GetBaseMargin(BlockBox box)
    {
        FieldInfo field = typeof(BlockBox).GetField("_margin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<Thickness>(field.GetValue(box));
    }

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

    private enum InlinePlacementKind
    {
        TextOnly,
        RasterOnly,
        LinkAndRaster,
        DisclosureLinkAndRaster,
        CodeAndRaster,
        UnsupportedCustomAndRaster,
    }

    private sealed class InlinePlacementFixture : IAsyncDisposable
    {
        private readonly CanvasDevice _device = new();
        private readonly ThemeSnapshot _theme;
        private readonly MarkdownExtensionRegistry _registry = new();
        private readonly MarkdownSourceMap _sourceMap;
        private readonly object _imagePreparationOwner = new();
        private bool _sourceRetired;
        private bool _candidateRetired;

        internal InlinePlacementFixture(
            InlinePlacementKind kind,
            double collapsedTopMargin = 0,
            bool suppressTopMargin = false,
            bool suppressBottomMargin = false)
        {
            string source = kind switch
            {
                InlinePlacementKind.TextOnly => "plain text",
                InlinePlacementKind.RasterOnly => "!",
                InlinePlacementKind.LinkAndRaster => "pre doc!",
                InlinePlacementKind.DisclosureLinkAndRaster => "click!",
                InlinePlacementKind.CodeAndRaster or InlinePlacementKind.UnsupportedCustomAndRaster => "code!",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            _sourceMap = new MarkdownSourceMap(source);
            var body = new ElementStyle
            {
                FontFamily = "Segoe UI",
                FontSize = 16f,
                Margin = new Thickness(2, 5, 3, 7),
            };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.Strong] = body,
                [MarkdownElementKeys.Emphasis] = body,
                [MarkdownElementKeys.Link] = body,
                [MarkdownElementKeys.Heading2] = body,
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
            SourceContext = new MarkdownLayoutContext(
                _device,
                _theme,
                _sourceMap,
                _registry,
                FlowDirection.LeftToRight,
                language: "en-US")
            {
                CancellationToken = CancellationToken.None,
                ImageCancellationToken = CancellationToken.None,
                ImagePreparationOwner = _imagePreparationOwner,
                DisclosureStates = new Dictionary<string, bool>(),
                RasterizationScale = 1.0,
            };
            Source = new InlineContainerBox(SourceContext, MarkdownElementKeys.Body)
            {
                BlockIndex = SourceContext.NextBlockIndex(),
            };
            Source.CollapsedTopMargin = collapsedTopMargin;
            Source.SuppressTopMargin = suppressTopMargin;
            Source.SuppressBottomMargin = suppressBottomMargin;

            switch (kind)
            {
                case InlinePlacementKind.TextOnly:
                    AddText("plain text", 0);
                    break;
                case InlinePlacementKind.RasterOnly:
                    AddImage("!", 0, "Raster only alt");
                    break;
                case InlinePlacementKind.LinkAndRaster:
                    AddText("pre ", 0);
                    AddLink("doc", 4, disclosureId: null, expanded: false);
                    AddImage("!", 7, "Link raster alt");
                    break;
                case InlinePlacementKind.DisclosureLinkAndRaster:
                    AddLink("click", 0, disclosureId: "section-1", expanded: true);
                    AddImage("!", 5, "Disclosure raster alt");
                    break;
                case InlinePlacementKind.CodeAndRaster:
                    Source.Add(new CodeInlineRun("code")
                    {
                        SourceSpan = new SourceSpan(0, 4),
                        StyleAliases = new[] { "inline-code" },
                        SemanticHeadingKey = MarkdownElementKeys.Heading2,
                    });
                    AddImage("!", 4, "Code raster alt");
                    break;
                case InlinePlacementKind.UnsupportedCustomAndRaster:
                    Source.Add(new OpaqueTestRun("code")
                    {
                        SourceSpan = new SourceSpan(0, 4),
                        StyleAliases = new[] { "unsupported-custom" },
                    });
                    AddImage("!", 4, "Unsupported raster alt");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            _ = Source.Measure(320f);
            Source.Arrange(0f, 0f, 320f);
            if (ImageRuns.Count != 0)
                Assert.All(ImageRuns, static run => Assert.NotNull(run.Image.Bitmap));
        }

        internal MarkdownLayoutContext SourceContext { get; }
        internal InlineContainerBox Source { get; }
        internal List<InlineImageRun> ImageRuns { get; } = new();

        internal MarkdownLayoutContext CreateCandidateContext()
            => SourceContext.CreateRasterPlacementContext(
                CancellationToken.None,
                _imagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null);

        internal void SetCandidate(MarkdownLayoutContext context, InlineContainerBox? candidate)
        {
            CandidateContext = context;
            Candidate = candidate;
        }

        private MarkdownLayoutContext? CandidateContext { get; set; }
        private InlineContainerBox? Candidate { get; set; }

        private void AddText(string text, int sourceStart)
        {
            Source.Add(new TextRun(text)
            {
                ElementKey = MarkdownElementKeys.Strong,
                SourceSpan = new SourceSpan(sourceStart, text.Length),
                StyleAliases = new[] { "inline-text-alias" },
                StyleModifierKeys = new[] { MarkdownElementKeys.Emphasis },
                SemanticHeadingKey = MarkdownElementKeys.Heading2,
            });
        }

        private void AddLink(string text, int sourceStart, string? disclosureId, bool expanded)
        {
            Source.Add(new LinkRun(text, "https://example.test/docs", "Docs title")
            {
                AccessibilityName = "Accessible docs link",
                DisclosureId = disclosureId,
                IsExpanded = expanded,
                IsSuperscript = true,
                SourceSpan = new SourceSpan(sourceStart, text.Length),
                StyleAliases = new[] { "inline-link-alias" },
                StyleModifierKeys = new[] { MarkdownElementKeys.Emphasis },
                SemanticHeadingKey = MarkdownElementKeys.Heading2,
            });
        }

        private void AddImage(string source, int sourceStart, string altText)
        {
            string url = $"ms-appx:///inline-placement-{Guid.NewGuid():N}.png";
            Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(url, null, out MarkdownBuiltInImageSource resolved));
            using SharedCanvasBitmapCache.Lease cachedLease = SharedCanvasBitmapCache.StoreAndAcquire(
                _device,
                resolved.CacheKey,
                CreateBitmap(_device, 48, 24),
                new Size(32, 16));
            var run = new InlineImageRun(SourceContext, altText, url)
            {
                ElementKey = MarkdownElementKeys.Strong,
                SourceSpan = new SourceSpan(sourceStart, 1),
                StyleAliases = new[] { "inline-image-alias" },
                StyleModifierKeys = new[] { MarkdownElementKeys.Emphasis },
                SemanticHeadingKey = MarkdownElementKeys.Heading2,
            };
            Source.Add(run);
            ImageRuns.Add(run);
        }

        public async ValueTask DisposeAsync()
        {
            if (Candidate is not null && CandidateContext is not null && !_candidateRetired)
            {
                _candidateRetired = true;
                ImageBox[] candidateImages = Candidate.Runs.OfType<InlineImageRun>()
                    .Select(static run => run.Image)
                    .ToArray();
                CandidateContext.DiscardConstructionBlock(Candidate);
                await Task.WhenAll(candidateImages.Select(static image => image.RetirementTask))
                    .WaitAsync(TimeSpan.FromSeconds(10));
                await CandidateContext.DrainOwnedRetirementsAsync();
            }

            if (!_sourceRetired)
            {
                _sourceRetired = true;
                SourceContext.DiscardConstructionBlock(Source);
                await Task.WhenAll(ImageRuns.Select(static run => run.Image.RetirementTask))
                    .WaitAsync(TimeSpan.FromSeconds(10));
                await SourceContext.DrainOwnedRetirementsAsync();
            }

            SharedCanvasBitmapCache.ReleaseDevice(_device);
            _device.Dispose();
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
