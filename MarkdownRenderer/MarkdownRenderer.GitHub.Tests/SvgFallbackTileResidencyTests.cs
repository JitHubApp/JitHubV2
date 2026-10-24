using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgFallbackTileResidencyTests
{
    private const int TileSize = 1024;

    [Fact]
    public void EqualAllocationDifferentFractionalViewportStillNeedsTheNextTile()
    {
        var destination = new Rect(0, 0, 1024.25, 100);
        var previous = new MarkdownSvgPhysicalViewport(1024.25, 100);
        var replacement = new MarkdownSvgPhysicalViewport(1024.75, 100);
        var tiles = NewTileMap((0, 0, TileSize, 100));
        Assert.True(ImageBox.ShouldRetainSvgFallbackTile(
            (0, 0, TileSize, 100), 1025, 100, 1025, 100,
            destination, destination, tiles, previous, replacement));
        tiles.Add((1024, 0, 1, 100), null!);
        Assert.False(ImageBox.ShouldRetainSvgFallbackTile(
            (0, 0, TileSize, 100), 1025, 100, 1025, 100,
            destination, destination, tiles, previous, replacement));
    }

    [Fact]
    public void OffBandUncoveredFallbackTileIsPruned()
    {
        var tiles = NewTileMap();
        var destination = new Rect(0, 0, 3072, 2048);
        var visible = new Rect(2600, 0, 472, 512);

        Assert.False(Retain(
            (0, 0, TileSize, TileSize),
            fallbackWidth: 3072,
            fallbackHeight: 2048,
            currentWidth: 3072,
            currentHeight: 2048,
            destination,
            visible,
            tiles));
    }

    [Fact]
    public void OneFallbackTileOverscanKeepsExactContactAndPrunesStrictlyOutside()
    {
        var tiles = NewTileMap();
        var destination = new Rect(0, 0, 3072, 2048);

        Assert.True(Retain(
            (0, 0, TileSize, TileSize),
            fallbackWidth: 3072,
            fallbackHeight: 2048,
            currentWidth: 3072,
            currentHeight: 2048,
            destination,
            visible: new Rect(2048, 0, 512, 512),
            tiles));
        Assert.False(Retain(
            (0, 0, TileSize, TileSize),
            fallbackWidth: 3072,
            fallbackHeight: 2048,
            currentWidth: 3072,
            currentHeight: 2048,
            destination,
            visible: new Rect(2049, 0, 511, 512),
            tiles));
    }

    [Fact]
    public void OneFallbackTileOverscanAlsoAppliesAtVerticalBoundaries()
    {
        var tiles = NewTileMap();
        var destination = new Rect(0, 0, 3072, 3072);
        var key = (0, 0, TileSize, TileSize);

        Assert.True(Retain(
            key,
            fallbackWidth: 3072,
            fallbackHeight: 3072,
            currentWidth: 3072,
            currentHeight: 3072,
            destination,
            visible: new Rect(0, 2048, 512, 512),
            tiles));
        Assert.False(Retain(
            key,
            fallbackWidth: 3072,
            fallbackHeight: 3072,
            currentWidth: 3072,
            currentHeight: 3072,
            destination,
            visible: new Rect(0, 2049, 512, 512),
            tiles));
    }

    [Fact]
    public void DpiChangedCurrentGridRequiresEveryCanonicalTileForCompleteCoverage()
    {
        // The old 1024-pixel cell projects to a 256-DIP square here, while a
        // current 1024-pixel tile projects to 384 DIPs. The old cell therefore
        // crosses the exact boundary between two current-generation columns.
        var destination = new Rect(0, 0, 768, 768);
        var visible = destination;
        var oldTile = (1024, 0, TileSize, TileSize);
        var complete = NewTileMap(
            (0, 0, TileSize, TileSize),
            (TileSize, 0, TileSize, TileSize));
        var partial = NewTileMap((0, 0, TileSize, TileSize));

        Assert.False(Retain(oldTile, 3072, 3072, 2048, 2048, destination, visible, complete));
        Assert.True(Retain(oldTile, 3072, 3072, 2048, 2048, destination, visible, partial));
    }

    [Fact]
    public void GeometricallyCoveringNonCanonicalKeysDoNotRetireFallbackTiles()
    {
        var destination = new Rect(0, 0, 768, 768);
        var oldTile = (1024, 0, TileSize, TileSize);
        var nonCanonical = NewTileMap(
            (0, 0, 2048, TileSize),
            (0, TileSize, 2048, TileSize));

        // These rectangles cover the old tile geometrically and the map has
        // enough entries, but only exact canonical 1024-grid keys are evidence.
        Assert.True(Retain(oldTile, 3072, 3072, 2048, 2048, destination, destination, nonCanonical));
    }

    [Fact]
    public void PartialFinalRasterEdgesAreCoveredOnlyByTheirCanonicalPartialTiles()
    {
        var destination = new Rect(5, 9, 100, 100);
        var oldFinalPixel = (2048, 2048, 1, 1);
        var currentFinalCells = NewTileMap(
            (3072, 3072, TileSize, TileSize),
            (4096, 3072, 1, TileSize),
            (3072, 4096, TileSize, 1),
            (4096, 4096, 1, 1));

        Assert.False(Retain(
            oldFinalPixel,
            fallbackWidth: 2049,
            fallbackHeight: 2049,
            currentWidth: 4097,
            currentHeight: 4097,
            destination,
            visible: destination,
            currentFinalCells));

        currentFinalCells.Remove((4096, 4096, 1, 1));
        Assert.True(Retain(
            oldFinalPixel,
            fallbackWidth: 2049,
            fallbackHeight: 2049,
            currentWidth: 4097,
            currentHeight: 4097,
            destination,
            visible: destination,
            currentFinalCells));
    }

    [Fact]
    public void Int32MaximumSquaredProjectionRecognizesCanonicalPartialFinalTile()
    {
        int dimension = int.MaxValue;
        int finalTileOrigin = ((dimension - 1) / TileSize) * TileSize;
        int finalTileExtent = dimension - finalTileOrigin;
        var finalTile = (finalTileOrigin, finalTileOrigin, finalTileExtent, finalTileExtent);
        var currentTiles = NewTileMap(finalTile);

        // The inclusive right/bottom endpoint is Int32.MaxValue, so the exact
        // rational projection multiplies Int32.MaxValue by itself. It fits in
        // Int64 and maps to the one canonical partial tile at each far edge.
        Assert.Equal(int.MaxValue, finalTileOrigin + finalTileExtent);
        Assert.False(Retain(
            finalTile,
            fallbackWidth: dimension,
            fallbackHeight: dimension,
            currentWidth: dimension,
            currentHeight: dimension,
            destination: new Rect(0, 0, 1, 1),
            visible: new Rect(0.9999995, 0.9999995, 0.0000005, 0.0000005),
            currentTiles));
    }

    [Fact]
    public void FiniteTranslatedHugeDipRectsStillRetainNearbyFallbackTiles()
    {
        var destination = new Rect(1e200, -1e200, 1e190, 1e190);
        var visible = new Rect(
            destination.Left + 4e189,
            destination.Top + 4e189,
            1e189,
            1e189);

        // The first old-grid tile lies one-tile overscan from this viewport.
        // A finite translated origin must not overflow intermediate DIP math
        // or spuriously make this still-useful fallback look off-band.
        Assert.True(Retain(
            (0, 0, TileSize, TileSize),
            fallbackWidth: 3072,
            fallbackHeight: 3072,
            currentWidth: 3072,
            currentHeight: 3072,
            destination,
            visible,
            NewTileMap()));
    }

    [Fact]
    public void InvalidOrUnmappableGeometryFailsSafeByRetainingFallback()
    {
        var tiles = NewTileMap((0, 0, TileSize, TileSize));
        var destination = new Rect(0, 0, 2048, 2048);
        var visible = destination;

        Assert.True(Retain((0, 0, TileSize, TileSize), 2048, 2048, 2048, 2048, destination, visible, null!));
        Assert.True(Retain((0, 0, TileSize, TileSize), 0, 2048, 2048, 2048, destination, visible, tiles));
        Assert.True(Retain((0, 0, TileSize, TileSize), 2048, 2048, 0, 2048, destination, visible, tiles));
        Assert.True(Retain((0, 0, 0, TileSize), 2048, 2048, 2048, 2048, destination, visible, tiles));
        Assert.True(Retain((-1, 0, TileSize, TileSize), 2048, 2048, 2048, 2048, destination, visible, tiles));
        Assert.True(Retain((1536, 0, TileSize, TileSize), 2048, 2048, 2048, 2048, destination, visible, tiles));
        Assert.True(Retain((int.MaxValue - 1, 0, TileSize, 1), int.MaxValue, 2048, 2048, 2048, destination, visible, tiles));
        Assert.True(Retain((0, 0, TileSize, TileSize), 2048, 2048, 2048, 2048,
            new Rect(0, 0, 0, 100), visible, tiles));
        Assert.True(Retain((0, 0, TileSize, TileSize), 2048, 2048, 2048, 2048,
            destination, new Rect(0, 0, 0, 100), tiles));
        Assert.True(Retain((0, 0, TileSize, TileSize), 2048, 2048, 2048, 2048,
            destination, new Rect(2049, 2049, 100, 100), tiles));
        Assert.True(Retain((0, 0, TileSize, TileSize), 2048, 2048, 2048, 2048,
            new Rect(double.NaN, 0, 100, 100), visible, tiles));
        Assert.True(Retain((0, 0, TileSize, TileSize), 2048, 2048, 2048, 2048,
            destination, new Rect(0, 0, double.PositiveInfinity, 100), tiles));
    }

    [Fact]
    public void HugeCoverageRejectsBeforeWalkingTheDocumentSizedCurrentGrid()
    {
        var oneCurrentTile = NewTileMap((0, 0, TileSize, TileSize));
        var destination = new Rect(0, 0, 1024, 1024);

        // A single old tile maps to millions of current tiles per axis. The
        // helper must compare the long required-key count to Dictionary.Count
        // before attempting any coverage walk or overflowing an int product.
        Assert.True(Retain(
            (0, 0, TileSize, TileSize),
            fallbackWidth: TileSize,
            fallbackHeight: TileSize,
            currentWidth: int.MaxValue,
            currentHeight: int.MaxValue,
            destination,
            visible: destination,
            oneCurrentTile));
    }

    [Fact]
    public void WarmResidencyDecisionUsesNoManagedAllocationsAndDoesNotReadLeaseValues()
    {
        var tiles = NewTileMap(
            (0, 0, TileSize, TileSize),
            (TileSize, 0, TileSize, TileSize));
        var destination = new Rect(0, 0, 768, 768);
        var key = (1024, 0, TileSize, TileSize);

        for (int index = 0; index < 32; index++)
            _ = Retain(key, 3072, 3072, 2048, 2048, destination, destination, tiles);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
            _ = Retain(key, 3072, 3072, 2048, 2048, destination, destination, tiles);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ViewportMutationReservesRetirementOwnershipBeforeRemovalAndDrainsAfterGate()
    {
        string source = File.ReadAllText(FindImageBoxSource());
        int methodStart = source.IndexOf("private void QueueVisibleSvgTiles(", StringComparison.Ordinal);
        int methodEnd = source.IndexOf(
            "internal static bool ShouldRetainSvgFallbackTile(",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);

        string method = source[methodStart..methodEnd];
        int retirementLocals = method.IndexOf(
            "List<SharedCanvasBitmapCache.Lease>? retired = null;",
            StringComparison.Ordinal);
        int positiveViewportMutation = method.IndexOf("if (!plan.TryBeginViewportMutation())", retirementLocals, StringComparison.Ordinal);
        int lockStart = method.IndexOf("lock (_svgTileLock)", positiveViewportMutation, StringComparison.Ordinal);
        int lockOpen = method.IndexOf('{', lockStart);
        int lockClose = FindMatchingCloseBrace(method, lockOpen);
        int mutationFinally = method.IndexOf("finally", lockClose, StringComparison.Ordinal);
        Assert.True(retirementLocals >= 0 && positiveViewportMutation > retirementLocals &&
            lockStart > positiveViewportMutation && lockOpen > lockStart &&
            lockClose > lockOpen && mutationFinally > lockClose);

        string mutation = method[lockOpen..(lockClose + 1)];
        int cachedRetirementReserve = mutation.IndexOf(
            "retired ??= new List<SharedCanvasBitmapCache.Lease>(checked(",
            StringComparison.Ordinal);
        int cachedRemoval = mutation.IndexOf("_svgTiles.Remove(key)", StringComparison.Ordinal);
        int fallbackRetirementReserve = mutation.IndexOf(
            "retired ??= new List<SharedCanvasBitmapCache.Lease>(checked(",
            cachedRetirementReserve + 1,
            StringComparison.Ordinal);
        int fallbackRemoval = mutation.IndexOf("_svgFallbackTiles.Remove(tile.Key)", StringComparison.Ordinal);

        Assert.True(cachedRetirementReserve >= 0 && cachedRemoval > cachedRetirementReserve);
        Assert.True(fallbackRetirementReserve > cachedRemoval && fallbackRemoval > fallbackRetirementReserve);
        Assert.Contains("_svgTiles.Count + (_svgFallbackTiles?.Count ?? 0)",
            mutation[cachedRetirementReserve..fallbackRetirementReserve], StringComparison.Ordinal);
        Assert.Contains("_svgTiles.Count + _svgFallbackTiles.Count",
            mutation[fallbackRetirementReserve..fallbackRemoval], StringComparison.Ordinal);
        Assert.DoesNotContain("tile.Dispose", mutation, StringComparison.Ordinal);

        int staleWorkReserve = mutation.IndexOf(
            "staleWork ??= new List<CancellationTokenSource>(_svgTilesInFlight.Count)",
            StringComparison.Ordinal);
        int staleOwnerClaim = mutation.IndexOf(
            "Interlocked.CompareExchange(ref owner.State, 2, 0)",
            StringComparison.Ordinal);
        int staleMapRemoval = mutation.IndexOf("_svgTilesInFlight.Remove(work.Key)", StringComparison.Ordinal);
        Assert.True(staleWorkReserve >= 0 && staleOwnerClaim > staleWorkReserve && staleMapRemoval > staleWorkReserve,
            "The cancellation owner must be retained before claiming it or removing its in-flight mapping.");

        int endMutation = method.IndexOf("plan.EndViewportMutation();", mutationFinally, StringComparison.Ordinal);
        int retiredDrain = method.IndexOf("if (retired is not null)", endMutation, StringComparison.Ordinal);
        int nativeCleanup = method.IndexOf("TryImageCleanup(tile.Dispose)", retiredDrain, StringComparison.Ordinal);
        int staleWorkDrain = method.IndexOf("CancellationTokenSourceRetirement.RequestCancellation(cancellation)", nativeCleanup, StringComparison.Ordinal);
        int promoteVisible = method.IndexOf("plan.Demand.PromoteVisible();", staleWorkDrain, StringComparison.Ordinal);
        Assert.True(endMutation > mutationFinally && retiredDrain > endMutation && nativeCleanup > retiredDrain &&
            staleWorkDrain > nativeCleanup && promoteVisible > staleWorkDrain);
    }

    [Fact]
    public void ViewportPaintPrunesOffBandAndCoveredFallbackButKeepsAnUncoveredInBandTile()
    {
        var document = new NonRenderingSvgDocument();
        var renderer = new NonOpeningSvgRenderer();
        var plan = new ImageBox.SvgTileRenderPlan(
            document,
            "svg-cache-prefix:fallback-pruning-integration",
            fullWidthPixels: 4096,
            fullHeightPixels: 1024,
            renderGeneration: 91);
        ImageBox box = CreateBareBox(plan, renderer);
        var fallback = new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>
        {
            [(0, 0, TileSize, TileSize)] = new(),
            [(TileSize, 0, TileSize, TileSize)] = new(),
            [(2 * TileSize, 0, TileSize, TileSize)] = new(),
        };
        SetField(box, "_svgFallbackTiles", fallback);
        SetField(box, "_svgFallbackWidthPixels", 3072);
        SetField(box, "_svgFallbackHeightPixels", 1024);

        Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> current =
            GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(box, "_svgTiles");
        try
        {
            current.Add((0, 0, TileSize, TileSize), new());
            current.Add((TileSize, 0, TileSize, TileSize), new());

            QueueVisible(box, new Rect(0, 0, 1024, 256), new Rect(0, 0, 256, 256));

            Assert.False(fallback.ContainsKey((0, 0, TileSize, TileSize)));
            Assert.True(fallback.ContainsKey((TileSize, 0, TileSize, TileSize)));
            Assert.False(fallback.ContainsKey((2 * TileSize, 0, TileSize, TileSize)));
            Assert.Equal(0, document.RenderCount);
            Assert.Equal(0, renderer.OpenCount);

            // Current tiles zero and one remain cached; queued work is absent,
            // so repeated paint exercises the warm no-retirement/no-open path.
            for (int index = 0; index < 32; index++)
                QueueVisible(box, new Rect(0, 0, 1024, 256), new Rect(0, 0, 256, 256));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1000; index++)
                QueueVisible(box, new Rect(0, 0, 1024, 256), new Rect(0, 0, 256, 256));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);

            // Widen the viewport, add the remaining canonical current tiles,
            // and verify replacement coverage retires the formerly retained tile.
            current.Add((2 * TileSize, 0, TileSize, TileSize), new());
            current.Add((3 * TileSize, 0, TileSize, TileSize), new());
            QueueVisible(box, new Rect(0, 0, 1024, 256), new Rect(0, 0, 768, 256));

            Assert.Empty(fallback);
            Assert.Equal(0, document.RenderCount);
            Assert.Equal(0, renderer.OpenCount);
        }
        finally
        {
            foreach (SharedCanvasBitmapCache.Lease lease in fallback.Values)
                lease.Dispose();
            foreach (SharedCanvasBitmapCache.Lease lease in current.Values)
                lease.Dispose();
        }
    }

    private static bool Retain(
        (int X, int Y, int Width, int Height) key,
        int fallbackWidth,
        int fallbackHeight,
        int currentWidth,
        int currentHeight,
        Rect destination,
        Rect visible,
        Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> currentTiles) =>
        ImageBox.ShouldRetainSvgFallbackTile(
            key,
            fallbackWidth,
            fallbackHeight,
            currentWidth,
            currentHeight,
            destination,
            visible,
            currentTiles);

    private static ImageBox CreateBareBox(
        ImageBox.SvgTileRenderPlan plan,
        IMarkdownSvgRenderer renderer)
    {
        // Current-generation keys are already cached/pending in these tests;
        // no native resource creator, SVG provider, or dispatcher is accessed.
        var box = (ImageBox)RuntimeHelpers.GetUninitializedObject(typeof(ImageBox));
        SetField(box, "_svgWorkSync", new object());
        SetField(box, "_svgPublicationSync", new object());
        SetField(box, "_svgTileLock", new object());
        SetField(box, "_svgTiles", new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>());
        SetField(box, "_svgTilesInFlight", new Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>());
        SetField(box, "_svgTileWorkOwners", Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(
                typeof(ValueTuple<int, int, int, int>),
                typeof(ImageBox).GetNestedType("SvgTileWorkOwner", BindingFlags.NonPublic)!))!);
        SetField(box, "_svgTileCacheTransitionToken", null);
        SetField(box, "_svgFallbackTiles", null);
        SetField(box, "_svgTileCacheIdentityPrefix", null);
        SetField(box, "_svgTileCacheWidthPixels", 0);
        SetField(box, "_svgTileCacheHeightPixels", 0);
        SetField(box, "_svgVisibleTileRangeKnown", false);
        SetField(box, "_svgCurrentViewportVisible", -1);
        SetField(box, "_svgTileRenderPlan", plan);
        SetField(box, "_svgDocument", plan.Document);
        SetField(box, "_svgLoadDemandTicket", plan.Demand);
        SetField(box, "_svgRenderGeneration", plan.RenderGeneration);
        SetField(box, "_svgUsesTiles", true);
        SetField(box, "_resourcesSuspended", false);
        SetField(box, "_disposed", false);

        var context = new MarkdownLayoutContext(
            resourceCreator: null!,
            themeSnapshot: null!,
            sourceMap: null!,
            registry: null!,
            flowDirection: FlowDirection.LeftToRight)
        {
            SvgRenderer = renderer,
        };
        SetField(box, "Context", context);
        SetField(box, "_svgRendererIdentity", GetRendererIdentity(renderer));
        SetField(box, "_svgProviderCacheGeneration", renderer.CacheGeneration);
        SetField(box, "_svgInfo", plan.Document.Info);
        return box;
    }

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Missing field '{name}' on '{target.GetType().Name}'.");
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Missing field '{name}' on '{target.GetType().Name}'.");
        return (T)field.GetValue(target)!;
    }

    private static long GetRendererIdentity(IMarkdownSvgRenderer renderer)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
                "GetSvgRendererIdentity",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, "GetSvgRendererIdentity");
        return (long)method.Invoke(null, [renderer])!;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "QueueVisibleSvgTiles")]
    private static extern void QueueVisible(ImageBox box, Rect destination, Rect viewport);

    private static Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> NewTileMap(
        params (int X, int Y, int Width, int Height)[] keys)
    {
        var tiles = new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>();
        foreach (var key in keys)
            tiles.Add(key, null!);
        return tiles;
    }

    private static int FindMatchingCloseBrace(string source, int openBrace)
    {
        Assert.InRange(openBrace, 0, source.Length - 1);
        Assert.Equal('{', source[openBrace]);
        int depth = 0;
        for (int index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return index;
        }

        return -1;
    }

    private static string FindImageBoxSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Layout",
                "Boxes",
                "ImageBox.cs");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "Layout",
                "Boxes",
                "ImageBox.cs");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException("Could not locate ImageBox.cs from the test base directory.");
    }

    private sealed class NonRenderingSvgDocument : IMarkdownSvgDocument
    {
        internal int RenderCount;

        public MarkdownSvgDocumentInfo Info { get; } = new(4096, 1024);

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RenderCount);
            throw new InvalidOperationException("The residency integration test must not render SVG content.");
        }

        public void Dispose() { }
    }

    private sealed class NonOpeningSvgRenderer : IMarkdownSvgRenderer
    {
        internal int OpenCount;

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The residency integration test must not preflight SVG content.");

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref OpenCount);
            throw new InvalidOperationException("The residency integration test must not open SVG content.");
        }
    }
}
