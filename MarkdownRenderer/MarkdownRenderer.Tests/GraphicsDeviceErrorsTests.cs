using MarkdownRenderer.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace MarkdownRenderer.Tests;

public class GraphicsDeviceErrorsTests
{
    [Theory]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDeviceRemoved)]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDeviceHung)]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDeviceReset)]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDriverInternalError)]
    [InlineData(GraphicsDeviceErrors.D2DErrorRecreateTarget)]
    [InlineData(GraphicsDeviceErrors.D3DErrorDeviceLost)]
    [InlineData(GraphicsDeviceErrors.D3DErrorDeviceNotReset)]
    public void IsDeviceLostHResult_KnownTransientGraphicsFailures_ReturnsTrue(int hresult)
    {
        Assert.True(GraphicsDeviceErrors.IsDeviceLostHResult(hresult));
    }

    [Fact]
    public void IsDeviceLost_WalksInnerExceptions()
    {
        var inner = new COMException("device removed", GraphicsDeviceErrors.DxgiErrorDeviceRemoved);
        var outer = new Exception("wrapper", inner);

        Assert.True(GraphicsDeviceErrors.IsDeviceLost(outer));
    }

    [Fact]
    public void IsDeviceLostHResult_NonGraphicsFailure_ReturnsFalse()
    {
        Assert.False(GraphicsDeviceErrors.IsDeviceLostHResult(unchecked((int)0x80004005)));
    }

    [Fact]
    public void FormatHResult_UsesUnsignedHex()
    {
        Assert.Equal("0x887A0005", GraphicsDeviceErrors.FormatHResult(GraphicsDeviceErrors.DxgiErrorDeviceRemoved));
    }
}
