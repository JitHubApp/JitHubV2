using JitHub.Core.Models.Startup;
using JitHub.Core.Services.Startup;
using Xunit;

namespace JitHub.Core.Tests.Services.Startup;

public sealed class StartupWindowPlacementTests
{
    [Theory]
    [InlineData(96u, 1360, 900)]
    [InlineData(144u, 2040, 1350)]
    [InlineData(192u, 2720, 1800)]
    public void Calculate_UsesDefaultSizeAtEachScale(uint dpi, int expectedWidth, int expectedHeight)
    {
        StartupWindowBounds workArea = new(0, 0, 3840, 2160);

        StartupWindowBounds bounds = StartupWindowPlacement.Calculate(workArea, dpi);

        Assert.Equal(expectedWidth, bounds.Width);
        Assert.Equal(expectedHeight, bounds.Height);
    }

    [Fact]
    public void Calculate_ClampsToWorkAreaFraction()
    {
        StartupWindowBounds bounds = StartupWindowPlacement.Calculate(new StartupWindowBounds(0, 0, 1280, 720), 96);

        Assert.Equal(1126, bounds.Width);
        Assert.Equal(634, bounds.Height);
    }

    [Fact]
    public void Calculate_CentersWithinOffsetMonitor()
    {
        StartupWindowBounds workArea = new(-1920, 100, 1920, 1040);

        StartupWindowBounds bounds = StartupWindowPlacement.Calculate(workArea, 96);

        Assert.Equal(-1920 + ((1920 - bounds.Width) / 2), bounds.X);
        Assert.Equal(100 + ((1040 - bounds.Height) / 2), bounds.Y);
    }
}
