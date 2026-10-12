#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Diagnostic-only measurement of immutable placement publication after one
/// completed top-level image changes height. Optimized sibling roots must not
/// be re-arranged. No elapsed/allocation limit here qualifies the renderer or
/// the browser-relative release gate.
/// </summary>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RootLocalImageSuffixReflowScalingDiagnosticTests
{
    private const float LayoutWidth = 320f;
    private const float TailHeight = 8f;
    private static readonly int[] s_rootCounts = [2_000, 10_000, 100_000];
    private readonly ITestOutputHelper _output;

    public RootLocalImageSuffixReflowScalingDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "LayoutReflowDiagnostic")]
    public async Task CompletedImageSuffixReflow_ReportsDocumentScaleAndMatchesFallback()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        byte[] png = DisplaySizedRasterDecodeTests.CreatePng(96, 192);
        try
        {
            foreach (int rootCount in s_rootCounts)
            {
                var optimizedResolver = new RasterResolver(png, "optimized-" + Guid.NewGuid().ToString("N"));
                var fallbackResolver = new RasterResolver(png, "fallback-" + Guid.NewGuid().ToString("N"));
                await using ReflowFixture optimized = CreateFixture(seed, session, optimizedResolver, rootCount);
                await using ReflowFixture fallback = CreateFixture(seed, session, fallbackResolver, rootCount);

                Rect optimizedPlaceholder = optimized.Image.Bounds;
                Rect fallbackPlaceholder = fallback.Image.Bounds;
                Assert.Equal(optimizedPlaceholder.Height, fallbackPlaceholder.Height);
                await LoadImageAsync(optimized.Image);
                await LoadImageAsync(fallback.Image);
                Assert.Equal(optimizedPlaceholder, optimized.Image.Bounds);
                Assert.Equal(fallbackPlaceholder, fallback.Image.Bounds);

                int[] optimizedArrangeCounts = optimized.TailRoots
                    .Select(static root => root.ArrangeCount).ToArray();
                int[] fallbackArrangeCounts = fallback.TailRoots
                    .Select(static root => root.ArrangeCount).ToArray();

                long fallbackAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var fallbackTimer = Stopwatch.StartNew();
                fallback.Snapshot.RelayoutChangedBlocks(
                    [fallback.Image.BlockIndex], LayoutWidth, CancellationToken.None);
                fallbackTimer.Stop();
                long fallbackAllocated = GC.GetAllocatedBytesForCurrentThread() - fallbackAllocatedBefore;

                MarkdownLayoutContext? constructionContext = null;
                PreparedImageRelayoutResult result;
                long optimizedAllocated;
                var optimizedTimer = Stopwatch.StartNew();
                long optimizedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    result = optimized.Snapshot.TryRelayoutCompletedImageRoot(
                        [optimized.Image.BlockIndex], LayoutWidth, CancellationToken.None,
                        out constructionContext,
                        expectedPointerInputEpoch: optimized.Snapshot.PointerInputEpoch);
                }
                finally
                {
                    optimizedTimer.Stop();
                    optimizedAllocated = GC.GetAllocatedBytesForCurrentThread() - optimizedAllocatedBefore;
                    if (constructionContext is not null)
                        await constructionContext.DrainOwnedRetirementsAsync()
                            .WaitAsync(TimeSpan.FromSeconds(20));
                }

                Assert.Equal(PreparedImageRelayoutResult.Committed, result);
                Assert.NotEqual(optimizedPlaceholder.Height, optimized.Image.Bounds.Height);
                Assert.Equal(fallback.Snapshot.Size, optimized.Snapshot.Size);
                Assert.Equal(optimized.Snapshot.LayoutRevision, fallback.Snapshot.LayoutRevision);
                Assert.Equal(rootCount, optimized.Snapshot.Blocks.Count);
                Assert.Equal(rootCount, fallback.Snapshot.Blocks.Count);

                long optimizedSuffixArranges = AssertNoSuffixArrange(
                    optimized.TailRoots, optimizedArrangeCounts);
                long fallbackSuffixArranges = AssertOneSuffixArrangePerTail(
                    fallback.TailRoots, fallbackArrangeCounts);
                Assert.Equal(0L, optimizedSuffixArranges);
                Assert.Equal(rootCount - 1L, fallbackSuffixArranges);

                AssertGeometryAndQueriesMatch(optimized.Snapshot, fallback.Snapshot, rootCount);
                _output.WriteLine(
                    $"Root-local image suffix diagnostic: roots={rootCount}; " +
                    $"optimizedMs={optimizedTimer.Elapsed.TotalMilliseconds:F3}; " +
                    $"optimizedCurrentThreadAllocated={optimizedAllocated}; " +
                    $"fallbackMs={fallbackTimer.Elapsed.TotalMilliseconds:F3}; " +
                    $"fallbackCurrentThreadAllocated={fallbackAllocated}; " +
                    $"optimizedTailArrangeCalls={optimizedSuffixArranges}; " +
                    $"fallbackTailArrangeCalls={fallbackSuffixArranges}.");
            }
        }
        finally
        {
            Array.Clear(png);
        }
    }

    [Fact]
    [Trait("Category", "LayoutReflowDiagnostic")]
    public async Task SuffixCommitReadProbe_RejectsReadAdmissionWhileCommitHeld()
    {
        const int rootCount = 2_000;
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        byte[] png = DisplaySizedRasterDecodeTests.CreatePng(96, 192);
        var resolver = new RasterResolver(png, "lock-probe-" + Guid.NewGuid().ToString("N"));
        using var gate = new CommitGate();
        ReflowFixture? fixture = null;
        Task<PreparedImageRelayoutResult>? reflowTask = null;
        MarkdownLayoutContext? constructionContext = null;
        bool paintLeaseAdmitted = true;
        bool interactionLeaseAdmitted = true;
        double readAdmissionElapsedMs = 0;
        try
        {
            fixture = CreateFixture(seed, session, resolver, rootCount);
            ReflowFixture activeFixture = fixture;
            await LoadImageAsync(activeFixture.Image);
            int[] tailArrangeCounts = activeFixture.TailRoots
                .Select(static root => root.ArrangeCount).ToArray();
            gate.Arm();

            reflowTask = Task.Run(() => activeFixture.Snapshot.TryRelayoutCompletedImageRoot(
                [activeFixture.Image.BlockIndex], LayoutWidth, CancellationToken.None,
                out constructionContext,
                expectedPointerInputEpoch: activeFixture.Snapshot.PointerInputEpoch,
                whileCommitGateHeldForTests: gate.PauseIfArmed));
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(20));

            var readerTimer = Stopwatch.StartNew();
            // Exercise the same nonblocking read-admission primitives used by
            // paint and pointer handlers while another thread owns the commit.
            // Release leases if the implementation unexpectedly admits either.
            paintLeaseAdmitted = activeFixture.Snapshot.TryBeginPaint();
            if (paintLeaseAdmitted)
                activeFixture.Snapshot.EndPaint();
            interactionLeaseAdmitted = activeFixture.Snapshot.TryBeginInteraction();
            if (interactionLeaseAdmitted)
                activeFixture.Snapshot.EndInteraction();
            readerTimer.Stop();
            readAdmissionElapsedMs = readerTimer.Elapsed.TotalMilliseconds;
            gate.Release();

            PreparedImageRelayoutResult result = await reflowTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PreparedImageRelayoutResult.Committed, result);
            Assert.False(paintLeaseAdmitted);
            Assert.False(interactionLeaseAdmitted);
            Assert.Equal(0L, AssertNoSuffixArrange(activeFixture.TailRoots, tailArrangeCounts));
            _output.WriteLine(
                $"Suffix commit read probe: roots={rootCount}; " +
                $"paintLeaseAdmittedWhileCommitHeld={paintLeaseAdmitted}; " +
                $"interactionLeaseAdmittedWhileCommitHeld={interactionLeaseAdmitted}; " +
                $"readAdmissionElapsedMs={readAdmissionElapsedMs:F3}. " +
                "Admission rejection is the assertion; elapsed time is diagnostic only. " +
                "This does not claim that direct HitTest queries are nonblocking.");
        }
        finally
        {
            gate.Release();
            if (reflowTask is not null)
            {
                try { await reflowTask.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch { /* Preserve the primary assertion; resource cleanup follows. */ }
            }
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20));
            if (fixture is not null)
                await fixture.DisposeAsync();
            Array.Clear(png);
        }
    }

    private static ReflowFixture CreateFixture(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        RasterResolver resolver,
        int rootCount)
    {
        var sourceMap = new MarkdownSourceMap(string.Empty);
        var context = new MarkdownLayoutContext(
            seed.Device,
            seed.SourceContext.ThemeSnapshot,
            sourceMap,
            seed.SourceContext.Registry,
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = seed.PerformanceDocumentOwner,
            PerformanceSession = session,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = 1,
        };

        var image = new ImageBox(context, resolver.Url, "diagnostic target") { BlockIndex = 0 };
        var roots = new BlockBox[rootCount];
        var tails = new CountedTailRoot[rootCount - 1];
        roots[0] = image;
        float imageHeight = image.Measure(LayoutWidth);
        image.Arrange(0, 0, LayoutWidth);
        float y = (float)image.Bounds.Bottom;
        for (int ordinal = 1; ordinal < rootCount; ordinal++)
        {
            var tail = new CountedTailRoot(ordinal, TailHeight);
            roots[ordinal] = tail;
            tails[ordinal - 1] = tail;
            float height = tail.Measure(LayoutWidth);
            tail.Arrange(0, y, LayoutWidth);
            y += height;
        }

        if (image.Bounds.Height != imageHeight)
            throw new InvalidOperationException("The diagnostic image's initial measured height changed during Arrange.");
        var snapshot = new LayoutSnapshot(roots, sourceMap, LayoutWidth, y, blockSpacing: 0);
        return new ReflowFixture(context, snapshot, image, tails);
    }

    private static async Task LoadImageAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.ActiveImageWorkCountForTests == 0);
        Assert.NotNull(image.Bitmap);
    }

    private static long AssertNoSuffixArrange(
        IReadOnlyList<CountedTailRoot> roots,
        IReadOnlyList<int> before)
    {
        Assert.Equal(roots.Count, before.Count);
        long total = 0;
        for (int index = 0; index < roots.Count; index++)
        {
            int arrangeDelta = roots[index].ArrangeCount - before[index];
            Assert.Equal(0, arrangeDelta);
            total += arrangeDelta;
        }
        return total;
    }

    private static long AssertOneSuffixArrangePerTail(
        IReadOnlyList<CountedTailRoot> roots,
        IReadOnlyList<int> before)
    {
        Assert.Equal(roots.Count, before.Count);
        long total = 0;
        for (int index = 0; index < roots.Count; index++)
        {
            int arrangeDelta = roots[index].ArrangeCount - before[index];
            Assert.Equal(1, arrangeDelta);
            total += arrangeDelta;
        }
        return total;
    }

    private static void AssertGeometryAndQueriesMatch(
        LayoutSnapshot actual,
        LayoutSnapshot oracle,
        int rootCount)
    {
        Assert.Equal(oracle.Size, actual.Size);
        Assert.Equal(rootCount, actual.Blocks.Count);
        Assert.Equal(rootCount, oracle.Blocks.Count);
        LayoutPlacementGeneration actualIndex = GetIndex(actual);
        LayoutPlacementGeneration oracleIndex = GetIndex(oracle);
        Assert.Equal(oracleIndex.Count, actualIndex.Count);
        Assert.Equal(oracleIndex.IsDocumentOrder, actualIndex.IsDocumentOrder);

        for (int ordinal = 0; ordinal < rootCount; ordinal++)
        {
            Rect expectedBounds = oracleIndex.ToLogicalRect(ordinal, oracle.Blocks[ordinal].Bounds);
            Rect actualBounds = actualIndex.ToLogicalRect(ordinal, actual.Blocks[ordinal].Bounds);
            Assert.Equal(expectedBounds, actualBounds);
            Assert.Equal(oracleIndex.GetBlockOrdinal(ordinal), actualIndex.GetBlockOrdinal(ordinal));
            Assert.Equal(oracleIndex.GetIndexedBounds(ordinal), actualIndex.GetIndexedBounds(ordinal));
        }

        int[] sampleOrdinals =
        [0, 1, rootCount / 4, rootCount / 2, (rootCount * 3) / 4, rootCount - 1];
        foreach (int ordinal in sampleOrdinals.Distinct())
        {
            Rect bounds = actualIndex.ToLogicalRect(ordinal, actual.Blocks[ordinal].Bounds);
            foreach ((double top, double bottom) in new[]
                     {
                         (bounds.Top, bounds.Top),
                         (bounds.Bottom, bounds.Bottom),
                         (bounds.Top, bounds.Bottom),
                         (System.Math.Max(0, bounds.Top - 1), bounds.Bottom + 1),
                     })
            {
                Assert.Equal(oracleIndex.Find(top, bottom), actualIndex.Find(top, bottom));
            }

            if (ordinal == 0)
                continue;
            Point point = new(1, bounds.Top + (bounds.Height / 2));
            Assert.True(actual.HitTest(point, out DocumentPosition actualPosition));
            Assert.True(oracle.HitTest(point, out DocumentPosition oraclePosition));
            Assert.Equal(oraclePosition, actualPosition);

            var range = new DocumentRange(
                new DocumentPosition(actual.Blocks[ordinal].BlockIndex, 0, 0),
                new DocumentPosition(actual.Blocks[ordinal].BlockIndex, 0, 1));
            Rect[] expectedSelection = LayoutPlacementTestGeometry.ToLogical(
                oracle, ordinal, oracle.Blocks[ordinal].GetSelectionRects(range));
            Rect[] actualSelection = LayoutPlacementTestGeometry.ToLogical(
                actual, ordinal, actual.Blocks[ordinal].GetSelectionRects(range));
            Assert.Equal(expectedSelection, actualSelection);
        }
    }

    private static LayoutPlacementGeneration GetIndex(LayoutSnapshot snapshot)
        => snapshot.PublishedPlacementGeneration;

    private sealed class ReflowFixture(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        ImageBox image,
        CountedTailRoot[] tailRoots) : IAsyncDisposable
    {
        internal MarkdownLayoutContext Context { get; } = context;
        internal LayoutSnapshot Snapshot { get; } = snapshot;
        internal ImageBox Image { get; } = image;
        internal CountedTailRoot[] TailRoots { get; } = tailRoots;

        public async ValueTask DisposeAsync()
        {
            try { await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        }
    }

    private sealed class CountedTailRoot : BlockBox
    {
        private readonly float _height;
        private int _arrangeCount;

        internal CountedTailRoot(int blockIndex, float height)
        {
            BlockIndex = blockIndex;
            _height = height;
        }

        internal int ArrangeCount => Volatile.Read(ref _arrangeCount);

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, _height);
            return _height;
        }

        public override void Arrange(float x, float y, float width)
        {
            base.Arrange(x, y, width);
            Interlocked.Increment(ref _arrangeCount);
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }

    private sealed class CommitGate : IDisposable
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);
        private int _armed;

        internal Task Entered => _entered.Task;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.Set();

        internal void PauseIfArmed()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
                return;
            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("The diagnostic commit gate was not released.");
        }

        public void Dispose() => _release.Dispose();
    }

    private sealed class RasterResolver(byte[] png, string key) : IMarkdownImageResolver
    {
        internal string Url { get; } = $"https://layout-diagnostic.example.test/{Guid.NewGuid():N}.png";

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(source, Url, StringComparison.Ordinal))
                throw new InvalidOperationException("The diagnostic resolver received an unexpected URL.");
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(png, "image/png", CacheKey: key)));
        }
    }
}
#endif
