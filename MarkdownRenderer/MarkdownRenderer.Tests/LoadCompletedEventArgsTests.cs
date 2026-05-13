using System;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public class LoadCompletedEventArgsTests
{
    [Fact]
    public void LayoutInvalidated_PropagatesTrue()
    {
        var args = new LoadCompletedEventArgs(layoutInvalidated: true);
        Assert.True(args.LayoutInvalidated);
    }

    [Fact]
    public void LayoutInvalidated_PropagatesFalse()
    {
        var args = new LoadCompletedEventArgs(layoutInvalidated: false);
        Assert.False(args.LayoutInvalidated);
    }

    [Fact]
    public void IsEventArgs()
    {
        var args = new LoadCompletedEventArgs(true);
        Assert.IsAssignableFrom<EventArgs>(args);
    }
}
