using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class InternalReadonlyStructCarrierSemanticsTests
{
    [Fact]
    public void RasterIntrinsicMetadata_PreservesValueEqualityUsedByPerformanceCache()
    {
        RasterIntrinsicMetadata dimensions = new(Width: 320, Height: 180);
        RasterIntrinsicMetadata sameDimensions = new(320, 180);
        RasterIntrinsicMetadata swappedDimensions = new(180, 320);

        Assert.True(dimensions == sameDimensions);
        Assert.False(dimensions != sameDimensions);
        Assert.Equal(dimensions, sameDimensions);
        Assert.Equal(dimensions.GetHashCode(), sameDimensions.GetHashCode());
        Assert.False(dimensions == swappedDimensions);
        Assert.True(dimensions != swappedDimensions);
    }

    [Fact]
    public void PlacementSourceFences_PreserveOptionalDefaultsAndWithCopies()
    {
        RasterPlacementSourceFence rasterFence = new(
            Image: null!,
            Lease: null,
            SvgMetadata: null,
            IsIdleDescriptor: false,
            IdleLoadDemandGeneration: 0);
        RasterPlacementSourceFence unsubscribedRasterFence = rasterFence with { WasSubscribed = false };

        Assert.True(rasterFence.WasSubscribed);
        Assert.False(unsubscribedRasterFence.WasSubscribed);
        Assert.Equal(rasterFence.Image, unsubscribedRasterFence.Image);
        Assert.Equal(rasterFence.Lease, unsubscribedRasterFence.Lease);
        Assert.Equal(rasterFence.SvgMetadata, unsubscribedRasterFence.SvgMetadata);

        CodeBlockPlacementSourceFence codeFence = new(
            Source: null!,
            Placement: null!,
            Revision: 7);
        CodeBlockPlacementSourceFence unmeasuredCodeFence = codeFence with { WasMeasured = false };

        Assert.True(codeFence.WasMeasured);
        Assert.False(unmeasuredCodeFence.WasMeasured);
        Assert.Equal(7, unmeasuredCodeFence.Revision);
        Assert.Equal(codeFence.Source, unmeasuredCodeFence.Source);
        Assert.Equal(codeFence.Placement, unmeasuredCodeFence.Placement);
    }

    [Fact]
    public void MeasuredPlacementTransfer_PreservesNamedConstructorAndWithUpdates()
    {
        ImageBox.MeasuredPlacementTransfer transfer = new(
            Source: null!,
            Prepared: null!,
            ExpectedSourceLease: null!,
            ExpectedSvgMetadataFence: null,
            ExpectedAvailableWidth: 160f);
        ImageBox.NativeLayoutPlacementFence nativeFence = new(
            Placeholder: null,
            PlaceholderRetirementRecord: null,
            Caption: null,
            CaptionRetirementRecord: null,
            Generation: 3,
            IsValid: true);

        ImageBox.MeasuredPlacementTransfer withNativeFence = transfer with
        {
            ExpectedPreparedNativeLayouts = nativeFence,
        };
        ImageBox.MeasuredPlacementTransfer withPreparedWidth = withNativeFence with
        {
            ExpectedPreparedAvailableWidth = 240f,
        };

        Assert.False(transfer.ExpectedPreparedNativeLayouts.HasValue);
        Assert.Equal(160f, transfer.ExpectedAvailableWidth);
        Assert.Equal(0f, transfer.ExpectedPreparedAvailableWidth);
        Assert.True(withNativeFence.ExpectedPreparedNativeLayouts.HasValue);
        Assert.Equal(3, withNativeFence.ExpectedPreparedNativeLayouts.Value.Generation);
        Assert.True(withNativeFence.ExpectedPreparedNativeLayouts.Value.IsValid);
        Assert.Equal(240f, withPreparedWidth.ExpectedPreparedAvailableWidth);
        Assert.Equal(160f, withPreparedWidth.ExpectedAvailableWidth);
        Assert.Equal(3, withPreparedWidth.ExpectedPreparedNativeLayouts!.Value.Generation);
    }
}
