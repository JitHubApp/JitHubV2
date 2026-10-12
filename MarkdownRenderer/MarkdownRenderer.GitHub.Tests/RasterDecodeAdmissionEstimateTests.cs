using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class RasterDecodeAdmissionEstimateTests
{
    [Fact]
    public async Task OwnedStaticWeightAccountsForSourceScratchAndOverlappingOutput()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
            width: 37, height: 23, withAlphaEdges: true);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        var sizing = new RasterImageSizingRequest(
            availableWidth: 20,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: 512);

        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out RasterImageDecodePlan plan));
        Assert.True(plan.OutputPixels < plan.SourcePixels);

        long expected = checked(source.Length + plan.SourcePixels * 12 + plan.OutputPixels * 12);
        Assert.Equal(expected, RasterDecodeAdmissionEstimate.ForOwnedStaticWic(source.Length, plan));
    }

    [Fact]
    public void OwnedGifWeightUsesLogicalCanvasAndFirstFrameScratchSeparately()
    {
        OwnedNativeWicGifFixtureFactory.Fixture fixture = OwnedNativeWicGifFixtureFactory.CreateCoreMatrix()[1];
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Bytes);
        var sizing = new RasterImageSizingRequest(
            availableWidth: fixture.CanvasWidth,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: fixture.CanvasWidth * fixture.CanvasHeight);

        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(
            fixture.Bytes, budget, sizing, out RasterGifFirstFrameDecodePlan plan));
        long canvasPixels = (long)plan.CanvasWidth * plan.CanvasHeight;
        long frameSourcePixels = (long)plan.FrameWidth * plan.FrameHeight;
        Assert.True(frameSourcePixels < canvasPixels, "Fixture must distinguish first-frame scratch from logical-canvas size.");

        long expected = checked(
            fixture.Bytes.Length +
            canvasPixels * 4 +
            frameSourcePixels * 8 +
            plan.CanvasOutputPixels * 12);
        Assert.Equal(expected, RasterDecodeAdmissionEstimate.ForOwnedGifWic(fixture.Bytes.Length, plan));
    }

    [Fact]
    public void SchedulingWeightArithmeticIsChecked()
    {
        Assert.Throws<OverflowException>(() =>
            RasterDecodeAdmissionEstimate.CalculateOwnedWicSchedulingWeight(
                long.MaxValue, sourcePixels: 1, scratchPixels: 1, outputPixels: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterDecodeAdmissionEstimate.CalculateOwnedWicSchedulingWeight(
                encodedBytes: 0, sourcePixels: -1, scratchPixels: 0, outputPixels: 0));
    }

    [Fact]
    public void PortableWeightIncludesSourceProviderBoundAndSeparateUploadCopy()
    {
        long sourceBytes = 12_345;
        long providerWorkingBytes = IntPtr.Size == 4 ? 32L * 1024 * 1024 : 64L * 1024 * 1024;
        long uploadBytes = 800L * 400 * 4;

        long weight = RasterDecodeAdmissionEstimate.ForPortableProvider(
            sourceBytes, providerWorkingBytes, uploadBytes);

        Assert.Equal(sourceBytes + providerWorkingBytes + uploadBytes, weight);
        Assert.True(weight > MarkdownImagePreparationAdmission.Shared.CapacityBytes / 2,
            "The full provider bound plus the host upload copy is conservative and may serialize portable decodes.");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterDecodeAdmissionEstimate.ForPortableProvider(-1, providerWorkingBytes, uploadBytes));
        Assert.Throws<OverflowException>(() =>
            RasterDecodeAdmissionEstimate.ForPortableProvider(long.MaxValue, providerWorkingBytes, uploadBytes));
    }

    [Fact]
    public async Task NativeWeightsPermitSmallConcurrencyAndSerializeOversizedGraphsWithoutRejection()
    {
        const long capacity = 1_000;
        var admission = new MarkdownImagePreparationAdmission(capacity, maxConcurrency: 2);
        long smallWeight = RasterDecodeAdmissionEstimate.CalculateOwnedWicSchedulingWeight(
            encodedBytes: 1, sourcePixels: 20, scratchPixels: 20, outputPixels: 10);

        using IDisposable smallA = await admission.EnterAsync(new object(), smallWeight, false, CancellationToken.None);
        using IDisposable smallB = await admission.EnterAsync(new object(), smallWeight, false, CancellationToken.None);
        Assert.Equal(2, admission.ActiveCount);
        Assert.Equal(smallWeight * 2, admission.ActiveBytes);
        smallA.Dispose();
        smallB.Dispose();
        Assert.Equal(0, admission.ActiveBytes);

        long oversizedWeight = RasterDecodeAdmissionEstimate.CalculateOwnedWicSchedulingWeight(
            encodedBytes: 1, sourcePixels: 80, scratchPixels: 80, outputPixels: 10);
        Assert.True(oversizedWeight > capacity);
        object firstOwner = new();
        object waitingOwner = new();
        using IDisposable firstLarge = await admission.EnterAsync(
            firstOwner, oversizedWeight, exclusive: false, CancellationToken.None);
        Assert.Equal(capacity, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);

        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> canceledLarge = admission.EnterAsync(
            waitingOwner, oversizedWeight, exclusive: false, cancellation.Token).AsTask();
        Assert.Equal(1, admission.PendingRequests);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledLarge.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, admission.PendingRequests);
        Assert.Equal(capacity, admission.ActiveBytes);

        Task<IDisposable> queuedLarge = admission.EnterAsync(
            waitingOwner, oversizedWeight, exclusive: false, CancellationToken.None).AsTask();
        Assert.False(queuedLarge.IsCompleted);
        Assert.Equal(1, admission.PendingRequests);
        firstLarge.Dispose();
        using IDisposable secondLarge = await queuedLarge.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(capacity, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);
        Assert.Equal(0, admission.PendingRequests);
        secondLarge.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Equal(0, admission.ActiveCount);
    }
}
