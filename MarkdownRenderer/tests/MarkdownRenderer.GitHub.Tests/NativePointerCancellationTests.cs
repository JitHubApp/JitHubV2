using System;
using System.Reflection;
using MarkdownRenderer.Controls;
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
        // Reflection is confined to the test assembly; the production path is
        // a statically bound call and introduces no public testing API.
        MethodInfo method = typeof(MarkdownRendererControl).GetMethod(
            "NormalizeCanceledPointerInputKind", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The real pointer dispatcher must classify device cancellation before dispatch.");
        Type kindType = method.GetParameters()[0].ParameterType;
        object value = Enum.Parse(kindType, kind);
        Assert.Equal(kind, method.Invoke(null, [value, false])?.ToString());
        Assert.Equal(canceledKind, method.Invoke(null, [value, true])?.ToString());
    }
}

