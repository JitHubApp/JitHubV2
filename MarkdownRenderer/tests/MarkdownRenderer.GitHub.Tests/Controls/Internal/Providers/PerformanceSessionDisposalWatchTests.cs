using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Providers;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Providers;

/// <summary>
/// A loaded view rebuilds once, on its UI thread, when its borrowed
/// performance session is disposed, and the queued rebuild re-checks that the
/// session is still the view's and that the view is still loaded.
/// </summary>
public sealed class PerformanceSessionDisposalWatchTests
{
    [Fact]
    public void DisposalQueuesOneRebuildThatRunsOnlyOnTheUiThread()
    {
        var fixture = new Fixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        fixture.Watch.Watch(session);

        session.Dispose();

        Action queued = Assert.Single(fixture.Queued);
        Assert.Equal(0, fixture.Rebuilds);
        queued();
        Assert.Equal(1, fixture.Rebuilds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedRebuildIsDroppedWhenTheViewUnloadsOrDisposesFirst(bool dispose)
    {
        var fixture = new Fixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        fixture.Watch.Watch(session);
        session.Dispose();

        if (dispose)
            fixture.Lifetime.MarkDisposing();
        else
            fixture.Lifetime.MarkUnloaded();
        Assert.Single(fixture.Queued)();

        Assert.Equal(0, fixture.Rebuilds);
    }

    [Fact]
    public void QueuedRebuildIsDroppedWhenAnotherSessionReplacedTheDisposedOne()
    {
        var fixture = new Fixture();
        using var disposed = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using var replacement = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        fixture.Watch.Watch(disposed);
        disposed.Dispose();

        fixture.Watch.Watch(replacement);
        Assert.Single(fixture.Queued)();

        Assert.Equal(0, fixture.Rebuilds);
    }

    [Fact]
    public void QueuedRebuildIsDroppedAfterTheWatchStops()
    {
        var fixture = new Fixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        fixture.Watch.Watch(session);
        session.Dispose();

        fixture.Watch.Stop();
        Assert.Single(fixture.Queued)();

        Assert.Equal(0, fixture.Rebuilds);
    }

    [Fact]
    public void DisposalOfAnUnloadedViewsSessionQueuesNothing()
    {
        var fixture = new Fixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        fixture.Watch.Watch(session);
        fixture.Lifetime.MarkUnloaded();

        session.Dispose();

        Assert.Empty(fixture.Queued);
    }

    [Fact]
    public void ReplacedOrStoppedSessionsNoLongerReportDisposal()
    {
        var fixture = new Fixture();
        using var replaced = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using var current = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        fixture.Watch.Watch(replaced);
        fixture.Watch.Watch(current);

        replaced.Dispose();
        Assert.Empty(fixture.Queued);

        fixture.Watch.Stop();
        current.Dispose();
        Assert.Empty(fixture.Queued);
    }

    [Fact]
    public void WatchingAnAlreadyDisposedSessionQueuesTheRebuild()
    {
        var fixture = new Fixture();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        session.Dispose();

        fixture.Watch.Watch(session);

        Assert.Single(fixture.Queued)();
        Assert.Equal(1, fixture.Rebuilds);
    }

    [Fact]
    public void RejectedUiDispatchRequestsNoRebuild()
    {
        var fixture = new Fixture(acceptDispatch: false);
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        fixture.Watch.Watch(session);

        session.Dispose();

        Assert.Empty(fixture.Queued);
        Assert.Equal(0, fixture.Rebuilds);
    }

    private sealed class Fixture
    {
        internal Fixture(bool acceptDispatch = true)
        {
            Lifetime.MarkLoaded();
            Watch = new PerformanceSessionDisposalWatch(
                Lifetime,
                callback =>
                {
                    if (acceptDispatch)
                        Queued.Add(callback);
                    return acceptDispatch;
                },
                () => Rebuilds++);
        }

        internal MarkdownViewLifetime Lifetime { get; } = new();

        internal List<Action> Queued { get; } = [];

        internal int Rebuilds { get; private set; }

        internal PerformanceSessionDisposalWatch Watch { get; }
    }
}
