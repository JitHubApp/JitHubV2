using MarkdownRenderer.Controls.Input;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class NativePointerCancellationTests
{
    [Theory]
    [InlineData("Press", "Cancel")]
    [InlineData("Move", "Cancel")]
    [InlineData("Release", "Cancel")]
    [InlineData("Exit", "Cancel")]
    [InlineData("Wheel", "Cancel")]
    [InlineData("Cancel", "Cancel")]
    [InlineData("HandlePress", "HandleCancel")]
    [InlineData("HandleMove", "HandleCancel")]
    [InlineData("HandleRelease", "HandleCancel")]
    [InlineData("HandleCancel", "HandleCancel")]
    [InlineData("Tap", "Tap")]
    [InlineData("DoubleTap", "DoubleTap")]
    [InlineData("Hold", "Hold")]
    [InlineData("RightTap", "RightTap")]
    public void NativeCancellationIsClassifiedBeforeLiveOrDeferredInput(string kind, string canceledKind)
    {
        PointerInputKind input = Enum.Parse<PointerInputKind>(kind);

        Assert.Equal(input, PointerInputClassification.NormalizeCanceledPointerInputKind(input, isCanceled: false));
        Assert.Equal(
            Enum.Parse<PointerInputKind>(canceledKind),
            PointerInputClassification.NormalizeCanceledPointerInputKind(input, isCanceled: true));
    }
}
