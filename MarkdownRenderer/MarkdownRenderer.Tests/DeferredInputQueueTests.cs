using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class DeferredInputQueueTests
{
    [Fact]
    public void CoalescesMovesWithoutDroppingPressOrRelease()
    {
        var queue = new DeferredInputQueue<string>(3);
        Assert.True(queue.TryEnqueue("press", 1, false));
        Assert.True(queue.TryEnqueue("move-old", 1, true));
        Assert.True(queue.TryEnqueue("move-latest", 1, true));
        Assert.True(queue.TryEnqueue("release", 1, false));
        Assert.Equal(3, queue.Count);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("press", first);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("move-latest", second);
        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal("release", third);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void DifferentPointersAndTransitionsNeverCoalesce()
    {
        var queue = new DeferredInputQueue<int>(3);
        Assert.True(queue.TryEnqueue(1, 1, true));
        Assert.True(queue.TryEnqueue(2, 2, true));
        Assert.True(queue.TryEnqueue(3, 2, false));
        Assert.False(queue.TryEnqueue(4, 2, true));
        Assert.Equal(3, queue.Count);
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.True(queue.TryEnqueue(5, 3, false));
        Assert.True(queue.TryDequeue(out int value));
        Assert.Equal(5, value);
    }

    [Fact]
    public void ReusesRingSlotsAndReleasesPayloads()
    {
        var queue = new DeferredInputQueue<int>(2);
        for (int index = 0; index < 1_000; index++)
        {
            Assert.True(queue.TryEnqueue(index, 1, false));
            Assert.True(queue.TryDequeue(out int value));
            Assert.Equal(index, value);
        }
        Assert.Equal(0, queue.Count);
    }
}
