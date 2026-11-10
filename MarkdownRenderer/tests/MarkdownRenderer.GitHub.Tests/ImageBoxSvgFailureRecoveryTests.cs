using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks SVG failures and recovery: provider failures and runtime
/// resource limits do not poison later renderers or generations, prepared
/// source limits stay cached, and missing providers or blocked nested
/// images become accessible failures.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgFailureRecoveryTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task ProviderFailureDoesNotPoisonReplacementRenderer()
    {
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        var rejectingRenderer = new RecoveringSvgRenderer(info, reject: true);
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var rejected = new ImageBox(CreateContext(_renderResources, rejectingRenderer), _renderResources.Images, source, "rejected");
        rejected.Measure(80);
        await WaitForLoadAsync(rejected);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejected.SvgFailureReason);
        rejected.Dispose();

        var replacementRenderer = new RecordingSvgRenderer(info);
        var replacement = new ImageBox(CreateContext(_renderResources, replacementRenderer), _renderResources.Images, source, "replacement");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.Equal(1, replacementRenderer.OpenCount);
            Assert.Single(replacementRenderer.RenderRequests);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public async Task ProviderFailureDoesNotPoisonLaterGeneration()
    {
        var renderer = new RecoveringSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            reject: true);
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var rejected = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "generation zero");
        rejected.Measure(80);
        await WaitForLoadAsync(rejected);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejected.SvgFailureReason);
        rejected.Dispose();

        renderer.EnableAndAdvanceGeneration();
        var recovered = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "generation one");
        try
        {
            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.NotNull(recovered.Bitmap);
            Assert.Null(recovered.SvgFailureReason);
            Assert.Single(renderer.RenderRequests);
        }
        finally
        {
            recovered.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeOpenResourceLimitDoesNotPoisonSameRendererAndGeneration()
    {
        var renderer = new TransientResourceLimitSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            TransientResourceLimitStage.Open);
        string source = CreateSvgDataUri($"runtime-open-limit-{Guid.NewGuid():N}");
        var rejected = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "runtime open limit");

        try
        {
            rejected.Measure(80);
            await WaitForLoadAsync(rejected);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, rejected.SvgFailureReason);
            Assert.Null(rejected.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(0, renderer.RenderCount);
        }
        finally
        {
            rejected.Dispose();
        }

        var recovered = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "runtime open retry");
        try
        {
            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.Null(recovered.SvgFailureReason);
            Assert.NotNull(recovered.Bitmap);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(2, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
        }
        finally
        {
            recovered.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeFirstRenderResourceLimitDoesNotPoisonSameRendererAndGeneration()
    {
        var renderer = new TransientResourceLimitSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            TransientResourceLimitStage.Render);
        string source = CreateSvgDataUri($"runtime-render-limit-{Guid.NewGuid():N}");
        var rejected = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "runtime render limit");

        try
        {
            rejected.Measure(80);
            await WaitForLoadAsync(rejected);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, rejected.SvgFailureReason);
            Assert.Null(rejected.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
        }
        finally
        {
            rejected.Dispose();
        }

        var recovered = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "runtime render retry");
        try
        {
            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.Null(recovered.SvgFailureReason);
            Assert.NotNull(recovered.Bitmap);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(2, renderer.OpenCount);
            Assert.Equal(2, renderer.RenderCount);
        }
        finally
        {
            recovered.Dispose();
        }
    }

    [Fact]
    public async Task ExplicitPrepareSourceResourceLimitRemainsCachedAcrossImageBoxes()
    {
        var renderer = new TransientResourceLimitSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            TransientResourceLimitStage.PrepareSource);
        string source = CreateSvgDataUri($"source-budget-limit-{Guid.NewGuid():N}");
        var rejected = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "source budget limit");

        try
        {
            rejected.Measure(80);
            await WaitForLoadAsync(rejected);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, rejected.SvgFailureReason);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
        }
        finally
        {
            rejected.Dispose();
        }

        // The provider admits this second preparation. The same renderer,
        // generation and source must still observe the earlier deterministic
        // content rejection before an OpenAsync call can begin.
        var cached = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "cached source budget limit");
        try
        {
            cached.Measure(80);
            await WaitForLoadAsync(cached);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, cached.SvgFailureReason);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Null(cached.Bitmap);
        }
        finally
        {
            cached.Dispose();
        }
    }

    [Fact]
    public async Task MissingProviderProducesTypedAccessibleFailure()
    {
        var unavailable = new ConcurrentQueue<MarkdownSvgFailureReason?>();
        var image = new ImageBox(
            CreateContext(_renderResources, renderer: null, imageUnavailable: (_, _, svgReason) => unavailable.Enqueue(svgReason)), _renderResources.Images,
            CreateSvgDataUri("missing-provider"),
            "Unavailable diagram");
        try
        {
            image.Measure(120);
            await WaitForLoadAsync(image);

            Assert.Equal(
                MarkdownSvgFailureReason.ArchitectureMismatch,
                image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
            Assert.Contains("No static SVG renderer", image.SvgDesc, StringComparison.Ordinal);
            Assert.Equal(MarkdownSvgFailureReason.ArchitectureMismatch, Assert.Single(unavailable));
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task BlockedNestedImageBecomesAccessibleFailureWithoutFirstChanceException()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2))
        {
            SourcePreparationOverride = MarkdownSvgSourcePreparation.Reject(
                MarkdownSvgFailureReason.UnsupportedContent,
                "The SVG was rejected by host preflight (external-image-reference)."),
        };
        string svg =
            "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='40'>" +
            "<image href='https://example.test/avatar.png' width='40' height='40'/>" +
            "</svg>";
        string source = "data:image/svg+xml," + Uri.EscapeDataString(svg);
        int firstChancePreflightExceptions = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception is MarkdownSvgException exception &&
                exception.Message.Contains(
                    "host preflight (external-image-reference)",
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref firstChancePreflightExceptions);
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        var image = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "Contributor avatars");
        try
        {
            image.Measure(120);
            await WaitForLoadAsync(image);

            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
            Assert.Contains("external-image-reference", image.SvgDesc, StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref firstChancePreflightExceptions));
        }
        finally
        {
            image.Dispose();
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }
    }

    private enum TransientResourceLimitStage
    {
        PrepareSource,
        Open,
        Render,
    }

    private sealed class TransientResourceLimitSvgRenderer(
        MarkdownSvgDocumentInfo info,
        TransientResourceLimitStage failureStage) : IMarkdownSvgRenderer
    {
        private int _prepareSourceCount;
        private int _openCount;
        private int _renderCount;

        internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int RenderCount => Volatile.Read(ref _renderCount);
        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref _prepareSourceCount);
            if (failureStage == TransientResourceLimitStage.PrepareSource && ordinal == 1)
            {
                return MarkdownSvgSourcePreparation.Reject(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The test provider rejected an oversized SVG source.");
            }

            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref _openCount);
            if (failureStage == TransientResourceLimitStage.Open && ordinal == 1)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The test worker queue was temporarily full during OpenAsync.");
            }

            return ValueTask.FromResult<IMarkdownSvgDocument>(
                new TransientResourceLimitSvgDocument(info, this));
        }

        internal int RecordRenderAndShouldFail()
        {
            int ordinal = Interlocked.Increment(ref _renderCount);
            if (failureStage == TransientResourceLimitStage.Render && ordinal == 1)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The test worker queue was temporarily full during RenderAsync.");
            }

            return ordinal;
        }
    }

    private sealed class TransientResourceLimitSvgDocument(
        MarkdownSvgDocumentInfo info,
        TransientResourceLimitSvgRenderer owner) : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = owner.RecordRenderAndShouldFail();

            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int length = checked(width * height * 4);
            IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
            memory.Memory.Span[..length].Clear();
            return ValueTask.FromResult(new MarkdownSvgRaster(
                memory,
                length,
                width,
                height,
                checked(width * 4),
                request.PixelFormat));
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecoveringSvgRenderer(
        MarkdownSvgDocumentInfo info,
        bool reject)
        : IMarkdownSvgRenderer
    {
        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        private long _cacheGeneration;
        private int _reject = reject ? 1 : 0;

        public long CacheGeneration => Volatile.Read(ref _cacheGeneration);

        internal ConcurrentQueue<MarkdownSvgRenderRequest> RenderRequests { get; } = new();

        internal void EnableAndAdvanceGeneration()
        {
            Volatile.Write(ref _reject, 0);
            Interlocked.Increment(ref _cacheGeneration);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _reject) != 0)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.UnsupportedContent,
                    "Rejected by the test provider.");
            }

            return ValueTask.FromResult<IMarkdownSvgDocument>(
                new RecordingSvgDocument(info, RenderRequests));
        }
    }
}
