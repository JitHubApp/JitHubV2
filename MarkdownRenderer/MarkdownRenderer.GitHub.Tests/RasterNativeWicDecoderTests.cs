using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class RasterNativeWicDecoderTests
{
    [Fact]
    public async Task PreCanceledAndRejectedRequestsConsumeTheirProcessCreditExactlyOnce()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync();
        var sizing = new RasterImageSizingRequest(17, 1, null, null, 1_000);
        Assert.True(RasterImageDecodePlan.TryCreate(source, RasterImageResourceBudget.Validate(source), sizing, out var plan));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledCredit = new RasterNativeWicProductParity.CountingCredit();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            RasterNativeWicDecoder.Decode(source, plan, canceledCredit, cancellation.Token)));
        Assert.Equal(1, canceledCredit.DisposeCount);

        var rejectedCredit = new RasterNativeWicProductParity.CountingCredit();
        await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            RasterNativeWicDecoder.Decode(source, default, rejectedCredit, CancellationToken.None)));
        Assert.Equal(1, rejectedCredit.DisposeCount);

        var nullCredit = new RasterNativeWicProductParity.CountingCredit();
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.Run(() =>
            RasterNativeWicDecoder.Decode(null!, plan, nullCredit, CancellationToken.None)));
        Assert.Equal(1, nullCredit.DisposeCount);
    }

    [Fact]
    public void DefaultAndUnqualifiedPlanCannotStartTheNativePath()
    {
        Assert.False(RasterNativeWicDecoder.IsQualifiedPlan(default));
    }

    [Fact]
    public async Task SameDimensionsFromAnotherContainerCannotUseTheAdmittedPlan()
    {
        byte[] admittedPng = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync();
        byte[] jpeg = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync();
        var sizing = new RasterImageSizingRequest(17, 1, null, null, 1_000);
        Assert.True(RasterImageDecodePlan.TryCreate(admittedPng,
            RasterImageResourceBudget.Validate(admittedPng), sizing, out var plan));
        var credit = new RasterNativeWicProductParity.CountingCredit();
        await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
            RasterNativeWicDecoder.Decode(jpeg, plan, credit, CancellationToken.None)));
        Assert.Equal(1, credit.DisposeCount);
    }
}
