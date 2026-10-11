using System.Collections;
using System.Buffers;
using System.Reflection;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class CompletedSvgPlacementCloneTests
{
    private const float LayoutWidth = 320f;

    [Fact]
    public async Task FullSnapshotCandidateClonesAcceptedSvgAndPaintsAfterSourceRetirement()
    {
        await using var fixture = await SvgPlacementFixture.CreateReadyAsync(hasText: true);
        LayoutSnapshot sourceSnapshot = Assert.IsType<LayoutSnapshot>(fixture.Snapshot);
        InlineImageRun sourceRun = fixture.ImageRun;
        ImageBox sourceImage = sourceRun.Image;
        CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(sourceImage.Bitmap);
        SharedCanvasBitmapCache.Lease sourceLease = GetField<SharedCanvasBitmapCache.Lease>(sourceImage, "_bitmapLease");
        MarkdownSvgDocumentInfo sourceInfo = GetField<MarkdownSvgDocumentInfo>(sourceImage, "_svgInfo");
        var sourceIntrinsic = GetField<Size>(sourceImage, "_svgIntrinsicSize");
        int sourceWidth = sourceImage.SvgRasterPixelSize.Width;
        int sourceHeight = sourceImage.SvgRasterPixelSize.Height;
        string? sourceTitle = sourceImage.SvgTitle;
        string? sourceDescription = sourceImage.SvgDesc;
        string semanticText = sourceSnapshot.SemanticDocument.Text;
        var layoutLock = GetField<object>(sourceSnapshot, "_layoutLock");
        var imageLock = GetField<object>(sourceImage, "_svgWorkSync");
        fixture.Renderer.GenerationReadHook = () =>
        {
            Assert.False(Monitor.IsEntered(layoutLock));
            Assert.False(Monitor.IsEntered(imageLock));
        };

        SingleRasterLayoutCandidate candidate = Assert.IsType<SingleRasterLayoutCandidate>(
            sourceSnapshot.CreateSingleRasterCandidate(
                [fixture.Root.BlockIndex],
                LayoutWidth,
                CancellationToken.None,
                CancellationToken.None,
                fixture.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out MarkdownLayoutContext? candidateContext));
        fixture.SetCandidate(candidate, Assert.IsType<MarkdownLayoutContext>(candidateContext));

        Assert.Equal(1, candidate.SourceImageCount);
        Assert.True(candidate.HasCurrentSourceRaster());
        Assert.Same(fixture.SourceContext.SourceMap, candidate.Snapshot.SourceMap);
        Assert.Equal(semanticText, candidate.Snapshot.SemanticDocument.Text);
        Assert.Equal(
            sourceSnapshot.SemanticDocument.SemanticRangeFingerprint,
            candidate.Snapshot.SemanticDocument.SemanticRangeFingerprint);

        InlineContainerBox privateRoot = Assert.IsType<InlineContainerBox>(candidate.Snapshot.Blocks[0]);
        InlineImageRun privateRun = Assert.Single(privateRoot.Runs.OfType<InlineImageRun>());
        ImageBox privateImage = privateRun.Image;
        SharedCanvasBitmapCache.Lease privateLease = GetField<SharedCanvasBitmapCache.Lease>(privateImage, "_bitmapLease");

        Assert.NotSame(sourceRun, privateRun);
        Assert.Equal(sourceRun.SourceSpan, privateRun.SourceSpan);
        Assert.Equal(sourceRun.AltText, privateRun.AltText);
        Assert.NotSame(sourceImage, privateImage);
        Assert.NotSame(sourceLease, privateLease);
        Assert.Same(bitmap, privateImage.Bitmap);
        Assert.Equal(GetField<MarkdownSvgPhysicalViewport>(sourceImage, "_svgPhysicalViewport"),
            GetField<MarkdownSvgPhysicalViewport>(privateImage, "_svgPhysicalViewport"));
        Assert.Equal(GetField<MarkdownSvgPhysicalViewport>(sourceImage, "_bitmapPhysicalViewport"),
            GetField<MarkdownSvgPhysicalViewport>(privateImage, "_bitmapPhysicalViewport"));
        Assert.True(privateLease.IsBitmapOutputAdmitted);
        Assert.True(privateImage.IsSvg);
        Assert.Equal(sourceTitle, privateImage.SvgTitle);
        Assert.Equal(sourceDescription, privateImage.SvgDesc);
        Assert.Equal(sourceInfo, GetField<MarkdownSvgDocumentInfo>(privateImage, "_svgInfo"));
        Assert.Equal(sourceIntrinsic, GetField<Size>(privateImage, "_svgIntrinsicSize"));
        Assert.Equal((sourceWidth, sourceHeight), privateImage.SvgRasterPixelSize);
        Assert.Equal(MarkdownImageAccessibilityState.Loaded, privateImage.AccessibilityState);
        Assert.NotSame(
            GetField<CancellationTokenSource>(sourceImage, "_imageLifetimeCancellation"),
            GetField<CancellationTokenSource>(privateImage, "_imageLifetimeCancellation"));

        // Only accepted immutable placement metadata and a cloned bitmap lease
        // cross the COW boundary. The provider document, tile state, and any
        // image work/old lifetime do not.
        Assert.Null(GetField(privateImage, "_svgDocument"));
        Assert.Null(GetField(privateImage, "_svgTileRenderPlan"));
        Assert.Null(GetField(privateImage, "_svgParkingPlan"));
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(GetField(privateImage, "_svgTiles")));
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(GetField(privateImage, "_svgTilesInFlight")));
        Assert.Empty(Assert.IsAssignableFrom<IDictionary>(GetField(privateImage, "_svgTileWorkOwners")));
        Assert.Equal(0, privateImage.ActiveImageWorkCountForTests);

        await fixture.RetireSourceAsync();
        Assert.Null(sourceImage.Bitmap);
        Assert.True(sourceImage.ImageLifetimeDisposedForTests);
        Assert.Same(bitmap, privateImage.Bitmap);
        Assert.False(privateImage.ImageLifetimeDisposedForTests);

        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            (float)System.Math.Max(1, privateRoot.Bounds.Width),
            (float)System.Math.Max(1, privateRoot.Bounds.Height),
            96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            privateRoot.Paint(drawing, new Rect(0, 0, target.Size.Width, target.Size.Height));
        }
        Assert.Same(bitmap, privateImage.Bitmap);
    }

    [Fact]
    public async Task TextSvgProviderFenceIsSeparateFromLocalRasterFenceAndRunsOutsideLocks()
    {
        await using var fixture = await SvgPlacementFixture.CreateReadyAsync(hasText: true);
        LayoutSnapshot snapshot = Assert.IsType<LayoutSnapshot>(fixture.Snapshot);
        object layoutLock = GetField<object>(snapshot, "_layoutLock");
        object imageLock = GetField<object>(fixture.Image, "_svgWorkSync");
        fixture.Renderer.GenerationReadHook = () =>
        {
            Assert.False(Monitor.IsEntered(layoutLock));
            Assert.False(Monitor.IsEntered(imageLock));
        };

        SingleRasterLayoutCandidate candidate = Assert.IsType<SingleRasterLayoutCandidate>(
            snapshot.CreateSingleRasterCandidate(
                [fixture.Root.BlockIndex], LayoutWidth,
                CancellationToken.None, CancellationToken.None,
                fixture.ImagePreparationOwner, null, null,
                out MarkdownLayoutContext? candidateContext));
        fixture.SetCandidate(candidate, Assert.IsType<MarkdownLayoutContext>(candidateContext));

        Assert.True(candidate.HasCurrentSourceProviders());
        int providerReads = fixture.Renderer.CacheGenerationReadCount;
        Assert.True(candidate.HasCurrentSourceRaster());
        Assert.Equal(providerReads, fixture.Renderer.CacheGenerationReadCount);

        fixture.Renderer.SetGeneration(1);
        Assert.True(candidate.HasCurrentSourceRaster());
        Assert.False(candidate.HasCurrentSourceProviders());

        fixture.Renderer.GenerationReadHook = static () => throw new InvalidOperationException("provider read probe");
        Assert.False(candidate.HasCurrentSourceProviders());
    }

    [Fact]
    public async Task TextFreeSvgDoesNotDependOnFontGenerationOrReadItForProviderFence()
    {
        await using var fixture = await SvgPlacementFixture.CreateReadyAsync(hasText: false);
        LayoutSnapshot snapshot = Assert.IsType<LayoutSnapshot>(fixture.Snapshot);
        SingleRasterLayoutCandidate candidate = Assert.IsType<SingleRasterLayoutCandidate>(
            snapshot.CreateSingleRasterCandidate(
                [fixture.Root.BlockIndex], LayoutWidth,
                CancellationToken.None, CancellationToken.None,
                fixture.ImagePreparationOwner, null, null,
                out MarkdownLayoutContext? candidateContext));
        fixture.SetCandidate(candidate, Assert.IsType<MarkdownLayoutContext>(candidateContext));

        int readsBefore = fixture.Renderer.CacheGenerationReadCount;
        fixture.Renderer.SetGeneration(12);
        Assert.True(candidate.HasCurrentSourceProviders());
        Assert.True(candidate.HasCurrentSourceRaster());
        Assert.Equal(readsBefore, fixture.Renderer.CacheGenerationReadCount);
    }

    [Fact]
    public async Task SameLeaseSvgMetadataMutationInvalidatesThePrivatePlacementFence()
    {
        await using var fixture = await SvgPlacementFixture.CreateReadyAsync(hasText: true);
        MarkdownLayoutContext target = fixture.CreateTargetContext();
        Assert.True(fixture.Image.TryCreateCompletedRasterPlacementClone(
            target,
            out ImageBox? clone,
            out SharedCanvasBitmapCache.Lease? expectedLease,
            out ImageBox.CompletedSvgPlacementMetadataFence? metadataFence));
        Assert.NotNull(clone);
        Assert.NotNull(expectedLease);
        Assert.NotNull(metadataFence);
        try
        {
            Assert.Same(GetField<SharedCanvasBitmapCache.Lease>(fixture.Image, "_bitmapLease"), expectedLease);
            SetField(fixture.Image, "_svgDesc", "mutated without replacing the bitmap lease");
            Assert.Same(expectedLease, GetField<SharedCanvasBitmapCache.Lease>(fixture.Image, "_bitmapLease"));
            Assert.False(fixture.Image.IsCurrentCompletedRasterPlacement(
                expectedLease!, metadataFence, fixture.Renderer.CacheGeneration));
        }
        finally
        {
            await RetireStandaloneCloneAsync(target, clone!);
        }
    }

    [Fact]
    public async Task ProviderGetterReentryIsFollowedByLocalMetadataFence()
    {
        await using var fixture = await SvgPlacementFixture.CreateReadyAsync(hasText: true);
        LayoutSnapshot snapshot = Assert.IsType<LayoutSnapshot>(fixture.Snapshot);
        SingleRasterLayoutCandidate candidate = Assert.IsType<SingleRasterLayoutCandidate>(
            snapshot.CreateSingleRasterCandidate(
                [fixture.Root.BlockIndex], LayoutWidth,
                CancellationToken.None, CancellationToken.None,
                fixture.ImagePreparationOwner, null, null,
                out MarkdownLayoutContext? candidateContext));
        fixture.SetCandidate(candidate, Assert.IsType<MarkdownLayoutContext>(candidateContext));
        object layoutLock = GetField<object>(snapshot, "_layoutLock");
        object imageLock = GetField<object>(fixture.Image, "_svgWorkSync");
        fixture.Renderer.GenerationReadHook = () =>
        {
            Assert.False(Monitor.IsEntered(layoutLock));
            Assert.False(Monitor.IsEntered(imageLock));
            SetField(fixture.Image, "_svgTitle", "changed during provider callback");
        };

        Assert.False(candidate.HasCurrentSourceProviders());
        fixture.Renderer.GenerationReadHook = null;
        Assert.False(candidate.HasCurrentSourceRaster());
    }

    [Fact]
    public async Task CanceledImageThemeAndDpiMismatchRejectSvgClone()
    {
        await using var fixture = await SvgPlacementFixture.CreateReadyAsync(hasText: true);

        using var imageCancellation = new CancellationTokenSource();
        imageCancellation.Cancel();
        AssertCloneRejected(fixture.Image, fixture.CreateTargetContext(imageToken: imageCancellation.Token));

        ThemeSnapshot differentTheme = SvgPlacementFixture.CreateTheme();
        AssertCloneRejected(fixture.Image, fixture.CreateTargetContext(theme: differentTheme));
        AssertCloneRejected(fixture.Image, fixture.CreateTargetContext(rasterizationScale: 1.5));
    }

    [Fact]
    public async Task TiledSvgNeverEntersTheFullBitmapPlacementPath()
    {
        await using var fixture = await SvgPlacementFixture.CreateStartedAsync(
            hasText: false,
            rasterizationScale: 4,
            intrinsicWidth: 5_000,
            intrinsicHeight: 1_000,
            layoutWidth: 5_000);
        await WaitUntilAsync(() => fixture.Image.UsesSvgTilesForAutomation &&
            fixture.Image.ActiveImageWorkCountForTests == 0);

        Assert.True(fixture.Image.UsesSvgTilesForAutomation);
        Assert.Null(fixture.Image.Bitmap);
        AssertCloneRejected(fixture.Image, fixture.CreateTargetContext());
    }

    [Fact]
    public async Task ExistingSvgFallbackTileStateRejectsFullBitmapClone()
    {
        await using var fixture = await SvgPlacementFixture.CreateReadyAsync(hasText: false);
        var fallbackField = typeof(ImageBox).GetField("_svgFallbackTiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        fallbackField.SetValue(fixture.Image,
            new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>());

        AssertCloneRejected(fixture.Image, fixture.CreateTargetContext());
    }

    [Fact]
    public async Task PendingProviderOpenRejectsCloneUntilWorkDrains()
    {
        var renderer = new TestSvgRenderer(
            new MarkdownSvgDocumentInfo(80, 40, 2, "provider description", HasText: true))
        {
            HoldOpenUntilCanceled = true,
        };
        await using var fixture = await SvgPlacementFixture.CreateStartedAsync(renderer: renderer);
        await renderer.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Image.ActiveImageWorkCountForTests > 0);
        AssertCloneRejected(fixture.Image, fixture.CreateTargetContext());
    }

    [Fact]
    public async Task HostCapacityDeferralRejectsCloneAndRetiresItsWaiter()
    {
        var renderer = new HostDeferredSvgRenderer(
            new MarkdownSvgDocumentInfo(80, 40, 2, "provider description", HasText: false));
        await using var fixture = await SvgPlacementFixture.CreateStartedAsync(renderer: renderer);
        // Registration precedes dispatch of the provider's asynchronous wait.
        // Observe both milestones rather than racing that queued callback.
        await WaitUntilAsync(() => GetField<int>(fixture.Image, "_svgHostDeferred") != 0 &&
            GetField(fixture.Image, "_svgHostDeferredWaiter") is not null && renderer.WaitCount > 0);
        AssertCloneRejected(fixture.Image, fixture.CreateTargetContext());
        Assert.True(renderer.WaitCount > 0);
    }

    [Fact]
    public async Task BitmapOutputDeferralRejectsSvgCloneBeforeRasterPublication()
    {
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync();
        SvgPlacementFixture? fixture = null;
        try
        {
            SvgPlacementFixture readyFixture = await SvgPlacementFixture.CreateStartedAsync();
            fixture = readyFixture;
            await WaitUntilAsync(() => GetField<int>(readyFixture.Image, "_bitmapOutputDeferred") != 0 &&
                GetField(readyFixture.Image, "_bitmapOutputWaiter") is not null &&
                readyFixture.Image.ActiveImageWorkCountForTests == 0);
            Assert.Null(readyFixture.Image.Bitmap);
            AssertCloneRejected(readyFixture.Image, readyFixture.CreateTargetContext());
        }
        finally
        {
            try
            {
                if (fixture is not null)
                    await fixture.DisposeAsync();
            }
            finally
            {
                ReleaseReservations(blockers);
            }
        }
    }

    private static void AssertCloneRejected(ImageBox source, MarkdownLayoutContext target)
    {
        Assert.False(source.TryCreateCompletedRasterPlacementClone(
            target,
            out ImageBox? clone,
            out SharedCanvasBitmapCache.Lease? lease,
            out ImageBox.CompletedSvgPlacementMetadataFence? fence));
        Assert.Null(clone);
        Assert.Null(lease);
        Assert.Null(fence);
    }

    private static async Task RetireStandaloneCloneAsync(MarkdownLayoutContext context, ImageBox image)
    {
        image.Dispose();
        await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(20));
        await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task<List<BitmapOutputAdmission.Reservation>> SaturateAdmissionAsync()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        SharedCanvasBitmapCache.Clear();
        await WaitUntilAsync(() => admission.GetSnapshot().ChargedBytes == 0);
        var reservations = new List<BitmapOutputAdmission.Reservation>();
        while (true)
        {
            BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
            long remaining = snapshot.CapacityBytes - snapshot.ChargedBytes;
            if (remaining <= 0)
                return reservations;

            long demand = System.Math.Min(remaining, snapshot.MaximumSingleOutputBytes);
            if (!admission.TryReserve(
                    demand,
                    isVisible: true,
                    isReplacement: true,
                    out BitmapOutputAdmission.Reservation? reservation,
                    out _))
            {
                ReleaseReservations(reservations);
                throw new InvalidOperationException("Could not establish the isolated SVG output-deferral fixture.");
            }
            reservations.Add(Assert.IsType<BitmapOutputAdmission.Reservation>(reservation));
        }
    }

    private static void ReleaseReservations(List<BitmapOutputAdmission.Reservation> reservations)
    {
        for (int index = reservations.Count - 1; index >= 0; index--)
            reservations[index].Dispose();
        reservations.Clear();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static T GetField<T>(object owner, string name)
    {
        FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(owner));
    }

    private static object? GetField(object owner, string name)
    {
        FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(owner);
    }

    private static void SetField(object owner, string name, object? value)
    {
        FieldInfo field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(owner, value);
    }

    private sealed class SvgPlacementFixture : IAsyncDisposable
    {
        private const string UrlPrefix = "https://images.example/completed-placement-";
        private readonly string _sourceText = "before ! after";
        private bool _sourceRetired;
        private bool _disposed;

        private SvgPlacementFixture(
            bool hasText,
            double rasterizationScale,
            double intrinsicWidth,
            double intrinsicHeight,
            float layoutWidth,
            TestSvgRenderer? renderer)
        {
            RasterizationScale = rasterizationScale;
            LayoutWidth = layoutWidth;
            ImagePreparationOwner = new object();
            Session = new MarkdownPerformanceSession(
                MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 0 });
            Renderer = renderer ?? new TestSvgRenderer(new MarkdownSvgDocumentInfo(
                intrinsicWidth,
                intrinsicHeight,
                intrinsicWidth / intrinsicHeight,
                "provider description",
                HasText: hasText,
                UsesCurrentColor: true,
                UsesColorScheme: true));
            Resolver = new SvgResolver(CreateSvgBytes("Accessible diagram", "Detailed SVG description"));
            SourceMap = new MarkdownSourceMap(_sourceText);
            Theme = CreateTheme();
            SourceContext = CreateContext(Theme, rasterizationScale, CancellationToken.None);
            Root = new InlineContainerBox(SourceContext, MarkdownElementKeys.Body)
            {
                BlockIndex = SourceContext.NextBlockIndex(),
            };
            Root.Add(new TextRun("before ") { SourceSpan = new SourceSpan(0, 7) });
            ImageRun = new InlineImageRun(
                SourceContext,
                "diagram alt",
                UrlPrefix + Guid.NewGuid().ToString("N") + ".svg",
                title: "markdown title")
            {
                SourceSpan = new SourceSpan(7, 1),
            };
            Root.Add(ImageRun);
            Root.Add(new TextRun(" after") { SourceSpan = new SourceSpan(8, 6) });
        }

        internal static async Task<SvgPlacementFixture> CreateReadyAsync(
            bool hasText,
            double rasterizationScale = 1,
            double intrinsicWidth = 80,
            double intrinsicHeight = 40,
            float layoutWidth = CompletedSvgPlacementCloneTests.LayoutWidth,
            TestSvgRenderer? renderer = null)
        {
            var fixture = new SvgPlacementFixture(
                hasText, rasterizationScale, intrinsicWidth, intrinsicHeight, layoutWidth, renderer);
            try
            {
                fixture.Start();
                await fixture.WaitForFullBitmapAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal static async Task<SvgPlacementFixture> CreateStartedAsync(
            bool hasText = true,
            double rasterizationScale = 1,
            double intrinsicWidth = 80,
            double intrinsicHeight = 40,
            float layoutWidth = CompletedSvgPlacementCloneTests.LayoutWidth,
            TestSvgRenderer? renderer = null)
        {
            var fixture = new SvgPlacementFixture(
                hasText, rasterizationScale, intrinsicWidth, intrinsicHeight, layoutWidth, renderer);
            try
            {
                fixture.Start();
                await Task.Yield();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal TestSvgRenderer Renderer { get; }
        internal SvgResolver Resolver { get; }
        internal MarkdownPerformanceSession Session { get; }
        internal object ImagePreparationOwner { get; }
        internal double RasterizationScale { get; }
        internal float LayoutWidth { get; }
        internal MarkdownSourceMap SourceMap { get; }
        internal ThemeSnapshot Theme { get; }
        internal MarkdownLayoutContext SourceContext { get; }
        internal InlineContainerBox Root { get; }
        internal InlineImageRun ImageRun { get; }
        internal ImageBox Image => ImageRun.Image;
        internal LayoutSnapshot? Snapshot { get; private set; }
        private SingleRasterLayoutCandidate? Candidate { get; set; }
        private MarkdownLayoutContext? CandidateContext { get; set; }

        internal void SetCandidate(SingleRasterLayoutCandidate candidate, MarkdownLayoutContext context)
        {
            Candidate = candidate;
            CandidateContext = context;
        }

        internal MarkdownLayoutContext CreateTargetContext(
            CancellationToken imageToken = default,
            ThemeSnapshot? theme = null,
            double? rasterizationScale = null)
        {
            if (theme is null && rasterizationScale is null)
                return SourceContext.CreateRasterPlacementContext(
                    imageToken, ImagePreparationOwner, retirementSink: null, imageUnavailable: null);

            return new MarkdownLayoutContext(
                SourceContext.ResourceCreator,
                theme ?? Theme,
                SourceMap,
                SourceContext.Registry,
                SourceContext.FlowDirection,
                SourceContext.Dispatcher,
                SourceContext.Language)
            {
                CancellationToken = CancellationToken.None,
                ImageCancellationToken = imageToken,
                ImageResolver = SourceContext.ImageResolver,
                SvgRenderer = Renderer,
                PerformanceSession = Session,
                ImagePreparationOwner = ImagePreparationOwner,
                RasterizationScale = rasterizationScale ?? RasterizationScale,
                DisclosureStates = SourceContext.DisclosureStates,
                AllowThirdPartyRemoteImages = SourceContext.AllowThirdPartyRemoteImages,
                ImageDocumentSource = SourceContext.ImageDocumentSource,
                ImageDocumentPath = SourceContext.ImageDocumentPath,
                ImageBaseUri = SourceContext.ImageBaseUri,
            };
        }

        internal async Task RetireSourceAsync()
        {
            if (_sourceRetired)
                return;
            _sourceRetired = true;
            if (Snapshot is { } snapshot)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(20));
            else
            {
                Root.Dispose();
                await Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }

        private MarkdownLayoutContext CreateContext(
            ThemeSnapshot theme,
            double rasterizationScale,
            CancellationToken imageToken)
            => new(
                CanvasDevice.GetSharedDevice(),
                theme,
                SourceMap,
                new MarkdownExtensionRegistry(),
                FlowDirection.LeftToRight,
                language: "en-US")
            {
                ImageResolver = Resolver,
                SvgRenderer = Renderer,
                PerformanceSession = Session,
                PerformanceDocumentOwner = new object(),
                ImagePreparationOwner = ImagePreparationOwner,
                RasterizationScale = rasterizationScale,
                ImageCancellationToken = imageToken,
                DisclosureStates = new Dictionary<string, bool>(StringComparer.Ordinal),
            };

        private void Start()
        {
            _ = Root.Measure(LayoutWidth);
            Root.Arrange(0, 0, LayoutWidth);
            Image.EnsureLoading();
        }

        private async Task WaitForFullBitmapAsync()
        {
            await WaitUntilAsync(() => Image.Bitmap is not null && Image.ActiveImageWorkCountForTests == 0);
            float height = Root.Measure(LayoutWidth);
            Root.Arrange(0, 0, LayoutWidth);
            Snapshot = new LayoutSnapshot([Root], SourceMap, LayoutWidth, System.Math.Max(1, height));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (Candidate is { } candidate && CandidateContext is { } context)
                {
                    candidate.Dispose();
                    await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
            }
            finally
            {
                try
                {
                    await RetireSourceAsync();
                }
                finally
                {
                    try
                    {
                        await SourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    }
                    finally
                    {
                        await Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                    }
                }
            }
        }

        internal static ThemeSnapshot CreateTheme()
        {
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.ImageCaption] = body,
                [MarkdownElementKeys.ListMarker] = body,
                [MarkdownElementKeys.Strong] = body,
                [MarkdownElementKeys.Emphasis] = body,
                [MarkdownElementKeys.Link] = body,
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
                textScaleFactor: 1);
        }
    }

    private sealed class SvgResolver(byte[] bytes) : IMarkdownImageResolver
    {
        private readonly string _cacheKey = "svg-placement:" + Guid.NewGuid().ToString("N");

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/svg+xml", CacheKey: _cacheKey)));
        }
    }

    private class TestSvgRenderer(MarkdownSvgDocumentInfo info) : IMarkdownSvgRenderer
    {
        private long _generation;
        private int _cacheGenerationReadCount;
        private int _openCount;
        private int _renderCount;
        private int _documentDisposeCount;
        private readonly TaskCompletionSource _openRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool HoldOpenUntilCanceled { get; init; }
        internal TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CacheGenerationReadCount => Volatile.Read(ref _cacheGenerationReadCount);
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int RenderCount => Volatile.Read(ref _renderCount);
        internal int DocumentDisposeCount => Volatile.Read(ref _documentDisposeCount);
        internal Action? GenerationReadHook { get; set; }

        public long CacheGeneration
        {
            get
            {
                Interlocked.Increment(ref _cacheGenerationReadCount);
                GenerationReadHook?.Invoke();
                return Interlocked.Read(ref _generation);
            }
        }

        internal void SetGeneration(long generation) => Interlocked.Exchange(ref _generation, generation);
        internal void ReleaseOpen() => _openRelease.TrySetResult();

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public virtual async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            OpenStarted.TrySetResult();
            if (HoldOpenUntilCanceled)
                await _openRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new TestSvgDocument(this, info);
        }

        private sealed class TestSvgDocument(TestSvgRenderer renderer, MarkdownSvgDocumentInfo info)
            : IMarkdownSvgDocument
        {
            private int _disposed;
            public MarkdownSvgDocumentInfo Info { get; } = info;

            public ValueTask<MarkdownSvgRaster> RenderAsync(
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0)
                    throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, "The test document was retired.");
                Interlocked.Increment(ref renderer._renderCount);
                int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
                int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
                int stride = checked(width * 4);
                int length = checked(stride * height);
                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
                owner.Memory.Span[..length].Clear();
                for (int index = 3; index < length; index += 4)
                    owner.Memory.Span[index] = byte.MaxValue;
                return ValueTask.FromResult(new MarkdownSvgRaster(
                    owner,
                    length,
                    width,
                    height,
                    stride,
                    request.PixelFormat));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref renderer._documentDisposeCount);
            }
        }
    }

    private sealed class HostDeferredSvgRenderer(MarkdownSvgDocumentInfo info)
        : TestSvgRenderer(info), IMarkdownSvgHostAdmission
    {
        private readonly object _domain = new();
        private int _waitCount;
        private int _openForHostCount;
        internal int WaitCount => Volatile.Read(ref _waitCount);
        internal int OpenForHostCount => Volatile.Read(ref _openForHostCount);
        public object AdmissionDomain => _domain;
        public long CapacityVersion => 0;

        public Task WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waitCount);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<MarkdownSvgHostOpenResult> OpenForHostAsync(
            MarkdownSvgOpenRequest request,
            MarkdownSvgDemandTicket demand,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openForHostCount);
            return ValueTask.FromResult(MarkdownSvgHostOpenResult.CapacityUnavailable(
                CapacityVersion,
                requiredSourceReservationBytes: 1024));
        }
    }

    private static byte[] CreateSvgBytes(string title, string description) =>
        System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='40'>" +
            $"<title>{title}</title><desc>{description}</desc>" +
            "<rect width='80' height='40' fill='#2a6'/></svg>");
}
