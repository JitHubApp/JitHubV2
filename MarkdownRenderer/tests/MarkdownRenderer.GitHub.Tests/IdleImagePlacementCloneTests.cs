using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
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
        await using var fixture = new IdleImagePlacementFixture(seed, [builtinUrl, svgHintUrl], resolver: null);
        using var targetImageLifetime = new CancellationTokenSource();
        MarkdownLayoutContext targetContext = fixture.CreateTargetContext(targetImageLifetime.Token);
        InlineContainerBox? candidate = null;
        SharedCanvasBitmapCache.Lease? cacheProbe = null;

        try
        {
            Assert.All(fixture.ImageRuns, static run =>
            {
                Assert.Null(run.Image.Bitmap);
                Assert.False(run.Image.LoadState.IsStarted);
                Assert.Equal(0, run.Image.GetWorkState().ActiveWorkCount);
            });

            string builtInCacheKey = StoreLateBuiltInCacheEntry(fixture, seed.Device, builtinUrl);

            Assert.True(fixture.RenderResources.Images.BitmapCache.TryAcquire(seed.Device, builtInCacheKey, out cacheProbe));
            int cacheReferencesBefore = GetEntryReferenceCount(cacheProbe!);

            Assert.False(InlinePrivatePlacementCloner.TryCreatePrivateRasterImageCandidates(
                fixture.Source,
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
                AssertIdleRunCopiesAuthoredDescriptor(fixture.ImageRuns[index], clonedRuns[index], fences[index], targetContext);

            Assert.True(clonedRuns[1].Image.IsSvg); // A .svg URI hint is not accepted SVG state.
            Assert.Null(clonedRuns[1].Image.Svg.SourceIdentity);
            Assert.Equal(cacheReferencesBefore, GetEntryReferenceCount(cacheProbe!));
            Assert.All(fixture.ImageRuns, static run => Assert.Equal(0, run.Image.GetWorkState().ActiveWorkCount));

            CancellationTokenSource sourceLifetime = GetCancellationSource(fixture.ImageRuns[0].Image);
            CancellationTokenSource cloneLifetime = GetCancellationSource(clonedRuns[0].Image);
            targetImageLifetime.Cancel();
            Assert.True(cloneLifetime.IsCancellationRequested);
            Assert.False(sourceLifetime.IsCancellationRequested);
            await fixture.RetireCandidateAsync(targetContext, clone);
            candidate = null;
            Assert.True(clonedRuns[0].Image.GetWorkState().LifetimeDisposed);
            Assert.False(sourceLifetime.IsCancellationRequested);
        }
        finally
        {
            await ReleaseIdleCloneAsync(fixture, targetContext, cacheProbe, candidate);
        }
    }

    private static string StoreLateBuiltInCacheEntry(
        IdleImagePlacementFixture fixture,
        CanvasDevice device,
        string builtinUrl)
    {
        Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(
            builtinUrl, null, out MarkdownBuiltInImageSource builtIn));
        using (SharedCanvasBitmapCache.Lease ignoredSeedReference = fixture.RenderResources.Images.BitmapCache.StoreAndAcquire(
                   device,
                   builtIn.CacheKey,
                   CreateBitmap(device, 3, 2),
                   new Size(3, 2)))
        {
            // The source ImageBox was constructed before this cache entry
            // existed. A private idle clone must not adopt it as a hit.
        }
        return builtIn.CacheKey;
    }

    private static void AssertIdleRunCopiesAuthoredDescriptor(
        InlineImageRun sourceRun,
        InlineImageRun cloneRun,
        RasterPlacementSourceFence fence,
        MarkdownLayoutContext targetContext)
    {
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
        Assert.Equal(sourceImage.RawPlacementMargin,
            cloneImage.RawPlacementMargin);
        Assert.Equal(sourceImage.CollapsedTopMargin, cloneImage.CollapsedTopMargin);
        Assert.Equal(sourceImage.SuppressTopMargin, cloneImage.SuppressTopMargin);
        Assert.Equal(sourceImage.SuppressBottomMargin, cloneImage.SuppressBottomMargin);
        Assert.Same(targetContext, cloneImage.Context);
        Assert.NotSame(
            GetCancellationSource(sourceImage),
            GetCancellationSource(cloneImage));
        Assert.Null(cloneImage.Bitmap);
        Assert.False(cloneImage.LoadState.IsStarted);
        Assert.Equal(0, cloneImage.GetWorkState().ActiveWorkCount);
    }

    private static async Task ReleaseIdleCloneAsync(
        IdleImagePlacementFixture fixture,
        MarkdownLayoutContext targetContext,
        SharedCanvasBitmapCache.Lease? cacheProbe,
        InlineContainerBox? candidate)
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

    [Fact]
    public async Task ResolverStartsOnlyAfterIdleCandidateActivationAndSourceIdleFenceExpiresOnLoad()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 0);
        using var resolver = new IdleImageBlockingResolver();
        await using var fixture = new IdleImagePlacementFixture(
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
            Assert.True(cloneImage.GetWorkState().ActiveWorkCount > 0);
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
        await using var fixture = new IdleImagePlacementFixture(
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
            svgImage.Svg.RememberSource(acceptedBytes);
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

            Assert.All(fixture.ImageRuns, static run => Assert.Equal(0, run.Image.GetWorkState().ActiveWorkCount));
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
        using var resolver = new IdleImageBlockingResolver();
        await using var fixture = new IdleImagePlacementFixture(
            seed,
            [$"https://images.example.test/completed-{Guid.NewGuid():N}.png"],
            resolver);
        MarkdownLayoutContext targetContext = fixture.CreateTargetContext();
        ImageBox completedImage = fixture.ImageRuns[0].Image;
        (InlineContainerBox pendingRoot, InlineImageRun pendingRun, float pendingHeight) = fixture.CreatePendingRoot();

        SharedCanvasBitmapCache.Lease? completedLease = null;
        LayoutSnapshot? sourceSnapshot = null;
        MarkdownLayoutContext? candidateContext = null;
        SingleRasterLayoutCandidate? candidate = null;
        bool snapshotOwnsPendingRoot = false;
        bool pendingResolveStarted = false;

        try
        {
            completedLease = fixture.RenderResources.Images.BitmapCache.StoreAndAcquire(
                seed.Device,
                $"idle-preflight-completed-{Guid.NewGuid():N}",
                CreateBitmap(seed.Device, 8, 4),
                new Size(8, 4));
            completedImage.Presentation.Replace(completedLease.Bitmap, completedLease);
            completedLease = null; // Ownership transferred to the source ImageBox.
            completedImage.LoadState.IsStarted = true;

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
            Assert.True(pendingRun.Image.GetWorkState().ActiveWorkCount > 0);
            Assert.False(pendingRun.Image.IsCurrentIdleImagePlacement(0));

            AssertCaptureRejectsLaterActiveRoot(fixture, targetContext, pendingRoot, sourceLease, referenceCountBefore);

            candidate = CreateSourceCandidate(sourceSnapshot!, fixture, out candidateContext);

            Assert.Null(candidate);
            Assert.NotNull(candidateContext);
            Assert.Equal(referenceCountBefore, GetEntryReferenceCount(sourceLease));
            Assert.Same(sourceLease, GetImageLease(completedImage));
            Assert.True(pendingRun.Image.GetWorkState().ActiveWorkCount > 0);
        }
        finally
        {
            resolver.Release();
            try
            {
                await DisposeCandidateAndDrainAsync(candidate, candidateContext);
            }
            finally
            {
                try
                {
                    await RetirePendingRootAndSourceAsync(
                        fixture, targetContext, resolver, pendingRoot, pendingRun,
                        snapshotOwnsPendingRoot, pendingResolveStarted);
                }
                finally
                {
                    completedLease?.Dispose();
                }
            }
        }
    }

    private static SingleRasterLayoutCandidate? CreateSourceCandidate(
        LayoutSnapshot sourceSnapshot,
        IdleImagePlacementFixture fixture,
        out MarkdownLayoutContext? candidateContext) =>
        sourceSnapshot.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            320f,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwner,
            retirementSink: null,
            imageUnavailable: null,
            out candidateContext);

    private static async Task RetirePendingRootAndSourceAsync(
        IdleImagePlacementFixture fixture,
        MarkdownLayoutContext targetContext,
        IdleImageBlockingResolver resolver,
        InlineContainerBox pendingRoot,
        InlineImageRun pendingRun,
        bool snapshotOwnsPendingRoot,
        bool pendingResolveStarted)
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

    private static void AssertCaptureRejectsLaterActiveRoot(
        IdleImagePlacementFixture fixture,
        MarkdownLayoutContext targetContext,
        InlineContainerBox pendingRoot,
        SharedCanvasBitmapCache.Lease sourceLease,
        int referenceCountBefore)
    {
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
    }

    private static async Task DisposeCandidateAndDrainAsync(
        SingleRasterLayoutCandidate? candidate,
        MarkdownLayoutContext? candidateContext)
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

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    private static int GetEntryReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        int? count = lease.EntryReferenceCount;
        Assert.True(count.HasValue, "The lease no longer holds a cache entry.");
        return count.Value;
    }

    private static SharedCanvasBitmapCache.Lease GetImageLease(ImageBox image) =>
        Assert.IsType<SharedCanvasBitmapCache.Lease>(image.Presentation.Lease);

    private static CancellationTokenSource GetCancellationSource(ImageBox image) =>
        Assert.IsAssignableFrom<CancellationTokenSource>(image.Lifetime.LifetimeCancellation);
}
