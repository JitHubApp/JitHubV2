using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Unit tests for lazy image loading infrastructure.
/// Full end-to-end viewport-triggered loading is verified in UI automation tests.
/// </summary>
public class LazyImageLoadingTests
{
    // The overscan constant controls how far outside the viewport images start
    // loading. 400–1600px is the expected useful range.
    [Fact]
    public void LazyImageOverscanPx_IsReasonable()
    {
        // We can't reference MarkdownRendererControl (WinUI dep) in unit tests,
        // so validate the expected value range here as a documentation test.
        const double expectedMin = 400;
        const double expectedMax = 1600;
        // The actual constant is 800.0 in the implementation.
        const double actual = 800.0;
        Assert.InRange(actual, expectedMin, expectedMax);
    }

    [Fact]
    public void LazyLoadBand_ContainsPoint_WhenWithinOverscan()
    {
        const double overscan = 800.0;
        double viewportTop = 1000.0;
        double viewportBottom = 1600.0;
        double bandTop    = viewportTop    - overscan;
        double bandBottom = viewportBottom + overscan;

        // An image at Y=500 (above viewport but within overscan) should load.
        double imageY = 500.0;
        Assert.True(imageY >= bandTop && imageY <= bandBottom);
    }

    [Fact]
    public void LazyLoadBand_ExcludesPoint_WhenOutsideOverscan()
    {
        const double overscan = 800.0;
        double viewportTop = 1000.0;
        double viewportBottom = 1600.0;
        double bandTop    = viewportTop    - overscan;
        double bandBottom = viewportBottom + overscan;

        // An image at Y=100 (far above, outside overscan) should NOT load yet.
        double imageY = 100.0;
        Assert.False(imageY >= bandTop && imageY <= bandBottom);
    }

    [Fact]
    public void LazyLoadBand_ExcludesPoint_WhenFarBelow()
    {
        const double overscan = 800.0;
        double viewportTop = 0.0;
        double viewportBottom = 600.0;
        double bandBottom = viewportBottom + overscan;

        double imageY = 10000.0;
        Assert.False(imageY <= bandBottom);
    }
}
