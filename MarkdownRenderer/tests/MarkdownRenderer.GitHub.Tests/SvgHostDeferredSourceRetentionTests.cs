using System.Runtime.CompilerServices;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class SvgHostDeferredSourceRetentionTests
{
    [Fact]
    public async Task CompletedImageBoxCapacityDenialDoesNotRetainResolverSourceBytes()
    {
        var admission = new CapacityDeniedAdmission();
        var resolver = new GeneratedSvgResolver();
        var renderer = new CapacityDeniedRenderer(admission);
        var image = new ImageBox(
            CreateContext(renderer, resolver),
            "https://example.test/oversized-source.svg",
            alt: string.Empty);

        try
        {
            image.EnsureLoading();
            await admission.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await admission.CapacityWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            WeakReference<byte[]> source = Assert.IsType<WeakReference<byte[]>>(resolver.LastSource);
            Assert.Equal(Encoding.UTF8.GetByteCount(resolver.Svg), admission.ObservedSourceLength);
            Assert.Null(image.SvgFailureReason);
            Assert.False(image.ImageLifetimeDisposedForTests);
            AssertSourceReleased(source);

            GC.KeepAlive(image);
            GC.KeepAlive(admission);
            GC.KeepAlive(resolver);
            GC.KeepAlive(renderer);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static MarkdownLayoutContext CreateContext(
        IMarkdownSvgRenderer renderer,
        IMarkdownImageResolver resolver)
    {
        var body = new ElementStyle
        {
            Foreground = Color.FromArgb(255, 12, 34, 56),
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
            isDark: true,
            isHighContrast: false,
            textScaleFactor: 1);

        // This test stops at typed source-capacity deferral. No measurement,
        // bitmap creation, drawing, or SVG worker is involved.
        return new MarkdownLayoutContext(
            null!,
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = resolver,
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = 1,
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertSourceReleased(WeakReference<byte[]> source)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!source.TryGetTarget(out _))
                return;
        }

        Assert.False(source.TryGetTarget(out _),
            "A completed ImageBox capacity-deferral task retained the resolver's raw SVG byte array.");
    }

    private sealed class CapacityDeniedAdmission : IMarkdownSvgAdmission
    {
        private readonly TaskCompletionSource _capacityWaitStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _openStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public object AdmissionDomain { get; } = new();
        public long CapacityVersion => 0;
        internal TaskCompletionSource CapacityWaitStarted => _capacityWaitStarted;
        internal TaskCompletionSource OpenStarted => _openStarted;
        internal int ObservedSourceLength { get; private set; }

        public Task WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken)
        {
            Assert.Equal(1024, requiredFreeBytes);
            Assert.Equal(MarkdownSvgRenderPriority.Visible, priority);
            _capacityWaitStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
            MarkdownSvgOpenRequest request,
            MarkdownSvgRenderDemand demand,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _openStarted.TrySetResult();
            ObservedSourceLength = request.SanitizedSvgBytes.Length;
            return ValueTask.FromResult(MarkdownSvgAdmissionResult.CapacityUnavailable(
                capacityVersion: 0,
                requiredSourceReservationBytes: 1024));
        }
    }

    private sealed class GeneratedSvgResolver : IMarkdownImageResolver
    {
        internal string Svg { get; } =
            $"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'><rect id='test-{Guid.NewGuid():N}' width='16' height='8'/></svg>";

        private WeakReference<byte[]>? _lastSource;

        internal WeakReference<byte[]>? LastSource => Volatile.Read(ref _lastSource);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = Encoding.UTF8.GetBytes(Svg);
            Volatile.Write(ref _lastSource, new WeakReference<byte[]>(bytes));
            var asset = new MarkdownImageAsset(bytes, "image/svg+xml", CacheKey: string.Empty);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(asset));
        }
    }

    private sealed class CapacityDeniedRenderer(CapacityDeniedAdmission admission) :
        IMarkdownSvgRenderer,
        IMarkdownSvgAdmission
    {
        object IMarkdownSvgAdmission.AdmissionDomain => admission.AdmissionDomain;
        long IMarkdownSvgAdmission.CapacityVersion => admission.CapacityVersion;

        Task IMarkdownSvgAdmission.WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken) =>
            admission.WaitForCapacityAsync(requiredFreeBytes, priority, cancellationToken);

        ValueTask<MarkdownSvgAdmissionResult> IMarkdownSvgAdmission.TryOpenAsync(
            MarkdownSvgOpenRequest request,
            MarkdownSvgRenderDemand demand,
            CancellationToken cancellationToken) =>
            admission.TryOpenAsync(request, demand, cancellationToken);

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IMarkdownSvgDocument>(
                new InvalidOperationException("Host-capable opens must use the admission interface."));
    }
}
