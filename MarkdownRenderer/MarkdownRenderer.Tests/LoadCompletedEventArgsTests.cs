using System;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void For_ReusesAnImmutableInstanceForEachState()
    {
        LoadCompletedEventArgs layoutInvalidated = LoadCompletedEventArgs.For(true);
        LoadCompletedEventArgs paintOnly = LoadCompletedEventArgs.For(false);

        Assert.Same(layoutInvalidated, LoadCompletedEventArgs.For(true));
        Assert.Same(paintOnly, LoadCompletedEventArgs.For(false));
        Assert.NotSame(layoutInvalidated, paintOnly);
        Assert.True(layoutInvalidated.LayoutInvalidated);
        Assert.False(paintOnly.LayoutInvalidated);
        Assert.True(typeof(LoadCompletedEventArgs)
            .GetField(nameof(LoadCompletedEventArgs.LayoutInvalidated))!
            .IsInitOnly);
    }

    [Fact]
    public void For_ConcurrentRequestsDoNotMixStates()
    {
        int mismatches = 0;

        Parallel.For(0, 10_000, index =>
        {
            bool expected = (index & 1) == 0;
            LoadCompletedEventArgs args = LoadCompletedEventArgs.For(expected);
            if (args.LayoutInvalidated != expected)
                Interlocked.Increment(ref mismatches);
        });

        Assert.Equal(0, mismatches);
        Assert.True(LoadCompletedEventArgs.For(true).LayoutInvalidated);
        Assert.False(LoadCompletedEventArgs.For(false).LayoutInvalidated);
    }
}
