using System.Reflection;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

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
        var vector = new InlineVectorSceneRun(
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
        bool imageOwnershipDetached = false;

        try
        {
            Assert.Null(GetVectorState(box));
            box.Add(new TextRun("x") { SourceSpan = new SourceSpan(0, 1) });
            Assert.Null(GetVectorState(box));
            box.Add(image);
            Assert.Null(GetVectorState(box));
            box.Add(vector);

            object state = GetVectorState(box)
                ?? throw new InvalidOperationException("Adding an inline vector did not create its state holder.");
            Assert.Equal(1, ReadField<int>(state, "VectorSceneRunCount"));
            Assert.True(ReadField<bool>(state, "HasDeferredSceneMarker"));
            Assert.Equal(0L, ReadField<long>(state, "SceneGeometryGeneration"));
            Assert.Equal(0L, ReadField<long>(state, "PendingSceneGeometryGeneration"));
            Assert.Equal(0, ReadField<int>(state, "SceneCharacterRegionQueryCount"));
            Assert.Empty(ReadField<InlineSceneGeometry[]>(state, "PendingSceneGeometry"));

            InlineImageRun detachedImage = Assert.Single(box.DetachInlineImagesForTransfer());
            Assert.Same(image, detachedImage);
            imageOwnershipDetached = true;
            Assert.Same(state, GetVectorState(box));
            Assert.Equal(1, box.VectorSceneRunCount);
            Assert.True(box.HasDeferredSceneMarker);
            Assert.Equal(0, box.SceneGeometryGeneration);
            Assert.Equal(0, box.SceneCharacterRegionQueryCount);
            Assert.Empty(ReadField<InlineSceneGeometry[]>(state, "PendingSceneGeometry"));

            _ = box.Measure(320f);
            Assert.True(box.SceneGeometryGeneration > 0);
            Assert.True(box.SceneCharacterRegionQueryCount > 0);
            Assert.Equal(1, box.VectorSceneRunCount);
            Assert.True(box.TryGetCurrentSceneGeometry(out long generation, out InlineSceneGeometry[] geometry));
            Assert.Equal(box.SceneGeometryGeneration, generation);
            Assert.Single(geometry);
            Assert.Same(vector, geometry[0].Run);

            box.ReleasePendingSceneGeometry(generation);
            Assert.Same(state, GetVectorState(box));
            Assert.Empty(ReadField<InlineSceneGeometry[]>(state, "PendingSceneGeometry"));
            Assert.Equal(0L, ReadField<long>(state, "PendingSceneGeometryGeneration"));
            Assert.True(box.SceneCharacterRegionQueryCount > 0);
            Assert.False(box.TryGetCurrentSceneGeometry(
                out _, out InlineSceneGeometry[] releasedGeometry));
            Assert.Empty(releasedGeometry);
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

    private static object? GetVectorState(InlineContainerBox box)
    {
        FieldInfo field = typeof(InlineContainerBox).GetField(
            "_vectorSceneState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InlineContainerBox vector-state field was not found.");
        return field.GetValue(box);
    }

    private static T ReadField<T>(object state, string name)
    {
        FieldInfo field = state.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Vector scene state field '{name}' was not found.");
        return Assert.IsType<T>(field.GetValue(state));
    }
}
