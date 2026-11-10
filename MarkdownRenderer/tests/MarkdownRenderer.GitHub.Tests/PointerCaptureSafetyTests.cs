using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Input;
using Microsoft.UI.Xaml.Input;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PointerCaptureSafetyTests
{
    [Fact]
    public void MissingWinUiCaptureView_IsTreatedAsEmpty()
    {
        Assert.False(PointerInputClassification.ContainsPointerCapture(null, pointerId: 1));
    }

    [Fact]
    public void EmptyWinUiCaptureView_IsTreatedAsEmpty()
    {
        Assert.False(PointerInputClassification.ContainsPointerCapture(Array.Empty<Pointer>(), pointerId: 1));
    }
}
