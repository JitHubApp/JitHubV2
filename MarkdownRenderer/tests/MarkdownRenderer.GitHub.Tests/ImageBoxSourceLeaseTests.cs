using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSourceLeaseTests
{
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
        var renderer = new GatedSvgRenderer(blockDocumentDispose: true);
        var image = new ImageBox(
            CreateContext(renderer, session, documentScope),
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

            FieldInfo? obsoleteStrongSource = typeof(ImageBox).GetField(
                "_svgRawBytes",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.Null(obsoleteStrongSource);
            FieldInfo sourceIdentityField = typeof(ImageBox).GetField(
                "_svgSourceIdentity",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(typeof(WeakReference<byte[]>), sourceIdentityField.FieldType);
            var sourceIdentity = Assert.IsType<WeakReference<byte[]>>(
                sourceIdentityField.GetValue(image));
            Assert.True(IsAlive(sourceIdentity));

            // The fake provider document owns a sanitized OpenAsync snapshot.
            // Its copy remains alive during retirement but does not alias the
            // ImageBox source array, which must be collectible after eviction.
            int evicted = RemoveSvgCacheEntries(image);
            // Resolver sources have one content entry, never a built-in alias.
            Assert.Equal(1, evicted);
            ForceCollection();
            Assert.Equal(sourceTemplate.Length, renderer.RetainedDocumentSourceBytes);
            Assert.False(IsAlive(sourceIdentity));

            // Provider disposal clears its independent snapshot.
            renderer.ReleaseDocumentDisposals();
            await renderer.DocumentDisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() =>
                renderer.RetainedDocumentSourceBytes == 0 &&
                GetPrivateField<int>(image, "_svgWorkCount") == 0);
            Assert.Equal(1, renderer.DocumentDisposeCount);

            // Release the old exact raster/document and re-enter the same box.
            // With the source entry evicted, reopening must use the existing
            // authorized resolver path rather than a box-owned byte[] pin.
            image.SuspendResources();
            await WaitUntilAsync(() => GetPrivateField<int>(image, "_svgWorkCount") == 0);
            if (!image.TryReleaseSuspendedResources())
            {
                await WaitUntilAsync(() => GetPrivateField<int>(image, "_svgWorkCount") == 0);
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
                GetPrivateField<int>(image, "_svgWorkCount") == 0);

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

    [Fact]
    public async Task SvgMetadataFastPathRequiresCurrentSecurityPartition()
    {
        byte[] source = CreateSvgBytes("partition-bound-metadata");
        var session = CreateSession(source.Length + 128L);
        var image = new ImageBox(
            CreateContext(new GatedSvgRenderer(), session),
            "https://example.test/partition.svg",
            "Partitioned SVG");

        try
        {
            SetPrivateField(image, "_svgSourceIdentity", new WeakReference<byte[]>(source));
            SetPrivateField(image, "_svgInfo", new MarkdownSvgDocumentInfo(24, 12, 2));
            SetPrivateField(
                image,
                "_svgSecurityPartitionHash",
                HashSecurityPartition("https://example.test/trust-domain-a"));

            MethodInfo isKnownSource = typeof(ImageBox).GetMethod(
                "IsKnownSvgSource",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(Assert.IsType<bool>(isKnownSource.Invoke(image, [
                source,
                HashSecurityPartition("https://example.test/trust-domain-a")])));
            Assert.False(Assert.IsType<bool>(isKnownSource.Invoke(image, [
                source,
                HashSecurityPartition("https://example.test/trust-domain-b")])));
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
    public void QueuedSvgPublicationUsesWeakSourceMetadataCarrier()
    {
        Type metadata = typeof(ImageBox).GetNestedType(
            "SvgPublicationMetadata",
            BindingFlags.NonPublic)!;
        FieldInfo[] fields = metadata.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Contains(fields, field => field.FieldType == typeof(WeakReference<byte[]>));
        Assert.Contains(fields, field => field.FieldType == typeof(long));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(byte[]));
        Assert.DoesNotContain(fields, field => field.FieldType.Name.Contains(
            "SvgCacheEntry",
            StringComparison.Ordinal));

        foreach (string methodName in new[]
        {
            "CreateTiledSvgPublication",
            "CreateBitmapSvgPublication",
        })
        {
            MethodInfo method = typeof(ImageBox).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.DoesNotContain(method.GetParameters(), parameter =>
                parameter.ParameterType == typeof(byte[]) ||
                parameter.ParameterType.Name.Contains("SvgCacheEntry", StringComparison.Ordinal));
        }

        MethodInfo bitmapPublication = typeof(ImageBox).GetMethod(
            "CreateBitmapSvgPublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.DoesNotContain(bitmapPublication.GetParameters(), parameter =>
            typeof(IMarkdownSvgDocument).IsAssignableFrom(parameter.ParameterType));

        Type metadataType = typeof(ImageBox).GetNestedType(
            "SvgPublicationMetadata",
            BindingFlags.NonPublic)!;
        Type bitmapLeaseType = Assert.Single(bitmapPublication.GetParameters(), parameter =>
            parameter.ParameterType.Name == "Lease").ParameterType;
        Type[] bitmapPublicationClosures = typeof(ImageBox)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Where(type =>
            {
                FieldInfo[] closureFields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                // Inspect the queued publication callback owner, not the
                // separate Render state machine which must own a live document.
                return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(method => method.Name.StartsWith("<CreateBitmapSvgPublication>", StringComparison.Ordinal)) &&
                    closureFields.Any(field => field.FieldType == metadataType) &&
                    closureFields.Any(field => field.FieldType == bitmapLeaseType);
            })
            .ToArray();
        Assert.NotEmpty(bitmapPublicationClosures);
        Assert.All(bitmapPublicationClosures, closure =>
            Assert.DoesNotContain(
                closure.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                field => typeof(IMarkdownSvgDocument).IsAssignableFrom(field.FieldType)));
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
        var renderer = new GatedSvgRenderer();
        var image = new ImageBox(
            CreateContext(renderer, session, documentScope),
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
        var renderer = new GatedSvgRenderer(blockRender: true, distinctPreparation: true);
        var image = new ImageBox(CreateContext(renderer, session, resolver),
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
            ForceCollection();
            int retainedSourceCount = originalSources.Count(IsAlive);
            bool sourceRetained = retainedSourceCount != 0;
            string retentionPath = sourceRetained
                ? DescribeStrongSourceRetention(renderer.ReleaseRender.Task, renderer, image, resolver, originalSources)
                : string.Empty;
            Assert.False(sourceRetained,
                "Original source must not remain in caller/Open async frames while Render is pending." +
                (retentionPath.Length == 0 ? string.Empty :
                    $" Retained source arrays: {retainedSourceCount}; resolver source creations: " +
                    $"{Volatile.Read(ref sourceCreationCount)}; Open/PrepareSource/Render counts: " +
                    $"{renderer.OpenCount}/{renderer.PrepareSourceCount}/{renderer.RenderCount}; " +
                    $"retention path: {retentionPath}"));
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
        var renderer = new GatedSvgRenderer(blockRender: true, distinctPreparation: true);
        var image = new ImageBox(CreateContext(renderer, session), source, "Built-in source release");
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
        var renderer = new GatedSvgRenderer();
        var image = new ImageBox(
            CreateContext(renderer, session),
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
        var renderer = new GatedSvgRenderer();
        var image = new ImageBox(
            CreateContext(renderer, session),
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
        var renderer = new GatedSvgRenderer();
        renderer.ReleaseFirstOpen.TrySetResult();
        var image = new ImageBox(CreateContext(renderer, session: null),
            $"https://example.test/pending-{Guid.NewGuid():N}.svg", "Pending SVG");
        IDisposable? openedWork = null;
        Exception? primaryFailure = null;
        try
        {
            image.Measure(80);
            MethodInfo open = typeof(ImageBox).GetMethod("OpenSvgSourceAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object operation = open.Invoke(image, [CreateSvgBytes("unconsumed-handoff"),
                default(Windows.Foundation.Size), image.Url, null,
                new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible), 0])!;
            await ((Task)operation).WaitAsync(TimeSpan.FromSeconds(5));
            object result = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
            openedWork = Assert.IsAssignableFrom<IDisposable>(
                result.GetType().GetProperty("Work")!.GetValue(result));
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
        var renderer = new GatedSvgRenderer();
        var first = new ImageBox(
            CreateContext(renderer, session, documentScope),
            "https://example.test/first.svg",
            "First SVG");
        var second = new ImageBox(
            CreateContext(renderer, session, documentScope),
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

    private static MarkdownLayoutContext CreateContext(
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

    private static T GetPrivateField<T>(ImageBox image, string name)
    {
        FieldInfo field = typeof(ImageBox).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(image));
    }

    private static void SetPrivateField(ImageBox image, string name, object value)
    {
        FieldInfo field = typeof(ImageBox).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(image, value);
    }

    private static string HashSecurityPartition(string partition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partition)));

    private static int RemoveSvgCacheEntries(ImageBox image)
    {
        string activeCacheKey = GetPrivateField<string>(image, "_activeCacheKey");
        string contentHash = GetPrivateField<string>(image, "_svgContentHash");
        FieldInfo cacheField = typeof(ImageBox).GetField(
            "_svgCache",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object cache = cacheField.GetValue(null)!;
        Predicate<string> matches = key =>
            string.Equals(key, activeCacheKey, StringComparison.Ordinal) ||
            key.Contains($":content={contentHash}:", StringComparison.Ordinal);
        MethodInfo removeWhere = cache.GetType().GetMethod(
            "RemoveWhere",
            BindingFlags.Instance | BindingFlags.Public)!;
        return Assert.IsType<int>(removeWhere.Invoke(cache, [matches]));
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DescribeStrongSourceRetention(
        Task renderReleaseTask,
        GatedSvgRenderer renderer,
        ImageBox image,
        IMarkdownImageResolver resolver,
        IReadOnlyList<WeakReference<byte[]>> sourceIdentities)
    {
        var liveTargets = new List<(int Index, byte[] Target)>();
        for (int index = 0; index < sourceIdentities.Count; index++)
        {
            if (sourceIdentities[index].TryGetTarget(out byte[]? target) && target is not null)
                liveTargets.Add((index, target));
        }
        if (liveTargets.Count == 0)
            return "all targets collected before bounded scan";

        const int maxDepth = 32;
        const int maxNodes = 2_048;
        var pending = new PriorityQueue<(object Value, int Depth, string Path), int>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((renderReleaseTask, 0, "$release-task:" + renderReleaseTask.GetType().Name), 0);
        pending.Enqueue((renderer, 0, "$renderer:" + renderer.GetType().Name), 0);
        pending.Enqueue((image, 0, "$image:" + image.GetType().Name), 0);
        pending.Enqueue((resolver, 0, "$resolver:" + resolver.GetType().Name), -1);
        int visitedCount = 0;

        while (pending.Count > 0 && visitedCount < maxNodes)
        {
            (object value, int depth, string path) = pending.Dequeue();
            int directTargetIndex = liveTargets.FindIndex(item => ReferenceEquals(value, item.Target));
            if (directTargetIndex >= 0)
            {
                string foundPath = $"source[{liveTargets[directTargetIndex].Index}] via {path}";
                foreach (var item in liveTargets)
                    GC.KeepAlive(item.Target);
                return foundPath;
            }

            if (depth >= maxDepth || value is string || value is Type || value is MemberInfo ||
                value is Assembly || value is Module || value is byte[] || !IsAsyncOwnershipNode(value))
            {
                continue;
            }

            if (!value.GetType().IsValueType && !visited.Add(value))
                continue;
            visitedCount++;

            if (value is Array array)
            {
                int take = System.Math.Min(array.Length,
                    System.Math.Max(0, maxNodes - visitedCount - pending.Count));
                for (int index = 0; index < take; index++)
                {
                    object? child;
                    try { child = array.GetValue(index); }
                    catch { continue; }
                    if (child is null)
                        continue;
                    string childPath = $"{path}[{index}]:{child.GetType().Name}";
                    int targetIndex = liveTargets.FindIndex(item => ReferenceEquals(child, item.Target));
                    if (targetIndex >= 0)
                    {
                        string foundPath = $"source[{liveTargets[targetIndex].Index}] via {childPath}";
                        foreach (var item in liveTargets)
                            GC.KeepAlive(item.Target);
                        return foundPath;
                    }
                    if (visitedCount + pending.Count < maxNodes)
                        pending.Enqueue((child, depth + 1, childPath), GetNodePriority(child, $"[{index}]"));
                }
                continue;
            }

            for (Type? type = value.GetType(); type is not null &&
                 visitedCount + pending.Count < maxNodes; type = type.BaseType)
            {
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                foreach (FieldInfo field in fields.OrderBy(static field => GetFieldPriority(field.Name)))
                {
                    object? child;
                    try { child = field.GetValue(value); }
                    catch { continue; }
                    if (child is null)
                        continue;

                    string childPath = $"{path}.{type.Name}.{field.Name}:{child.GetType().Name}";
                    int targetIndex = liveTargets.FindIndex(item => ReferenceEquals(child, item.Target));
                    if (targetIndex >= 0)
                    {
                        string foundPath = $"source[{liveTargets[targetIndex].Index}] via {childPath}";
                        foreach (var item in liveTargets)
                            GC.KeepAlive(item.Target);
                        return foundPath;
                    }
                    if (child is string || child is Type || child is MemberInfo || child is Assembly ||
                        child is Module || child is byte[])
                    {
                        continue;
                    }
                    if (visitedCount + pending.Count < maxNodes)
                        pending.Enqueue((child, depth + 1, childPath), GetNodePriority(child, field.Name));
                }
            }
        }

        foreach (var item in liveTargets)
            GC.KeepAlive(item.Target);
        return $"no direct path found from provided roots (visited {visitedCount} objects; " +
            $"live targets {liveTargets.Count}; depth limit {maxDepth}; node limit {maxNodes})";
    }

    private static bool IsAsyncOwnershipNode(object value)
    {
        Type type = value.GetType();
        if (value is Task or Delegate or Array || type.IsValueType ||
            type == typeof(MarkdownImageResolution) || type == typeof(MarkdownImageAsset))
        {
            return true;
        }

        string? typeName = type.FullName;
        return value is not MarkdownLayoutContext &&
               typeName?.StartsWith("Microsoft.Graphics.Canvas.", StringComparison.Ordinal) != true &&
               typeName?.StartsWith("Microsoft.UI.", StringComparison.Ordinal) != true &&
               typeName?.StartsWith("Windows.", StringComparison.Ordinal) != true;
    }

    private static int GetNodePriority(object value, string fieldName)
    {
        int fieldPriority = GetFieldPriority(fieldName);
        if (value is Delegate || value.GetType().IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            return 0;
        if (value is Task or Delegate || value.GetType().IsValueType ||
            value.GetType().IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            return fieldPriority;
        }

        return fieldPriority + 1;
    }

    private static int GetFieldPriority(string fieldName)
    {
        if (fieldName.Contains("result", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("asset", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("resolution", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("source", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("rawBytes", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fieldName.Contains("continuation", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("action", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("awaiter", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("stateMachine", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("stateObject", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("u__", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 4;
    }

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

    private sealed class LeasedSvgResolver(Func<byte[]> createBytes, int byteLength) : IMarkdownLeasedImageResolver
    {
        private int _bufferAllocationCount;

        internal int BufferAllocationCount => Volatile.Read(ref _bufferAllocationCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.NotHandled);

        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            IDisposable reservation = await admission.ReserveAsync(byteLength, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                byte[] bytes = createBytes();
                if (bytes.Length != byteLength)
                    throw new InvalidOperationException("The test resolver returned a buffer of an unexpected size.");
                Interlocked.Increment(ref _bufferAllocationCount);
                var result = MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                    bytes,
                    "image/svg+xml",
                    ResolvedUri: new Uri(source),
                    CacheKey: source));
                return new MarkdownImageResolutionLease(result, reservation);
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }
    }

    private sealed class GatedSvgRenderer : IMarkdownSvgRenderer
    {
        private readonly bool _blockDocumentDispose;
        private int _prepareSourceCount;
        private int _openCount;
        private int _documentDisposeCount;
        private int _retainedDocumentSourceBytes;

        private readonly bool _blockRender;
        private readonly bool _distinctPreparation;
        private int _renderCount;

        internal GatedSvgRenderer(bool blockDocumentDispose = false, bool blockRender = false,
            bool distinctPreparation = false)
        {
            _blockDocumentDispose = blockDocumentDispose;
            _blockRender = blockRender;
            _distinctPreparation = distinctPreparation;
        }

        internal TaskCompletionSource RenderStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RenderAwaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RenderCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRender { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RenderCount => Volatile.Read(ref _renderCount);

        internal TaskCompletionSource FirstOpenStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstOpenCanceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstOpenReturned { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstOpenContinuationReturned { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondOpenStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstOpen { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecondOpen { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DocumentDisposeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DocumentDisposeCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource ReleaseDocumentDispose { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int DocumentDisposeCount => Volatile.Read(ref _documentDisposeCount);
        internal int RetainedDocumentSourceBytes => Volatile.Read(ref _retainedDocumentSourceBytes);

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _prepareSourceCount);
            return MarkdownSvgSourcePreparation.Admit(_distinctPreparation ? (byte[])source.Clone() : source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            int open = Interlocked.Increment(ref _openCount);
            CancellationTokenRegistration registration = cancellationToken.Register(
                () => FirstOpenCanceled.TrySetResult());
            TaskCompletionSource release = open == 1 ? ReleaseFirstOpen : ReleaseSecondOpen;
            if (open == 1)
                FirstOpenStarted.TrySetResult();
            else
                SecondOpenStarted.TrySetResult();

            // The provider deliberately ignores cancellation until its actual
            // OpenAsync transaction returns; it may still be reading request bytes.
            var pending = new PendingOpenValueTaskSource(
                this, request, release.Task, registration, open);
            return pending.AsValueTask();
        }

        private sealed class PendingOpenValueTaskSource :
            System.Threading.Tasks.Sources.IValueTaskSource<IMarkdownSvgDocument>
        {
            private readonly GatedSvgRenderer _owner;
            private System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<IMarkdownSvgDocument> _core;
            private MarkdownSvgOpenRequest? _request;
            private readonly Task _release;
            private readonly TaskCompletionSource _consumerRegistered = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration _registration;
            private readonly int _open;

            internal PendingOpenValueTaskSource(
                GatedSvgRenderer owner,
                MarkdownSvgOpenRequest request,
                Task release,
                CancellationTokenRegistration registration,
                int open)
            {
                _owner = owner;
                _request = request;
                _release = release;
                _registration = registration;
                _open = open;
                _core = new System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<IMarkdownSvgDocument>
                {
                    RunContinuationsAsynchronously = true,
                };
                _ = CompleteAfterReleaseAsync();
            }

            internal ValueTask<IMarkdownSvgDocument> AsValueTask() => new(this, _core.Version);

            public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) =>
                _core.GetStatus(token);

            public IMarkdownSvgDocument GetResult(short token) => _core.GetResult(token);

            public void OnCompleted(
                Action<object?> continuation,
                object? state,
                short token,
                System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
            {
                var registration = new ContinuationRegistration(this, continuation, state);
                _core.OnCompleted(
                    static callbackState =>
                    {
                        var callback = (ContinuationRegistration)callbackState!;
                        try
                        {
                            callback.Continuation(callback.State);
                        }
                        finally
                        {
                            if (callback.Owner._open == 1)
                                callback.Owner._owner.FirstOpenContinuationReturned.TrySetResult();
                        }
                    },
                    registration,
                    token,
                    flags);
                _consumerRegistered.TrySetResult();
            }

            private async Task CompleteAfterReleaseAsync()
            {
                try
                {
                    await _release.ConfigureAwait(false);
                    // ReleaseFirstOpen can race the caller's first await. Keep
                    // the provider completion pending until the source-backed
                    // ValueTask has a registered consumer callback, so the
                    // callback-return barrier below cannot be skipped.
                    await _consumerRegistered.Task.ConfigureAwait(false);
                    MarkdownSvgOpenRequest? request = _request;
                    if (request is null)
                        throw new InvalidOperationException("The pending SVG open lost its request.");

                    byte[] providerSnapshot = request.SanitizedSvgBytes.ToArray();
                    // The provider owns only its independent copy after this
                    // point. Clear both the field and this local before waking
                    // any consumer continuation.
                    _request = null;
                    request = null;
                    Interlocked.Add(ref _owner._retainedDocumentSourceBytes, providerSnapshot.Length);
                    var document = new GatedSvgDocument(_owner, providerSnapshot);
                    if (_open == 1)
                        _owner.FirstOpenReturned.TrySetResult();
                    _core.SetResult(document);
                }
                catch (Exception exception)
                {
                    _request = null;
                    _core.SetException(exception);
                }
                finally
                {
                    _registration.Dispose();
                    _registration = default;
                }
            }

            private sealed record ContinuationRegistration(
                PendingOpenValueTaskSource Owner,
                Action<object?> Continuation,
                object? State);
        }

        internal void ReleaseAllOpens()
        {
            ReleaseFirstOpen.TrySetResult();
            ReleaseSecondOpen.TrySetResult();
            ReleaseRender.TrySetResult();
            ReleaseDocumentDisposals();
        }

        internal void ReleaseDocumentDisposals() => ReleaseDocumentDispose.TrySetResult();

        private void RecordDocumentDisposal(int retainedBytes)
        {
            Interlocked.Add(ref _retainedDocumentSourceBytes, -retainedBytes);
            Interlocked.Increment(ref _documentDisposeCount);
            DocumentDisposeCompleted.TrySetResult();
        }

        private sealed class GatedSvgDocument(
            GatedSvgRenderer owner,
            byte[] sourceSnapshot) : IMarkdownSvgDocument
        {
            private byte[]? _sourceSnapshot = sourceSnapshot;

            public MarkdownSvgDocumentInfo Info { get; } = new(
                IntrinsicWidthDips: 24,
                IntrinsicHeightDips: 12,
                IntrinsicAspectRatio: 2);

            public ValueTask<MarkdownSvgRaster> RenderAsync(
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._renderCount);
                owner.RenderStarted.TrySetResult();
                if (owner._blockRender)
                {
                    var pending = new PendingRenderValueTaskSource(owner, request, cancellationToken);
                    return pending.AsValueTask();
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => owner.RenderCanceled.TrySetResult());
                cancellationToken.ThrowIfCancellationRequested();
                int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
                int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
                int length = checked(width * height * 4);
                IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
                bool ownershipTransferred = false;
                try
                {
                    memory.Memory.Span[..length].Clear();
                    var raster = new MarkdownSvgRaster(
                        memory,
                        length,
                        width,
                        height,
                        checked(width * 4),
                        request.PixelFormat);
                    ownershipTransferred = true;
                    return ValueTask.FromResult(raster);
                }
                finally
                {
                    if (!ownershipTransferred)
                        memory.Dispose();
                }
            }

            private sealed class PendingRenderValueTaskSource :
                System.Threading.Tasks.Sources.IValueTaskSource<MarkdownSvgRaster>
            {
                private readonly GatedSvgRenderer _owner;
                private System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<MarkdownSvgRaster> _core;
                private readonly CancellationTokenRegistration _registration;

                internal PendingRenderValueTaskSource(
                    GatedSvgRenderer owner,
                    MarkdownSvgRenderRequest request,
                    CancellationToken cancellationToken)
                {
                    _owner = owner;
                    _core = new System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore<MarkdownSvgRaster>
                    {
                        RunContinuationsAsynchronously = true,
                    };
                    _registration = cancellationToken.Register(
                        static state => ((GatedSvgRenderer)state!).RenderCanceled.TrySetResult(),
                        owner);
                    _ = CompleteAfterReleaseAsync(request, cancellationToken);
                }

                internal ValueTask<MarkdownSvgRaster> AsValueTask() => new(this, _core.Version);

                public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) =>
                    _core.GetStatus(token);

                public MarkdownSvgRaster GetResult(short token) => _core.GetResult(token);

                public void OnCompleted(
                    Action<object?> continuation,
                    object? state,
                    short token,
                    System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags)
                {
                    _core.OnCompleted(continuation, state, token, flags);
                    _owner.RenderAwaitRegistered.TrySetResult();
                }

                private async Task CompleteAfterReleaseAsync(
                    MarkdownSvgRenderRequest request,
                    CancellationToken cancellationToken)
                {
                    try
                    {
                        await _owner.ReleaseRender.Task.ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
                        int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
                        int length = checked(width * height * 4);
                        IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
                        bool ownershipTransferred = false;
                        try
                        {
                            memory.Memory.Span[..length].Clear();
                            var raster = new MarkdownSvgRaster(
                                memory,
                                length,
                                width,
                                height,
                                checked(width * 4),
                                request.PixelFormat);
                            ownershipTransferred = true;
                            _core.SetResult(raster);
                        }
                        finally
                        {
                            if (!ownershipTransferred)
                                memory.Dispose();
                        }
                    }
                    catch (Exception exception)
                    {
                        _core.SetException(exception);
                    }
                    finally
                    {
                        _registration.Dispose();
                    }
                }
            }

            public void Dispose()
            {
                owner.DocumentDisposeStarted.TrySetResult();
                if (owner._blockDocumentDispose)
                    owner.ReleaseDocumentDispose.Task.GetAwaiter().GetResult();

                int retainedBytes = Interlocked.Exchange(ref _sourceSnapshot, null)?.Length ?? 0;
                owner.RecordDocumentDisposal(retainedBytes);
            }
        }
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
