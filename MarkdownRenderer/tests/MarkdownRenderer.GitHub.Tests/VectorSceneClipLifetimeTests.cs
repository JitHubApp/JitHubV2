using System.Numerics;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorSceneClipLifetimeTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Red = Color.FromArgb(255, 255, 0, 0);
    private static readonly Color Blue = Color.FromArgb(255, 0, 0, 255);
    private static readonly Color Green = Color.FromArgb(255, 0, 128, 0);

    [Fact]
    public void FailedInnerPopNeverRetriesItOrPopsAnAncestorDuringUnwind()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 20, 20, 96);
        MarkdownVectorScene scene = NestedClipScene(20, RectanglePath(0, 0, 18, 18), RectanglePath(2, 2, 12, 12));
        var failure = new InvalidOperationException("Injected failed native pop before Close.");
        var attempts = new List<CanvasActiveLayer>();
        var geometryDisposalAttempts = new List<CanvasGeometry>();
        NativeResourceRetirement retirement = CreatePopFailingRetirement(failure, attempts, geometryDisposalAttempts);
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None,
            _renderResources,
            nativeRetirement: retirement);
        var slots = Assert.IsAssignableFrom<IReadOnlyList<CanvasActiveLayer?>>(drawing.ClipStackSlots);
        CanvasGeometry[] borrowedGeometries = BorrowGeometries(drawing);
        using CanvasDrawingSession session = target.CreateDrawingSession();
        Matrix3x2 original = Matrix3x2.CreateTranslation(1, 1);
        session.Transform = original;
        bool testResourcesClosed = false;
        try
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(
                () => PaintScene(drawing, session, new Rect(0, 0, 20, 20))));
            Assert.Equal(original, session.Transform);
            Assert.Equal(2, slots.Count);
            Assert.All(slots, Assert.NotNull);
            Assert.Equal(2, borrowedGeometries.Length);
            Assert.Empty(geometryDisposalAttempts);
            Assert.Same(slots[1], Assert.Single(attempts));
            AssertFailedPopIsQuarantinedWithoutRetry(drawing, retirement, slots, attempts, geometryDisposalAttempts);

            // Only after both active layers have been closed in LIFO order is
            // manual geometry cleanup safe. The terminal owner preserved both
            // clip geometries as well as both active wrappers.
            for (int index = slots.Count - 1; index >= 0; index--)
                slots[index]!.Dispose();
            Assert.Equal(new Rect(0, 0, 18, 18), borrowedGeometries[0].ComputeBounds());
            Assert.Equal(new Rect(2, 2, 12, 12), borrowedGeometries[1].ComputeBounds());
            DisposeAll(borrowedGeometries);
            testResourcesClosed = true;
        }
        finally
        {
            // The test owns a deterministic throw-before-close fault, so it
            // knows these layers are still open. Close them directly in LIFO
            // order while the real session lives, avoiding an orphaned native
            // test surface. Production deliberately cannot make this assumption.
            if (!testResourcesClosed)
            {
                CloseLayersInLifoOrder(slots);
                DisposeAll(borrowedGeometries);
            }
        }
    }

    [Fact]
    public void FailedPartialClipFactoryPushQuarantinesKnownAndHelperLayersWithoutRetry()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 24, 24, 96);
        using CanvasDrawingSession session = target.CreateDrawingSession();
        var recorder = new RecordingNativeRetirement();
        var resources = new MarkdownRenderResources(recorder.Retirement);

        NativePaintSessionResources.LayerLease helperOuterLease = CreateHelperOuterLayer(resources, session);
        CanvasActiveLayer helperOuter = Assert.IsType<CanvasActiveLayer>(helperOuterLease.Layer);
        MarkdownVectorScene scene = NestedClipScene(24, RectanglePath(1, 1, 20, 20), RectanglePath(3, 3, 16, 16));
        var createdVectorLayers = new List<CanvasActiveLayer>();
        var factoryFailure = new InvalidOperationException("Injected failure after native partial push.");
        using var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            resources,
            clipLayerFactory: (drawingSession, geometry) =>
            {
                CanvasActiveLayer layer = drawingSession.CreateLayer(1f, geometry);
                createdVectorLayers.Add(layer);
                if (createdVectorLayers.Count == 2)
                    throw factoryFailure;
                return layer;
            });

        var vectorSlots = Assert.IsAssignableFrom<IReadOnlyList<CanvasActiveLayer?>>(drawing.ClipStackSlots);
        CanvasGeometry[] borrowedGeometries = BorrowGeometries(drawing);
        Matrix3x2 original = Matrix3x2.CreateTranslation(2.25f, 1.75f);
        session.Transform = original;
        bool testResourcesClosed = false;
        try
        {
            Assert.Same(factoryFailure, Assert.Throws<InvalidOperationException>(
                () => PaintScene(drawing, session, new Rect(0, 0, 24, 24))));

            Assert.Equal(original, session.Transform);
            Assert.Equal(2, createdVectorLayers.Count);
            Assert.NotSame(createdVectorLayers[0], createdVectorLayers[1]);
            Assert.Equal(2, vectorSlots.Count);
            Assert.Same(createdVectorLayers[0], vectorSlots[0]);
            Assert.Null(vectorSlots[1]);
            AssertFailedPushPoisonsDrawingWithoutRetry(drawing, recorder, helperOuterLease);

            // Terminal disposal may retain the known first clip, but must not
            // guess at a pop for either it or the returned-less second push.
            // Keep these borrowed wrapper references until after that handoff.
            CanvasActiveLayer[] borrowedVectorLayers = createdVectorLayers.ToArray();
            Assert.Equal(2, borrowedGeometries.Length);
            AssertTerminalDisposeQuarantinesWithoutRetry(
                drawing, recorder, session, original, expectedQuarantinedResources: 4);

            // The injected factory is known to have left exactly these layers
            // open. Close them only while their real drawing session is live,
            // in native LIFO order; the production retirement path never does.
            CloseLayersInLifoOrder(borrowedVectorLayers);
            helperOuter.Dispose();
            Assert.Equal(new Rect(1, 1, 20, 20), borrowedGeometries[0].ComputeBounds());
            Assert.Equal(new Rect(3, 3, 16, 16), borrowedGeometries[1].ComputeBounds());
            DisposeAll(borrowedGeometries);
            testResourcesClosed = true;
        }
        finally
        {
            // If an assertion fails before the explicit terminal handoff,
            // still close only the layers whose successful pushes this test
            // observed, and only while the session remains open.
            if (!testResourcesClosed)
            {
                CloseLayersInLifoOrder(createdVectorLayers);
                helperOuter.Dispose();
                DisposeAll(borrowedGeometries);
            }
        }
    }

    [Fact]
    public void FirstClipFactoryPushWithoutReturnedWrapperQuarantinesGeometryUntilLayerCleanup()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 24, 24, 96);
        using CanvasDrawingSession session = target.CreateDrawingSession();
        var recorder = new RecordingNativeRetirement();
        var resources = new MarkdownRenderResources(recorder.Retirement);

        NativePaintSessionResources.LayerLease helperOuterLease = CreateHelperOuterLayer(resources, session);
        CanvasActiveLayer helperOuter = Assert.IsType<CanvasActiveLayer>(helperOuterLease.Layer);
        var scene = new MarkdownVectorScene(24, 24, 24,
        [
            MarkdownVectorCommand.BeginClip(RectanglePath(1, 1, 20, 20)),
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 24, 24), 0xFFFF0000),
            MarkdownVectorCommand.EndClip(),
        ]);
        var createdVectorLayers = new List<CanvasActiveLayer>();
        var factoryFailure = new InvalidOperationException("Injected failure after the first native clip push.");
        using var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            resources,
            clipLayerFactory: (drawingSession, geometry) =>
            {
                CanvasActiveLayer layer = drawingSession.CreateLayer(1f, geometry);
                createdVectorLayers.Add(layer);
                throw factoryFailure;
            });

        var vectorSlots = Assert.IsAssignableFrom<IReadOnlyList<CanvasActiveLayer?>>(drawing.ClipStackSlots);
        var geometrySlots = Assert.IsAssignableFrom<IReadOnlyList<CanvasGeometry?>>(drawing.GeometrySlots);
        CanvasGeometry geometryOwner = Assert.IsType<CanvasGeometry>(Assert.Single(geometrySlots));
        Matrix3x2 original = Matrix3x2.CreateTranslation(3.5f, 2.25f);
        session.Transform = original;
        bool testResourcesClosed = false;
        try
        {
            Assert.Same(factoryFailure, Assert.Throws<InvalidOperationException>(
                () => PaintScene(drawing, session, new Rect(0, 0, 24, 24))));

            Assert.Equal(original, session.Transform);
            Assert.Single(createdVectorLayers);
            Assert.Single(vectorSlots);
            Assert.Null(vectorSlots[0]);
            AssertFailedPushPoisonsDrawingWithoutRetry(drawing, recorder, helperOuterLease);

            CanvasActiveLayer returnedLessLayer = Assert.Single(createdVectorLayers);
            AssertTerminalDisposeQuarantinesWithoutRetry(
                drawing, recorder, session, original, expectedQuarantinedResources: 2);

            // Although the factory returned no wrapper to VectorSceneDrawing,
            // this test observed its native push. Close it and the poisoned
            // outer layer while the session is live, then prove the retained
            // clip geometry remained valid up through that point.
            returnedLessLayer.Dispose();
            helperOuter.Dispose();
            Assert.Equal(new Rect(1, 1, 20, 20), geometryOwner.ComputeBounds());
            geometryOwner.Dispose();
            testResourcesClosed = true;
        }
        finally
        {
            if (!testResourcesClosed)
            {
                CloseLayersInLifoOrder(createdVectorLayers);
                helperOuter.Dispose();
                geometryOwner.Dispose();
            }
        }
    }

    private static MarkdownVectorScene NestedClipScene(
        float size,
        MarkdownVectorPathOperation[] outerClip,
        MarkdownVectorPathOperation[] innerClip) => new(size, size, size,
    [
        MarkdownVectorCommand.BeginClip(outerClip),
        MarkdownVectorCommand.BeginClip(innerClip),
        MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, size, size), 0xFFFF0000),
        MarkdownVectorCommand.EndClip(),
        MarkdownVectorCommand.EndClip(),
    ]);

    private static NativeResourceRetirement CreatePopFailingRetirement(
        InvalidOperationException failure,
        List<CanvasActiveLayer> attempts,
        List<CanvasGeometry> geometryDisposalAttempts) => new(createTimer: false, resource =>
    {
        if (resource is CanvasGeometry geometry)
            geometryDisposalAttempts.Add(geometry);
        if (resource is CanvasActiveLayer layer)
        {
            attempts.Add(layer);
            // Leave the native stack open, unlike a throw-after-close
            // fixture. Production cannot assume a failed Pop succeeded.
            throw failure;
        }
        resource.Dispose();
    });

    private static NativePaintSessionResources.LayerLease CreateHelperOuterLayer(
        MarkdownRenderResources resources,
        CanvasDrawingSession session) => resources.PaintSessions.CreateLayer(
            session,
            1f,
            new Rect(0, 0, 24, 24));

    private static CanvasGeometry[] BorrowGeometries(VectorSceneDrawing drawing)
    {
        var geometrySlots = Assert.IsAssignableFrom<IReadOnlyList<CanvasGeometry?>>(drawing.GeometrySlots);
        return geometrySlots
            .Select(static geometry => Assert.IsType<CanvasGeometry>(geometry))
            .ToArray();
    }

    private static void PaintScene(VectorSceneDrawing drawing, CanvasDrawingSession session, Rect bounds)
        => drawing.Paint(
            new LayoutPlacementTransformContext(),
            session,
            bounds,
            new VectorScenePaintPalette(Black, White, Blue),
            highContrast: false);

    private static void AssertFailedPopIsQuarantinedWithoutRetry(
        VectorSceneDrawing drawing,
        NativeResourceRetirement retirement,
        IReadOnlyList<CanvasActiveLayer?> slots,
        List<CanvasActiveLayer> attempts,
        List<CanvasGeometry> geometryDisposalAttempts)
    {
        Assert.False(drawing.IsPrepared);
        Assert.Throws<InvalidOperationException>(() => drawing.PrepareNativeResources(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => drawing.ReleaseNativeResources());
        Assert.Same(slots[1], Assert.Single(attempts));
        Assert.Empty(geometryDisposalAttempts);
        Assert.Throws<InvalidOperationException>(() => drawing.Dispose());
        Assert.Equal(4, retirement.QuarantinedResourceCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Null(drawing.ClipStackSlots);
        Assert.Null(drawing.GeometrySlots);
        Assert.Empty(geometryDisposalAttempts);
        Assert.False(retirement.DrainOnce());
        Assert.Equal(4, retirement.QuarantinedResourceCount);
        Assert.Same(slots[1], Assert.Single(attempts));
        Assert.Empty(geometryDisposalAttempts);
    }

    private static void AssertFailedPushPoisonsDrawingWithoutRetry(
        VectorSceneDrawing drawing,
        RecordingNativeRetirement recorder,
        NativePaintSessionResources.LayerLease helperOuterLease)
    {
        Assert.Empty(recorder.LayerDisposalAttempts);
        Assert.Empty(recorder.GeometryDisposalAttempts);
        Assert.False(drawing.IsPrepared);
        Assert.Throws<InvalidOperationException>(() => drawing.PrepareNativeResources(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => drawing.ReleaseNativeResources());
        Assert.Empty(recorder.LayerDisposalAttempts);
        Assert.Empty(recorder.GeometryDisposalAttempts);
        Assert.Null(helperOuterLease.Layer);
        Assert.Equal(1, recorder.Retirement.QuarantinedResourceCount);
        Assert.Equal(0, recorder.Retirement.PendingResourceCount);
    }

    private static void AssertTerminalDisposeQuarantinesWithoutRetry(
        VectorSceneDrawing drawing,
        RecordingNativeRetirement recorder,
        CanvasDrawingSession session,
        Matrix3x2 original,
        int expectedQuarantinedResources)
    {
        NativeResourceRetirement retirement = recorder.Retirement;
        Assert.Throws<InvalidOperationException>(() => drawing.Dispose());
        Assert.Equal(original, session.Transform);
        Assert.Empty(recorder.LayerDisposalAttempts);
        Assert.Empty(recorder.GeometryDisposalAttempts);
        Assert.Equal(expectedQuarantinedResources, retirement.QuarantinedResourceCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Null(drawing.ClipStackSlots);
        Assert.Null(drawing.GeometrySlots);
        Assert.Empty(recorder.GeometryDisposalAttempts);
        Assert.False(retirement.DrainOnce());
        Assert.Equal(expectedQuarantinedResources, retirement.QuarantinedResourceCount);
        Assert.Empty(recorder.LayerDisposalAttempts);
        Assert.Empty(recorder.GeometryDisposalAttempts);
    }

    private static void CloseLayersInLifoOrder(IReadOnlyList<CanvasActiveLayer?> layers)
    {
        for (int index = layers.Count - 1; index >= 0; index--)
            layers[index]?.Dispose();
    }

    private static void DisposeAll(IEnumerable<CanvasGeometry> geometries)
    {
        foreach (CanvasGeometry geometry in geometries)
            geometry.Dispose();
    }

    [Fact]
    public void ClipLayerFactoryIsFixedWhenTheDrawingIsCreated()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var scene = new MarkdownVectorScene(8, 8, 8, Array.Empty<MarkdownVectorCommand>());
        Func<CanvasDrawingSession, CanvasGeometry, CanvasActiveLayer> factory =
            static (_, _) => throw new InvalidOperationException("The factory must not run.");
        using var plain = new VectorSceneDrawing(device, scene, CancellationToken.None, _renderResources);
        using var injected = new VectorSceneDrawing(
            device, scene, CancellationToken.None, _renderResources, clipLayerFactory: factory);

        Assert.Null(plain.ClipLayerFactory);
        Assert.Same(factory, injected.ClipLayerFactory);
        Assert.True(plain.IsPrepared);
        Assert.True(injected.IsPrepared);
    }

    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(2f, false)]
    [InlineData(4f, false)]
    [InlineData(1f, true)]
    [InlineData(1.5f, true)]
    [InlineData(2f, true)]
    [InlineData(4f, true)]
    public void NestedClipsDrainRestoreTransformAndReacquireExactPixels(float scale, bool warp)
    {
        using var device = new CanvasDevice(warp);
        var scene = new MarkdownVectorScene(20, 20, 20,
        [
            MarkdownVectorCommand.BeginGroup(new MarkdownVectorTransform(1, 0, 0, 1, 2, 3)),
            MarkdownVectorCommand.BeginClip(RectanglePath(0, 0, 8, 8)),
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20), 0xFFFF0000),
            MarkdownVectorCommand.BeginClip(RectanglePath(2, 2, 4, 4)),
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20), 0xFF0000FF),
            MarkdownVectorCommand.EndClip(),
            MarkdownVectorCommand.EndClip(),
            MarkdownVectorCommand.EndGroup(),
        ]);
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None, _renderResources);
        Color[] expected = Render();
        Assert.Equal(expected, Render());

        drawing.ReleaseNativeResources();
        Assert.False(drawing.IsPrepared);
        drawing.PrepareNativeResources(CancellationToken.None);
        Assert.True(drawing.IsPrepared);
        Assert.Equal(expected, Render());

        Color[] Render()
        {
            using var target = new CanvasRenderTarget(device, 40, 40, 96 * scale);
            using (CanvasDrawingSession ds = target.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(0, 0, 0, 0));
                Matrix3x2 original = Matrix3x2.CreateTranslation(4, 5);
                ds.Transform = original;
                drawing.Paint(new LayoutPlacementTransformContext(), ds, new Rect(0, 0, 20, 20),
                    new VectorScenePaintPalette(Black, White, Blue), highContrast: false);
                Assert.Equal(original, ds.Transform);
                ds.FillRectangle(new Rect(0, 0, 2, 2), Green);
            }

            Color[] pixels = target.GetPixelColors();
            int stride = (int)System.Math.Ceiling(40 * scale);
            Assert.Equal(Green, Sample(4.5f, 5.5f));
            Assert.Equal(Red, Sample(7, 9));
            Assert.Equal(Blue, Sample(9, 11));
            Assert.Equal((byte)0, Sample(16, 9).A);
            Assert.Equal((byte)0, Sample(3, 10).A);
            var clips = Assert.IsAssignableFrom<IReadOnlyList<CanvasActiveLayer?>>(drawing.ClipStackSlots);
            Assert.All(clips, Assert.Null);
            Assert.True(drawing.IsPrepared);
            return pixels;

            Color Sample(float x, float y) => pixels[(int)(y * scale) * stride + (int)(x * scale)];
        }
    }

    private static MarkdownVectorPathOperation[] RectanglePath(float x, float y, float width, float height) =>
    [
        MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(x, y)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x + width, y)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x + width, y + height)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x, y + height)),
        MarkdownVectorPathOperation.Close(),
    ];
}
