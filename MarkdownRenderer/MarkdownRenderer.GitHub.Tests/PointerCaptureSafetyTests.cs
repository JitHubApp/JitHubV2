using MarkdownRenderer.Controls;
using Microsoft.UI.Xaml.Input;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PointerCaptureSafetyTests
{
    [Fact]
    public void MissingWinUiCaptureView_IsTreatedAsEmpty()
    {
#pragma warning disable MR1001 // Exercises the compatibility control's internal capture helper.
        Assert.False(MarkdownRendererControl.ContainsPointerCapture(null, pointerId: 1));
#pragma warning restore MR1001
    }

    [Fact]
    public void EmptyWinUiCaptureView_IsTreatedAsEmpty()
    {
#pragma warning disable MR1001 // Exercises the compatibility control's internal capture helper.
        Assert.False(MarkdownRendererControl.ContainsPointerCapture(Array.Empty<Pointer>(), pointerId: 1));
#pragma warning restore MR1001
    }
}
