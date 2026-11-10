using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>How an SVG image box holds, releases and re-acquires its resolver source bytes.</summary>
/// <remarks>
/// This file is longer than 500 lines because each source-lease rule (eviction and re-entry,
/// partitioned metadata, release before an uncooperative render, admission before decode and
/// open-handoff retirement) is its own gated-provider scenario with ordered waits, and the
/// scenarios share one context, cleanup and wait toolkit kept at the end of the class.
/// </remarks>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSourceLeaseTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task SvgBoxDoesNotPinEvictedSourceAndReentryUsesAuthorizedResolver()
    {
        string sourceUrl = $"https://example.test/{Guid.NewGuid():N}.svg";
        byte[] sourceTemplate = CreateSvgBytes("cache-eviction-source");
        var resolver = new LeasedSvgResolver(() =>
        {
            var bytes = new byte[sourceTemplate.Length];
            Buffer.BlockCopy(sourceTemplate, 0, bytes, 0, bytes.Length);
            return bytes;
        }, sourceTemplate.Length);
        var session = CreateSession(sourceTemplate.Length + 128L);
        IMarkdownPerformanceDocumentScope documentScope =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(
                resolver,
                new MarkdownImageResolveContext(null));
        var renderer = new SourceLeaseGatedSvgRenderer(blockDocumentDispose: true);
        var image = new ImageBox(
            CreateContext(renderer, session, documentScope), _renderResources.Images,
            sourceUrl,
            "Evicted SVG source");

        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            Task firstLoad = WaitForLoadAsync(image);
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            renderer.ReleaseFirstOpen.TrySetResult();
            await firstLoad.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.DocumentDisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() =>
                session.GetSnapshot().InFlightSourceBytes == 0);
            Assert.Equal(sourceTemplate.Length, renderer.RetainedDocumentSourceBytes);
            Assert.Equal(0, renderer.DocumentDisposeCount);

            AssertEvictedSourceIsCollectible(image, renderer, sourceTemplate.Length);

            // Provider disposal clears its independent snapshot.
            renderer.ReleaseDocumentDisposals();
            await renderer.DocumentDisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() =>
                renderer.RetainedDocumentSourceBytes == 0 &&
                image.GetWorkState().ActiveWorkCount == 0);
            Assert.Equal(1, renderer.DocumentDisposeCount);

            await ReenterEvictedSourceAsync(image, renderer, session);

            Assert.Equal(2, resolver.BufferAllocationCount);
            Assert.Equal(2, renderer.DocumentDisposeCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            renderer.ReleaseAllOpens();
            await RunCleanupsAsync(
                primaryFailure,
                () => RetireImageAsync(image),
                () => DisposeDocumentScopeAsync(documentScope),
                () => DisposeSessionAsync(session));
        }
    }

    private void AssertEvictedSourceIsCollectible(
        ImageBox image,
        SourceLeaseGatedSvgRenderer renderer,
        int sourceLength)
    {
        // The accepted source is held only weakly; the collection check
        // below proves no other box state pins the source array.
        WeakReference<byte[]> sourceIdentity = Assert.IsType<WeakReference<byte[]>>(
            image.Svg.SourceIdentity);
        Assert.True(IsAlive(sourceIdentity));

        // The fake provider document owns a sanitized OpenAsync snapshot.
        // Its copy remains alive during retirement but does not alias the
        // ImageBox source array, which must be collectible after eviction.
        int evicted = RemoveSvgCacheEntries(image);
        // Resolver sources have one content entry, never a built-in alias.
        Assert.Equal(1, evicted);
        ForceCollection();
        Assert.Equal(sourceLength, renderer.RetainedDocumentSourceBytes);
        Assert.False(IsAlive(sourceIdentity));
    }

    private static async Task ReenterEvictedSourceAsync(
        ImageBox image,
        SourceLeaseGatedSvgRenderer renderer,
        MarkdownPerformanceSession session)
    {
        // Release the old exact raster/document and re-enter the same box.
        // With the source entry evicted, reopening must use the existing
        // authorized resolver path rather than a box-owned byte[] pin.
        image.SuspendResources();
        await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);
        if (!image.TryReleaseSuspendedResources())
        {
            await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);
            Assert.True(image.TryReleaseSuspendedResources());
        }
        Assert.True(image.TryResumeResources());

        // Measure a different target so the exact-size bitmap warm path
        // cannot satisfy re-entry. WaitForLoadAsync then calls
        // EnsureLoading against the evicted source cache entry.
        image.Measure(12);
        Task secondLoad = WaitForLoadAsync(image);
        await renderer.SecondOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        renderer.ReleaseSecondOpen.TrySetResult();
        await secondLoad.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() =>
            session.GetSnapshot().InFlightSourceBytes == 0 &&
            renderer.RetainedDocumentSourceBytes == 0 &&
            image.GetWorkState().ActiveWorkCount == 0);
    }

    [Fact]
    public async Task SvgMetadataFastPathRequiresCurrentSecurityPartition()
    {
        byte[] source = CreateSvgBytes("partition-bound-metadata");
        var session = CreateSession(source.Length + 128L);
        var image = new ImageBox(
            CreateContext(new SourceLeaseGatedSvgRenderer(), session), _renderResources.Images,
            "https://example.test/partition.svg",
            "Partitioned SVG");

        try
        {
            ImageBoxTestState.AdoptSvgMetadata(
                image,
                image.Context.SvgRenderer!,
                info: new MarkdownSvgDocumentInfo(24, 12, 2),
                sourceIdentity: new WeakReference<byte[]>(source),
                securityPartitionHash: HashSecurityPartition("https://example.test/trust-domain-a"));

            Assert.True(image.Svg.IsKnownSource(
                source,
                HashSecurityPartition("https://example.test/trust-domain-a")));
            Assert.False(image.Svg.IsKnownSource(
                source,
                HashSecurityPartition("https://example.test/trust-domain-b")));
        }
        finally
        {
            await RunCleanupsAsync(
                primaryFailure: null,
                () => RetireImageAsync(image),
                () => DisposeSessionAsync(session));
        }
    }

    [Fact]
    public async Task QueuedSvgPublicationUsesWeakSourceMetadataCarrier()
    {
        await using var fixture = new QueuedSvgPublicationFixture();

        // Whole bitmap: the completed render retires its provider document
        // whether or not the queued UI publication ever runs.
        var (bitmapDocument, bitmapInfo, bitmapUse, bitmapDocumentDisposed) = fixture.OpenCandidateDocument();
        var (bitmapMetadata, bitmapSource) = fixture.CreateMetadata(bitmapInfo, "queued-bitmap");
        Action queuedBitmap = fixture.Queue(fixture.Image.SvgPublication.CreateBitmapPublication(
            bitmapMetadata, new SharedCanvasBitmapCache.Lease()));
        fixture.RetireCandidateDocument(bitmapDocument, bitmapUse);
        await bitmapDocumentDisposed.WaitAsync(TimeSpan.FromSeconds(10));

        // Tiled: the queued publication must own its candidate document and
        // use, but still only a weak source identity.
        var tiledDocument = new PublicationCommitSvgDocument();
        var tiledUse = (SvgDocumentUseLease)SvgPublicationCommitTestOperations.AcquireCandidate(
            fixture.Image, tiledDocument);
        var (tiledMetadata, tiledSource) = fixture.CreateMetadata(tiledDocument.Info, "queued-tiled");
        Action queuedTiled = fixture.Queue(fixture.Image.SvgPublication.CreateTiledPublication(
            tiledMetadata, tiledDocument, tiledUse));

        RetentionCollector.CollectUntilDead(bitmapSource, bitmapDocument, tiledSource);

        Assert.False(bitmapSource.IsAlive, "A queued bitmap publication must not keep its source array alive.");
        Assert.False(bitmapDocument.IsAlive, "A queued bitmap publication must not keep its provider document alive.");
        Assert.False(tiledSource.IsAlive, "A queued tiled publication must not keep its source array alive.");
        Assert.False(bitmapMetadata.SourceIdentity.TryGetTarget(out _));
        Assert.False(tiledMetadata.SourceIdentity.TryGetTarget(out _));
        Assert.Equal(0, tiledDocument.DisposeCount);
        GC.KeepAlive(queuedBitmap);
        GC.KeepAlive(queuedTiled);

        // Deliver both queued publications so their work units drain before
        // the fixture retires the box.
        queuedTiled();
        queuedBitmap();
    }

    [Fact]
    public async Task ResolverSourceLeaseAndImageRetirementWaitForActualSvgOpenCompletion()
    {
        byte[] sourceBytes = CreateSvgBytes("leased-resolver");
        var resolver = new LeasedSvgResolver(() => sourceBytes, sourceBytes.Length);
        var session = CreateSession(sourceBytes.Length + 128L);
        var resolveContext = new MarkdownImageResolveContext(null);
        IMarkdownPerformanceDocumentScope documentScope =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, resolveContext);
        var renderer = new SourceLeaseGatedSvgRenderer();
        var image = new ImageBox(
            CreateContext(renderer, session, documentScope), _renderResources.Images,
            "https://example.test/leased.svg",
            "Leased SVG");

        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(sourceBytes.Length, session.GetSnapshot().InFlightSourceBytes);
            image.Dispose();
            await renderer.FirstOpenCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(image.RetirementTask.IsCompleted);
            Assert.Equal(sourceBytes.Length, session.GetSnapshot().InFlightSourceBytes);

            renderer.ReleaseFirstOpen.TrySetResult();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(renderer.FirstOpenReturned.Task.IsCompletedSuccessfully);
            Assert.Equal(1, renderer.DocumentDisposeCount);
            Assert.Equal(0, renderer.RenderCount);
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            renderer.ReleaseAllOpens();
            await RunCleanupsAsync(
                primaryFailure,
                () => RetireImageAsync(image),
                () => DisposeDocumentScopeAsync(documentScope),
                () => DisposeSessionAsync(session));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalResolverSourceIsReleasedBeforeUncooperativeRender(bool hasSession)
    {
        byte[] sourceTemplate = CreateSvgBytes($"open-render-{Guid.NewGuid():N}");
        WeakReference<byte[]>? originalSource = null;
        var originalSources = new List<WeakReference<byte[]>>();
        int sourceCreationCount = 0;
        byte[] MakeSource()
        {
            byte[] bytes = (byte[])sourceTemplate.Clone();
            var weakSource = new WeakReference<byte[]>(bytes);
            originalSources.Add(weakSource);
            originalSource = weakSource;
            Interlocked.Increment(ref sourceCreationCount);
            return bytes;
        }
        MarkdownPerformanceSession? session = hasSession ? CreateSession(sourceTemplate.Length + 128L) : null;
        IMarkdownPerformanceDocumentScope? scope = session is null ? null :
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(
                new LeasedSvgResolver(MakeSource, sourceTemplate.Length), new MarkdownImageResolveContext(null));
        IMarkdownImageResolver resolver = scope ?? (IMarkdownImageResolver)new GeneratedSvgResolver(MakeSource);
        var renderer = new SourceLeaseGatedSvgRenderer(blockRender: true, distinctPreparation: true);
        var image = new ImageBox(CreateContext(renderer, session, resolver), _renderResources.Images,
            $"https://example.test/{Guid.NewGuid():N}.svg", "Source release before render");
        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (session is not null)
                Assert.Equal(sourceTemplate.Length, session.GetSnapshot().InFlightSourceBytes);
            Assert.True(IsAlive(Assert.IsType<WeakReference<byte[]>>(originalSource)));

            renderer.ReleaseFirstOpen.TrySetResult();
            await renderer.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // RenderAsync is deliberately blocked. Wait until ImageBox has
            // registered its continuation on that pending ValueTask, proving
            // the provider method returned and the consumer is suspended.
            await renderer.RenderAwaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The open-result continuation may run the source-owning async
            // frames inline all the way through RenderAsync. Wait for that
            // continuation to return before collecting, not merely for its
            // first nested await registration.
            await renderer.FirstOpenContinuationReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (session is not null)
                Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
            AssertOriginalSourcesCollected(
                renderer, image, resolver, originalSources, () => Volatile.Read(ref sourceCreationCount));
            Assert.Equal(1, renderer.RenderCount);
            Assert.Equal(0, renderer.DocumentDisposeCount);
            Assert.Equal(sourceTemplate.Length, renderer.RetainedDocumentSourceBytes);

            image.Dispose();
            await renderer.RenderCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(image.RetirementTask.IsCompleted);
            Assert.Equal(0, renderer.DocumentDisposeCount);
            if (session is not null)
                Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
            renderer.ReleaseRender.TrySetResult();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, renderer.DocumentDisposeCount);
            Assert.Equal(0, renderer.RetainedDocumentSourceBytes);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            renderer.ReleaseAllOpens();
            await RunCleanupsAsync(primaryFailure,
                () => RetireImageAsync(image),
                () => scope is null ? Task.CompletedTask : DisposeDocumentScopeAsync(scope),
                () => session is null ? Task.CompletedTask : DisposeSessionAsync(session));
        }
    }

    private static void AssertOriginalSourcesCollected(
        SourceLeaseGatedSvgRenderer renderer,
        ImageBox image,
        IMarkdownImageResolver resolver,
        List<WeakReference<byte[]>> originalSources,
        Func<int> sourceCreationCount)
    {
        ForceCollection();
        int retainedSourceCount = originalSources.Count(IsAlive);
        // The renderer, image and resolver stay rooted here, so a retained
        // source means one of them or an async frame still holds it.
        GC.KeepAlive(renderer);
        GC.KeepAlive(image);
        GC.KeepAlive(resolver);
        Assert.True(retainedSourceCount == 0,
            "Original source must not remain in caller/Open async frames while Render is pending." +
            $" Retained source arrays: {retainedSourceCount}; resolver source creations: " +
            $"{sourceCreationCount()}; Open/PrepareSource/Render counts: " +
            $"{renderer.OpenCount}/{renderer.PrepareSourceCount}/{renderer.RenderCount}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltInSourceReservationIsReleasedBeforeUncooperativeRender(bool dataUri)
    {
        byte[] template = CreateSvgBytes($"builtin-open-render-{Guid.NewGuid():N}");
        string source = dataUri
            ? "data:image/svg+xml," + Uri.EscapeDataString(Encoding.UTF8.GetString(template))
            : new Uri(FindStaticSvgFixture()).AbsoluteUri;
        long sourceLength = dataUri ? template.Length : new FileInfo(new Uri(source).LocalPath).Length;
        var session = CreateSession(sourceLength + 128L);
        var renderer = new SourceLeaseGatedSvgRenderer(blockRender: true, distinctPreparation: true);
        var image = new ImageBox(CreateContext(renderer, session), _renderResources.Images, source, "Built-in source release");
        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(sourceLength, session.GetSnapshot().InFlightSourceBytes);
            renderer.ReleaseFirstOpen.TrySetResult();
            await renderer.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
            Assert.Equal(0, renderer.DocumentDisposeCount);
            image.Dispose();
            await renderer.RenderCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(image.RetirementTask.IsCompleted);
            Assert.Equal(0, renderer.DocumentDisposeCount);
            renderer.ReleaseRender.TrySetResult();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, renderer.DocumentDisposeCount);
            Assert.Equal(0, renderer.RetainedDocumentSourceBytes);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            renderer.ReleaseAllOpens();
            await RunCleanupsAsync(primaryFailure,
                () => RetireImageAsync(image), () => DisposeSessionAsync(session));
        }
    }

    private static string FindStaticSvgFixture()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "MarkdownRenderer", "tests", "MarkdownRenderer.Pixel.Tests",
                "Fixtures", "svg", "18-data-uris", "simple.svg");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("The committed offline SVG fixture could not be located.");
    }

    [Fact]
    public async Task RawSvgDataUriIsAdmittedBeforeDecodeAndHeldThroughActualOpen()
    {
        byte[] sourceBytes = CreateSvgBytes("leased-data-uri");
        string dataUri = "data:image/svg+xml," + Uri.EscapeDataString(Encoding.UTF8.GetString(sourceBytes));
        int decodedLength = MarkdownDataUriDecoder.GetDecodedLength(dataUri, 1024 * 1024);
        var session = CreateSession(sourceBytes.Length + 128L);
        var renderer = new SourceLeaseGatedSvgRenderer();
        var image = new ImageBox(
            CreateContext(renderer, session), _renderResources.Images,
            dataUri,
            "Data URI SVG");

        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(decodedLength, session.GetSnapshot().InFlightSourceBytes);
            image.Dispose();
            await renderer.FirstOpenCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(image.RetirementTask.IsCompleted);
            Assert.Equal(decodedLength, session.GetSnapshot().InFlightSourceBytes);

            renderer.ReleaseFirstOpen.TrySetResult();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(renderer.FirstOpenReturned.Task.IsCompletedSuccessfully);
            Assert.Equal(1, renderer.DocumentDisposeCount);
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            renderer.ReleaseAllOpens();
            await RunCleanupsAsync(
                primaryFailure,
                () => RetireImageAsync(image),
                () => DisposeSessionAsync(session));
        }
    }

    [Fact]
    public async Task LocalSvgBeyondAdmissionCapacityIsRejectedBeforeProviderSeesBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"markdown-source-{Guid.NewGuid():N}.svg");
        byte[] sourceBytes = CreateSvgBytes("local-source-capacity");
        await File.WriteAllBytesAsync(path, sourceBytes);
        var session = CreateSession(System.Math.Max(8, sourceBytes.Length / 2));
        var renderer = new SourceLeaseGatedSvgRenderer();
        var image = new ImageBox(
            CreateContext(renderer, session), _renderResources.Images,
            new Uri(path).AbsoluteUri,
            "Oversized local SVG");

        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            Task loaded = WaitForLoadAsync(image);
            await loaded;

            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await RunCleanupsAsync(
                primaryFailure,
                () => RetireImageAsync(image),
                () => DisposeSessionAsync(session),
                () =>
                {
                    File.Delete(path);
                    return Task.CompletedTask;
                });
        }
    }

    [Fact]
    public async Task UnconsumedOpenHandoffRetiresItsDocumentAndWorkExactlyOnce()
    {
        var renderer = new SourceLeaseGatedSvgRenderer();
        renderer.ReleaseFirstOpen.TrySetResult();
        var image = new ImageBox(CreateContext(renderer, session: null), _renderResources.Images,
            $"https://example.test/pending-{Guid.NewGuid():N}.svg", "Pending SVG");
        IDisposable? openedWork = null;
        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            var opener = new SvgSourceOpener(image.SourceLoadHost, image.Context, _renderResources.Images,
                image.OutputAdmission, image.OutputDeferral, image.Url);
            SvgOpenStageResult result = await opener.OpenAsync(CreateSvgBytes("unconsumed-handoff"),
                    default, image.Url, null,
                    new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible), 0)
                .WaitAsync(TimeSpan.FromSeconds(5));
            openedWork = Assert.IsAssignableFrom<IDisposable>(result.Work);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(0, renderer.RenderCount);
            Assert.Equal(0, renderer.DocumentDisposeCount);

            openedWork.Dispose();
            openedWork.Dispose();
            await renderer.DocumentDisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, renderer.DocumentDisposeCount);
            Assert.Equal(0, renderer.RetainedDocumentSourceBytes);
            Assert.Equal(0, renderer.RenderCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            openedWork?.Dispose();
            renderer.ReleaseAllOpens();
            await RunCleanupsAsync(primaryFailure, () => RetireImageAsync(image));
        }
    }

    [Fact]
    public async Task WaitingForSourceAdmissionDoesNotAllocateTheSecondResolverBuffer()
    {
        byte[] sourceTemplate = CreateSvgBytes("waiting-source-buffer");
        int capacity = (sourceTemplate.Length * 2) - 1;
        var resolver = new LeasedSvgResolver(() =>
        {
            var bytes = new byte[sourceTemplate.Length];
            Buffer.BlockCopy(sourceTemplate, 0, bytes, 0, bytes.Length);
            return bytes;
        }, sourceTemplate.Length);
        var session = CreateSession(capacity);
        var resolveContext = new MarkdownImageResolveContext(null);
        IMarkdownPerformanceDocumentScope documentScope =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, resolveContext);
        var renderer = new SourceLeaseGatedSvgRenderer();
        var first = new ImageBox(
            CreateContext(renderer, session, documentScope), _renderResources.Images,
            "https://example.test/first.svg",
            "First SVG");
        var second = new ImageBox(
            CreateContext(renderer, session, documentScope), _renderResources.Images,
            "https://example.test/second.svg",
            "Second SVG");

        Exception? primaryFailure = null;
        try
        {
            first.Measure(80);
            first.EnsureLoading();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, resolver.BufferAllocationCount);
            Assert.Equal(sourceTemplate.Length, session.GetSnapshot().InFlightSourceBytes);

            second.Measure(80);
            Task secondLoaded = WaitForLoadAsync(second);
            await WaitUntilAsync(() => session.GetSnapshot().PendingSourceByteRequests > 0);

            Assert.Equal(1, resolver.BufferAllocationCount);
            Assert.Equal(sourceTemplate.Length, session.GetSnapshot().InFlightSourceBytes);

            first.Dispose();
            await renderer.FirstOpenCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.RetirementTask.IsCompleted);
            Assert.Equal(1, resolver.BufferAllocationCount);

            renderer.ReleaseFirstOpen.TrySetResult();
            await first.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.SecondOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, resolver.BufferAllocationCount);
            Assert.Equal(sourceTemplate.Length, session.GetSnapshot().InFlightSourceBytes);

            renderer.ReleaseSecondOpen.TrySetResult();
            await secondLoaded.WaitAsync(TimeSpan.FromSeconds(5));
            // Publication can notify LoadCompleted before the resolver's using
            // scope exits. Require the bounded downstream drain, not an ordering
            // between two continuations racing on the thread pool.
            await WaitUntilAsync(() => session.GetSnapshot().InFlightSourceBytes == 0);
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            renderer.ReleaseAllOpens();
            await RunCleanupsAsync(
                primaryFailure,
                () => RetireImageAsync(first),
                () => RetireImageAsync(second),
                () => DisposeDocumentScopeAsync(documentScope),
                () => DisposeSessionAsync(session));
        }
    }

    private static MarkdownPerformanceSession CreateSession(long sourceBytes) =>
        new(MarkdownPerformanceOptions.Progressive with
        {
            MaxConcurrentImageFetches = 4,
            ReservedVisibleImageFetches = 1,
            MaxInFlightSourceBytes = sourceBytes,
            ReservedVisibleSourceBytes = System.Math.Max(1, sourceBytes / 4),
            SourceCacheBudgetBytes = 0,
        });

    private MarkdownLayoutContext CreateContext(
        IMarkdownSvgRenderer renderer,
        MarkdownPerformanceSession? session,
        IMarkdownImageResolver? resolver = null)
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 14,
            Foreground = Color.FromArgb(255, 24, 24, 24),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
            [MarkdownElementKeys.ListMarker] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = _renderResources,
            ImageResolver = resolver,
            PerformanceSession = session,
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static byte[] CreateSvgBytes(string title) => Encoding.UTF8.GetBytes(
        $"<svg xmlns='http://www.w3.org/2000/svg' width='24' height='12'>" +
        $"<title>{title}</title><rect width='24' height='12' fill='#336699'/></svg>");

    private static async Task WaitForLoadAsync(ImageBox image)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completion.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            image.EnsureLoading();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static string HashSecurityPartition(string partition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partition)));

    private int RemoveSvgCacheEntries(ImageBox image)
    {
        string activeCacheKey = Assert.IsType<string>(image.LoadState.ActiveCacheKey);
        string contentHash = Assert.IsType<string>(image.Svg.ContentHash);
        return _renderResources.Images.SvgSources.RemoveWhere(key =>
            string.Equals(key, activeCacheKey, StringComparison.Ordinal) ||
            key.Contains($":content={contentHash}:", StringComparison.Ordinal));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> sourceIdentity) =>
        sourceIdentity.TryGetTarget(out _);

    private static async Task RetireImageAsync(ImageBox image)
    {
        try
        {
            image.Dispose();
        }
        finally
        {
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Task DisposeDocumentScopeAsync(IMarkdownPerformanceDocumentScope scope)
    {
        scope.Dispose();
        return Task.CompletedTask;
    }

    private static async Task DisposeSessionAsync(MarkdownPerformanceSession session) =>
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task RunCleanupsAsync(
        Exception? primaryFailure,
        params Func<Task>[] cleanups)
    {
        List<Exception>? cleanupFailures = null;
        foreach (Func<Task> cleanup in cleanups)
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= new List<Exception>()).Add(exception);
            }
        }

        if (cleanupFailures is null || primaryFailure is not null)
            return;

        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        throw new AggregateException("ImageBox test cleanup failed.", cleanupFailures);
    }

    private sealed class GeneratedSvgResolver(Func<byte[]> createBytes) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(string source,
            MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(createBytes(), "image/svg+xml", CacheKey: source)));
        }
    }
}
