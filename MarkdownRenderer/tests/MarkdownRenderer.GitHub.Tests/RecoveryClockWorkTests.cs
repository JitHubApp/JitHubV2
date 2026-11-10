using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Controls.Internal.Pipeline.Commit;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Each recovery clock tick retries every deferred UI handoff in a fixed
/// order, only drains image completions while geometry recovery is required,
/// falls back to a rebuild when a retry fails, and stops the clock once
/// nothing is pending or the view is gone.
/// </summary>
public sealed class RecoveryClockWorkTests
{
    [Fact]
    public void TickRetriesEveryDeferredHandoffInOrder()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new WorkHost();
        RecoveryClockWork work = CreateWork(fixture, host, new FocusableItemsRefreshRequest(fixture.Published));
        fixture.Scheduler.RejectPosts = true;
        fixture.Relayout.Queue(1);
        fixture.Scheduler.RejectPosts = false;

        work.ProcessTick();

        Assert.Equal(
            [
                "RequestGeometryRecoveryIfRequired",
                "PublishPendingLayout",
                "ProcessQueuedLazyLayout",
                "TryPublishLazyLayoutCompletion",
                "ProcessOverflowScrollRetry",
                "ProcessFocusGeometryWork",
                "TryPublishPendingFocusableItems",
            ],
            host.Calls);
        // The tick retried the image relayout whose dispatch was rejected.
        fixture.Scheduler.WaitForPending();
        fixture.Scheduler.RunPending();
        Assert.Single(fixture.Host.Published);
    }

    [Fact]
    public void TickWhileGeometryRecoveryIsRequiredOnlyDrainsImageCompletionAndFocusRefresh()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new WorkHost { GeometryRecoveryRequired = true };
        var focusRefresh = new FocusableItemsRefreshRequest(fixture.Published);
        focusRefresh.Mark(fixture.Snapshot, fixture.Published.Generation);
        RecoveryClockWork work = CreateWork(fixture, host, focusRefresh);
        fixture.Relayout.Queue(1);
        fixture.Scheduler.RunPending();
        fixture.Scheduler.WaitForPending();

        work.ProcessTick();

        Assert.Equal(["RequestGeometryRecoveryIfRequired"], host.Calls);
        Assert.False(focusRefresh.Pending.IsPending);
        // The worker's ready completion published without its lost UI post.
        Assert.Single(fixture.Host.Published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRetryDropsItsPendingCommitAndFallsBackToARebuildOrRecovery(bool recoveryAfterFailure)
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new WorkHost
        {
            FailPublishPendingLayout = true,
            GeometryRecoveryAfterFailure = recoveryAfterFailure,
        };
        var focusRefresh = new FocusableItemsRefreshRequest(fixture.Published);
        focusRefresh.Mark(fixture.Snapshot, fixture.Published.Generation);
        fixture.LayoutCommit.Defer(new PendingLayoutCommit(fixture.Published.Generation, null, null));
        RecoveryClockWork work = CreateWork(fixture, host, focusRefresh);

        work.ProcessTick();

        Assert.Null(fixture.LayoutCommit.Pending);
        Assert.Equal(recoveryAfterFailure ? 0 : 1, host.FullRebuilds);
        Assert.Equal(!recoveryAfterFailure, focusRefresh.Pending.IsPending);
        Assert.Same(fixture.Snapshot, Assert.Single(host.FailedLazyLayouts));
    }

    [Fact]
    public void FailedRetryKeepsACommitDeferredWhileItRan()
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new WorkHost { FailPublishPendingLayout = true };
        host.BeforeFailure = () =>
            fixture.LayoutCommit.Defer(new PendingLayoutCommit(fixture.Published.Generation, null, null));
        RecoveryClockWork work = CreateWork(fixture, host, new FocusableItemsRefreshRequest(fixture.Published));

        work.ProcessTick();

        Assert.NotNull(fixture.LayoutCommit.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TickAfterUnloadOrDisposalForceStopsTheClockAndForgetsRetries(bool disposed)
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new WorkHost { PendingOverflowScrollRetry = true };
        var focusRefresh = new FocusableItemsRefreshRequest(fixture.Published);
        focusRefresh.Mark(fixture.Snapshot, fixture.Published.Generation);
        RecoveryClockWork work = CreateWork(fixture, host, focusRefresh);
        fixture.Clock.Start();
        if (disposed)
            fixture.Lifetime.MarkDisposing();
        else
            fixture.Lifetime.MarkUnloaded();

        work.ProcessTick();

        Assert.Empty(host.Calls);
        Assert.False(fixture.Clock.IsRunning);
        Assert.False(focusRefresh.Pending.IsPending);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("lazy layout", true)]
    [InlineData("layout commit", true)]
    [InlineData("overflow scroll", true)]
    [InlineData("focus geometry", true)]
    [InlineData("focus refresh", true)]
    [InlineData("image relayout", true)]
    public void IdleTickStopsTheClockButAnyPendingRetryKeepsItRunning(string pending, bool keepsRunning)
    {
        using var fixture = new ImageRelayoutFixture();
        var host = new WorkHost
        {
            PendingLazyLayout = pending == "lazy layout",
            PendingOverflowScrollRetry = pending == "overflow scroll",
            PendingFocusGeometryWork = pending == "focus geometry",
        };
        var focusRefresh = new FocusableItemsRefreshRequest(fixture.Published);
        if (pending == "focus refresh")
            focusRefresh.Mark(fixture.Snapshot, fixture.Published.Generation);
        if (pending == "layout commit")
            fixture.LayoutCommit.Defer(new PendingLayoutCommit(fixture.Published.Generation, null, null));
        RecoveryClockWork work = CreateWork(fixture, host, focusRefresh);
        fixture.Clock.Start();
        if (pending == "image relayout")
            fixture.Relayout.Queue(2);

        work.ProcessTick();

        Assert.Equal(keepsRunning, fixture.Clock.IsRunning);
    }

    private static RecoveryClockWork CreateWork(
        ImageRelayoutFixture fixture,
        WorkHost host,
        FocusableItemsRefreshRequest focusRefresh) =>
        new(host, fixture.Lifetime, fixture.Published, fixture.Clock, fixture.LayoutCommit,
            fixture.Subscriptions, fixture.Relayout, focusRefresh);

    private sealed class WorkHost : IRecoveryClockWorkHost
    {
        private bool _failed;

        internal List<string> Calls { get; } = [];

        internal List<LayoutSnapshot> FailedLazyLayouts { get; } = [];

        internal int FullRebuilds { get; private set; }

        internal bool GeometryRecoveryRequired { get; set; }

        internal bool GeometryRecoveryAfterFailure { get; set; }

        internal bool FailPublishPendingLayout { get; set; }

        internal Action? BeforeFailure { get; set; }

        internal bool PendingLazyLayout { get; set; }

        internal bool PendingOverflowScrollRetry { get; set; }

        internal bool PendingFocusGeometryWork { get; set; }

        public bool HasPendingLazyLayout => PendingLazyLayout;

        public bool HasPendingOverflowScrollRetry => PendingOverflowScrollRetry;

        public bool HasPendingFocusGeometryWork => PendingFocusGeometryWork;

        public bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        {
            Calls.Add(nameof(RequestGeometryRecoveryIfRequired));
            return GeometryRecoveryRequired || (_failed && GeometryRecoveryAfterFailure);
        }

        public void RequestFullRebuild() => FullRebuilds++;

        public void PublishPendingLayout()
        {
            Calls.Add(nameof(PublishPendingLayout));
            if (!FailPublishPendingLayout)
                return;
            BeforeFailure?.Invoke();
            _failed = true;
            throw new InvalidOperationException("The test failed layout publication.");
        }

        public void ProcessQueuedLazyLayout() => Calls.Add(nameof(ProcessQueuedLazyLayout));

        public void TryPublishLazyLayoutCompletion() => Calls.Add(nameof(TryPublishLazyLayoutCompletion));

        public void ProcessOverflowScrollRetry() => Calls.Add(nameof(ProcessOverflowScrollRetry));

        public void ProcessFocusGeometryWork() => Calls.Add(nameof(ProcessFocusGeometryWork));

        public void TryPublishPendingFocusableItems() => Calls.Add(nameof(TryPublishPendingFocusableItems));

        public void NotifyLazyLayoutFailed(LayoutSnapshot snapshot) => FailedLazyLayouts.Add(snapshot);
    }
}
