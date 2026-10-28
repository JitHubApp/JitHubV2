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
        object resolver = CreatePrivateInlineVectorResolver();
        string url = GetProperty<string>(resolver, "Url");
        object documentObject = await BuildProgressiveDocumentAsync(
            seed,
            session,
            resolver,
            $"before ![badge]({url}) then $x^2$ after");
        IAsyncDisposable documentLifetime = Assert.IsAssignableFrom<IAsyncDisposable>(documentObject);

        MarkdownLayoutContext? candidateContext = null;
        InlineContainerBox? prepared = null;
        try
        {
            LayoutSnapshot snapshot = GetProperty<LayoutSnapshot>(documentObject, "Snapshot");
            MarkdownLayoutContext sourceContext = GetProperty<MarkdownLayoutContext>(documentObject, "Context");
            InlineContainerBox source = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
            InlineVectorSceneRun sourceVector = Assert.Single(source.Runs.OfType<InlineVectorSceneRun>());
            ImageBox sourceImage = Assert.Single(source.InlineImageRuns).Run.Image;
            await LoadImageAsync(sourceImage);

            CanvasTextLayout sourceLayout = GetField<CanvasTextLayout>(source, "_layout");
            object sourceVectorState = GetObjectField(source, "_vectorSceneState");
            InlineSceneGeometry[] sourcePendingGeometry = GetField<InlineSceneGeometry[]>(
                sourceVectorState, "PendingSceneGeometry");
            long sourceRevision = snapshot.LayoutRevision;
            long sourceSceneGeneration = source.SceneGeometryGeneration;
            int sourceSceneRegionQueries = source.SceneCharacterRegionQueryCount;
            Rect sourceBounds = source.Bounds;
            Rect sourceVectorBounds = sourceVector.LocalBounds;
            float sourceVectorWidth = sourceVector.DesiredWidth;
            float sourceVectorHeight = sourceVector.DesiredHeight;
            float sourceVectorBaseline = sourceVector.DesiredBaseline;

            candidateContext = sourceContext.CreateRasterPlacementContext(
                sourceContext.ImageCancellationToken,
                sourceContext.ImagePreparationOwner!,
                retirementSink: null,
                imageUnavailable: null);
            Assert.True(source.TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(
                candidateContext,
                LayoutWidth,
                out prepared,
                out RasterPlacementSourceFence[]? sourceFences));
            Assert.NotNull(prepared);
            Assert.Single(sourceFences!);
            Assert.True(source.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
                prepared!, LayoutWidth, out InlineContainerBox.MeasuredPlacementTransfer? transfer));
            Assert.NotNull(transfer);
            object preparedVectorState = GetObjectField(prepared!, "_vectorSceneState");
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
            Assert.False(source.IsDirty);
            Assert.Equal(sourceRevision, snapshot.LayoutRevision);
            Assert.Equal(sourceSceneGeneration, source.SceneGeometryGeneration);
            Assert.True(sourceSceneRegionQueries > 0);
            Assert.Equal(sourceSceneRegionQueries, source.SceneCharacterRegionQueryCount);
            Assert.Equal(sourceBounds, source.Bounds);
            Assert.Same(sourceVectorState, GetObjectField(source, "_vectorSceneState"));
            Assert.Same(sourceLayout, GetField<CanvasTextLayout>(source, "_layout"));
            Assert.Same(sourcePendingGeometry, GetField<InlineSceneGeometry[]>(
                sourceVectorState, "PendingSceneGeometry"));
            Assert.Same(sourceVector.Scene, transfer.Source.Runs.OfType<InlineVectorSceneRun>().Single().Scene);
            Assert.Equal(sourceVectorBounds, sourceVector.LocalBounds);
            Assert.Equal(sourceVectorWidth, sourceVector.DesiredWidth);
            Assert.Equal(sourceVectorHeight, sourceVector.DesiredHeight);
            Assert.Equal(sourceVectorBaseline, sourceVector.DesiredBaseline);
        }
        finally
        {
            if (prepared is not null && candidateContext is not null)
                candidateContext.DiscardConstructionBlock(prepared);
            if (candidateContext is not null)
                await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await documentLifetime.DisposeAsync();
        }
    }

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

    private static object CreatePrivateInlineVectorResolver()
    {
        Type resolverType = typeof(MixedInlineVectorImageRelayoutTests).GetNestedType(
            "InlineVectorRasterResolver", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The inline-vector raster test resolver was not found.");
        ConstructorInfo constructor = resolverType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(byte[]) },
            modifiers: null)
            ?? throw new InvalidOperationException("The inline-vector raster test resolver constructor was not found.");
        return constructor.Invoke(new object[] { DisplaySizedRasterDecodeTests.CreatePng(96, 192) });
    }

    private static async Task<object> BuildProgressiveDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        object resolver,
        string source)
    {
        Type testType = typeof(MixedInlineVectorImageRelayoutTests);
        MethodInfo builder = testType.GetMethod(
            "BuildProgressiveLazyDocumentAsync",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The progressive mixed-vector fixture builder was not found.");
        Task operation = Assert.IsAssignableFrom<Task>(builder.Invoke(null, new object[]
        {
            seed,
            session,
            resolver,
            source,
            FlowDirection.LeftToRight,
            1f,
            true,
        }));
        await operation;
        return operation.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(operation)!;
    }

    private static async Task LoadImageAsync(ImageBox image)
    {
        MethodInfo loader = typeof(MixedInlineVectorImageRelayoutTests).GetMethod(
            "LoadImageAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The mixed-vector image load helper was not found.");
        Task operation = Assert.IsAssignableFrom<Task>(loader.Invoke(null, new object[] { image }));
        await operation;
    }

    private static T GetProperty<T>(object instance, string name)
    {
        PropertyInfo property = instance.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test property '{name}'.");
        return Assert.IsType<T>(property.GetValue(instance));
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field '{name}'.");
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static object GetObjectField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field '{name}'.");
        return field.GetValue(instance)
            ?? throw new InvalidOperationException($"Test field '{name}' was null.");
    }

    private static void SetField<T>(object instance, string name, T value)
    {
        FieldInfo field = instance.GetType().GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field '{name}'.");
        field.SetValue(instance, value);
    }

    public enum InvalidGeometry
    {
        EmptyRegions,
        NaNCoordinate,
        NegativeExtent,
        OverflowingRight,
    }
}
