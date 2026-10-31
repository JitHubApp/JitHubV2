using System.Buffers.Binary;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(NativeWicDecodeTestCollection.CollectionName)]
public sealed class RasterGifFirstFrameDecodePlanTests
{
    [Theory]
    [InlineData(0.75, 3)]
    [InlineData(1.0, 4)]
    [InlineData(1.25, 5)]
    [InlineData(1.5, 6)]
    [InlineData(1.75, 7)]
    [InlineData(2.0, 8)]
    [InlineData(2.25, 8)]
    [InlineData(3.0, 8)]
    [InlineData(4.0, 8)]
    public void LogicalAndFirstFrameSpacesStayIndependentAcrossPhysicalScales(double scale, int canvasSize)
    {
        byte[] bytes = OffsetGif();
        var sizing = new RasterImageSizingRequest(4, scale, null, null, 64);
        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(bytes,
            RasterImageResourceBudget.Validate(bytes), sizing, out var plan));
        Assert.Equal((8, 8), (plan.CanvasWidth, plan.CanvasHeight));
        Assert.Equal((2, 1, 2, 3), (plan.FrameLeft, plan.FrameTop, plan.FrameWidth, plan.FrameHeight));
        Assert.Equal((canvasSize, canvasSize), (plan.OutputWidth, plan.OutputHeight));
        Assert.Equal(System.Math.Max(1, (int)System.Math.Ceiling(2d * canvasSize / 8)), plan.DecodeWidth);
        Assert.Equal(System.Math.Max(1, (int)System.Math.Ceiling(3d * canvasSize / 8)), plan.DecodeHeight);
        Assert.Equal((long)canvasSize * canvasSize, plan.CanvasOutputPixels);
        Assert.InRange(plan.FrameOutputPixels, 1, plan.CanvasOutputPixels);
        Assert.True(plan.RequiresComposition);
    }

    [Fact]
    public void OnePixelCanvasRetainsDescriptorGeometryForFractionalComposition()
    {
        byte[] bytes = OffsetGif();
        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(bytes,
            RasterImageResourceBudget.Validate(bytes), new(4, 1, null, null, 1), out var plan));
        Assert.Equal((1, 1), (plan.OutputWidth, plan.OutputHeight));
        Assert.Equal((1, 1), (plan.DecodeWidth, plan.DecodeHeight));
        Assert.Equal((2, 1, 2, 3), (plan.FrameLeft, plan.FrameTop, plan.FrameWidth, plan.FrameHeight));
        Assert.True(plan.RequiresComposition);
    }

    [Fact]
    public void DefaultPolicyAndOutsideCanvasCannotCreateAPlan()
    {
        byte[] bytes = OffsetGif();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(bytes);
        Assert.False(RasterGifFirstFrameDecodePlan.TryCreate(bytes, budget, default, out _));
        Assert.False(RasterGifFirstFrameDecodePlan.TryCreate(Array.Empty<byte>(), budget,
            new(4, 1, null, null, 64), out _));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 1);
        Assert.False(RasterGifFirstFrameDecodePlan.TryCreate(bytes,
            RasterImageResourceBudget.Validate(bytes), new(4, 1, null, null, 64), out _));
        Assert.False(default(RasterGifFirstFrameDecodePlan).MatchesValidatedBudget(budget));
    }

    [Fact]
    public async Task WorkerRevalidatesTruncatedSourceBeforeFactoryAndRefundsCreditOnce()
    {
        byte[] bytes = OffsetGif();
        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(bytes,
            RasterImageResourceBudget.Validate(bytes), new(4, 1, null, null, 64), out var plan));
        byte[] truncated = bytes[..^1]; // A valid frame must not hide a missing trailer.
        var credit = new RasterNativeWicProductParity.CountingCredit();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        var before = RasterNativeWicDiagnostics.GetSnapshot();
#endif
        await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            RasterNativeWicDecoder.DecodeGifFirstFrame(truncated, plan, credit, CancellationToken.None)));
        Assert.Equal(1, credit.DisposeCount);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        Assert.Equal(before, RasterNativeWicDiagnostics.GetSnapshot());
#endif
    }

    [Fact]
    public async Task CancellationDefaultPlanAndNullSourceConsumeCreditBeforeAnyNativeCreation()
    {
        byte[] bytes = OffsetGif();
        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(bytes,
            RasterImageResourceBudget.Validate(bytes), new(4, 1, null, null, 64), out var plan));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = new RasterNativeWicProductParity.CountingCredit();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            RasterNativeWicDecoder.DecodeGifFirstFrame(bytes, plan, canceled, cancellation.Token)));
        Assert.Equal(1, canceled.DisposeCount);
        var rejected = new RasterNativeWicProductParity.CountingCredit();
        await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            RasterNativeWicDecoder.DecodeGifFirstFrame(bytes, default, rejected, CancellationToken.None)));
        Assert.Equal(1, rejected.DisposeCount);
        var nullSource = new RasterNativeWicProductParity.CountingCredit();
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.Run(() =>
            RasterNativeWicDecoder.DecodeGifFirstFrame(null!, plan, nullSource, CancellationToken.None)));
        Assert.Equal(1, nullSource.DisposeCount);
    }

    private static byte[] OffsetGif()
    {
        byte[] bytes = Convert.FromBase64String(
            "R0lGODlhAgADAHAAACH5BAEAAAEALAAAAAACAAMAgf8AAAAAAAAAAAAAAAIChF8AOw==");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24, 2), 1);
        return bytes;
    }
}
