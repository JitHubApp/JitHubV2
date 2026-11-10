using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how SVG preparation is admitted: shared process admission
/// across sessions and legacy controls, its scheduling weight, queued
/// request removal, and release of process and CPU slots before or after
/// provider opens.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgPreparationAdmissionTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task SvgPreparationWaitsForSharedProcessAdmissionAcrossSessionsAndLegacyControls()
    {
        MarkdownImagePreparationAdmission gate = _renderResources.Images.Preparation;
        using IDisposable blocker = await gate.EnterAsync(
            new object(), 1, exclusive: true, CancellationToken.None);
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: false);
        await using var firstSession = CreateSingleSlotCpuPreparationSession();
        await using var secondSession = CreateSingleSlotCpuPreparationSession();
        var images = new[]
        {
            new ImageBox(
                CreateContext(_renderResources, renderer, performanceSession: firstSession), _renderResources.Images,
                CreateSvgDataUri("process-preparation-session-one"),
                "session one"),
            new ImageBox(
                CreateContext(_renderResources, renderer, performanceSession: secondSession), _renderResources.Images,
                CreateSvgDataUri("process-preparation-session-two"),
                "session two"),
            new ImageBox(
                CreateContext(_renderResources, renderer), _renderResources.Images,
                CreateSvgDataUri("process-preparation-without-session"),
                "legacy control"),
        };
        Task[] loads = [];

        try
        {
            foreach (ImageBox image in images)
                image.Measure(80);
            loads = images.Select(WaitForLoadAsync).ToArray();

            await WaitUntilAsync(() => gate.PendingRequests == images.Length);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(1, gate.ActiveCount);
            Assert.Equal(gate.CapacityBytes, gate.ActiveBytes);

            blocker.Dispose();
            await Task.WhenAll(loads).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(images.Length, renderer.PrepareSourceCount);
            Assert.All(images, image => Assert.NotNull(image.Bitmap));
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
            Assert.Equal(0, gate.PendingRequests);
        }
        finally
        {
            blocker.Dispose();
            renderer.ReleaseOpenRequests();
            foreach (ImageBox image in images)
                image.Dispose();
            foreach (Task load in loads)
                await ObserveTaskForCleanupAsync(load);
            await WaitUntilAsync(() => images.All(image => image.GetWorkState().ActiveWorkCount == 0));
        }
    }

    [Fact]
    public async Task SvgPreparationUsesThreeTimesSourceLengthAsItsSchedulingWeight()
    {
        MarkdownImagePreparationAdmission gate = _renderResources.Images.Preparation;
        string dataUri = CreateSvgDataUri("process-preparation-weight");
        string sourceText = Uri.UnescapeDataString(dataUri[(dataUri.IndexOf(',') + 1)..]);
        long sourceLength = Encoding.UTF8.GetByteCount(sourceText);
        long heldWeight = gate.CapacityBytes - (sourceLength * 2);
        Assert.True(heldWeight > 0);

        using IDisposable blocker = await gate.EnterAsync(
            new object(), heldWeight, exclusive: false, CancellationToken.None);
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: false);
        var image = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, dataUri, "weighted SVG");
        Task load = Task.CompletedTask;

        try
        {
            image.Measure(80);
            load = WaitForLoadAsync(image);

            // One source-sized estimate would fit in the remaining budget;
            // the required 3x transient-work estimate must wait.
            await WaitUntilAsync(() => gate.PendingRequests == 1);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(1, gate.ActiveCount);
            Assert.Equal(heldWeight, gate.ActiveBytes);

            blocker.Dispose();
            await load.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
        }
        finally
        {
            blocker.Dispose();
            renderer.ReleaseOpenRequests();
            image.Dispose();
            await ObserveTaskForCleanupAsync(load);
            await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);
        }
    }

    [Fact]
    public async Task RetiringQueuedSvgSessionRemovesItsProcessPreparationRequest()
    {
        MarkdownImagePreparationAdmission gate = _renderResources.Images.Preparation;
        using IDisposable blocker = await gate.EnterAsync(
            new object(), 1, exclusive: true, CancellationToken.None);
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: false);
        await using var retiringSession = CreateSingleSlotCpuPreparationSession();
        var retired = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: retiringSession), _renderResources.Images,
            CreateSvgDataUri("process-preparation-retired"),
            "retired SVG");
        var survivor = new ImageBox(
            CreateContext(_renderResources, renderer), _renderResources.Images,
            CreateSvgDataUri("process-preparation-survivor"),
            "surviving SVG");
        int retiredPublications = 0;
        retired.LoadCompleted += (_, _) => Interlocked.Increment(ref retiredPublications);
        Task survivorLoad = Task.CompletedTask;

        try
        {
            retired.Measure(80);
            survivor.Measure(80);
            retired.EnsureLoading();
            survivorLoad = WaitForLoadAsync(survivor);

            await WaitUntilAsync(() => gate.PendingRequests == 2);
            Assert.Equal(0, renderer.PrepareSourceCount);

            await retiringSession.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => gate.PendingRequests == 1 &&
                retired.GetWorkState().ActiveWorkCount == 0);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(0, Volatile.Read(ref retiredPublications));

            blocker.Dispose();
            await survivorLoad.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(survivor.Bitmap);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
            Assert.Equal(0, gate.PendingRequests);
        }
        finally
        {
            blocker.Dispose();
            renderer.ReleaseOpenRequests();
            retired.Dispose();
            survivor.Dispose();
            await ObserveTaskForCleanupAsync(survivorLoad);
            await WaitUntilAsync(() => retired.GetWorkState().ActiveWorkCount == 0 &&
                survivor.GetWorkState().ActiveWorkCount == 0);
        }
    }

    [Fact]
    public async Task SvgPreparationReleasesProcessAdmissionBeforeBlockedProviderOpens()
    {
        MarkdownImagePreparationAdmission gate = _renderResources.Images.Preparation;
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: true);
        var images = new[]
        {
            new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, CreateSvgDataUri("provider-block-first"), "first"),
            new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, CreateSvgDataUri("provider-block-second"), "second"),
            new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, CreateSvgDataUri("provider-block-third"), "third"),
        };
        Task[] loads = [];

        try
        {
            foreach (ImageBox image in images.Take(2))
                image.Measure(80);
            loads = images.Take(2).Select(WaitForLoadAsync).ToArray();

            await renderer.TwoOpensStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.PendingRequests);

            images[2].Measure(80);
            loads = [.. loads, WaitForLoadAsync(images[2])];
            await renderer.ThreeOpensStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // All three provider opens remain blocked, yet a third source has
            // prepared and reached OpenAsync: the process gate was released
            // before provider admission/waiting.
            Assert.Equal(3, renderer.PrepareSourceCount);
            Assert.Equal(3, renderer.OpenCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.PendingRequests);

            renderer.ReleaseOpenRequests();
            await Task.WhenAll(loads).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.All(images, image => Assert.NotNull(image.Bitmap));
        }
        finally
        {
            renderer.ReleaseOpenRequests();
            foreach (ImageBox image in images)
                image.Dispose();
            foreach (Task load in loads)
                await ObserveTaskForCleanupAsync(load);
            await WaitUntilAsync(() => images.All(image => image.GetWorkState().ActiveWorkCount == 0));
        }
    }

    [Fact]
    public async Task CpuPreparationAdmissionSerializesPreflightButIsReleasedBeforeProviderOpen()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstPreparation: true,
            blockFirstOpen: true);
        await using var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var first = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: session, performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-admission-first-" + Guid.NewGuid().ToString("N")),
            "first admitted SVG");
        var second = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: session, performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-admission-second-" + Guid.NewGuid().ToString("N")),
            "queued SVG");
        Task firstLoad = Task.CompletedTask;
        Task secondLoad = Task.CompletedTask;

        try
        {
            first.Measure(80);
            second.Measure(80);
            firstLoad = WaitForLoadAsync(first);
            await renderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            secondLoad = WaitForLoadAsync(second);
            await WaitUntilAsync(() => session.PendingCpuPreparations == 1);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.False(renderer.SecondPreparationStarted.Task.IsCompleted);

            renderer.ReleaseFirstPreparation.Set();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The first provider OpenAsync remains deliberately blocked. The
            // second synchronous prepare must nevertheless enter, proving the
            // CPU lease ends after host preflight and before provider awaits.
            await renderer.SecondPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.False(firstLoad.IsCompleted);

            await secondLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(second.Bitmap);
            renderer.ReleaseFirstOpen.TrySetResult();
            await firstLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(first.Bitmap);
        }
        finally
        {
            renderer.ReleaseFirstPreparation.Set();
            renderer.ReleaseFirstOpen.TrySetResult();
            await ObserveTaskForCleanupAsync(firstLoad);
            await ObserveTaskForCleanupAsync(secondLoad);
            first.Dispose();
            second.Dispose();
            await WaitUntilAsync(() =>
                first.GetWorkState().ActiveWorkCount == 0 &&
                second.GetWorkState().ActiveWorkCount == 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrThrowingProviderPreparationReturnsCpuSlot(bool providerThrows)
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            firstPreparationFailure: providerThrows
                ? CpuAdmissionSvgRenderer.FirstPreparationFailure.Throw
                : CpuAdmissionSvgRenderer.FirstPreparationFailure.Reject);
        await using var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var failed = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: session, performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-failure-first-" + Guid.NewGuid().ToString("N")),
            "provider failure");
        var recovered = new ImageBox(
            CreateContext(_renderResources, renderer, performanceSession: session, performanceDocumentOwner: documentOwner), _renderResources.Images,
            CreateSvgDataUri("cpu-failure-recovered-" + Guid.NewGuid().ToString("N")),
            "subsequent provider request");

        try
        {
            failed.Measure(80);
            await WaitForLoadAsync(failed);
            Assert.NotNull(failed.SvgFailureReason);

            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.Null(recovered.SvgFailureReason);
            Assert.NotNull(recovered.Bitmap);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(1, renderer.OpenCount);
        }
        finally
        {
            failed.Dispose();
            recovered.Dispose();
            await session.DisposeAsync();
            await WaitUntilAsync(() =>
                failed.GetWorkState().ActiveWorkCount == 0 &&
                recovered.GetWorkState().ActiveWorkCount == 0);
        }
    }

    private sealed class ProcessPreparationSvgRenderer(bool holdOpens) : IMarkdownSvgRenderer, IDisposable
    {
        private readonly MarkdownSvgDocumentInfo _info = new(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        private readonly TaskCompletionSource _releaseOpenRequests = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prepareSourceCount;
        private int _openCount;

        internal TaskCompletionSource TwoOpensStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ThreeOpensStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);

        internal int OpenCount => Volatile.Read(ref _openCount);

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _prepareSourceCount);
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Interlocked.Increment(ref _openCount);
            if (count >= 2)
                TwoOpensStarted.TrySetResult();
            if (count >= 3)
                ThreeOpensStarted.TrySetResult();
            if (holdOpens)
                await _releaseOpenRequests.Task.WaitAsync(cancellationToken);

            return new RecordingSvgDocument(
                _info,
                new ConcurrentQueue<MarkdownSvgRenderRequest>());
        }

        internal void ReleaseOpenRequests() => _releaseOpenRequests.TrySetResult();

        public void Dispose() => ReleaseOpenRequests();
    }
}
