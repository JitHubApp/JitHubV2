using System.Collections.Concurrent;
using MarkdownRenderer.GitHub.Tests.Performance;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg;

/// <summary>
/// Checks how image disposal and session shutdown cancel and drain SVG
/// work: admitted work, accepted publications, queued and active CPU
/// preparations, provider opens and saturated provider admission.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgPreparationShutdownTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task DisposeCancelsAndDrainsAdmittedSvgWorkBeforeReleasingLifetime()
    {
        var renderer = new BlockingSvgRenderer();
        var image = new ImageBox(
            CreateContext(_renderResources, renderer), _renderResources.Images,
            CreateSvgDataUri(Guid.NewGuid().ToString("N")),
            "dispose race");
        image.Measure(80);
        image.EnsureLoading();
        await renderer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        image.Dispose();

        await renderer.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() =>
            image.GetWorkState().ActiveWorkCount == 0 &&
            image.GetWorkState().LifetimeDisposed);
    }

    [Fact]
    public async Task SessionShutdownDropsAcceptedQueuedImagePublicationExactlyOnce()
    {
        await using var session = CreateSingleSlotCpuPreparationSession();
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80, IntrinsicHeightDips: 40, IntrinsicAspectRatio: 2));
        var image = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: session), _renderResources.Images, "", "queued publication");
        Action? delivery = null;
        int published = 0;
        int dropped = 0;
        try
        {
            image.PublishOnUiThread(
                () => Interlocked.Increment(ref published),
                () => Interlocked.Increment(ref dropped),
                callback =>
                {
                    delivery = callback;
                    return true;
                });
            Assert.Equal(1, image.GetWorkState().ActiveWorkCount);

            await session.DisposeAsync();
            Assert.Equal(0, Volatile.Read(ref dropped));
            Action callback = Assert.IsType<Action>(delivery);
            callback();
            callback();

            Assert.Equal(0, Volatile.Read(ref published));
            Assert.Equal(1, Volatile.Read(ref dropped));
            Assert.Equal(0, image.GetWorkState().ActiveWorkCount);
        }
        finally
        {
            delivery?.Invoke();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RetiringImageCancelsQueuedCpuPreparationWithoutPublishingAndDrainsIt()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstPreparation: true);
        await using var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var first = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: session, performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-retire-first-" + Guid.NewGuid().ToString("N")),
            "admitted SVG");
        var queued = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: session, performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-retire-queued-" + Guid.NewGuid().ToString("N")),
            "retired queued SVG");
        int queuedPublications = 0;
        queued.LoadCompleted += (_, _) => Interlocked.Increment(ref queuedPublications);
        Task firstLoad = Task.CompletedTask;

        try
        {
            first.Measure(80);
            queued.Measure(80);
            firstLoad = WaitForLoadAsync(first);
            await renderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            queued.EnsureLoading();
            await WaitUntilAsync(() => session.PendingCpuPreparations == 1);
            Assert.Equal(1, renderer.PrepareSourceCount);

            queued.Dispose();
            await WaitUntilAsync(() =>
                queued.GetWorkState().ActiveWorkCount == 0 &&
                queued.GetWorkState().LifetimeDisposed);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, Volatile.Read(ref queuedPublications));
            Assert.Null(queued.SvgFailureReason);

            renderer.ReleaseFirstPreparation.Set();
            await firstLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(first.Bitmap);
        }
        finally
        {
            renderer.ReleaseFirstPreparation.Set();
            await ObserveTaskForCleanupAsync(firstLoad);
            first.Dispose();
            queued.Dispose();
            await WaitUntilAsync(() =>
                first.GetWorkState().ActiveWorkCount == 0 &&
                queued.GetWorkState().ActiveWorkCount == 0);
        }
    }

    [Fact]
    public async Task SessionShutdownCancelsQueuedAndActiveSvgPreparationAndWaitsForActiveDrain()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstPreparation: true,
            holdFirstPreparationAfterCancellation: true);
        var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var unavailable = new ConcurrentQueue<MarkdownSvgFailureReason?>();
        var first = new ImageBox(
            CreateContext(
                _renderResources,
                renderer,
                imageUnavailable: (_, _, failure) => unavailable.Enqueue(failure),
                performanceSession: session,
                performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-shutdown-first-" + Guid.NewGuid().ToString("N")),
            "active SVG");
        var queued = new ImageBox(
            CreateContext(
                _renderResources,
                renderer,
                imageUnavailable: (_, _, failure) => unavailable.Enqueue(failure),
                performanceSession: session,
                performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-shutdown-queued-" + Guid.NewGuid().ToString("N")),
            "queued SVG");
        int firstPublications = 0;
        int queuedPublications = 0;
        first.LoadCompleted += (_, _) => Interlocked.Increment(ref firstPublications);
        queued.LoadCompleted += (_, _) => Interlocked.Increment(ref queuedPublications);

        try
        {
            first.Measure(80);
            queued.Measure(80);
            first.EnsureLoading();
            await renderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            queued.EnsureLoading();
            await WaitUntilAsync(() => session.PendingCpuPreparations == 1);

            Task shutdown = session.DisposeAsync().AsTask();
            await renderer.FirstPreparationCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => queued.GetWorkState().ActiveWorkCount == 0);

            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(0, Volatile.Read(ref firstPublications));
            Assert.Equal(0, Volatile.Read(ref queuedPublications));
            Assert.Empty(unavailable);
            Assert.Null(first.SvgFailureReason);
            Assert.Null(queued.SvgFailureReason);

            renderer.ReleaseFirstPreparationAfterCancellation.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => first.GetWorkState().ActiveWorkCount == 0);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(0, Volatile.Read(ref firstPublications));
            Assert.Empty(unavailable);
            Assert.Null(first.SvgFailureReason);
        }
        finally
        {
            renderer.ReleaseFirstPreparation.Set();
            renderer.ReleaseFirstPreparationAfterCancellation.Set();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            first.Dispose();
            queued.Dispose();
            await WaitUntilAsync(() =>
                first.GetWorkState().ActiveWorkCount == 0 &&
                queued.GetWorkState().ActiveWorkCount == 0);
        }
    }

    [Fact]
    public async Task SessionShutdownCancelsProviderOpenAfterCpuPreparationWithoutPublishingRetry()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstOpen: true);
        await using var session = CreateSingleSlotCpuPreparationSession();
        var unavailable = new ConcurrentQueue<MarkdownSvgFailureReason?>();
        var image = new ImageBox(
            CreateContext(
                _renderResources,
                renderer,
                imageUnavailable: (_, _, failure) => unavailable.Enqueue(failure),
                performanceSession: session), _renderResources.Images,
            CreateSvgDataUri("cpu-shutdown-open-" + Guid.NewGuid().ToString("N")),
            "SVG awaiting provider open");
        int publications = 0;
        image.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);

        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(1, renderer.OpenCount);

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);

            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Empty(unavailable);
            Assert.Null(image.SvgFailureReason);
            Assert.Null(image.Bitmap);
        }
        finally
        {
            renderer.ReleaseFirstOpen.TrySetResult();
            image.Dispose();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);
        }
    }

    [Fact]
    public async Task SessionShutdownCancelsSaturatedProviderAdmissionWithoutWaitingForOtherImages()
    {
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        var blockers = new List<(ImageBox Image, CpuAdmissionSvgRenderer Renderer)>();
        using var queuedRenderer = new CpuAdmissionSvgRenderer(info);
        await using var session = CreateSingleSlotCpuPreparationSession();
        var queued = new ImageBox(
            CreateContext(_renderResources, queuedRenderer, performanceSession: session), _renderResources.Images,
            CreateSvgDataUri("provider-admission-queued-" + Guid.NewGuid().ToString("N")),
            "SVG waiting for provider admission");
        int publications = 0;
        queued.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);

        try
        {
            // Hold all sixteen global provider slots with unrelated images.
            // Each first OpenAsync signals only after that slot was acquired.
            for (int i = 0; i < 16; i++)
            {
                var renderer = new CpuAdmissionSvgRenderer(info, blockFirstOpen: true);
                var image = new ImageBox(
                    CreateContext(_renderResources, renderer), _renderResources.Images,
                    CreateSvgDataUri("provider-admission-blocker-" + Guid.NewGuid().ToString("N")),
                    "unrelated admitted SVG");
                blockers.Add((image, renderer));
                image.Measure(80);
                image.EnsureLoading();
                await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            queued.Measure(80);
            queued.EnsureLoading();
            await queuedRenderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Acquiring this CPU slot proves the queued image released its
            // synchronous preparation slot; the provider gate is still full.
            using (await session.EnterCpuPreparationAsync(new object(), false, CancellationToken.None))
            {
                Assert.Equal(0, queuedRenderer.OpenCount);
                Assert.True(queued.GetWorkState().ActiveWorkCount > 0);
            }

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => queued.GetWorkState().ActiveWorkCount == 0);

            Assert.Equal(0, queuedRenderer.OpenCount);
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Null(queued.SvgFailureReason);
            Assert.Null(queued.Bitmap);
            Assert.All(blockers, blocker => Assert.False(blocker.Renderer.ReleaseFirstOpen.Task.IsCompleted));
        }
        finally
        {
            foreach (var blocker in blockers)
                blocker.Renderer.ReleaseFirstOpen.TrySetResult();
            queued.Dispose();
            foreach (var blocker in blockers)
                blocker.Image.Dispose();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() =>
                queued.GetWorkState().ActiveWorkCount == 0 &&
                blockers.All(blocker => blocker.Image.GetWorkState().ActiveWorkCount == 0));
            foreach (var blocker in blockers)
                blocker.Renderer.Dispose();
        }
    }

    private sealed class BlockingSvgRenderer : IMarkdownSvgRenderer
    {
        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        internal TaskCompletionSource Opened { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Canceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            Opened.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking renderer unexpectedly resumed.");
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }
}
