using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.DisplaySizedRasterDecodeTests;

namespace MarkdownRenderer.GitHub.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class ProcessRasterDecodeCollection
{
    public const string CollectionName = "Process raster decode admission";
}

// The process-static production gate must not be held while unrelated image
// tests run. These tests exercise real WIC/Win2D ImageBox paths, not a fake codec.
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ProcessRasterDecodeIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyKeyCacheMissWithoutSessionWaitsForProcessAdmission(bool hasDocumentOwner)
    {
        var gate = MarkdownImagePreparationAdmission.Shared;
        using IDisposable held = await gate.EnterAsync(new object(), 1, exclusive: true, CancellationToken.None);
        var image = CreateImage(new Bytes(CreatePng(64, 32), string.Empty), null, hasDocumentOwner);
        Task load = WaitForLoadAsync(image);
        try
        {
            await WaitUntilAsync(() => gate.PendingRequests == 1);
            Assert.Null(image.Bitmap);
            Assert.Equal(1, gate.ActiveCount);
            held.Dispose();
            await load;
            Assert.NotNull(image.Bitmap);
            Assert.Equal(64u, image.Bitmap.SizeInPixels.Width);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
            Assert.Equal(0, gate.PendingRequests);
        }
        finally { image.Dispose(); held.Dispose(); }
    }

    [Fact]
    public async Task IndependentlyConfiguredSessionsShareOneCodecAdmission()
    {
        var gate = MarkdownImagePreparationAdmission.Shared;
        using IDisposable held = await gate.EnterAsync(new object(), 1, exclusive: true, CancellationToken.None);
        using var firstSession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentCpuPreparations = 1 });
        using var secondSession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentCpuPreparations = 1 });
        byte[] bytes = CreatePng(64, 32);
        // Exercise aggregate codec admission independently of keyed dedup.
        // Random nonempty keys can share a bounded decode stripe, legitimately
        // parking the second image before it reaches either CPU admission.
        var first = CreateImage(new Bytes(bytes, string.Empty), firstSession);
        var second = CreateImage(new Bytes(bytes, string.Empty), secondSession);
        Task firstLoad = WaitForLoadAsync(first);
        Task secondLoad = WaitForLoadAsync(second);
        try
        {
            await WaitUntilAsync(() => gate.PendingRequests == 2);
            Assert.Null(first.Bitmap);
            Assert.Null(second.Bitmap);
            Assert.Equal(1, firstSession.GetSnapshot().CpuPreparations);
            Assert.Equal(1, secondSession.GetSnapshot().CpuPreparations);
            Assert.Equal(1, gate.ActiveCount);
            held.Dispose();
            await Task.WhenAll(firstLoad, secondLoad);
            Assert.NotNull(first.Bitmap);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.PendingRequests);
        }
        finally { first.Dispose(); second.Dispose(); held.Dispose(); }
    }

    [Fact]
    public async Task DistinctSourcesSharingDecodeStripeWaitBeforeCpuAndProcessAdmission()
    {
        var gate = MarkdownImagePreparationAdmission.Shared;
        using IDisposable held = await gate.EnterAsync(new object(), 1, exclusive: true, CancellationToken.None);
        using var firstSession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { UseDisplaySizedRasterDecode = false });
        using var secondSession = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { UseDisplaySizedRasterDecode = false });
        byte[] bytes = CreatePng(64, 32);
        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        var stripes = new Dictionary<SemaphoreSlim, string>();
        string prefix = Guid.NewGuid().ToString("N");
        string? firstKey = null;
        string? secondKey = null;
        SemaphoreSlim? sharedStripe = null;
        // There are 64 bounded stripes. 65 unique keys must include a collision;
        // no dependence on randomized string hashes or a probabilistic retry.
        for (int i = 0; i <= 64; i++)
        {
            string key = $"{prefix}/{i}";
            SemaphoreSlim stripe = SharedCanvasBitmapCache.GetDecodeGate(device, BoundedSourceSizedCacheKey(key));
            if (stripes.TryGetValue(stripe, out firstKey))
            {
                secondKey = key;
                sharedStripe = stripe;
                break;
            }
            stripes.Add(stripe, key);
        }
        Assert.NotNull(firstKey);
        Assert.NotNull(secondKey);
        Assert.NotNull(sharedStripe);
        Assert.NotEqual(firstKey, secondKey);
        var first = CreateImage(new Bytes(bytes, firstKey), firstSession);
        var second = CreateImage(new Bytes(bytes, secondKey), secondSession);
        Task? secondLoad = null;
        Task firstLoad = WaitForLoadAsync(first);
        try
        {
            await WaitUntilAsync(() => gate.PendingRequests == 1);
            secondLoad = WaitForLoadAsync(second);
            await WaitUntilAsync(() => second.ActiveImageWorkCountForTests >= 2);
            Assert.Equal(1, gate.PendingRequests);
            Assert.Equal(1, gate.ActiveCount);
            Assert.Equal(0, sharedStripe.CurrentCount);
            Assert.Equal(1, firstSession.GetSnapshot().CpuPreparations);
            Assert.Equal(0, secondSession.GetSnapshot().CpuPreparations);
            Assert.Null(first.Bitmap);
            Assert.Null(second.Bitmap);
            Assert.False(secondLoad.IsCompleted);

            held.Dispose();
            await Task.WhenAll(firstLoad, secondLoad);
            await WaitUntilAsync(() => first.ActiveImageWorkCountForTests == 0 &&
                second.ActiveImageWorkCountForTests == 0);
            Assert.NotNull(first.Bitmap);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(0, gate.PendingRequests);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
            Assert.Equal(1, sharedStripe.CurrentCount);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            held.Dispose();
            await WaitUntilAsync(() => first.ActiveImageWorkCountForTests == 0 &&
                second.ActiveImageWorkCountForTests == 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiringQueuedImageOrSessionDoesNotStartNativeDecode(bool retireSession)
    {
        var gate = MarkdownImagePreparationAdmission.Shared;
        using IDisposable held = await gate.EnterAsync(new object(), 1, exclusive: true, CancellationToken.None);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var image = CreateImage(new Bytes(CreatePng(64, 32), Guid.NewGuid().ToString()), session);
        int publications = 0;
        image.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);
        try
        {
            image.EnsureLoading();
            await WaitUntilAsync(() => gate.PendingRequests == 1);
            if (retireSession)
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            else
                image.Dispose();
            await WaitUntilAsync(() => gate.PendingRequests == 0 && image.ActiveImageWorkCountForTests == 0);
            Assert.Null(image.Bitmap);
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Equal(1, gate.ActiveCount); // Only the deliberately held lease remains.
            held.Dispose();
            Assert.Equal(0, gate.ActiveBytes);
        }
        finally { image.Dispose(); held.Dispose(); }
    }

    [Fact]
    public async Task WarmBitmapReuseDoesNotEnterTheOccupiedCodecGate()
    {
        string key = Guid.NewGuid().ToString();
        var source = new Bytes(CreatePng(64, 32), key);
        var first = CreateImage(source, null);
        var second = CreateImage(source, null);
        try
        {
            await WaitForLoadAsync(first);
            var gate = MarkdownImagePreparationAdmission.Shared;
            using IDisposable held = await gate.EnterAsync(new object(), 1, exclusive: true, CancellationToken.None);
            await WaitForLoadAsync(second);
            Assert.Same(first.Bitmap, second.Bitmap);
            Assert.Equal(0, gate.PendingRequests);
            Assert.Equal(1, gate.ActiveCount);
        }
        finally { first.Dispose(); second.Dispose(); }
    }

    [Fact]
    public async Task SessionRetirementCancelsAndDrainsThePreCpuKeyedWait()
    {
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { UseDisplaySizedRasterDecode = false });
        string key = Guid.NewGuid().ToString();
        var context = CreateContext(new Bytes(CreatePng(64, 32), key), session, 1);
        SemaphoreSlim keyedGate = SharedCanvasBitmapCache.GetDecodeGate(
            context.ResourceCreator.Device, BoundedSourceSizedCacheKey(key));
        await keyedGate.WaitAsync();
        var image = new ImageBox(context, "https://images.example/keyed-cancel.png", string.Empty);
        int publications = 0;
        image.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);
        try
        {
            image.Measure(64);
            image.EnsureLoading();
            var activeWork = typeof(MarkdownPerformanceSession).GetField("_activeWork",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests >= 2 &&
                (int)activeWork.GetValue(session)! == 1);
            Assert.Equal(0, session.GetSnapshot().CpuPreparations);
            Assert.Equal(0, MarkdownImagePreparationAdmission.Shared.PendingRequests);

            // The external keyed blocker stays held. Only the linked session
            // token can end this wait; CPU admission has not been entered yet.
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, (int)activeWork.GetValue(session)!);
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
            Assert.Null(image.Bitmap);
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Equal(0, keyedGate.CurrentCount);
        }
        finally { image.Dispose(); keyedGate.Release(); }
    }

    [Fact]
    public async Task RasterLifetimeBarrierKeepsSessionDrainOpenUntilFinalCleanup()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var service = (IMarkdownPerformanceSessionInternal)session;
        IDisposable barrier = service.TryBeginRasterWork()!;
        Assert.NotNull(barrier);
        using IDisposable cpu = await service.EnterCpuPreparationAsync(new object(), false, CancellationToken.None);
        Task retirement = session.DisposeAsync().AsTask();
        Assert.True(service.LifetimeToken.IsCancellationRequested);
        cpu.Dispose();
        Assert.False(retirement.IsCompleted);
        Assert.Null(service.TryBeginRasterWork());
        barrier.Dispose();
        barrier.Dispose(); // Cleanup callbacks can race retirement acknowledgement.
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MalformedNativeDecodeCleansUpBothAdmissionsBeforePublicationAndRecovers()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        // This has an admitted PNG header, but no pixel payload or IEND.
        byte[] malformed = CreatePng(64, 32)[..33];
        Assert.True(RasterImageResourceBudget.Validate(malformed).Accepted);
        var image = CreateImage(new Bytes(malformed, Guid.NewGuid().ToString()), session);
        var recovered = CreateImage(new Bytes(CreatePng(64, 32), Guid.NewGuid().ToString()), session);
        bool observedPublication = false;
        image.LoadCompleted += (_, _) =>
        {
            Assert.Null(image.Bitmap);
            Assert.Equal(0, MarkdownImagePreparationAdmission.Shared.ActiveCount);
            Assert.Equal(0, MarkdownImagePreparationAdmission.Shared.ActiveBytes);
            Assert.Equal(0, session.PendingCpuPreparations);
            observedPublication = true;
        };
        try
        {
            await WaitForLoadAsync(image);
            Assert.True(observedPublication);
            await WaitForLoadAsync(recovered);
            Assert.NotNull(recovered.Bitmap);
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { image.Dispose(); recovered.Dispose(); }
    }

    private static ImageBox CreateImage(IMarkdownImageResolver source,
        MarkdownPerformanceSession? session, bool hasDocumentOwner = true)
    {
        var image = new ImageBox(CreateContext(source, session, 1, hasDocumentOwner),
            "https://images.example/process-admission.png", string.Empty);
        image.Measure(64);
        return image;
    }

    // Disabling display-size optimization does not disable the opt-in live
    // output ceiling. These fixtures must hold/collide on the bounded variant
    // actually used by production, not the legacy unbounded source key.
    private static string BoundedSourceSizedCacheKey(string sourceIdentity)
    {
        var sizing = new RasterImageSizingRequest(
            64, 1, null, null,
            System.Math.Min(MarkdownPerformanceOptions.Progressive.MaxRasterOutputPixels,
                BitmapOutputAdmission.Shared.MaximumSingleOutputBytes / 4));
        // These admitted, identity-oriented PNG fixtures use the owned WIC
        // variant. Hold/collide its actual stripe, not the retired WinRT key.
        return sizing.GetCacheKey(sizing.GetCacheFamily(
            sourceIdentity + RasterNativeWicDecoder.CachePartition, displaySized: false), (64, 32));
    }

    private sealed class Bytes(byte[] bytes, string key) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(string source,
            MarkdownImageResolveContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/png", CacheKey: key)));
    }
}
