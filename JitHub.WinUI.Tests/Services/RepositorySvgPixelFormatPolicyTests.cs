using JitHub.Services.CodeViewer;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositorySvgPixelFormatPolicyTests
{
    [Fact]
    public void Select_UsesRgbaForHardwareDeviceThatSupportsIt()
    {
        MarkdownSvgPixelFormat result = RepositorySvgPixelFormatPolicy.Select(
            forceSoftwareRenderer: false,
            rgbaSupported: true);

        Assert.Equal(MarkdownSvgPixelFormat.Rgba8Premultiplied, result);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Select_UsesBgraForSoftwareOrUnsupportedDevice(
        bool forceSoftwareRenderer,
        bool rgbaSupported)
    {
        MarkdownSvgPixelFormat result = RepositorySvgPixelFormatPolicy.Select(
            forceSoftwareRenderer,
            rgbaSupported);

        Assert.Equal(MarkdownSvgPixelFormat.Bgra8Premultiplied, result);
    }
}
