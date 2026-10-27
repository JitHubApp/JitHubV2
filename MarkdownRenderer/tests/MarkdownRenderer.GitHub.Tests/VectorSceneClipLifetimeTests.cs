using System.Numerics;
using System.Reflection;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorSceneClipLifetimeTests
{
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
        var retirement = new NativeResourceRetirement(createTimer: false);
        var scene = new MarkdownVectorScene(20, 20, 20,
        [
            MarkdownVectorCommand.BeginClip(RectanglePath(0, 0, 18, 18)),
            MarkdownVectorCommand.BeginClip(RectanglePath(2, 2, 12, 12)),
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20), 0xFFFF0000),
            MarkdownVectorCommand.EndClip(),
            MarkdownVectorCommand.EndClip(),
        ]);
        var failure = new InvalidOperationException("Injected failed native pop before Close.");
        var attempts = new List<CanvasActiveLayer>();
        var geometryDisposalAttempts = new List<CanvasGeometry>();
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None,
            nativeRetirement: retirement,
            nativeDisposer: resource =>
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
        FieldInfo clipStackField = typeof(VectorSceneDrawing)
            .GetField("_clipStack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo geometryField = typeof(VectorSceneDrawing)
            .GetField("_geometries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var slots = Assert.IsType<CanvasActiveLayer?[]>(clipStackField.GetValue(drawing));
        var geometrySlots = Assert.IsType<CanvasGeometry?[]>(geometryField.GetValue(drawing));
        CanvasGeometry[] borrowedGeometries = geometrySlots
            .Select(static geometry => Assert.IsType<CanvasGeometry>(geometry))
            .ToArray();
        using CanvasDrawingSession session = target.CreateDrawingSession();
        Matrix3x2 original = Matrix3x2.CreateTranslation(1, 1);
        session.Transform = original;
        bool testResourcesClosed = false;
        try
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => drawing.Paint(
                session, new Rect(0, 0, 20, 20),
                new VectorScenePaintPalette(Black, White, Blue), highContrast: false)));
            Assert.Equal(original, session.Transform);
            Assert.Equal(2, slots.Length);
            Assert.All(slots, Assert.NotNull);
            Assert.Equal(2, borrowedGeometries.Length);
            Assert.Empty(geometryDisposalAttempts);
            Assert.Same(slots[1], Assert.Single(attempts));
            Assert.False(drawing.IsPrepared);
            Assert.Throws<InvalidOperationException>(() => drawing.PrepareNativeResources(CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => drawing.ReleaseNativeResources());
            Assert.Same(slots[1], Assert.Single(attempts));
            Assert.Empty(geometryDisposalAttempts);
            Assert.Throws<InvalidOperationException>(() => drawing.Dispose());
            Assert.Equal(4, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingResourceCount);
            Assert.Null(clipStackField.GetValue(drawing));
            Assert.Null(geometryField.GetValue(drawing));
            Assert.Empty(geometryDisposalAttempts);
            Assert.False(retirement.DrainOnce());
            Assert.Equal(4, retirement.QuarantinedResourceCount);
            Assert.Same(slots[1], Assert.Single(attempts));
            Assert.Empty(geometryDisposalAttempts);

            // Only after both active layers have been closed in LIFO order is
            // manual geometry cleanup safe. The terminal owner preserved both
            // clip geometries as well as both active wrappers.
            for (int index = slots.Length - 1; index >= 0; index--)
                slots[index]!.Dispose();
            Assert.Equal(new Rect(0, 0, 18, 18), borrowedGeometries[0].ComputeBounds());
            Assert.Equal(new Rect(2, 2, 12, 12), borrowedGeometries[1].ComputeBounds());
            foreach (CanvasGeometry geometry in borrowedGeometries)
                geometry.Dispose();
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
                for (int index = slots.Length - 1; index >= 0; index--)
                    slots[index]?.Dispose();
                foreach (CanvasGeometry geometry in borrowedGeometries)
                    geometry.Dispose();
            }
        }
    }

    [Fact]
    public void FailedPartialClipFactoryPushQuarantinesKnownAndHelperLayersWithoutRetry()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 24, 24, 96);
        using CanvasDrawingSession session = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var helperDisposalAttempts = new List<CanvasActiveLayer>();
        NativePaintSessionResources.RegisterTestOwner(
            session,
            retirement,
            resource =>
            {
                if (resource is CanvasActiveLayer layer)
                    helperDisposalAttempts.Add(layer);
                resource.Dispose();
            });

        NativePaintSessionResources.LayerLease helperOuterLease = NativePaintSessionResources.CreateLayer(
            session,
            1f,
            new Rect(0, 0, 24, 24));
        CanvasActiveLayer helperOuter = Assert.IsType<CanvasActiveLayer>(helperOuterLease.Layer);
        var scene = new MarkdownVectorScene(24, 24, 24,
        [
            MarkdownVectorCommand.BeginClip(RectanglePath(1, 1, 20, 20)),
            MarkdownVectorCommand.BeginClip(RectanglePath(3, 3, 16, 16)),
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 24, 24), 0xFFFF0000),
            MarkdownVectorCommand.EndClip(),
            MarkdownVectorCommand.EndClip(),
        ]);
        var vectorDisposalAttempts = new List<CanvasActiveLayer>();
        var geometryDisposalAttempts = new List<CanvasGeometry>();
        var createdVectorLayers = new List<CanvasActiveLayer>();
        var factoryFailure = new InvalidOperationException("Injected failure after native partial push.");
        using var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            nativeRetirement: retirement,
            nativeDisposer: resource =>
            {
                if (resource is CanvasGeometry geometry)
                    geometryDisposalAttempts.Add(geometry);
                if (resource is CanvasActiveLayer layer)
                    vectorDisposalAttempts.Add(layer);
                resource.Dispose();
            });
        drawing.SetClipFactoryForTest((drawingSession, geometry) =>
        {
            CanvasActiveLayer layer = drawingSession.CreateLayer(1f, geometry);
            createdVectorLayers.Add(layer);
            if (createdVectorLayers.Count == 2)
                throw factoryFailure;
            return layer;
        });

        FieldInfo clipStackField = typeof(VectorSceneDrawing)
            .GetField("_clipStack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo geometryField = typeof(VectorSceneDrawing)
            .GetField("_geometries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var vectorSlots = Assert.IsType<CanvasActiveLayer?[]>(clipStackField.GetValue(drawing));
        var geometrySlots = Assert.IsType<CanvasGeometry?[]>(geometryField.GetValue(drawing));
        CanvasGeometry[] borrowedGeometries = geometrySlots
            .Select(static geometry => Assert.IsType<CanvasGeometry>(geometry))
            .ToArray();
        Matrix3x2 original = Matrix3x2.CreateTranslation(2.25f, 1.75f);
        session.Transform = original;
        bool testResourcesClosed = false;
        try
        {
            Assert.Same(factoryFailure, Assert.Throws<InvalidOperationException>(() => drawing.Paint(
                session,
                new Rect(0, 0, 24, 24),
                new VectorScenePaintPalette(Black, White, Blue),
                highContrast: false)));

            Assert.Equal(original, session.Transform);
            Assert.Equal(2, createdVectorLayers.Count);
            Assert.NotSame(createdVectorLayers[0], createdVectorLayers[1]);
            Assert.Equal(2, vectorSlots.Length);
            Assert.Same(createdVectorLayers[0], vectorSlots[0]);
            Assert.Null(vectorSlots[1]);
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);
            Assert.Empty(geometryDisposalAttempts);
            Assert.False(drawing.IsPrepared);
            Assert.Throws<InvalidOperationException>(() => drawing.PrepareNativeResources(CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => drawing.ReleaseNativeResources());
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);
            Assert.Empty(geometryDisposalAttempts);
            Assert.Null(helperOuterLease.Layer);
            Assert.Equal(1, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingResourceCount);

            // Terminal disposal may retain the known first clip, but must not
            // guess at a pop for either it or the returned-less second push.
            // Keep these borrowed wrapper references until after that handoff.
            CanvasActiveLayer[] borrowedVectorLayers = createdVectorLayers.ToArray();
            Assert.Throws<InvalidOperationException>(() => drawing.Dispose());
            Assert.Equal(original, session.Transform);
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);
            Assert.Equal(2, borrowedGeometries.Length);
            Assert.Equal(4, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingResourceCount);
            Assert.Null(clipStackField.GetValue(drawing));
            Assert.Null(geometryField.GetValue(drawing));
            Assert.Empty(geometryDisposalAttempts);
            Assert.False(retirement.DrainOnce());
            Assert.Equal(4, retirement.QuarantinedResourceCount);
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);
            Assert.Empty(geometryDisposalAttempts);

            // The injected factory is known to have left exactly these layers
            // open. Close them only while their real drawing session is live,
            // in native LIFO order; the production retirement path never does.
            for (int index = borrowedVectorLayers.Length - 1; index >= 0; index--)
                borrowedVectorLayers[index].Dispose();
            helperOuter.Dispose();
            Assert.Equal(new Rect(1, 1, 20, 20), borrowedGeometries[0].ComputeBounds());
            Assert.Equal(new Rect(3, 3, 16, 16), borrowedGeometries[1].ComputeBounds());
            foreach (CanvasGeometry geometry in borrowedGeometries)
                geometry.Dispose();
            testResourcesClosed = true;
        }
        finally
        {
            // If an assertion fails before the explicit terminal handoff,
            // still close only the layers whose successful pushes this test
            // observed, and only while the session remains open.
            if (!testResourcesClosed)
            {
                for (int index = createdVectorLayers.Count - 1; index >= 0; index--)
                    createdVectorLayers[index].Dispose();
                helperOuter.Dispose();
                foreach (CanvasGeometry geometry in borrowedGeometries)
                    geometry.Dispose();
            }
        }
    }

    [Fact]
    public void FirstClipFactoryPushWithoutReturnedWrapperQuarantinesGeometryUntilLayerCleanup()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 24, 24, 96);
        using CanvasDrawingSession session = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var helperDisposalAttempts = new List<CanvasActiveLayer>();
        NativePaintSessionResources.RegisterTestOwner(
            session,
            retirement,
            resource =>
            {
                if (resource is CanvasActiveLayer layer)
                    helperDisposalAttempts.Add(layer);
                resource.Dispose();
            });

        NativePaintSessionResources.LayerLease helperOuterLease = NativePaintSessionResources.CreateLayer(
            session,
            1f,
            new Rect(0, 0, 24, 24));
        CanvasActiveLayer helperOuter = Assert.IsType<CanvasActiveLayer>(helperOuterLease.Layer);
        var scene = new MarkdownVectorScene(24, 24, 24,
        [
            MarkdownVectorCommand.BeginClip(RectanglePath(1, 1, 20, 20)),
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 24, 24), 0xFFFF0000),
            MarkdownVectorCommand.EndClip(),
        ]);
        var vectorDisposalAttempts = new List<CanvasActiveLayer>();
        var geometryDisposalAttempts = new List<CanvasGeometry>();
        var createdVectorLayers = new List<CanvasActiveLayer>();
        var factoryFailure = new InvalidOperationException("Injected failure after the first native clip push.");
        using var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            nativeRetirement: retirement,
            nativeDisposer: resource =>
            {
                if (resource is CanvasActiveLayer layer)
                    vectorDisposalAttempts.Add(layer);
                if (resource is CanvasGeometry geometry)
                    geometryDisposalAttempts.Add(geometry);
                resource.Dispose();
            });
        drawing.SetClipFactoryForTest((drawingSession, geometry) =>
        {
            CanvasActiveLayer layer = drawingSession.CreateLayer(1f, geometry);
            createdVectorLayers.Add(layer);
            throw factoryFailure;
        });

        FieldInfo clipStackField = typeof(VectorSceneDrawing)
            .GetField("_clipStack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo geometryField = typeof(VectorSceneDrawing)
            .GetField("_geometries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var vectorSlots = Assert.IsType<CanvasActiveLayer?[]>(clipStackField.GetValue(drawing));
        var geometrySlots = Assert.IsType<CanvasGeometry?[]>(geometryField.GetValue(drawing));
        CanvasGeometry geometryOwner = Assert.IsType<CanvasGeometry>(Assert.Single(geometrySlots));
        Matrix3x2 original = Matrix3x2.CreateTranslation(3.5f, 2.25f);
        session.Transform = original;
        bool testResourcesClosed = false;
        try
        {
            Assert.Same(factoryFailure, Assert.Throws<InvalidOperationException>(() => drawing.Paint(
                session,
                new Rect(0, 0, 24, 24),
                new VectorScenePaintPalette(Black, White, Blue),
                highContrast: false)));

            Assert.Equal(original, session.Transform);
            Assert.Single(createdVectorLayers);
            Assert.Single(vectorSlots);
            Assert.Null(vectorSlots[0]);
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(geometryDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);
            Assert.False(drawing.IsPrepared);
            Assert.Throws<InvalidOperationException>(() => drawing.PrepareNativeResources(CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => drawing.ReleaseNativeResources());
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(geometryDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);
            Assert.Null(helperOuterLease.Layer);
            Assert.Equal(1, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingResourceCount);

            CanvasActiveLayer returnedLessLayer = Assert.Single(createdVectorLayers);
            Assert.Throws<InvalidOperationException>(() => drawing.Dispose());
            Assert.Equal(original, session.Transform);
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(geometryDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);
            Assert.Equal(2, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingResourceCount);
            Assert.Null(clipStackField.GetValue(drawing));
            Assert.Null(geometryField.GetValue(drawing));
            Assert.False(retirement.DrainOnce());
            Assert.Equal(2, retirement.QuarantinedResourceCount);
            Assert.Empty(vectorDisposalAttempts);
            Assert.Empty(geometryDisposalAttempts);
            Assert.Empty(helperDisposalAttempts);

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
                for (int index = createdVectorLayers.Count - 1; index >= 0; index--)
                    createdVectorLayers[index].Dispose();
                helperOuter.Dispose();
                geometryOwner.Dispose();
            }
        }
    }

    [Fact]
    public void ClipFactoryFaultSeamRejectsSharedProductionRetirementOwner()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var scene = new MarkdownVectorScene(8, 8, 8, Array.Empty<MarkdownVectorCommand>());
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => drawing.SetClipFactoryForTest(
            static (_, _) => throw new InvalidOperationException("The factory must not run.")));
        Assert.True(drawing.IsPrepared);
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
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None);
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
                drawing.Paint(ds, new Rect(0, 0, 20, 20),
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
            var clips = Assert.IsType<CanvasActiveLayer?[]>(typeof(VectorSceneDrawing)
                .GetField("_clipStack", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(drawing));
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
