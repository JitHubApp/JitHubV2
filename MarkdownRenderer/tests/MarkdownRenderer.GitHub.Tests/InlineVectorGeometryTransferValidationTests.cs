using System.Reflection;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class InlineVectorGeometryTransferValidationTests
{
    private const float LayoutWidth = 320f;

    [Theory]
    [InlineData(InvalidGeometry.EmptyRegions)]
    [InlineData(InvalidGeometry.NaNCoordinate)]
    [InlineData(InvalidGeometry.NegativeExtent)]
    [InlineData(InvalidGeometry.OverflowingRight)]
    public async Task MeasuredVectorTransferRejectsInvalidCandidateRegionsBeforeSourceMutation(
        InvalidGeometry invalidGeometry)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new InlineVectorRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        MixedInlineVectorImageDocument document = await MixedInlineVectorImageDocument.BuildProgressiveLazyAsync(
            seed,
            session,
            resolver,
            $"before ![badge]({resolver.Url}) then $x^2$ after",
            FlowDirection.LeftToRight,
            rasterizationScale: 1f,
            materializeAllInlineMarkers: true);

        MarkdownLayoutContext? candidateContext = null;
        InlineContainerBox? prepared = null;
        try
        {
            LayoutSnapshot snapshot = document.Snapshot;
            MarkdownLayoutContext sourceContext = document.Context;
            InlineContainerBox source = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
            InlineVectorSceneRun sourceVector = Assert.Single(source.Runs.OfType<InlineVectorSceneRun>());
            ImageBox sourceImage = Assert.Single(source.InlineImageRuns).Run.Image;
            await ImageRelayoutTestSupport.LoadImageAsync(sourceImage);

            SourceVectorPlacement sourceBefore = SourceVectorPlacement.Capture(snapshot, source, sourceVector);
            object sourceVectorState = sourceBefore.VectorState;
            InlineSceneGeometry[] sourcePendingGeometry = sourceBefore.PendingGeometry;

            candidateContext = CreateCandidateContext(sourceContext);
            Assert.True(source.TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(
                candidateContext,
                LayoutWidth,
                out prepared,
                out RasterPlacementSourceFence[]? sourceFences));
            Assert.NotNull(prepared);
            Assert.Single(sourceFences!);
            Assert.True(source.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
                prepared!, LayoutWidth, out InlineMeasuredPlacementTransfer? transfer));
            Assert.NotNull(transfer);
            object preparedVectorState = prepared!.VectorSceneStateHolder!;
            Assert.NotSame(sourceVectorState, preparedVectorState);
            InlineSceneGeometry[] initialPreparedGeometry = GetField<InlineSceneGeometry[]>(
                preparedVectorState, "PendingSceneGeometry");
            Assert.Empty(initialPreparedGeometry);
            // The immutable empty array is intentionally shared; ownership
            // isolation applies to the holder and populated geometry arrays.
            Assert.Same(Array.Empty<InlineSceneGeometry>(), initialPreparedGeometry);

            _ = prepared!.Measure(LayoutWidth);
            prepared.Arrange(0f, 0f, LayoutWidth);
            InlineVectorSceneRun preparedVector = Assert.Single(prepared.Runs.OfType<InlineVectorSceneRun>());
            InlineSceneGeometry[] candidateGeometry = GetField<InlineSceneGeometry[]>(
                preparedVectorState, "PendingSceneGeometry");
            Assert.Single(candidateGeometry);
            Assert.NotSame(sourcePendingGeometry, candidateGeometry);
            Assert.True(candidateGeometry[0].RegionCount > 0);
            Assert.True(transfer!.TryPrepareMeasuredVectorGeometry());

            var malformed = (InlineSceneGeometry[])candidateGeometry.Clone();
            malformed[0] = CreateInvalidGeometry(preparedVector, invalidGeometry);
            SetField(preparedVectorState, "PendingSceneGeometry", malformed);

            Assert.False(transfer!.TryPrepareMeasuredVectorGeometry());

            // Preparation is candidate-only. Rejection must leave the live
            // semantic run, native drawing/layout owners and source geometry
            // generation untouched for the ordinary fallback/retry path.
            sourceBefore.AssertUnchanged(transfer);
        }
        finally
        {
            if (prepared is not null && candidateContext is not null)
                candidateContext.DiscardConstructionBlock(prepared);
            if (candidateContext is not null)
                await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await document.DisposeAsync();
        }
    }

    private static MarkdownLayoutContext CreateCandidateContext(MarkdownLayoutContext sourceContext) =>
        sourceContext.CreateRasterPlacementContext(
            sourceContext.ImageCancellationToken,
            sourceContext.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);

    private static InlineSceneGeometry CreateInvalidGeometry(
        InlineVectorSceneRun run,
        InvalidGeometry invalidGeometry) => invalidGeometry switch
    {
        InvalidGeometry.EmptyRegions => new InlineSceneGeometry(run, Array.Empty<Rect>()),
        InvalidGeometry.NaNCoordinate => new InlineSceneGeometry(
            run, new[] { new Rect(double.NaN, 0d, 12d, 10d) }),
        InvalidGeometry.NegativeExtent => new InlineSceneGeometry(
            run, new[] { WithCorruptedNegativeWidth(new Rect(0d, 0d, 12d, 10d)) }),
        InvalidGeometry.OverflowingRight => new InlineSceneGeometry(
            run, new[] { new Rect(double.MaxValue, 0d, double.MaxValue, 10d) }),
        _ => throw new ArgumentOutOfRangeException(nameof(invalidGeometry)),
    };

    private static Rect WithCorruptedNegativeWidth(Rect validRect)
    {
        object boxedRect = validRect;
        FieldInfo widthField = Assert.Single(typeof(Rect).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            static field =>
                field.Name.Contains("width", StringComparison.OrdinalIgnoreCase) &&
                (field.FieldType == typeof(float) || field.FieldType == typeof(double)));
        widthField.SetValue(boxedRect, widthField.FieldType == typeof(float) ? (object)(-1f) : -1d);
        return Assert.IsType<Rect>(boxedRect);
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field '{name}'.");
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static void SetField<T>(object instance, string name, T value)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field '{name}'.");
        field.SetValue(instance, value);
    }

    /// <summary>
    /// The live source paragraph's vector placement, captured before candidate
    /// preparation so a rejected transfer can prove it never touched the source.
    /// </summary>
    private sealed record SourceVectorPlacement(
        LayoutSnapshot Snapshot,
        InlineContainerBox Source,
        InlineVectorSceneRun Vector,
        CanvasTextLayout Layout,
        object VectorState,
        InlineSceneGeometry[] PendingGeometry,
        long Revision,
        long SceneGeneration,
        int SceneRegionQueries,
        Rect Bounds,
        Rect VectorBounds,
        float VectorWidth,
        float VectorHeight,
        float VectorBaseline)
    {
        internal static SourceVectorPlacement Capture(
            LayoutSnapshot snapshot,
            InlineContainerBox source,
            InlineVectorSceneRun sourceVector)
        {
            object vectorState = source.VectorSceneStateHolder!;
            return new(
                snapshot,
                source,
                sourceVector,
                source.TextLayout!,
                vectorState,
                GetField<InlineSceneGeometry[]>(vectorState, "PendingSceneGeometry"),
                snapshot.LayoutRevision,
                source.SceneGeometryGeneration,
                source.SceneCharacterRegionQueryCount,
                source.Bounds,
                sourceVector.LocalBounds,
                sourceVector.DesiredWidth,
                sourceVector.DesiredHeight,
                sourceVector.DesiredBaseline);
        }

        internal void AssertUnchanged(InlineMeasuredPlacementTransfer transfer)
        {
            Assert.False(Source.IsDirty);
            Assert.Equal(Revision, Snapshot.LayoutRevision);
            Assert.Equal(SceneGeneration, Source.SceneGeometryGeneration);
            Assert.True(SceneRegionQueries > 0);
            Assert.Equal(SceneRegionQueries, Source.SceneCharacterRegionQueryCount);
            Assert.Equal(Bounds, Source.Bounds);
            Assert.Same(VectorState, Source.VectorSceneStateHolder);
            Assert.Same(Layout, Source.TextLayout!);
            Assert.Same(PendingGeometry, GetField<InlineSceneGeometry[]>(
                VectorState, "PendingSceneGeometry"));
            Assert.Same(Vector.Scene, transfer.Source.Runs.OfType<InlineVectorSceneRun>().Single().Scene);
            Assert.Equal(VectorBounds, Vector.LocalBounds);
            Assert.Equal(VectorWidth, Vector.DesiredWidth);
            Assert.Equal(VectorHeight, Vector.DesiredHeight);
            Assert.Equal(VectorBaseline, Vector.DesiredBaseline);
        }
    }

    public enum InvalidGeometry
    {
        EmptyRegions,
        NaNCoordinate,
        NegativeExtent,
        OverflowingRight,
    }
}
