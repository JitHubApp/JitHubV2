using MarkdownRenderer.Controls;
using Microsoft.UI.Xaml.Input;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PointerCaptureSafetyTests
{
    [Fact]
    public void MissingWinUiCaptureView_IsTreatedAsEmpty()
    {
        Assert.False(MarkdownRendererControl.ContainsPointerCapture(null, pointerId: 1));
    }

    [Fact]
    public void EmptyWinUiCaptureView_IsTreatedAsEmpty()
    {
        Assert.False(MarkdownRendererControl.ContainsPointerCapture(Array.Empty<Pointer>(), pointerId: 1));
    }
}
