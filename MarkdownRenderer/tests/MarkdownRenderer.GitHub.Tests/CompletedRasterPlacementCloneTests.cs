using System.Collections;
using System.Reflection;
using MarkdownRenderer.Images;
using MarkdownRenderer.Document;
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
public sealed class CompletedRasterPlacementCloneTests
{
    [Fact]
    public async Task CandidateOwnsFreshImageAndTextLayoutAndCopiesRunSemantics()
    {
        int unavailableCalls = 0;
        int taskAvailabilityCalls = 0;
        int retirementCalls = 0;
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?> sourceUnavailable =
            (_, _, _) => unavailableCalls++;
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?> candidateUnavailable =
            (_, _, _) => unavailableCalls++;
        Action sourceTaskAvailabilityChanged = () => taskAvailabilityCalls++;
        Action candidateTaskAvailabilityChanged = () => taskAvailabilityCalls++;
        Action<Task> sourceRetirementSink = _ => retirementCalls++;
        Action<Task> candidateRetirementSink = _ => retirementCalls++;
        await using var fixture = new PlacementFixture(
            sourceImageUnavailable: sourceUnavailable,
            sourceTaskAvailabilityChanged: sourceTaskAvailabilityChanged,
            sourceRetirementSink: sourceRetirementSink);
        CanvasTextLayout sourceLayout = GetField<CanvasTextLayout>(fixture.Source, "_layout");
        Rect sourceBounds = fixture.Source.Bounds;
        MarkdownLayoutContext candidateContext = fixture.CreateContext(
            imageUnavailable: candidateUnavailable,
            taskAvailabilityChanged: candidateTaskAvailabilityChanged,
            retirementSink: candidateRetirementSink);

        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));
        fixture.SetCandidate(candidateContext, candidate);
        RasterPlacementSourceFence expectedSource = Assert.Single(Assert.IsType<RasterPlacementSourceFence[]>(expectedSources));
        ImageBox expectedSourceImage = expectedSource.Image;
        SharedCanvasBitmapCache.Lease expectedSourceLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(expectedSource.Lease);

        InlineContainerBox clonedContainer = Assert.IsType<InlineContainerBox>(candidate);
        Assert.False(clonedContainer.HasMeasuredLayout);
        Assert.Same(expectedSourceImage, fixture.ImageRun.Image);
        Assert.Same(GetField<SharedCanvasBitmapCache.Lease>(fixture.ImageRun.Image, "_bitmapLease"), expectedSourceLease);
        Assert.True(fixture.ImageRun.Image.IsCurrentCompletedRasterPlacement(
            Assert.IsType<SharedCanvasBitmapCache.Lease>(expectedSourceLease)));
        Assert.Same(candidateContext, clonedContainer.Context);
        Assert.Same(candidateUnavailable, clonedContainer.Context.ImageUnavailable);
        Assert.Same(candidateTaskAvailabilityChanged, clonedContainer.Context.TaskCommandAvailabilityChanged);
        Assert.Same(candidateRetirementSink, candidateContext.RetirementSink);

        InlineRun[] sourceRuns = fixture.Source.Runs.ToArray();
        InlineRun[] clonedRuns = clonedContainer.Runs.ToArray();
        Assert.Equal(2, clonedRuns.Length);
        Assert.All(clonedRuns, run => Assert.DoesNotContain(run, sourceRuns));
        for (int index = 0; index < sourceRuns.Length; index++)
        {
            Assert.Equal(sourceRuns[index].InlineIndex, clonedRuns[index].InlineIndex);
            Assert.Equal(sourceRuns[index].RenderedLength, clonedRuns[index].RenderedLength);
            Assert.Equal(sourceRuns[index].SourceSpan, clonedRuns[index].SourceSpan);
            Assert.Equal(sourceRuns[index].ElementKey, clonedRuns[index].ElementKey);
            Assert.Equal(sourceRuns[index].StyleAliases, clonedRuns[index].StyleAliases);
            Assert.Equal(sourceRuns[index].StyleModifierKeys, clonedRuns[index].StyleModifierKeys);
            Assert.Equal(sourceRuns[index].SemanticHeadingKey, clonedRuns[index].SemanticHeadingKey);
            Assert.NotSame(sourceRuns[index].StyleAliases, clonedRuns[index].StyleAliases);
        }

        InlineImageRun clonedImageRun = Assert.IsType<InlineImageRun>(clonedRuns[1]);
        Assert.NotSame(fixture.ImageRun.Image, clonedImageRun.Image);
        Assert.Equal(fixture.ImageRun.Url, clonedImageRun.Url);
        Assert.Equal(fixture.ImageRun.AltText, clonedImageRun.AltText);
        Assert.Same(fixture.ImageRun.Image.Bitmap, clonedImageRun.Image.Bitmap);
        Size expectedIntrinsicSize = expectedSourceLease!.IntrinsicSize.GetValueOrDefault();
        Assert.Equal(new Size(96, 48), expectedIntrinsicSize);
        Assert.Equal(expectedIntrinsicSize, GetField<Size>(clonedImageRun.Image, "_rasterIntrinsicSize"));
        Assert.Equal(fixture.ImageRun.DesiredWidth, clonedImageRun.DesiredWidth);
        Assert.Equal(fixture.ImageRun.DesiredHeight, clonedImageRun.DesiredHeight);
        Assert.NotSame(
            GetField<CancellationTokenSource>(fixture.ImageRun.Image, "_imageLifetimeCancellation"),
            GetField<CancellationTokenSource>(clonedImageRun.Image, "_imageLifetimeCancellation"));
        Assert.Equal(0, clonedImageRun.Image.ActiveImageWorkCountForTests);
        Assert.Null(GetNullableField<IMarkdownSvgDocument>(clonedImageRun.Image, "_svgDocument"));
        Assert.Null(GetNullableField<WeakReference<byte[]>>(clonedImageRun.Image, "_svgSourceIdentity"));
        AssertPrivateDictionaryEmpty(clonedImageRun.Image, "_svgTiles");
        AssertPrivateDictionaryEmpty(clonedImageRun.Image, "_svgTilesInFlight");
        AssertPrivateDictionaryEmpty(clonedImageRun.Image, "_svgTileWorkOwners");

        float measuredHeight = clonedContainer.Measure(320f);
        clonedContainer.Arrange(0f, 0f, 320f);
        Assert.True(float.IsFinite(measuredHeight));
        Assert.True(measuredHeight > 0);
        Assert.True(clonedContainer.HasMeasuredLayout);
        Assert.NotSame(sourceLayout, GetField<CanvasTextLayout>(clonedContainer, "_layout"));
        Assert.Equal(sourceBounds, fixture.Source.Bounds);
        Assert.Same(sourceLayout, GetField<CanvasTextLayout>(fixture.Source, "_layout"));
        Assert.Same(fixture.SourceContext.SourceMap, candidateContext.SourceMap);
        Assert.Equal(0, unavailableCalls);
        Assert.Equal(0, taskAvailabilityCalls);
        Assert.Equal(0, retirementCalls);
    }

    [Fact]
    public async Task UncachedPlacementCloneCopiesAlreadyResolvedIntrinsicGeometry()
    {
        await using var fixture = new PlacementFixture();
        SharedCanvasBitmapCache.Lease uncached = SharedCanvasBitmapCache.CreateUncachedLease(
            CreateBitmap(fixture.Device, 40, 20));
        InvokeReplaceBitmap(fixture.ImageRun.Image, uncached);
        Assert.Null(uncached.IntrinsicSize);
        Size resolvedIntrinsic = GetField<Size>(fixture.ImageRun.Image, "_rasterIntrinsicSize");
        MarkdownLayoutContext targetContext = fixture.CreateContext();

        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            targetContext, 320f, out InlineContainerBox? candidate, out _));
        fixture.SetCandidate(targetContext, candidate);
        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        ImageBox clonedImage = Assert.Single(clone.Runs.OfType<InlineImageRun>()).Image;

        Assert.Same(uncached.Bitmap, clonedImage.Bitmap);
        Assert.Equal(resolvedIntrinsic, GetField<Size>(clonedImage, "_rasterIntrinsicSize"));
        clone.Measure(320f);
        clone.Arrange(0, 0, 320f);
        Assert.Equal(resolvedIntrinsic, GetField<Size>(clonedImage, "_rasterIntrinsicSize"));
        Assert.Equal(MarkdownRenderer.Accessibility.MarkdownImageAccessibilityState.Loaded,
            clonedImage.AccessibilityState);
    }

    [Fact]
    public async Task ClonedBitmapLeaseKeepsRasterAliveAfterOriginalBoxRetires()
    {
        await using var fixture = new PlacementFixture();
        MarkdownLayoutContext candidateContext = fixture.CreateContext();
        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out _));
        fixture.SetCandidate(candidateContext, candidate);
        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        InlineImageRun cloneRun = clone.Runs.OfType<InlineImageRun>().Single();
        CanvasBitmap originalBitmap = Assert.IsType<CanvasBitmap>(fixture.ImageRun.Image.Bitmap);
        Assert.Same(originalBitmap, cloneRun.Image.Bitmap);

        fixture.ReleaseCacheEntry();
        await fixture.RetireSourceAsync();

        Assert.Null(fixture.ImageRun.Image.Bitmap);
        Assert.Same(originalBitmap, cloneRun.Image.Bitmap);
        Assert.Equal(64u, cloneRun.Image.Bitmap!.SizeInPixels.Width);
        Assert.Equal(32u, cloneRun.Image.Bitmap.SizeInPixels.Height);

        await fixture.RetireCandidateAsync();
        Assert.Null(cloneRun.Image.Bitmap);
    }

    [Fact]
    public async Task FinalFenceRejectsWrongAndReplacedBitmapLease()
    {
        await using var fixture = new PlacementFixture();
        MarkdownLayoutContext candidateContext = fixture.CreateContext();
        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out RasterPlacementSourceFence[]? expectedSources));
        SharedCanvasBitmapCache.Lease expectedSourceLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(Assert.Single(
            Assert.IsType<RasterPlacementSourceFence[]>(expectedSources)).Lease);
        fixture.SetCandidate(candidateContext, candidate);
        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        InlineImageRun cloneRun = clone.Runs.OfType<InlineImageRun>().Single();
        SharedCanvasBitmapCache.Lease expected = Assert.IsType<SharedCanvasBitmapCache.Lease>(expectedSourceLease);
        CanvasBitmap expectedBitmap = expected.Bitmap;

        using (SharedCanvasBitmapCache.Lease wrongLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(
                   fixture.ImageRun.Image.AcquireBitmapLease()))
        {
            Assert.Same(expected.Bitmap, wrongLease.Bitmap);
            Assert.False(fixture.ImageRun.Image.IsCurrentCompletedRasterPlacement(wrongLease));
        }
        Assert.True(fixture.ImageRun.Image.IsCurrentCompletedRasterPlacement(expected));

        SharedCanvasBitmapCache.Lease replacementLease = SharedCanvasBitmapCache.CreateUncachedLease(
            CreateBitmap(fixture.Device, 40, 20));
        CanvasBitmap replacementBitmap = replacementLease.Bitmap;
        InvokeReplaceBitmap(fixture.ImageRun.Image, replacementLease);

        Assert.False(fixture.ImageRun.Image.IsCurrentCompletedRasterPlacement(expected));
        Assert.Same(replacementBitmap, fixture.ImageRun.Image.Bitmap);
        Assert.Same(expectedBitmap, cloneRun.Image.Bitmap);
    }

    [Fact]
    public async Task CandidateLifetimeCancellationAndRetirementAreIndependent()
    {
        using var candidateImageLifetime = new CancellationTokenSource();
        int oldUnavailableCalls = 0;
        int candidateUnavailableCalls = 0;
        await using var fixture = new PlacementFixture(
            sourceImageUnavailable: (_, _, _) => oldUnavailableCalls++);
        MarkdownLayoutContext candidateContext = fixture.CreateContext(
            imageCancellationToken: candidateImageLifetime.Token,
            imageUnavailable: (_, _, _) => candidateUnavailableCalls++,
            taskAvailabilityChanged: () => { });
        Assert.True(fixture.Source.TryCreatePrivateRasterImageCandidates(
            candidateContext,
            320f,
            out InlineContainerBox? candidate,
            out _));
        fixture.SetCandidate(candidateContext, candidate);
        InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
        InlineImageRun cloneRun = clone.Runs.OfType<InlineImageRun>().Single();
        CancellationTokenSource originalLifetime = GetCancellationSource(
            fixture.ImageRun.Image,
            "_imageLifetimeCancellation");
        CancellationTokenSource clonedLifetime = GetCancellationSource(
            cloneRun.Image,
            "_imageLifetimeCancellation");

        Assert.NotSame(originalLifetime, clonedLifetime);
        candidateImageLifetime.Cancel();
        Assert.True(clonedLifetime.IsCancellationRequested);
        Assert.False(originalLifetime.IsCancellationRequested);
        Assert.NotNull(cloneRun.Image.Bitmap);
        Assert.Equal(0, oldUnavailableCalls);
        Assert.Equal(0, candidateUnavailableCalls);

        await fixture.RetireCandidateAsync();
        Assert.True(cloneRun.Image.ImageLifetimeDisposedForTests);
        Assert.Null(cloneRun.Image.Bitmap);
        Assert.False(originalLifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task CloneRejectsSvgAndActiveImageWork()
    {
        await using (var svgFixture = new PlacementFixture(svgSource: true))
        {
            Assert.True(svgFixture.ImageRun.Image.IsSvg);
            Assert.False(svgFixture.Source.TryCreatePrivateRasterImageCandidates(
                svgFixture.CreateContext(),
                320f,
                out InlineContainerBox? candidate,
                out _));
            Assert.Null(candidate);
        }

        await using (var activeFixture = new PlacementFixture())
        {
            using IDisposable activeWork = BeginImageWork(activeFixture.ImageRun.Image);
            Assert.Equal(1, activeFixture.ImageRun.Image.ActiveImageWorkCountForTests);
            Assert.False(activeFixture.Source.TryCreatePrivateRasterImageCandidates(
                activeFixture.CreateContext(),
                320f,
                out InlineContainerBox? candidate,
                out _));
            Assert.Null(candidate);
        }
    }

    [Fact]
    public async Task CloneRejectsDataUriAndNonSimpleInlineContainer()
    {
        await using (var dataFixture = new PlacementFixture(dataUriSource: true))
        {
            Assert.NotNull(dataFixture.ImageRun.Image.Bitmap);
            Assert.False(dataFixture.Source.TryCreatePrivateRasterImageCandidates(
                dataFixture.CreateContext(),
                320f,
                out InlineContainerBox? candidate,
                out _));
            Assert.Null(candidate);
        }

        await using (var multiRunFixture = new PlacementFixture(extraTextRun: true))
        {
            // Known text recipes are supported; arbitrary extension-owned
            // runs still have no replay contract. Test a clean measured shape.
            multiRunFixture.Source.Add(new OpaqueTestRun(string.Empty));
            _ = multiRunFixture.Source.Measure(320f);
            multiRunFixture.Source.Arrange(0, 0, 320f);
            Assert.Equal(4, multiRunFixture.Source.Runs.Count);
            Assert.False(multiRunFixture.Source.TryCreatePrivateRasterImageCandidates(
                multiRunFixture.CreateContext(),
                320f,
                out InlineContainerBox? candidate,
                out _));
            Assert.Null(candidate);
        }
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

    private static IDisposable BeginImageWork(ImageBox image)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "TryBeginImageWork",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [null];
        Assert.True(Assert.IsType<bool>(method.Invoke(image, arguments)));
        return Assert.IsAssignableFrom<IDisposable>(arguments[0]);
    }

    private static void InvokeReplaceBitmap(
        ImageBox image,
        SharedCanvasBitmapCache.Lease replacementLease)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "ReplaceBitmap",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        _ = method.Invoke(image, [replacementLease.Bitmap, replacementLease, null]);
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

    private static CancellationTokenSource GetCancellationSource(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // CreateLinkedTokenSource intentionally returns an internal CTS subtype.
        return Assert.IsAssignableFrom<CancellationTokenSource>(field.GetValue(instance));
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static T? GetNullableField<T>(object instance, string name) where T : class
    {
        FieldInfo field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? value = field.GetValue(instance);
        return value is null ? null : Assert.IsType<T>(value);
    }

    private static void AssertPrivateDictionaryEmpty(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(field.GetValue(instance)));
    }

    internal sealed class PlacementFixture : IAsyncDisposable
    {
        private readonly TestContextEnvironment _environment;
        private bool _sourceRetired;
        private bool _candidateRetired;
        private LayoutSnapshot? _sourceSnapshot;

        internal PlacementFixture(
            bool svgSource = false,
            bool dataUriSource = false,
            bool extraTextRun = false,
            Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? sourceImageUnavailable = null,
            Action? sourceTaskAvailabilityChanged = null,
            Action<Task>? sourceRetirementSink = null,
            bool forceSoftwareRenderer = false)
        {
            _environment = new TestContextEnvironment(forceSoftwareRenderer);
            RasterSource = dataUriSource
                ? "data:image/png;base64,AA=="
                : new Uri($"ms-appx:///placement-{Guid.NewGuid():N}.png").AbsoluteUri;
            string cacheIdentity = GetBuiltInCacheIdentity(RasterSource);
            if (!svgSource)
                _environment.StoreRaster(cacheIdentity, 64, 32, new Size(96, 48));

            SourceContext = _environment.CreateContext(
                CancellationToken.None,
                sourceImageUnavailable,
                sourceTaskAvailabilityChanged,
                sourceRetirementSink);
            Source = BuildSource(SourceContext, svgSource, extraTextRun, out InlineImageRun imageRun);
            ImageRun = imageRun;
            if (!svgSource)
                Assert.NotNull(ImageRun.Image.Bitmap);
        }

        internal string RasterSource { get; private set; }
        internal MarkdownLayoutContext SourceContext { get; private set; } = null!;
        internal InlineContainerBox Source { get; private set; } = null!;
        internal InlineImageRun ImageRun { get; private set; } = null!;
        internal MarkdownLayoutContext? CandidateContext { get; private set; }
        internal InlineContainerBox? Candidate { get; private set; }
        internal CanvasDevice Device => _environment.Device;
        internal bool ForceSoftwareRenderer => _environment.ForceSoftwareRenderer;
        internal object ImagePreparationOwnerToken => _environment.ImagePreparationOwnerToken;
        internal object PerformanceDocumentOwner => _environment.DocumentOwner;

        internal MarkdownLayoutContext CreateContext(
            CancellationToken imageCancellationToken = default,
            Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable = null,
            Action? taskAvailabilityChanged = null,
            Action<Task>? retirementSink = null,
            bool includeImagePreparationOwner = true) =>
            _environment.CreateContext(
                imageCancellationToken,
                imageUnavailable,
                taskAvailabilityChanged,
                retirementSink,
                includeImagePreparationOwner);

        internal void SetCandidate(MarkdownLayoutContext context, InlineContainerBox? candidate)
        {
            CandidateContext = context;
            Candidate = candidate;
        }

        internal void TransferSourceOwnershipToSnapshot(LayoutSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (_sourceRetired || _sourceSnapshot is not null)
                throw new InvalidOperationException("The fixture source already has a retirement owner.");
            _sourceSnapshot = snapshot;
            _sourceRetired = true;
        }

        internal void ReleaseCacheEntry() => SharedCanvasBitmapCache.ReleaseDevice(Device);

        internal async Task RetireSourceAsync()
        {
            if (_sourceSnapshot is { } snapshot)
            {
                // Snapshot.Dispose starts asynchronous image retirement. Join
                // that same owner before releasing the fixture's device.
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
                return;
            }
            if (_sourceRetired)
                return;
            _sourceRetired = true;
            SourceContext.DiscardConstructionBlock(Source);
            await ImageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        internal async Task RetireCandidateAsync()
        {
            if (_candidateRetired || Candidate is null || CandidateContext is null)
                return;
            _candidateRetired = true;
            InlineImageRun cloneImageRun = Candidate.Runs.OfType<InlineImageRun>().Single();
            CandidateContext.DiscardConstructionBlock(Candidate);
            await cloneImageRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            await RetireCandidateAsync();
            await RetireSourceAsync();
            _environment.Dispose();
        }

        private InlineContainerBox BuildSource(
            MarkdownLayoutContext context,
            bool svgSource,
            bool extraTextRun,
            out InlineImageRun imageRun,
            string? source = null)
        {
            string imageSource = source ?? RasterSource;
            if (svgSource)
            {
                imageSource = "data:image/svg+xml,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2710%27%20height%3D%275%27%2F%3E";
            }

            var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
            {
                BlockIndex = context.NextBlockIndex(),
            };
            var textRun = new TextRun("caption ")
            {
                ElementKey = MarkdownElementKeys.Strong,
                SourceSpan = new SourceSpan(0, 8),
                StyleAliases = new[] { "paragraph-label" },
                StyleModifierKeys = new[] { MarkdownElementKeys.Emphasis },
                SemanticHeadingKey = MarkdownElementKeys.Heading2,
            };
            imageRun = new InlineImageRun(context, "Raster alt", imageSource)
            {
                ElementKey = MarkdownElementKeys.Link,
                SourceSpan = new SourceSpan(8, 1),
                StyleAliases = new[] { "image-label" },
                StyleModifierKeys = new[] { MarkdownElementKeys.Strong },
                SemanticHeadingKey = MarkdownElementKeys.Heading3,
            };
            paragraph.Add(textRun);
            paragraph.Add(imageRun);
            if (extraTextRun)
            {
                paragraph.Add(new TextRun(" extra")
                {
                    SourceSpan = new SourceSpan(9, 6),
                });
            }

            _ = paragraph.Measure(320f);
            paragraph.Arrange(0f, 0f, 320f);
            return paragraph;
        }

        private static string GetBuiltInCacheIdentity(string source)
        {
            Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(source, null, out MarkdownBuiltInImageSource resolved));
            return resolved.CacheKey;
        }
    }

    private sealed class TestContextEnvironment : IDisposable
    {
        private const string SourceText = "caption ! extra";
        private readonly ThemeSnapshot _theme;
        private readonly MarkdownSourceMap _sourceMap = new(SourceText);
        private readonly MarkdownExtensionRegistry _registry = new();
        private readonly IReadOnlyDictionary<string, bool> _disclosures = new Dictionary<string, bool>();

        internal TestContextEnvironment(bool forceSoftwareRenderer = false)
        {
            ForceSoftwareRenderer = forceSoftwareRenderer;
            Device = new CanvasDevice(forceSoftwareRenderer);
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.Strong] = body,
                [MarkdownElementKeys.Emphasis] = body,
                [MarkdownElementKeys.Heading2] = body,
                [MarkdownElementKeys.Heading3] = body,
                [MarkdownElementKeys.Link] = body,
                [MarkdownElementKeys.ImageCaption] = body,
                [MarkdownElementKeys.ListMarker] = body,
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
        }

        internal CanvasDevice Device { get; }
        internal bool ForceSoftwareRenderer { get; }
        internal object ImagePreparationOwnerToken { get; } = new();
        internal object DocumentOwner { get; } = new();

        internal MarkdownLayoutContext CreateContext(
            CancellationToken imageCancellationToken = default,
            Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable = null,
            Action? taskAvailabilityChanged = null,
            Action<Task>? retirementSink = null,
            bool includeImagePreparationOwner = true) =>
            new MarkdownLayoutContext(
                Device,
                _theme,
                _sourceMap,
                _registry,
                FlowDirection.LeftToRight,
                language: "en-US")
            {
                CancellationToken = CancellationToken.None,
                ImageCancellationToken = imageCancellationToken,
                ImageUnavailable = imageUnavailable,
                TaskCommandAvailabilityChanged = taskAvailabilityChanged,
                RetirementSink = retirementSink,
                PerformanceDocumentOwner = DocumentOwner,
                ImagePreparationOwner = includeImagePreparationOwner ? ImagePreparationOwnerToken : null,
                DisclosureStates = _disclosures,
            };

        internal void StoreRaster(string identity, int width, int height, Size intrinsicSize)
        {
            using SharedCanvasBitmapCache.Lease lease = SharedCanvasBitmapCache.StoreAndAcquire(
                Device,
                identity,
                CreateBitmap(Device, width, height),
                intrinsicSize);
        }

        public void Dispose()
        {
            SharedCanvasBitmapCache.ReleaseDevice(Device);
            Device.Dispose();
        }
    }
}
