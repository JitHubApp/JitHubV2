using System.Runtime.CompilerServices;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ImageNativeDurableRetirementTests
{
    private const float Width = 320f;

    [Fact]
    public async Task CaptionOptionsFailureKeepsBothOldAndFailedNativeLayoutsInSourceFreeCustody()
    {
        using var harness = new Harness();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe();
        ImageEvidence evidence = await CreateFailedCaptionEvidence(harness, retirement, disposer);

        // The image's active placeholder is synchronously disposed during
        // RetirementTask; the failed caption-construction candidate is the
        // durable manager-owned resource.
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.True(evidence.PlaceholderRetirementRecordWasReserved);
        Assert.True(evidence.CaptionCandidateWasPublished);
        CollectGarbage();
        AssertCollected(evidence.Image);
        AssertCollected(evidence.Context);

        disposer.FailNextDisposal();
        Assert.False(retirement.DrainOnce());
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Single(disposer.ObservedLayouts, IsAlive);
        AssertRetained(Assert.Single(disposer.ObservedLayouts, IsAlive));

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.All(disposer.ObservedLayouts, AssertCollected);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task TerminalDisposalAttemptsEveryLayoutAndPublishesFailedOwnerBeforeReturning()
    {
        using var harness = new Harness();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe();
        TerminalFailureEvidence evidence = await CreateTerminalFailureEvidence(harness, retirement, disposer);

        Assert.Equal("Injected native image-layout disposal failure.", evidence.Failure.Message);
        Assert.Equal(2, disposer.ObservedLayouts.Length);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.Image);
        AssertCollected(evidence.Context);
        Assert.Single(disposer.ObservedLayouts, IsAlive);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.All(disposer.ObservedLayouts, AssertCollected);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task DisposeDuringInlineCheckpointRetainsOriginalPairAndRevokesLateRollback()
    {
        using var harness = new Harness();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe();
        DisposedCheckpointEvidence evidence = await CreateDisposedCheckpointEvidence(
            harness, retirement, disposer);

        Assert.Equal("Injected native image-layout disposal failure.", evidence.Failure.Message);
        Assert.Equal(4, disposer.ObservedLayouts.Length);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.Image);
        AssertCollected(evidence.Context);
        Assert.Single(disposer.ObservedLayouts, IsAlive);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.All(disposer.ObservedLayouts, AssertCollected);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task InlineCheckpointRollbackRestoresOriginalPairAndCommitRetiresItOnce()
    {
        using var harness = new Harness();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe();
        var image = new ImageBox(harness.CreateContext(), string.Empty, "inline failure", retirement, disposer.Dispose);

        _ = image.MeasureInline(180f, 22f);
        CanvasTextLayout original = Assert.IsType<CanvasTextLayout>(image.PlaceholderLayoutForTests);
        NativeResourceRetirementRecord originalRecord = Assert.IsType<NativeResourceRetirementRecord>(
            image.PlaceholderRetirementRecordForTests);

        ImageBox.InlineMeasureCheckpoint rollback = image.BeginInlineCheckpoint();
        _ = image.MeasureInline(100f, 22f, rollback);
        CanvasTextLayout rolledBackCandidate = Assert.IsType<CanvasTextLayout>(image.PlaceholderLayoutForTests);
        NativeResourceRetirementRecord rolledBackRecord = Assert.IsType<NativeResourceRetirementRecord>(
            image.PlaceholderRetirementRecordForTests);
        Assert.NotSame(original, rolledBackCandidate);
        Assert.NotSame(originalRecord, rolledBackRecord);
        rollback.Rollback();

        Assert.Same(original, image.PlaceholderLayoutForTests);
        Assert.Same(originalRecord, image.PlaceholderRetirementRecordForTests);
        Assert.True(image.HasNativeLayoutCustodyForTests);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.True(retirement.DrainOnce());

        ImageBox.InlineMeasureCheckpoint commit = image.BeginInlineCheckpoint();
        _ = image.MeasureInline(80f, 22f, commit);
        CanvasTextLayout committed = Assert.IsType<CanvasTextLayout>(image.PlaceholderLayoutForTests);
        NativeResourceRetirementRecord committedRecord = Assert.IsType<NativeResourceRetirementRecord>(
            image.PlaceholderRetirementRecordForTests);
        Assert.NotSame(original, committed);
        Assert.NotSame(originalRecord, committedRecord);
        commit.Commit();
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.True(retirement.DrainOnce());
        Assert.Same(committed, image.PlaceholderLayoutForTests);
        Assert.Same(committedRecord, image.PlaceholderRetirementRecordForTests);

        image.Dispose();
        await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task PlacementTransferMovesCaptionLayoutAndItsRetirementRecordAsOnePair()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe();
        ImageBox source = new(
            fixture.SourceContext,
            fixture.RasterSource,
            "Placement custody",
            retirement,
            disposer.Dispose)
        {
            BlockIndex = fixture.Source.BlockIndex,
        };
        source.SetCompletedBitmapForTests(Assert.IsType<SharedCanvasBitmapCache.Lease>(
            fixture.ImageRun.Image.AcquireBitmapLease()));
        _ = source.Measure(Width);
        source.Arrange(0, 0, Width);
        LayoutSnapshot snapshot = new(new BlockBox[] { source }, fixture.SourceContext.SourceMap,
            Width, (float)source.Bounds.Height);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);

            CanvasTextLayout? sourcePlaceholder = source.PlaceholderLayoutForTests;
            NativeResourceRetirementRecord? sourcePlaceholderRecord = source.PlaceholderRetirementRecordForTests;
            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.CaptionLayoutForTests);
            NativeResourceRetirementRecord sourceCaptionRecord = Assert.IsType<NativeResourceRetirementRecord>(
                source.CaptionRetirementRecordForTests);
            var transfer = new ImageBox.MeasuredPlacementTransfer(
                source,
                prepared,
                expectedLease,
                ExpectedSvgMetadataFence: null,
                ExpectedInlineLayout: false,
                ExpectedAvailableWidth: Width,
                ExpectedPreparedAvailableWidth: Width);
            Assert.True(transfer.ExpectedSourceNativeLayouts.IsValid);
            Assert.Same(sourcePlaceholder, transfer.ExpectedSourceNativeLayouts.Placeholder);
            Assert.Same(sourcePlaceholderRecord, transfer.ExpectedSourceNativeLayouts.PlaceholderRetirementRecord);
            Assert.Same(sourceCaption, transfer.ExpectedSourceNativeLayouts.Caption);
            Assert.Same(sourceCaptionRecord, transfer.ExpectedSourceNativeLayouts.CaptionRetirementRecord);

            _ = prepared.Measure(Width);
            prepared.Arrange(0, 0, Width);
            Assert.True(prepared.TryCaptureNativeLayoutPlacementFence(out var preparedFence));
            transfer = transfer with { ExpectedPreparedNativeLayouts = preparedFence };

            CanvasTextLayout? preparedPlaceholder = prepared.PlaceholderLayoutForTests;
            NativeResourceRetirementRecord? preparedPlaceholderRecord = prepared.PlaceholderRetirementRecordForTests;
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.CaptionLayoutForTests);
            NativeResourceRetirementRecord preparedRecord = Assert.IsType<NativeResourceRetirementRecord>(
                prepared.CaptionRetirementRecordForTests);
            Assert.Same(preparedPlaceholder, preparedFence.Placeholder);
            Assert.Same(preparedPlaceholderRecord, preparedFence.PlaceholderRetirementRecord);
            Assert.Same(preparedCaption, preparedFence.Caption);
            Assert.Same(preparedRecord, preparedFence.CaptionRetirementRecord);

            // A same-reference owner mutation still advances the native
            // generation. The stale source fence must not authorize a swap.
            source.SetCaptionRetirementRecordForTests(sourceCaptionRecord);
            Assert.True(source.TryCaptureNativeLayoutPlacementFence(out var currentSourceFence));
            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    [transfer], currentProviderCacheGeneration: 0, expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }
            transfer = transfer with { ExpectedSourceNativeLayouts = currentSourceFence };

            prepared.SetCaptionRetirementRecordForTests(preparedRecord);
            Assert.True(prepared.TryCaptureNativeLayoutPlacementFence(out var currentPreparedFence));
            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    [transfer], currentProviderCacheGeneration: 0, expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }
            transfer = transfer with { ExpectedPreparedNativeLayouts = currentPreparedFence };

            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.True(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    [transfer],
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(preparedPlaceholder, source.PlaceholderLayoutForTests);
            Assert.Same(preparedPlaceholderRecord, source.PlaceholderRetirementRecordForTests);
            Assert.Same(sourcePlaceholder, prepared.PlaceholderLayoutForTests);
            Assert.Same(sourcePlaceholderRecord, prepared.PlaceholderRetirementRecordForTests);
            Assert.Same(preparedCaption, source.CaptionLayoutForTests);
            Assert.Same(preparedRecord, source.CaptionRetirementRecordForTests);
            Assert.Same(sourceCaption, prepared.CaptionLayoutForTests);
            Assert.Same(sourceCaptionRecord, prepared.CaptionRetirementRecordForTests);
            Assert.True(source.HasNativeLayoutCustodyForTests);
            Assert.True(prepared.HasNativeLayoutCustodyForTests);
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task PlacementTransferRejectsCrossImageRetirementRecordAlias()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new LayoutDisposalProbe();
        ImageBox source = new(
            fixture.SourceContext,
            fixture.RasterSource,
            "Record alias",
            retirement,
            disposer.Dispose)
        {
            BlockIndex = fixture.Source.BlockIndex,
        };
        source.SetCompletedBitmapForTests(Assert.IsType<SharedCanvasBitmapCache.Lease>(
            fixture.ImageRun.Image.AcquireBitmapLease()));
        _ = source.Measure(Width);
        source.Arrange(0, 0, Width);
        LayoutSnapshot snapshot = new(new BlockBox[] { source }, fixture.SourceContext.SourceMap,
            Width, (float)source.Bounds.Height);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;
        NativeResourceRetirementRecord? originalPreparedRecord = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            ImageBox candidate = Assert.IsType<ImageBox>(prepared);
            Assert.NotNull(expectedLease);
            _ = candidate.Measure(Width);
            candidate.Arrange(0, 0, Width);

            NativeResourceRetirementRecord sharedRecord = Assert.IsType<NativeResourceRetirementRecord>(
                source.CaptionRetirementRecordForTests);
            originalPreparedRecord = Assert.IsType<NativeResourceRetirementRecord>(
                candidate.CaptionRetirementRecordForTests);
            candidate.SetCaptionRetirementRecordForTests(sharedRecord);
            var transfer = new ImageBox.MeasuredPlacementTransfer(
                source,
                candidate,
                expectedLease,
                ExpectedSvgMetadataFence: null,
                ExpectedAvailableWidth: Width,
                ExpectedPreparedAvailableWidth: Width);

            Assert.True(transfer.ExpectedSourceNativeLayouts.IsValid);
            Assert.Same(sharedRecord,
                transfer.ExpectedPreparedNativeLayouts!.Value.CaptionRetirementRecord);
            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    [transfer], currentProviderCacheGeneration: 0, expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(sharedRecord, source.CaptionRetirementRecordForTests);
            Assert.Same(sharedRecord, candidate.CaptionRetirementRecordForTests);
        }
        finally
        {
            if (prepared is not null && originalPreparedRecord is not null)
                prepared.SetCaptionRetirementRecordForTests(originalPreparedRecord);
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, retirement.PendingResourceCount);
        GC.KeepAlive(retirement);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ImageEvidence> CreateFailedCaptionEvidence(
        Harness harness,
        NativeResourceRetirement retirement,
        LayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext context = harness.CreateContext();
        int optionsCalls = 0;
        var image = new ImageBox(
            context,
            string.Empty,
            "caption failure",
            retirement,
            disposer.Dispose,
            _ =>
            {
                if (Interlocked.Increment(ref optionsCalls) == 2)
                    throw new InvalidOperationException("Injected caption Options failure.");
            });

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => image.Measure(Width));
        Assert.Equal("Injected caption Options failure.", failure.Message);
        Assert.NotNull(image.PlaceholderLayoutForTests);
        Assert.NotNull(image.PlaceholderRetirementRecordForTests);
        Assert.Null(image.CaptionLayoutForTests);
        Assert.True(image.HasNativeLayoutCustodyForTests);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        bool placeholderRecordWasReserved = image.PlaceholderRetirementRecordForTests is not null;

        image.Dispose();
        await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        var evidence = new ImageEvidence(
            disposer,
            new WeakReference<ImageBox>(image),
            new WeakReference<MarkdownLayoutContext>(context),
            placeholderRecordWasReserved,
            retirement.PendingRecordCount == 1);
        return evidence;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<TerminalFailureEvidence> CreateTerminalFailureEvidence(
        Harness harness,
        NativeResourceRetirement retirement,
        LayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext context = harness.CreateContext();
        var image = new ImageBox(context, string.Empty, "terminal failure", retirement, disposer.Dispose);
        _ = image.Measure(Width);
        Assert.NotNull(image.PlaceholderLayoutForTests);
        Assert.NotNull(image.CaptionLayoutForTests);
        var imageReference = new WeakReference<ImageBox>(image);
        var contextReference = new WeakReference<MarkdownLayoutContext>(context);

        disposer.FailNextDisposal();
        image.Dispose();
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await image.RetirementTask);
        return new TerminalFailureEvidence(failure, imageReference, contextReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<DisposedCheckpointEvidence> CreateDisposedCheckpointEvidence(
        Harness harness,
        NativeResourceRetirement retirement,
        LayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext context = harness.CreateContext();
        var image = new ImageBox(context, string.Empty, "disposed checkpoint", retirement, disposer.Dispose);
        _ = image.MeasureInline(180f, 22f);
        CanvasTextLayout original = Assert.IsType<CanvasTextLayout>(image.PlaceholderLayoutForTests);
        ImageBox.InlineMeasureCheckpoint checkpoint = image.BeginInlineCheckpoint();
        _ = image.MeasureInline(100f, 22f, checkpoint);
        CanvasTextLayout candidate = Assert.IsType<CanvasTextLayout>(image.PlaceholderLayoutForTests);
        Assert.NotSame(original, candidate);
        var imageReference = new WeakReference<ImageBox>(image);
        var contextReference = new WeakReference<MarkdownLayoutContext>(context);

        disposer.FailNextDisposal();
        image.Dispose();
        // Invalidation is synchronous with Dispose; this late token must not
        // publish or restore either pair before RetirementTask owns them.
        checkpoint.Rollback();
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await image.RetirementTask);
        return new DisposedCheckpointEvidence(failure, imageReference, contextReference);
    }

    private static void AssertCollected<T>(WeakReference<T> reference)
        where T : class => Assert.False(IsAlive(reference), "The native retirement root retained its former owner.");

    private static void AssertRetained<T>(WeakReference<T> reference)
        where T : class => Assert.True(IsAlive(reference), "The retirement manager lost a failed native layout.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    private static void CollectGarbage()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed record ImageEvidence(
        LayoutDisposalProbe Disposer,
        WeakReference<ImageBox> Image,
        WeakReference<MarkdownLayoutContext> Context,
        bool PlaceholderRetirementRecordWasReserved,
        bool CaptionCandidateWasPublished);

    private sealed record TerminalFailureEvidence(
        InvalidOperationException Failure,
        WeakReference<ImageBox> Image,
        WeakReference<MarkdownLayoutContext> Context);

    private sealed record DisposedCheckpointEvidence(
        InvalidOperationException Failure,
        WeakReference<ImageBox> Image,
        WeakReference<MarkdownLayoutContext> Context);

    private sealed class Harness : IDisposable
    {
        private readonly IReadOnlyDictionary<string, bool> _disclosures =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly ThemeSnapshot _theme;

        internal Harness()
        {
            Device = new CanvasDevice(forceSoftwareRenderer: true);
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.ImageCaption] = body,
            };
            Color transparent = Color.FromArgb(0, 0, 0, 0);
            _theme = new ThemeSnapshot(
                styles,
                new Dictionary<string, ElementStyleOverride>(),
                transparent,
                transparent,
                transparent,
                transparent,
                isDark: false,
                isHighContrast: false,
                textScaleFactor: 1f);
        }

        internal CanvasDevice Device { get; }

        internal MarkdownLayoutContext CreateContext() => new(
            Device,
            _theme,
            new MarkdownSourceMap("image native retirement"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            DisclosureStates = _disclosures,
        };

        public void Dispose()
        {
            SharedCanvasBitmapCache.ReleaseDevice(Device);
            Device.Dispose();
        }
    }

    private sealed class LayoutDisposalProbe
    {
        private readonly List<WeakReference<CanvasTextLayout>> _observed = [];
        private bool _failNext;
        private bool _allowDisposal;

        internal WeakReference<CanvasTextLayout>[] ObservedLayouts => _observed.ToArray();
        internal void FailNextDisposal() => _failNext = true;
        internal void AllowDisposal() => _allowDisposal = true;

        internal void Dispose(IDisposable resource)
        {
            CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(resource);
            _observed.Add(new WeakReference<CanvasTextLayout>(layout));
            if (!_allowDisposal && _failNext)
            {
                _failNext = false;
                throw new InvalidOperationException("Injected native image-layout disposal failure.");
            }
            layout.Dispose();
        }
    }
}
