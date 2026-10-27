using System.Reflection;
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
public sealed class IdleImagePlacementCloneTests
{
    [Fact]
    public async Task OptedInIdleCloneCopiesAuthoredDescriptorWithoutCacheOrResolverAdoption()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 0);
        string builtinUrl = $"ms-appx:///idle-placement-{Guid.NewGuid():N}.png";
        string svgHintUrl = $"https://images.example.test/idle-placement-{Guid.NewGuid():N}.svg";
        await using var fixture = new ImagePlacementFixture(seed, [builtinUrl, svgHintUrl], resolver: null);
        using var targetImageLifetime = new CancellationTokenSource();
        MarkdownLayoutContext targetContext = fixture.CreateTargetContext(targetImageLifetime.Token);
        InlineContainerBox? candidate = null;
        SharedCanvasBitmapCache.Lease? cacheProbe = null;

        try
        {
            Assert.All(fixture.ImageRuns, static run =>
            {
                Assert.Null(run.Image.Bitmap);
                Assert.False(GetField<bool>(run.Image, "_loadStarted"));
                Assert.Equal(0, run.Image.ActiveImageWorkCountForTests);
            });

            Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
                builtinUrl, null, out MarkdownBuiltInImageSource builtIn));
            using (SharedCanvasBitmapCache.Lease ignoredSeedReference = SharedCanvasBitmapCache.StoreAndAcquire(
                       seed.Device,
                       builtIn.CacheKey,
                       CreateBitmap(seed.Device, 3, 2),
                       new Size(3, 2)))
            {
                // The source ImageBox was constructed before this cache entry
                // existed. A private idle clone must not adopt it as a hit.
            }

            Assert.True(SharedCanvasBitmapCache.TryAcquire(seed.Device, builtIn.CacheKey, out cacheProbe));
            int cacheReferencesBefore = GetEntryReferenceCount(cacheProbe!);

            Assert.False(fixture.Source.TryCreatePrivateRasterImageCandidates(
                targetContext, 320f, out InlineContainerBox? completedOnlyCandidate, out _));
            Assert.Null(completedOnlyCandidate);

            Assert.True(fixture.Source.TryCreatePrivateInlinePlacementClone(
                targetContext,
                320f,
                out candidate,
                out RasterPlacementSourceFence[]? sourceFences,
                allowIdleImagePlacements: true));
            fixture.SetCandidate(targetContext, candidate);

            InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
            RasterPlacementSourceFence[] fences = Assert.IsType<RasterPlacementSourceFence[]>(sourceFences);
            Assert.Equal(fixture.ImageRuns.Count, fences.Length);
            Assert.Same(fixture.SourceContext.SourceMap, clone.Context.SourceMap);
            Assert.Same(targetContext, clone.Context);
            float cloneHeight = clone.Measure(320f);
            Assert.Equal((float)fixture.Source.Bounds.Height, cloneHeight);
            clone.Arrange(0f, 0f, 320f);

            InlineImageRun[] clonedRuns = clone.Runs.OfType<InlineImageRun>().ToArray();
            Assert.Equal(fixture.ImageRuns.Count, clonedRuns.Length);
            for (int index = 0; index < clonedRuns.Length; index++)
            {
                InlineImageRun sourceRun = fixture.ImageRuns[index];
                InlineImageRun cloneRun = clonedRuns[index];
                RasterPlacementSourceFence fence = fences[index];
                ImageBox sourceImage = sourceRun.Image;
                ImageBox cloneImage = cloneRun.Image;

                Assert.True(fence.IsIdleDescriptor);
                Assert.Equal(0, fence.IdleLoadDemandGeneration);
                Assert.Null(fence.Lease);
                Assert.Null(fence.SvgMetadata);
                Assert.Same(sourceImage, fence.Image);
                Assert.NotSame(sourceRun, cloneRun);
                Assert.NotSame(sourceImage, cloneImage);
                Assert.Equal(sourceRun.Url, cloneRun.Url);
                Assert.Equal(sourceRun.Title, cloneRun.Title);
                Assert.Equal(sourceRun.LinkUrl, cloneRun.LinkUrl);
                Assert.Equal(sourceRun.LinkTitle, cloneRun.LinkTitle);
                Assert.Equal(sourceRun.AltText, cloneRun.AltText);
                Assert.Equal(sourceRun.SourceSpan, cloneRun.SourceSpan);
                Assert.Equal(sourceRun.InlineIndex, cloneRun.InlineIndex);
                Assert.Equal(sourceRun.RenderedLength, cloneRun.RenderedLength);
                Assert.Equal(sourceRun.ElementKey, cloneRun.ElementKey);
                Assert.Equal(sourceRun.StyleAliases, cloneRun.StyleAliases);
                Assert.NotSame(sourceRun.StyleAliases, cloneRun.StyleAliases);
                Assert.Equal(sourceRun.StyleModifierKeys, cloneRun.StyleModifierKeys);
                Assert.NotSame(sourceRun.StyleModifierKeys, cloneRun.StyleModifierKeys);
                Assert.Equal(sourceRun.SemanticHeadingKey, cloneRun.SemanticHeadingKey);
                Assert.Equal(sourceRun.DesiredWidth, cloneRun.DesiredWidth);
                Assert.Equal(sourceRun.DesiredHeight, cloneRun.DesiredHeight);
                Assert.Equal(sourceImage.Url, cloneImage.Url);
                Assert.Equal(sourceImage.Alt, cloneImage.Alt);
                Assert.Equal(sourceImage.BlockIndex, cloneImage.BlockIndex);
                Assert.Equal(sourceImage.SafeHtmlInlineFlowScopeId, cloneImage.SafeHtmlInlineFlowScopeId);
                Assert.Equal(sourceImage.Margin, cloneImage.Margin);
                Assert.Equal(GetBaseField<Thickness>(sourceImage, "_margin"),
                    GetBaseField<Thickness>(cloneImage, "_margin"));
                Assert.Equal(sourceImage.CollapsedTopMargin, cloneImage.CollapsedTopMargin);
                Assert.Equal(sourceImage.SuppressTopMargin, cloneImage.SuppressTopMargin);
                Assert.Equal(sourceImage.SuppressBottomMargin, cloneImage.SuppressBottomMargin);
                Assert.Same(targetContext, cloneImage.Context);
                Assert.NotSame(
                    GetCancellationSource(sourceImage),
                    GetCancellationSource(cloneImage));
                Assert.Null(cloneImage.Bitmap);
                Assert.False(GetField<bool>(cloneImage, "_loadStarted"));
                Assert.Equal(0, cloneImage.ActiveImageWorkCountForTests);
            }

            Assert.True(clonedRuns[1].Image.IsSvg); // A .svg URI hint is not accepted SVG state.
            Assert.Null(GetNullableField<WeakReference<byte[]>>(clonedRuns[1].Image, "_svgSourceIdentity"));
            Assert.Equal(cacheReferencesBefore, GetEntryReferenceCount(cacheProbe!));
            Assert.All(fixture.ImageRuns, static run => Assert.Equal(0, run.Image.ActiveImageWorkCountForTests));

            CancellationTokenSource sourceLifetime = GetCancellationSource(fixture.ImageRuns[0].Image);
            CancellationTokenSource cloneLifetime = GetCancellationSource(clonedRuns[0].Image);
            targetImageLifetime.Cancel();
            Assert.True(cloneLifetime.IsCancellationRequested);
            Assert.False(sourceLifetime.IsCancellationRequested);
            await fixture.RetireCandidateAsync(targetContext, clone);
            candidate = null;
            Assert.True(clonedRuns[0].Image.ImageLifetimeDisposedForTests);
            Assert.False(sourceLifetime.IsCancellationRequested);
        }
        finally
        {
            try
            {
                cacheProbe?.Dispose();
            }
            finally
            {
                try
                {
                    if (candidate is not null)
                        await fixture.RetireCandidateAsync(targetContext, candidate);
                }
                finally
                {
                    try
                    {
                        await fixture.RetireSourceAsync();
                    }
                    finally
                    {
                        await targetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task ResolverStartsOnlyAfterIdleCandidateActivationAndSourceIdleFenceExpiresOnLoad()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 0);
        using var resolver = new BlockingResolver();
        await using var fixture = new ImagePlacementFixture(
            seed,
            [$"https://images.example.test/activation-{Guid.NewGuid():N}.png"],
            resolver);
        MarkdownLayoutContext targetContext = fixture.CreateTargetContext();
        InlineContainerBox? candidate = null;

        try
        {
            Assert.True(fixture.Source.TryCreatePrivateInlinePlacementClone(
                targetContext,
                320f,
                out candidate,
                out RasterPlacementSourceFence[]? sourceFences,
                allowIdleImagePlacements: true));
            fixture.SetCandidate(targetContext, candidate);

            InlineImageRun clonedRun = Assert.Single(Assert.IsType<InlineContainerBox>(candidate)
                .Runs.OfType<InlineImageRun>());
            ImageBox cloneImage = clonedRun.Image;
            ImageBox sourceImage = fixture.ImageRuns[0].Image;
            Assert.Equal(0, resolver.ResolveCount);
            Assert.True(sourceImage.IsCurrentIdleImagePlacement(0));
            Assert.True(cloneImage.IsCurrentIdleImagePlacement(0));
            Assert.True(Assert.Single(Assert.IsType<RasterPlacementSourceFence[]>(sourceFences)).IsIdleDescriptor);

            cloneImage.EnsureLoading();
            await resolver.WaitForCallAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, resolver.ResolveCount);
            cloneImage.EnsureLoading();
            Assert.Equal(1, resolver.ResolveCount);
            Assert.True(cloneImage.ActiveImageWorkCountForTests > 0);
            Assert.False(cloneImage.IsCurrentIdleImagePlacement(0));

            // Starting the original source after candidate capture invalidates
            // its idle fence even before the resolver returns or a bitmap exists.
            sourceImage.EnsureLoading();
            await resolver.WaitForCallAsync(2).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, resolver.ResolveCount);
            Assert.False(sourceImage.IsCurrentIdleImagePlacement(0));
            Assert.False(fixture.ImageRuns[0].TryCreatePrivateImagePlacementClone(
                targetContext,
                out _, out _, out _, out _, out _, out _));

            resolver.Release();
            await resolver.WaitForAllReturnedAsync(2).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            resolver.Release();
            try
            {
                if (candidate is not null)
                    await fixture.RetireCandidateAsync(targetContext, candidate);
            }
            finally
            {
                await fixture.RetireSourceAsync();
                await targetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task IdleCloneRejectsDataKnownSvgSuspendedDisposedAndCanceledTargets()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 0);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        await using var fixture = new ImagePlacementFixture(
            seed,
            [
                "data:image/png;base64,AA==",
                $"https://images.example.test/accepted-{Guid.NewGuid():N}.svg",
                $"https://images.example.test/suspended-{Guid.NewGuid():N}.png",
                $"https://images.example.test/disposed-{Guid.NewGuid():N}.png",
                $"https://images.example.test/canceled-{Guid.NewGuid():N}.png",
            ],
            resolver: null,
            session);
        MarkdownLayoutContext targetContext = fixture.CreateTargetContext();
        using var canceledLifetime = new CancellationTokenSource();
        canceledLifetime.Cancel();
        MarkdownLayoutContext canceledTarget = fixture.CreateTargetContext(canceledLifetime.Token);

        try
        {
            ImageBox dataImage = fixture.ImageRuns[0].Image;
            ImageBox svgImage = fixture.ImageRuns[1].Image;
            ImageBox suspendedImage = fixture.ImageRuns[2].Image;
            ImageBox disposedImage = fixture.ImageRuns[3].Image;
            ImageBox canceledTargetImage = fixture.ImageRuns[4].Image;

            byte[] acceptedBytes = [1, 2, 3, 4];
            SetField(svgImage, "_svgSourceIdentity", new WeakReference<byte[]>(acceptedBytes));
            SetField(svgImage, "_svgSourceByteLength", (long)acceptedBytes.Length);
            suspendedImage.SuspendResources();
            disposedImage.Dispose();

            Assert.False(dataImage.TryCreatePrivateImagePlacementClone(
                targetContext, out _, out _, out _, out _, out _));
            Assert.False(svgImage.TryCreatePrivateImagePlacementClone(
                targetContext, out _, out _, out _, out _, out _));
            Assert.False(svgImage.IsCurrentIdleImagePlacement(0));
            Assert.False(suspendedImage.TryCreatePrivateImagePlacementClone(
                targetContext, out _, out _, out _, out _, out _));
            Assert.False(disposedImage.TryCreatePrivateImagePlacementClone(
                targetContext, out _, out _, out _, out _, out _));
            Assert.False(canceledTargetImage.TryCreatePrivateImagePlacementClone(
                canceledTarget, out _, out _, out _, out _, out _));

            Assert.All(fixture.ImageRuns, static run => Assert.Equal(0, run.Image.ActiveImageWorkCountForTests));
            Assert.All(fixture.ImageRuns, static run => Assert.Null(run.Image.Bitmap));
        }
        finally
        {
            await fixture.RetireSourceAsync();
            await targetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await canceledTarget.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SnapshotPreflightRejectsLaterActiveRootBeforeCloningEarlierCompletedImage()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 0);
        using var resolver = new BlockingResolver();
        await using var fixture = new ImagePlacementFixture(
            seed,
            [$"https://images.example.test/completed-{Guid.NewGuid():N}.png"],
            resolver);
        MarkdownLayoutContext targetContext = fixture.CreateTargetContext();
        ImageBox completedImage = fixture.ImageRuns[0].Image;
        var pendingRoot = new InlineContainerBox(fixture.SourceContext, MarkdownElementKeys.Body)
        {
            BlockIndex = fixture.SourceContext.NextBlockIndex(),
            UseContainerMargin = false,
        };
        var pendingRun = new InlineImageRun(
            fixture.SourceContext,
            "pending-root-image",
            $"https://images.example.test/pending-{Guid.NewGuid():N}.png")
        {
            ElementKey = MarkdownElementKeys.Link,
            SourceSpan = new SourceSpan(0, 1),
        };
        pendingRoot.Add(pendingRun);
        float pendingHeight = pendingRoot.Measure(320f);
        pendingRoot.Arrange(0f, (float)fixture.Source.Bounds.Height, 320f);

        SharedCanvasBitmapCache.Lease? completedLease = null;
        LayoutSnapshot? sourceSnapshot = null;
        MarkdownLayoutContext? candidateContext = null;
        SingleRasterLayoutCandidate? candidate = null;
        bool snapshotOwnsPendingRoot = false;
        bool pendingResolveStarted = false;

        try
        {
            completedLease = SharedCanvasBitmapCache.StoreAndAcquire(
                seed.Device,
                $"idle-preflight-completed-{Guid.NewGuid():N}",
                CreateBitmap(seed.Device, 8, 4),
                new Size(8, 4));
            InvokeReplaceBitmap(completedImage, completedLease);
            completedLease = null; // Ownership transferred to the source ImageBox.
            SetField(completedImage, "_loadStarted", true);

            sourceSnapshot = new LayoutSnapshot(
                new BlockBox[] { fixture.Source, pendingRoot },
                fixture.SourceContext.SourceMap,
                320f,
                (float)(fixture.Source.Bounds.Height + pendingHeight));
            fixture.TransferSourceOwnershipToSnapshot(sourceSnapshot);
            snapshotOwnsPendingRoot = true;

            SharedCanvasBitmapCache.Lease sourceLease = GetImageLease(completedImage);
            int referenceCountBefore = GetEntryReferenceCount(sourceLease);
            Assert.True(pendingRun.Image.IsCurrentIdleImagePlacement(0));

            pendingRun.Image.EnsureLoading();
            await resolver.WaitForCallAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
            pendingResolveStarted = true;
            Assert.True(pendingRun.Image.ActiveImageWorkCountForTests > 0);
            Assert.False(pendingRun.Image.IsCurrentIdleImagePlacement(0));

            var capture = new PrivateRasterPlacementTree.CaptureState(allowIdleImagePlacements: true);
            Assert.True(capture.TryAddRoot(
                fixture.Source,
                fixture.SourceContext,
                targetContext,
                320f,
                out PrivateRasterPlacementTree.RootCounts firstRootCounts));
            Assert.Equal(1, firstRootCounts.ImageCount);
            Assert.Equal(referenceCountBefore, GetEntryReferenceCount(sourceLease));

            Assert.False(capture.TryAddRoot(
                pendingRoot,
                fixture.SourceContext,
                targetContext,
                320f,
                out _));
            Assert.Equal(referenceCountBefore, GetEntryReferenceCount(sourceLease));

            candidate = sourceSnapshot!.CreateSingleRasterCandidate(
                new[] { fixture.Source.BlockIndex },
                320f,
                CancellationToken.None,
                CancellationToken.None,
                fixture.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out candidateContext);

            Assert.Null(candidate);
            Assert.NotNull(candidateContext);
            Assert.Equal(referenceCountBefore, GetEntryReferenceCount(sourceLease));
            Assert.Same(sourceLease, GetImageLease(completedImage));
            Assert.True(pendingRun.Image.ActiveImageWorkCountForTests > 0);
        }
        finally
        {
            resolver.Release();
            try
            {
                try
                {
                    candidate?.Dispose();
                }
                finally
                {
                    if (candidateContext is not null)
                        await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                try
                {
                    if (!snapshotOwnsPendingRoot)
                    {
                        fixture.SourceContext.DiscardConstructionBlock(pendingRoot);
                        await pendingRun.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    if (pendingResolveStarted)
                        await resolver.WaitForAllReturnedAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
                    await fixture.RetireSourceAsync();
                    await targetContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    completedLease?.Dispose();
                }
            }
        }
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
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

    private static int GetEntryReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        FieldInfo entryField = typeof(SharedCanvasBitmapCache.Lease).GetField(
            "_entry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object entry = Assert.IsAssignableFrom<SharedCanvasBitmapCache.Entry>(entryField.GetValue(lease));
        FieldInfo countField = typeof(SharedCanvasBitmapCache.Entry).GetField(
            "_referenceCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<int>(countField.GetValue(entry));
    }

    private static SharedCanvasBitmapCache.Lease GetImageLease(ImageBox image)
    {
        FieldInfo field = typeof(ImageBox).GetField(
            "_bitmapLease", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<SharedCanvasBitmapCache.Lease>(field.GetValue(image));
    }

    private static CancellationTokenSource GetCancellationSource(ImageBox image)
    {
        FieldInfo field = typeof(ImageBox).GetField(
            "_imageLifetimeCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsAssignableFrom<CancellationTokenSource>(field.GetValue(image));
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static T GetBaseField<T>(BlockBox instance, string name)
    {
        FieldInfo field = typeof(BlockBox).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static T? GetNullableField<T>(object instance, string name) where T : class
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(instance) is { } value ? Assert.IsType<T>(value) : null;
    }

    private static void SetField<T>(object instance, string name, T value)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }

    private sealed class ImagePlacementFixture : IAsyncDisposable
    {
        private readonly MultiRasterPlacementCloneTests.MultiRasterFixture _seed;
        private readonly object _imagePreparationOwner = new();
        private readonly List<(MarkdownLayoutContext Context, InlineContainerBox Root)> _candidates = new();
        private bool _sourceRetired;
        private LayoutSnapshot? _sourceSnapshot;

        internal ImagePlacementFixture(
            MultiRasterPlacementCloneTests.MultiRasterFixture seed,
            IReadOnlyList<string> urls,
            IMarkdownImageResolver? resolver,
            MarkdownPerformanceSession? session = null)
        {
            _seed = seed;
            string sourceText = new string('!', urls.Count);
            SourceContext = new MarkdownLayoutContext(
                seed.SourceContext.ResourceCreator,
                seed.SourceContext.ThemeSnapshot,
                new MarkdownSourceMap(sourceText),
                seed.SourceContext.Registry,
                seed.SourceContext.FlowDirection,
                seed.SourceContext.Dispatcher,
                seed.SourceContext.Language)
            {
                CancellationToken = CancellationToken.None,
                ImageCancellationToken = CancellationToken.None,
                RasterizationScale = seed.SourceContext.RasterizationScale,
                ImagePreparationOwner = _imagePreparationOwner,
                DisclosureStates = seed.SourceContext.DisclosureStates,
                ImageResolver = resolver,
                PerformanceSession = session,
                SvgRenderer = seed.SourceContext.SvgRenderer,
                ImageBaseUri = seed.SourceContext.ImageBaseUri,
                ImageDocumentPath = seed.SourceContext.ImageDocumentPath,
                ImageDocumentSource = seed.SourceContext.ImageDocumentSource,
                AllowThirdPartyRemoteImages = seed.SourceContext.AllowThirdPartyRemoteImages,
            };

            Source = new InlineContainerBox(SourceContext, MarkdownElementKeys.Body)
            {
                BlockIndex = SourceContext.NextBlockIndex(),
                UseContainerMargin = false,
            };
            ImageRuns = new List<InlineImageRun>(urls.Count);
            for (int index = 0; index < urls.Count; index++)
            {
                var run = new InlineImageRun(
                    SourceContext,
                    $"idle-alt-{index}",
                    urls[index],
                    title: $"idle-title-{index}",
                    linkUrl: $"https://example.test/link-{index}",
                    linkTitle: $"idle-link-title-{index}",
                    requestedWidth: new SafeHtmlLength(37f + index, IsPercent: false),
                    requestedHeight: new SafeHtmlLength(19f + index, IsPercent: false))
                {
                    ElementKey = MarkdownElementKeys.Link,
                    SourceSpan = new SourceSpan(index, 1),
                    StyleAliases = [$"idle-alias-{index}"],
                    StyleModifierKeys = [$"idle-modifier-{index}"],
                    SemanticHeadingKey = MarkdownElementKeys.Heading2,
                };
                Source.Add(run);
                run.Image.Margin = new Thickness(2 + index, 3 + index, 4 + index, 5 + index);
                run.Image.CollapsedTopMargin = 1 + index;
                run.Image.SuppressTopMargin = index % 2 != 0;
                run.Image.SuppressBottomMargin = index % 2 == 0;
                run.Image.SafeHtmlInlineFlowScopeId = 70 + index;
                ImageRuns.Add(run);
            }

            _ = Source.Measure(320f);
            Source.Arrange(0f, 0f, 320f);
        }

        internal MarkdownLayoutContext SourceContext { get; }
        internal InlineContainerBox Source { get; }
        internal List<InlineImageRun> ImageRuns { get; }
        internal object ImagePreparationOwner => _imagePreparationOwner;

        internal MarkdownLayoutContext CreateTargetContext(CancellationToken imageCancellationToken = default) =>
            SourceContext.CreateRasterPlacementContext(
                imageCancellationToken,
                _imagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null);

        internal void SetCandidate(MarkdownLayoutContext context, InlineContainerBox? candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            _candidates.Add((context, candidate));
        }

        internal void TransferSourceOwnershipToSnapshot(LayoutSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (_sourceRetired || _sourceSnapshot is not null ||
                !snapshot.Blocks.Any(block => ReferenceEquals(block, Source)))
                throw new InvalidOperationException("The fixture source snapshot ownership is invalid.");
            _sourceSnapshot = snapshot;
        }

        internal async Task RetireCandidateAsync(MarkdownLayoutContext context, InlineContainerBox candidate)
        {
            context.DiscardConstructionBlock(candidate);
            await Task.WhenAll(candidate.Runs.OfType<InlineImageRun>()
                    .Select(static run => run.Image.RetirementTask))
                .WaitAsync(TimeSpan.FromSeconds(10));
            await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _candidates.RemoveAll(item => ReferenceEquals(item.Context, context) && ReferenceEquals(item.Root, candidate));
        }

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
                await Task.WhenAll(ImageRuns.Select(static run => run.Image.RetirementTask))
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            await SourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            foreach ((MarkdownLayoutContext context, InlineContainerBox root) in _candidates.ToArray())
                await RetireCandidateAsync(context, root);
            await RetireSourceAsync();
            SharedCanvasBitmapCache.ReleaseDevice(_seed.Device);
        }
    }

    private sealed class BlockingResolver : IMarkdownImageResolver, IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _bothReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resolveCount;
        private int _returnedCount;

        internal int ResolveCount => Volatile.Read(ref _resolveCount);
        internal Task WaitForCallAsync(int count) => count switch
        {
            1 => _firstCall.Task,
            2 => _secondCall.Task,
            _ => throw new ArgumentOutOfRangeException(nameof(count)),
        };
        internal Task WaitForAllReturnedAsync(int count) => count switch
        {
            1 => _firstReturned.Task,
            2 => _bothReturned.Task,
            _ => throw new ArgumentOutOfRangeException(nameof(count)),
        };
        internal void Release() => _release.TrySetResult();

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _resolveCount);
            if (call == 1)
                _firstCall.TrySetResult();
            else if (call == 2)
                _secondCall.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline);
            }
            finally
            {
                int returned = Interlocked.Increment(ref _returnedCount);
                if (returned == 1)
                    _firstReturned.TrySetResult();
                else if (returned == 2)
                    _bothReturned.TrySetResult();
            }
        }

        public void Dispose() => Release();
    }
}
