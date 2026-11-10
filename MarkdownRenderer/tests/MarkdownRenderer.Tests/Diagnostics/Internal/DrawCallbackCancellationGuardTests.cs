using System;
using Xunit;
using MarkdownRenderer.Diagnostics.Internal;

namespace MarkdownRenderer.Tests.Diagnostics.Internal;

public sealed class DrawCallbackCancellationGuardTests
{
    [Fact]
    public void TryDraw_ContainsOnlyExpectedCancellation()
    {
        Assert.False(DrawCallbackCancellationGuard.TryDraw(
            static () => throw new OperationCanceledException()));

        InvalidOperationException failure = new("draw failed");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => DrawCallbackCancellationGuard.TryDraw(() => throw failure)));
    }

    [Fact]
    public void TryDraw_ReturnsTrueAfterSuccessfulDraw()
    {
        bool called = false;
        Assert.True(DrawCallbackCancellationGuard.TryDraw(() => called = true));
        Assert.True(called);
    }

}
