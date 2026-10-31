using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
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
public sealed class SvgHostPreparationAdmissionTests
{
    private const long PreparationScratchBytes = 2L * 1024 * 1024;

    [Fact]
    public async Task HostPreparationScratchDenialDefersBeforeSourcePreparationAndHostOpen()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Denied,
        };
        var resolver = new GeneratedSvgResolver();
        var image = CreateImage(renderer, resolver, session);

        try
        {
            image.EnsureLoading();
            await renderer.CapacityWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            Assert.Equal(1, renderer.ScratchReservationCount);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Null(renderer.LastLease);
            Assert.Equal(PreparationScratchBytes, renderer.CapacityWaitRequiredBytes);
            Assert.Null(image.SvgFailureReason);
            AssertSourceReleased(Assert.IsType<WeakReference<byte[]>>(resolver.LastSource));
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    [Fact]
    public async Task OptionalPreparationAdmissionIsSkippedWithoutProgressiveSession()
    {
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Denied,
            Prepare = static (_, _) => MarkdownSvgSourcePreparation.Reject(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "Rejected by the test source policy."),
        };
        var image = CreateImage(renderer, new GeneratedSvgResolver(), session: null, needsCanvasFallback: true);
        using LoadObservation load = StartLoad(image);

        try
        {
            await load.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            Assert.Equal(0, renderer.ScratchReservationCount);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    [Fact]
    public async Task LegacyHostAdmissionStillPreparesAndOpensWithoutScratchReservation()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new LegacyHostRenderer();
        var image = CreateImage(renderer, new GeneratedSvgResolver(), session);

        try
        {
            image.EnsureLoading();
            await renderer.CapacityWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(4_096L, renderer.CapacityWaitRequiredBytes);
            Assert.Null(image.SvgFailureReason);
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    [Theory]
    [InlineData(1_048_576L)]
    [InlineData(4_194_304L)]
    public async Task DistinctPreparedBytesRemainAdmittedUntilOpenCompletesAndDenialWaitBegins(
        long providerRequiredBytes)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Granted,
            ScratchPeakBytes = PreparationScratchBytes,
            HoldOpenUntilReleased = true,
            OpenRequiredBytes = providerRequiredBytes,
        };
        string preparedSvg = GeneratedSvgResolver.CreateSvg(Guid.NewGuid().ToString("N"));
        renderer.Prepare = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            byte[] preparedBytes = Encoding.UTF8.GetBytes(preparedSvg);
            renderer.LastPreparedSource = new WeakReference<byte[]>(preparedBytes);
            return MarkdownSvgSourcePreparation.Admit(preparedBytes);
        };
        var resolver = new GeneratedSvgResolver();
        var image = CreateImage(renderer, resolver, session);

        try
        {
            image.EnsureLoading();
            await renderer.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            TrackingPreparationLease lease = Assert.IsType<TrackingPreparationLease>(renderer.LastLease);
            int preparedLength = Encoding.UTF8.GetByteCount(preparedSvg);
            Assert.Equal((long)preparedLength, renderer.OpenSourceLength);
            Assert.Equal(1, lease.SetLengthCount);
            Assert.Equal((long)preparedLength, lease.RetainedLength);
            Assert.Equal(0, lease.DisposeCount);
            Assert.False(renderer.CapacityWaitStarted.Task.IsCompleted);

            renderer.ReleaseOpen();
            await renderer.CapacityWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(0, renderer.OpenLeaseDisposeCount);
            Assert.Equal(1, renderer.CapacityWaitLeaseDisposeCount);
            Assert.Equal(1, lease.DisposeCount);
            Assert.Equal(
                System.Math.Max(PreparationScratchBytes, preparedLength + providerRequiredBytes),
                renderer.CapacityWaitRequiredBytes);
            Assert.Null(image.SvgFailureReason);

            AssertSourceReleased(Assert.IsType<WeakReference<byte[]>>(resolver.LastSource));
            AssertSourceReleased(Assert.IsType<WeakReference<byte[]>>(renderer.LastPreparedSource));
        }
        finally
        {
            renderer.ReleaseOpen();
            await RetireAsync(image);
        }
    }

    [Fact]
    public async Task PreparationLeaseIsReleasedExactlyOnceWhenPreparationAliasesResolverBytes()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Granted,
            ScratchPeakBytes = PreparationScratchBytes,
        };
        var image = CreateImage(renderer, new GeneratedSvgResolver(), session);

        try
        {
            image.EnsureLoading();
            await renderer.CapacityWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            TrackingPreparationLease lease = Assert.IsType<TrackingPreparationLease>(renderer.LastLease);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, lease.DisposeCount);
            Assert.Equal(0, lease.SetLengthCount);
            Assert.Equal(1, renderer.OpenLeaseDisposeCount);
            Assert.Equal(1, renderer.CapacityWaitLeaseDisposeCount);
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    [Fact]
    public async Task PreparationLeaseIsReleasedExactlyOnceWhenSourceIsRejected()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Granted,
            Prepare = static (_, _) => MarkdownSvgSourcePreparation.Reject(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "Rejected by the test source policy."),
        };
        var image = CreateImage(renderer, new GeneratedSvgResolver(), session, needsCanvasFallback: true);
        using LoadObservation load = StartLoad(image);

        try
        {
            await load.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            TrackingPreparationLease lease = Assert.IsType<TrackingPreparationLease>(renderer.LastLease);
            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, image.SvgFailureReason);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(1, lease.DisposeCount);
            Assert.Equal(0, lease.SetLengthCount);
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    [Fact]
    public async Task PreparationLeaseIsReleasedExactlyOnceWhenPrepareSourceThrows()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Granted,
            Prepare = static (_, _) => throw new InvalidOperationException("Synthetic preparation failure."),
        };
        var image = CreateImage(renderer, new GeneratedSvgResolver(), session, needsCanvasFallback: true);
        using LoadObservation load = StartLoad(image);

        try
        {
            await load.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            TrackingPreparationLease lease = Assert.IsType<TrackingPreparationLease>(renderer.LastLease);
            Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
            Assert.Null(image.Bitmap);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(1, lease.DisposeCount);
            Assert.Equal(0, lease.SetLengthCount);
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    [Fact]
    public async Task PreparationLeaseIsReleasedExactlyOnceWhenCancellationRacesAdmission()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using var cancellation = new CancellationTokenSource();
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Granted,
            AfterScratchLeaseCreated = cancellation.Cancel,
        };
        var image = CreateImage(
            renderer,
            new GeneratedSvgResolver(),
            session,
            cancellationToken: cancellation.Token);

        try
        {
            image.EnsureLoading();
            await WaitUntilAsync(() => renderer.LastLease?.DisposeCount == 1);
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            TrackingPreparationLease lease = Assert.IsType<TrackingPreparationLease>(renderer.LastLease);
            Assert.Equal(1, renderer.ScratchReservationCount);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(1, lease.DisposeCount);
            Assert.Null(image.SvgFailureReason);
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    [Fact]
    public async Task OpenedSvgKeepsProviderAndDocumentOwnersUntilCanceledRenderDrains()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = ScratchReply.Granted,
            ScratchPeakBytes = PreparationScratchBytes,
        };
        string preparedSvg = GeneratedSvgResolver.CreateSvg(Guid.NewGuid().ToString("N"));
        renderer.Prepare = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            byte[] preparedBytes = Encoding.UTF8.GetBytes(preparedSvg);
            renderer.LastPreparedSource = new WeakReference<byte[]>(preparedBytes);
            return MarkdownSvgSourcePreparation.Admit(preparedBytes);
        };

        const long providerSourceWeight = 32_768;
        TrackingSvgDocument? openedDocument = null;
        TrackingProviderSourceOwner? sourceOwner = null;
        renderer.OpenResultFactory = (_, _) =>
        {
            sourceOwner = renderer.CreateProviderSourceOwner(providerSourceWeight);
            openedDocument = new TrackingSvgDocument(sourceOwner);
            renderer.LastOpenedDocument = openedDocument;
            return MarkdownSvgAdmissionResult.Opened(
                openedDocument,
                providerSourceWeight,
                renderer.AdmissionDomain);
        };

        var image = CreateImage(
            renderer,
            new GeneratedSvgResolver(),
            session,
            needsCanvasFallback: true);

        try
        {
            image.EnsureLoading();
            await renderer.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            openedDocument = Assert.IsType<TrackingSvgDocument>(renderer.LastOpenedDocument);
            sourceOwner = Assert.IsType<TrackingProviderSourceOwner>(renderer.LastProviderSourceOwner);
            await openedDocument.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            TrackingPreparationLease preparationLease =
                Assert.IsType<TrackingPreparationLease>(renderer.LastLease);
            Assert.Equal(1, preparationLease.SetLengthCount);
            Assert.Equal(1, preparationLease.DisposeCount);
            Assert.Equal(0, preparationLease.CurrentBytes);
            Assert.Equal(0, openedDocument.DisposeCount);
            Assert.Equal(0, sourceOwner.DisposeCount);
            Assert.Equal(providerSourceWeight, sourceOwner.CurrentBytes);
            Assert.Equal(providerSourceWeight, renderer.ProviderSourceWeight);

            image.Dispose();
            Task retirement = image.RetirementTask;
            await openedDocument.RenderCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(retirement.IsCompleted,
                "Image retirement must wait for a provider render that has not drained yet.");
            Assert.Equal(0, openedDocument.DisposeCount);
            Assert.Equal(0, sourceOwner.DisposeCount);
            Assert.Equal(providerSourceWeight, renderer.ProviderSourceWeight);

            openedDocument.ReleaseRender();
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, openedDocument.DisposeCount);
            Assert.Equal(1, sourceOwner.DisposeCount);
            Assert.Equal(0, sourceOwner.CurrentBytes);
            Assert.Equal(0, renderer.ProviderSourceWeight);
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
        }
        finally
        {
            renderer.LastOpenedDocument?.ReleaseRender();
            await RetireAsync(image);
        }
    }

    [Theory]
    [InlineData(ScratchReply.GrantedWithoutLease)]
    [InlineData(ScratchReply.DeniedWithLease)]
    [InlineData(ScratchReply.NegativeCapacityVersion)]
    [InlineData(ScratchReply.ZeroScratchWeight)]
    [InlineData(ScratchReply.ThrowsAfterLeaseCreation)]
    public async Task MalformedScratchAdmissionPublishesTypedFailureAndReleasesAnyLeaseExactlyOnce(
        ScratchReply scratchReply)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var renderer = new PreparationAdmissionRenderer
        {
            ScratchMode = scratchReply,
        };
        var image = CreateImage(renderer, new GeneratedSvgResolver(), session, needsCanvasFallback: true);
        using LoadObservation load = StartLoad(image);

        try
        {
            await load.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
            Assert.Null(image.Bitmap);
            Assert.Equal(1, renderer.ScratchReservationCount);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            if (renderer.LastLease is { } lease)
                Assert.Equal(1, lease.DisposeCount);
        }
        finally
        {
            await RetireAsync(image);
        }
    }

    private static ImageBox CreateImage(
        IMarkdownSvgRenderer renderer,
        GeneratedSvgResolver resolver,
        MarkdownPerformanceSession? session,
        bool needsCanvasFallback = false,
        CancellationToken cancellationToken = default)
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

        // Capacity deferrals stop before bitmap creation and use no graphics
        // device. Terminal failures create a visible CanvasTextLayout, while a
        // successful open reaches the CanvasDevice bitmap-cache lookup.
        var context = new MarkdownLayoutContext(
            needsCanvasFallback ? CanvasDevice.GetSharedDevice() : null!,
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = resolver,
            SvgRenderer = renderer,
            PerformanceSession = session,
            PerformanceDocumentOwner = session is null ? null : new object(),
            ImageCancellationToken = cancellationToken,
            RasterizationScale = 1,
        };

        return new ImageBox(
            context,
            $"https://example.test/svg-preparation/{Guid.NewGuid():N}.svg",
            alt: "SVG preparation admission test");
    }

    private static LoadObservation StartLoad(ImageBox image)
    {
        var observation = new LoadObservation(image);
        image.EnsureLoading();
        return observation;
    }

    private static async Task RetireAsync(ImageBox image)
    {
        image.Dispose();
        await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
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
            "A completed SVG host-capacity deferral retained a resolver or prepared byte array.");
    }

    private sealed class LoadObservation : IDisposable
    {
        private readonly ImageBox _image;
        private readonly EventHandler<LoadCompletedEventArgs> _handler;
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal LoadObservation(ImageBox image)
        {
            _image = image;
            _handler = (_, _) => _completion.TrySetResult();
            image.LoadCompleted += _handler;
        }

        internal Task Completion => _completion.Task;

        public void Dispose() => _image.LoadCompleted -= _handler;
    }

    private sealed class GeneratedSvgResolver : IMarkdownImageResolver
    {
        private readonly string _svg = CreateSvg(Guid.NewGuid().ToString("N"));
        private WeakReference<byte[]>? _lastSource;

        internal WeakReference<byte[]>? LastSource => Volatile.Read(ref _lastSource);

        internal static string CreateSvg(string identity) =>
            $"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'>" +
            $"<rect id='svg-host-preparation-{identity}' width='16' height='8'/></svg>";

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = Encoding.UTF8.GetBytes(_svg);
            Volatile.Write(ref _lastSource, new WeakReference<byte[]>(bytes));
            var asset = new MarkdownImageAsset(bytes, "image/svg+xml", CacheKey: string.Empty);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(asset));
        }
    }

    public enum ScratchReply
    {
        Granted,
        Denied,
        GrantedWithoutLease,
        DeniedWithLease,
        NegativeCapacityVersion,
        ZeroScratchWeight,
        ThrowsAfterLeaseCreation,
    }

    private abstract class SvgTestRenderer : IMarkdownSvgRenderer, IMarkdownSvgAdmission
    {
        private readonly TaskCompletionSource _capacityWaitStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _openStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseOpen = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prepareSourceCount;
        private int _openCount;

        internal Func<byte[], CancellationToken, MarkdownSvgSourcePreparation>? Prepare { get; set; }
        internal long OpenRequiredBytes { get; set; } = 4_096;
        internal bool HoldOpenUntilReleased { get; set; }
        internal Func<MarkdownSvgOpenRequest, CancellationToken, MarkdownSvgAdmissionResult>? OpenResultFactory { get; set; }
        internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal long OpenSourceLength { get; private set; }
        internal int OpenLeaseDisposeCount { get; private set; }
        internal int CapacityWaitLeaseDisposeCount { get; private set; }
        internal long CapacityWaitRequiredBytes { get; private set; }
        internal TaskCompletionSource CapacityWaitStarted => _capacityWaitStarted;
        internal TaskCompletionSource OpenStarted => _openStarted;
        internal virtual TrackingPreparationLease? LastLease => null;

        public object AdmissionDomain { get; } = new();
        public long CapacityVersion => 0;

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepareSourceCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Prepare?.Invoke(source, cancellationToken) ??
                MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IMarkdownSvgDocument>(
                new InvalidOperationException("Host-capable opens use TryOpenAsync."));

        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
            MarkdownSvgOpenRequest request,
            MarkdownSvgRenderDemand demand,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            OpenSourceLength = request.SanitizedSvgBytes.Length;
            OpenLeaseDisposeCount = LastLease?.DisposeCount ?? 0;

            if (OpenResultFactory is { } openResultFactory)
            {
                MarkdownSvgAdmissionResult result = openResultFactory(request, cancellationToken);
                _openStarted.TrySetResult();
                return ValueTask.FromResult(result);
            }

            _openStarted.TrySetResult();

            if (HoldOpenUntilReleased)
                return new ValueTask<MarkdownSvgAdmissionResult>(CompleteHeldOpenAsync(cancellationToken));

            return ValueTask.FromResult(CreateCapacityDenial());
        }

        public Task WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapacityWaitRequiredBytes = requiredFreeBytes;
            CapacityWaitLeaseDisposeCount = LastLease?.DisposeCount ?? 0;
            _capacityWaitStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        internal void ReleaseOpen() => _releaseOpen.TrySetResult();

        private async Task<MarkdownSvgAdmissionResult> CompleteHeldOpenAsync(
            CancellationToken cancellationToken)
        {
            await _releaseOpen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CreateCapacityDenial();
        }

        private MarkdownSvgAdmissionResult CreateCapacityDenial() =>
            MarkdownSvgAdmissionResult.CapacityUnavailable(CapacityVersion, OpenRequiredBytes);
    }

    private sealed class LegacyHostRenderer : SvgTestRenderer { }

    private sealed class PreparationAdmissionRenderer : SvgTestRenderer,
        IMarkdownSvgPreparationAdmission
    {
        private int _scratchReservationCount;
        private long _providerSourceWeight;

        internal ScratchReply ScratchMode { get; set; } = ScratchReply.Granted;
        internal long ScratchPeakBytes { get; set; } = PreparationScratchBytes;
        internal Action? AfterScratchLeaseCreated { get; set; }
        internal WeakReference<byte[]>? LastPreparedSource { get; set; }
        internal int ScratchReservationCount => Volatile.Read(ref _scratchReservationCount);
        internal long ProviderSourceWeight => Volatile.Read(ref _providerSourceWeight);
        internal TrackingSvgDocument? LastOpenedDocument { get; set; }
        internal TrackingProviderSourceOwner? LastProviderSourceOwner { get; private set; }
        private TrackingPreparationLease? _lastLease;
        internal override TrackingPreparationLease? LastLease => Volatile.Read(ref _lastLease);

        internal TrackingProviderSourceOwner CreateProviderSourceOwner(long bytes)
        {
            if (bytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytes));

            Interlocked.Add(ref _providerSourceWeight, bytes);
            var owner = new TrackingProviderSourceOwner(bytes, releasedBytes =>
                Interlocked.Add(ref _providerSourceWeight, -releasedBytes));
            LastProviderSourceOwner = owner;
            return owner;
        }

        public bool TryReservePreparationScratch(
            MarkdownSvgRenderDemand demand,
            out IMarkdownSvgPreparationLease? lease,
            out long observedCapacityVersion,
            out long requiredFreeBytes)
        {
            Interlocked.Increment(ref _scratchReservationCount);
            observedCapacityVersion = CapacityVersion;
            requiredFreeBytes = ScratchPeakBytes;
            lease = null;

            switch (ScratchMode)
            {
                case ScratchReply.Denied:
                    return false;
                case ScratchReply.GrantedWithoutLease:
                    return true;
                case ScratchReply.DeniedWithLease:
                    lease = _lastLease = new TrackingPreparationLease();
                    return false;
                case ScratchReply.NegativeCapacityVersion:
                    observedCapacityVersion = -1;
                    lease = _lastLease = new TrackingPreparationLease();
                    return true;
                case ScratchReply.ZeroScratchWeight:
                    requiredFreeBytes = 0;
                    lease = _lastLease = new TrackingPreparationLease();
                    return true;
                case ScratchReply.ThrowsAfterLeaseCreation:
                    lease = _lastLease = new TrackingPreparationLease();
                    throw new InvalidOperationException("Synthetic admission failure after lease creation.");
                case ScratchReply.Granted:
                    lease = _lastLease = new TrackingPreparationLease();
                    AfterScratchLeaseCreated?.Invoke();
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ScratchMode));
            }
        }
    }

    private sealed class TrackingPreparationLease : IMarkdownSvgPreparationLease
    {
        private int _disposeCount;
        private int _setLengthCount;
        private long _retainedLength;
        private long _currentBytes;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal int SetLengthCount => Volatile.Read(ref _setLengthCount);
        internal long RetainedLength => Volatile.Read(ref _retainedLength);
        internal long CurrentBytes => Volatile.Read(ref _currentBytes);

        public void SetLength(long retainedBytes)
        {
            Interlocked.Increment(ref _setLengthCount);
            Volatile.Write(ref _retainedLength, retainedBytes);
            Volatile.Write(ref _currentBytes, retainedBytes);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Interlocked.Exchange(ref _currentBytes, 0);
        }
    }

    private sealed class TrackingProviderSourceOwner : IDisposable
    {
        private readonly Action<long> _onReleased;
        private int _disposeCount;
        private int _disposed;
        private long _currentBytes;

        internal TrackingProviderSourceOwner(long bytes, Action<long> onReleased)
        {
            _currentBytes = bytes;
            _onReleased = onReleased;
        }

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal long CurrentBytes => Volatile.Read(ref _currentBytes);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            long releasedBytes = Interlocked.Exchange(ref _currentBytes, 0);
            if (releasedBytes != 0)
                _onReleased(releasedBytes);
        }
    }

    private sealed class TrackingSvgDocument : IMarkdownSvgDocument
    {
        private readonly TrackingProviderSourceOwner _sourceOwner;
        private readonly TaskCompletionSource _renderStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _renderCancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRender = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        internal TrackingSvgDocument(TrackingProviderSourceOwner sourceOwner) =>
            _sourceOwner = sourceOwner;

        public MarkdownSvgDocumentInfo Info { get; } = new(
            IntrinsicWidthDips: 16,
            IntrinsicHeightDips: 8,
            IntrinsicAspectRatio: 2,
            HasText: false);

        internal TaskCompletionSource RenderStarted => _renderStarted;
        internal TaskCompletionSource RenderCancellationObserved => _renderCancellationObserved;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            _renderStarted.TrySetResult();
            return new ValueTask<MarkdownSvgRaster>(WaitForReleaseThenCancelAsync(cancellationToken));
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _sourceOwner.Dispose();
        }

        internal void ReleaseRender() => _releaseRender.TrySetResult();

        private async Task<MarkdownSvgRaster> WaitForReleaseThenCancelAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _renderCancellationObserved);

            await _releaseRender.Task.ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
