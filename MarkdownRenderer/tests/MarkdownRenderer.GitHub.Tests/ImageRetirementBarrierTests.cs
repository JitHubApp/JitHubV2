using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageRetirementBarrierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyImageRetirementLeavesCallerBeforeNativeCleanup(bool throwOnDispose)
    {
        var image = new ImageBox(CreateContext(renderer: null), "", "ready image");
        var document = new BlockingReadySvgDocument(throwOnDispose);
        SetPrivateField(image, "_svgDocument", document);
        var callerReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callerContext = new TrackingSynchronizationContext();
        int callerThreadId = 0;
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            callerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                image.Dispose();
                callerReturned.TrySetResult();
            }
            catch (Exception exception)
            {
                callerReturned.TrySetException(exception);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }) { IsBackground = true };

        caller.Start();
        try
        {
            await document.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // A completed cancellation task must not inline a blocking native
            // disposer on the caller or post it back to the caller's context.
            await callerReturned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(callerThreadId, document.DisposeThreadId);
            Assert.True(document.DisposedOnPoolThread);
            Assert.Null(document.DisposeContext);
            Assert.Equal(0, Volatile.Read(ref callerContext.PostCount));
            Assert.False(image.RetirementTask.IsCompleted);
            Assert.False(image.ImageLifetimeDisposedForTests);

            image.Dispose();
            document.ReleaseDispose.TrySetResult();
            if (throwOnDispose)
            {
                InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Same(document.DisposeFailure, failure);
            }
            else
            {
                await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.Equal(1, Volatile.Read(ref document.DisposeCount));
            Assert.True(image.ImageLifetimeDisposedForTests);
        }
        finally
        {
            document.ReleaseDispose.TrySetResult();
            await callerReturned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            image.Dispose();
            try
            {
                await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch when (image.RetirementTask.IsFaulted)
            {
                // Observe injected cleanup failures without replacing the
                // primary assertion failure or stranding the blocked disposer.
                _ = image.RetirementTask.Exception;
            }
        }
    }

    [Fact]
    public async Task SnapshotRetirementWaitsForNestedImageProviderAndPaintLease()
    {
        var renderer = new NonCooperativeSvgRenderer();
        MarkdownLayoutContext context = CreateContext(renderer);
        var cell = new InlineContainerBox(context, MarkdownElementKeys.Body) { BlockIndex = 1 };
        cell.Add(new InlineImageRun(context, "late SVG", CreateSvgDataUri()));
        ImageBox image = Assert.Single(cell.InlineImageRuns).Run.Image;
        image.Measure(80);
        image.EnsureLoading();

        var table = new TableBox(
            context,
            new[] { new[] { cell } },
            Array.Empty<InlineContainerBox[]>());
        var listContent = new StackBox();
        listContent.Add(table);
        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker) { BlockIndex = 2 };
        var listItem = new ListItemBox(marker, listContent, markerWidth: 20);
        var root = new StackBox();
        root.Add(listItem);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { root },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 100);

        await renderer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(snapshot.TryBeginPaint());
        Task retirement = snapshot.Retire();

        // Retirement is nonblocking on the caller and the disposer cannot
        // cancel/release this image while an admitted paint lease holds the
        // snapshot lock.
        Assert.False(retirement.IsCompleted);
        Thread.Sleep(50);
        Assert.False(renderer.Canceled.Task.IsCompleted);
        snapshot.EndPaint();

        await renderer.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(retirement.IsCompleted);
        Assert.False(image.ImageLifetimeDisposedForTests);

        renderer.ReleaseOpen.TrySetResult();
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, renderer.DocumentDisposeCount);
        Assert.True(image.ImageLifetimeDisposedForTests);
    }

    [Fact]
    public async Task AcceptedQueuedPublicationRemainsInRetirementBarrierUntilExactlyOnceDrop()
    {
        var image = new ImageBox(CreateContext(renderer: null), CreateSvgDataUri(), "queued publication");
        byte[] rawBytes = [1, 2, 3, 4];
        SetPrivateField(image, "_svgSourceIdentity", new WeakReference<byte[]>(rawBytes));
        SetPrivateField(image, "_svgTitle", "accessible title");
        SetPrivateField(image, "_svgDesc", "accessible description");
        Action? queuedPublication = null;
        int published = 0;
        int dropped = 0;

        image.PublishOnUiThread(
            () => Interlocked.Increment(ref published),
            () => Interlocked.Increment(ref dropped),
            callback =>
            {
                queuedPublication = callback;
                return true;
            });

        image.Dispose();
        Task retirement = image.RetirementTask;
        Assert.False(retirement.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref dropped));
        Assert.NotNull(GetPrivateField(image, "_svgSourceIdentity"));

        Action delivery = Assert.IsType<Action>(queuedPublication);
        delivery();
        delivery();
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, Volatile.Read(ref published));
        Assert.Equal(1, Volatile.Read(ref dropped));
        Assert.Null(GetPrivateField(image, "_svgSourceIdentity"));
        Assert.Equal("queued publication", image.Alt);
        Assert.Equal("accessible title", image.SvgTitle);
        Assert.Equal("accessible description", image.SvgDesc);
        Assert.Equal(CreateSvgDataUri(), image.Url);
    }

    [Fact]
    public async Task ImageRetirementWaitsForTileCancellationCallbacksBeforeDisposingSource()
    {
        var image = new ImageBox(CreateContext(renderer: null), "", "tile cancellation");
        using var source = new CancellationTokenSource();
        var callbackStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        using CancellationTokenRegistration registration = source.Token.Register(() =>
        {
            Interlocked.Increment(ref callbackCount);
            callbackStarted.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });

        MethodInfo retireSource = typeof(ImageBox).GetMethod(
            "RetireTileCancellationSource",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(typeof(ImageBox).FullName, "RetireTileCancellationSource");
        Task request = Task.Run(() => retireSource.Invoke(image, new object[] { source }));

        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // This would remain blocked if a tile cancellation path called
            // Cancel() synchronously instead of requesting asynchronous
            // cancellation and tracking the callback lifetime.
            await request.WaitAsync(TimeSpan.FromSeconds(10));

            image.Dispose();
            Task retirement = image.RetirementTask;
            Assert.False(retirement.IsCompleted);
            Assert.True(source.Token.IsCancellationRequested);

            releaseCallback.TrySetResult();
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, Volatile.Read(ref callbackCount));
            Assert.Throws<ObjectDisposedException>(() => _ = source.Token);
        }
        finally
        {
            releaseCallback.TrySetResult();
            try
            {
                await request.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the test's primary assertion failure; the request
                // task is observed here so a failed reflection invocation does
                // not become an unobserved test-runner task.
                _ = request.Exception;
            }
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RejectedQueuedPublicationDropsOwnedResourceOnceAndRetires()
    {
        var image = new ImageBox(CreateContext(renderer: null), "", "rejected publication");
        int dropped = 0;

        image.PublishOnUiThread(
            static () => { },
            () => Interlocked.Increment(ref dropped),
            static _ => false);

        image.Dispose();
        await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref dropped));
    }

    [Fact]
    public async Task ImageRetirementFaultsOnRejectedPublicationCleanupFailure()
    {
        var image = new ImageBox(CreateContext(renderer: null), "", "rejected cleanup");
        int dropped = 0;

        InvalidOperationException rejected = Assert.Throws<InvalidOperationException>(() =>
            image.PublishOnUiThread(
                static () => { },
                () =>
                {
                    Interlocked.Increment(ref dropped);
                    throw new InvalidOperationException("drop cleanup sentinel");
                },
                static _ => false));

        Assert.Equal("drop cleanup sentinel", rejected.Message);
        Assert.Equal(1, Volatile.Read(ref dropped));
        image.Dispose();

        InvalidOperationException retirement = await Assert.ThrowsAsync<InvalidOperationException>(
            () => image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("drop cleanup sentinel", retirement.Message);
    }

    [Fact]
    public async Task ResolverThatIgnoresCancellationRemainsInImageRetirementBarrier()
    {
        var resolver = new NonCooperativeImageResolver();
        var image = new ImageBox(
            CreateContext(renderer: null, resolver: resolver),
            "https://example.test/late.png",
            "late response");
        image.Measure(80);
        image.EnsureLoading();
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        image.Dispose();
        Task retirement = image.RetirementTask;
        Assert.False(retirement.IsCompleted);

        resolver.Release.TrySetResult();
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(image.ImageLifetimeDisposedForTests);
    }

    [Fact]
    public async Task SnapshotRetirementFaultsWhenLateSvgResultDisposalFails()
    {
        var renderer = new NonCooperativeSvgRenderer(throwOnDocumentDispose: true);
        MarkdownLayoutContext context = CreateContext(renderer);
        var image = new ImageBox(context, CreateSvgDataUri(), "late SVG cleanup failure");
        image.Measure(80);
        image.EnsureLoading();

        var root = new StackBox();
        root.Add(image);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { root },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 100);

        await renderer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task retirement = snapshot.Retire();
        await renderer.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        renderer.ReleaseOpen.TrySetResult();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => retirement.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("cleanup sentinel", failure.Message);
        Assert.Equal(1, renderer.DocumentDisposeCount);
    }

    [Fact]
    public async Task SnapshotRetirementFaultsWhenAdoptedSvgDocumentReleaseFails()
    {
        var renderer = new NonCooperativeSvgRenderer(throwOnDocumentDispose: true);
        MarkdownLayoutContext context = CreateContext(renderer);
        var image = new ImageBox(context, CreateSvgDataUri(), "adopted SVG cleanup failure");
        image.Measure(80);

        var root = new StackBox();
        root.Add(image);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { root },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 100);

        image.EnsureLoading();
        await renderer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        renderer.ReleaseOpen.TrySetResult();
        await renderer.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task retirement = snapshot.Retire();
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => retirement.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("cleanup sentinel", failure.Message);
        Assert.Equal(1, renderer.DocumentDisposeCount);
    }

    private static MarkdownLayoutContext CreateContext(
        IMarkdownSvgRenderer? renderer,
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
            [MarkdownElementKeys.ListMarker] = body,
            [MarkdownElementKeys.Table] = body,
            [MarkdownElementKeys.TableHeader] = body,
            [MarkdownElementKeys.TableCell] = body,
            [MarkdownElementKeys.ImageCaption] = body,
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
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = resolver,
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static string CreateSvgDataUri()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='24' height='12'><rect width='24' height='12'/></svg>";
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    private static void SetPrivateField<T>(ImageBox image, string name, T value)
    {
        FieldInfo field = typeof(ImageBox).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingFieldException(typeof(ImageBox).FullName, name);
        field.SetValue(image, value);
    }

    private static object? GetPrivateField(ImageBox image, string name)
    {
        FieldInfo field = typeof(ImageBox).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingFieldException(typeof(ImageBox).FullName, name);
        return field.GetValue(image);
    }

    private sealed class TrackingSynchronizationContext : SynchronizationContext
    {
        internal int PostCount;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref PostCount);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class BlockingReadySvgDocument(bool throwOnDispose) : IMarkdownSvgDocument
    {
        internal TaskCompletionSource DisposeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException DisposeFailure { get; } = new("ready cleanup sentinel");
        internal int DisposeCount;
        internal int DisposeThreadId;
        internal bool DisposedOnPoolThread;
        internal SynchronizationContext? DisposeContext;

        public MarkdownSvgDocumentInfo Info { get; } = new(
            IntrinsicWidthDips: 24,
            IntrinsicHeightDips: 12,
            IntrinsicAspectRatio: 2);

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The retirement test does not render.");

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            DisposeThreadId = Environment.CurrentManagedThreadId;
            DisposedOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            DisposeContext = SynchronizationContext.Current;
            DisposeStarted.TrySetResult();
            ReleaseDispose.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (throwOnDispose)
                throw DisposeFailure;
        }
    }

    private sealed class NonCooperativeSvgRenderer : IMarkdownSvgRenderer
    {
        private readonly bool _throwOnDocumentDispose;
        private int _documentDisposeCount;

        internal NonCooperativeSvgRenderer(bool throwOnDocumentDispose = false)
        {
            _throwOnDocumentDispose = throwOnDocumentDispose;
        }

        internal TaskCompletionSource Opened { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RenderStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseOpen { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DocumentDisposeCount => Volatile.Read(ref _documentDisposeCount);

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => Canceled.TrySetResult());
            Opened.TrySetResult();
            // Deliberately ignore cancellation: retirement must wait for this
            // operation and dispose its eventual, otherwise-unclaimed result.
            await ReleaseOpen.Task.ConfigureAwait(false);
            return new Document(this);
        }

        private sealed class Document(NonCooperativeSvgRenderer owner) : IMarkdownSvgDocument
        {
            public MarkdownSvgDocumentInfo Info { get; } = new(
                IntrinsicWidthDips: 24,
                IntrinsicHeightDips: 12,
                IntrinsicAspectRatio: 2);

            public ValueTask<MarkdownSvgRaster> RenderAsync(
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken = default)
            {
                owner.RenderStarted.TrySetResult();
                throw new NotSupportedException("The test renderer does not rasterize.");
            }

            public void Dispose()
            {
                Interlocked.Increment(ref owner._documentDisposeCount);
                if (owner._throwOnDocumentDispose)
                    throw new InvalidOperationException("cleanup sentinel");
            }
        }
    }

    private sealed class NonCooperativeImageResolver : IMarkdownImageResolver
    {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                new byte[] { 1, 2, 3 },
                ContentType: "image/png",
                CacheKey: "late-test-response"));
        }
    }
}
