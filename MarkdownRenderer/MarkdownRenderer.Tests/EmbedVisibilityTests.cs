using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public class EmbedVisibilityTests
{
    // Viewport spans y in [1000, 2000] (height 1000). Overscan 400, derealize overscan 1200.
    private const double VTop = 1000;
    private const double VBottom = 2000;
    private const double Realize = 400;
    private const double Derealize = 1200;

    [Fact]
    public void Plan_InsideViewport_IsInBothBands()
    {
        Assert.True(EmbedVisibility.IsInRealizeBand(1500, 1550, VTop, VBottom, Realize));
        Assert.True(EmbedVisibility.IsInDerealizeBand(1500, 1550, VTop, VBottom, Derealize));
    }

    [Fact]
    public void Plan_JustAboveRealizeBand_FallsOutOfRealize()
    {
        // Just above [600, 2400]
        Assert.False(EmbedVisibility.IsInRealizeBand(500, 599, VTop, VBottom, Realize));
    }

    [Fact]
    public void Plan_AtRealizeBandEdge_IsInside()
    {
        // [600, 2400] → top edge inclusive
        Assert.True(EmbedVisibility.IsInRealizeBand(500, 600, VTop, VBottom, Realize));
        Assert.True(EmbedVisibility.IsInRealizeBand(2400, 2500, VTop, VBottom, Realize));
    }

    [Fact]
    public void HysteresisZone_OutsideRealizeButInsideDerealize()
    {
        // Realize band: [600, 2400]; Derealize band: [-200, 3200]
        // A plan at [400, 500] is above realize band but inside derealize band.
        Assert.False(EmbedVisibility.IsInRealizeBand(400, 500, VTop, VBottom, Realize));
        Assert.True(EmbedVisibility.IsInDerealizeBand(400, 500, VTop, VBottom, Derealize));
    }

    [Fact]
    public void Plan_FarOutsideEverything_IsOutOfBothBands()
    {
        Assert.False(EmbedVisibility.IsInRealizeBand(-5000, -4900, VTop, VBottom, Realize));
        Assert.False(EmbedVisibility.IsInDerealizeBand(-5000, -4900, VTop, VBottom, Derealize));
        Assert.False(EmbedVisibility.IsInRealizeBand(10000, 10100, VTop, VBottom, Realize));
        Assert.False(EmbedVisibility.IsInDerealizeBand(10000, 10100, VTop, VBottom, Derealize));
    }

    [Fact]
    public void DerealizeBand_AlwaysContainsRealizeBand()
    {
        // For every plan, if it's in the realize band it must also be in the derealize band.
        for (double y = -3000; y <= 5000; y += 50)
        {
            double pTop = y, pBottom = y + 25;
            if (EmbedVisibility.IsInRealizeBand(pTop, pBottom, VTop, VBottom, Realize))
                Assert.True(EmbedVisibility.IsInDerealizeBand(pTop, pBottom, VTop, VBottom, Derealize));
        }
    }
}
