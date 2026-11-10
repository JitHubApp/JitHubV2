using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class VectorSceneStateTests
{
    [Fact]
    public async Task TextOnlyBoxKeepsVectorStateAbsentAcrossMeasurementAndCleanup()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        MarkdownLayoutContext context = fixture.SourceContext;
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };

        try
        {
            Assert.Null(GetVectorState(box));
            Assert.Equal(0, box.VectorSceneRunCount);
            Assert.Equal(0, box.SceneCharacterRegionQueryCount);
            Assert.False(box.HasDeferredSceneMarker);
            Assert.False(box.TryGetCurrentSceneGeometry(out long initialGeneration, out InlineSceneGeometry[] initialGeometry));
            Assert.Equal(0L, initialGeneration);
            Assert.Empty(initialGeometry);
            box.ReleasePendingSceneGeometry(0);
            Assert.Null(GetVectorState(box));

            box.Add(new TextRun("plain text")
            {
                SourceSpan = new SourceSpan(0, 10),
            });

            Assert.Null(GetVectorState(box));
            Assert.Equal(0, box.VectorSceneRunCount);
            _ = box.Measure(320f);

            Assert.True(box.HasMeasuredLayout);
            Assert.Null(GetVectorState(box));
            Assert.Equal(0, box.SceneGeometryGeneration);
            Assert.Equal(0, box.SceneCharacterRegionQueryCount);
            Assert.False(box.TryCaptureCurrentSceneGeometry(
                CancellationToken.None,
                out long capturedGeneration,
                out InlineSceneGeometry[] capturedGeometry));
            Assert.Equal(0L, capturedGeneration);
            Assert.Empty(capturedGeometry);
            Assert.Null(GetVectorState(box));
        }
        finally
        {
            box.Dispose();
        }

        // Dispose resets vector state when present, but must not materialize a
        // holder for an ordinary text-only paragraph.
        Assert.Null(GetVectorState(box));
    }

    [Fact]
    public async Task VectorStateIsBoxOwnedAndImageDetachRebuildPreservesTelemetry()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        MarkdownLayoutContext context = fixture.SourceContext;
        context.UseProgressiveNativeSceneResidency = true;
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var image = new InlineImageRun(context, "badge", fixture.RasterSource)
        {
            SourceSpan = new SourceSpan(0, 1),
        };
        InlineVectorSceneRun vector = CreateDeferredFormulaRun(context);
        bool imageOwnershipDetached = false;

        try
        {
            Assert.Null(GetVectorState(box));
            box.Add(new TextRun("x") { SourceSpan = new SourceSpan(0, 1) });
            Assert.Null(GetVectorState(box));
            box.Add(image);
            Assert.Null(GetVectorState(box));
            box.Add(vector);

            InlineVectorSceneState state = GetVectorState(box)
                ?? throw new InvalidOperationException("Adding an inline vector did not create its state holder.");
            AssertFreshDeferredVectorState(state);

            InlineImageRun detachedImage = Assert.Single(box.DetachInlineImagesForTransfer());
            Assert.Same(image, detachedImage);
            imageOwnershipDetached = true;
            Assert.Same(state, GetVectorState(box));
            Assert.Equal(1, box.VectorSceneRunCount);
            Assert.True(box.HasDeferredSceneMarker);
            Assert.Equal(0, box.SceneGeometryGeneration);
            Assert.Equal(0, box.SceneCharacterRegionQueryCount);
            Assert.Empty(state.PendingSceneGeometry);

            _ = box.Measure(320f);
            long generation = AssertMeasuredVectorGeometry(box, vector);

            box.ReleasePendingSceneGeometry(generation);
            AssertReleasedVectorGeometryKeepsTelemetry(box, state);
        }
        finally
        {
            try
            {
                box.Dispose();
            }
            finally
            {
                // Detach transfers ImageBox ownership out of the container.
                // Dispose only that detached image here; otherwise box cleanup
                // remains responsible for it.
                if (imageOwnershipDetached)
                    image.Image.Dispose();
            }
        }
    }

    private static InlineVectorSceneState? GetVectorState(InlineContainerBox box)
        => box.VectorSceneStateHolder;

    private static InlineVectorSceneRun CreateDeferredFormulaRun(MarkdownLayoutContext context)
        => new(
            context,
            new MarkdownVectorScene(24f, 12f, 9f, Array.Empty<MarkdownVectorCommand>()),
            semanticText: "formula",
            accessibilityName: "formula",
            accessibilityDescription: null,
            elementKey: MarkdownElementKeys.Math)
        {
            SourceSpan = new SourceSpan(1, 1),
            DeferredSceneMarker = "test-deferred-scene",
        };

    private static void AssertFreshDeferredVectorState(InlineVectorSceneState state)
    {
        Assert.Equal(1, state.VectorSceneRunCount);
        Assert.True(state.HasDeferredSceneMarker);
        Assert.Equal(0L, state.SceneGeometryGeneration);
        Assert.Equal(0L, state.PendingSceneGeometryGeneration);
        Assert.Equal(0, state.SceneCharacterRegionQueryCount);
        Assert.Empty(state.PendingSceneGeometry);
    }

    private static long AssertMeasuredVectorGeometry(InlineContainerBox box, InlineVectorSceneRun vector)
    {
        Assert.True(box.SceneGeometryGeneration > 0);
        Assert.True(box.SceneCharacterRegionQueryCount > 0);
        Assert.Equal(1, box.VectorSceneRunCount);
        Assert.True(box.TryGetCurrentSceneGeometry(out long generation, out InlineSceneGeometry[] geometry));
        Assert.Equal(box.SceneGeometryGeneration, generation);
        Assert.Single(geometry);
        Assert.Same(vector, geometry[0].Run);
        return generation;
    }

    private static void AssertReleasedVectorGeometryKeepsTelemetry(
        InlineContainerBox box,
        InlineVectorSceneState state)
    {
        Assert.Same(state, GetVectorState(box));
        Assert.Empty(state.PendingSceneGeometry);
        Assert.Equal(0L, state.PendingSceneGeometryGeneration);
        Assert.True(box.SceneCharacterRegionQueryCount > 0);
        Assert.False(box.TryGetCurrentSceneGeometry(
            out _, out InlineSceneGeometry[] releasedGeometry));
        Assert.Empty(releasedGeometry);
    }
}
