using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JitHub.Markdown.Lifetime;
using JitHub.Markdown.Runtime;
using Xunit;

namespace JitHub.Markdown.Tests.Lifetime;

public sealed class MarkdownRendererSlotTests
{
    [Fact]
    public async Task Retire_KeepsRendererTrackedUntilItsResourcesDrain()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost();
        var harness = new SlotHarness(registry, host);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new FakeRenderer(host.Log, () => new ValueTask(drain.Task));

        harness.Slot.Attach(renderer);
        Assert.Equal(1, registry.ActiveRendererCount);

        harness.Slot.Retire();

        Assert.Equal(["detach", "dispose"], host.Log);
        Assert.Null(harness.Slot.Current);
        Assert.True(harness.Slot.IsRetirementPending);
        Assert.Equal(1, registry.ActiveRendererCount);
        Assert.Equal(0, host.ReleasedCount);
        Task<bool> waitForDisposal = registry.WaitForRendererDisposalAsync(TimeSpan.FromSeconds(30));
        Assert.False(waitForDisposal.IsCompleted);

        drain.SetResult();
        await harness.Retirement;

        Assert.False(harness.Slot.IsRetirementPending);
        Assert.False(harness.Slot.IsDisposalFaulted);
        Assert.Equal(0, registry.ActiveRendererCount);
        Assert.Equal(1, host.ReleasedCount);
        Assert.True(await waitForDisposal);
    }

    [Fact]
    public async Task Retire_RepeatedWhileDrainingJoinsTheOriginalRetirement()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost();
        var harness = new SlotHarness(registry, host);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new FakeRenderer(host.Log, () => new ValueTask(drain.Task));
        harness.Slot.Attach(renderer);

        harness.Slot.Retire();
        harness.Slot.Retire();
        Assert.True(harness.Slot.TryRetireFromLifecycle());

        Assert.Equal(1, renderer.DisposeCount);
        Assert.Equal(1, harness.ObservedRetirements);
        drain.SetResult();
        await harness.Retirement;
        Assert.Equal(0, registry.ActiveRendererCount);
        Assert.Equal(1, host.ReleasedCount);
    }

    [Fact]
    public void TryRetireFromLifecycle_TeardownFailureLatchesFaultAndKeepsRendererTracked()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost();
        var harness = new SlotHarness(registry, host);
        var teardownFailure = new InvalidOperationException("Teardown failed.");
        var renderer = new FakeRenderer(host.Log, () => throw teardownFailure);
        harness.Slot.Attach(renderer);
        bool latchedBeforeReport = false;
        host.OnFault = () => latchedBeforeReport = harness.Slot.IsDisposalFaulted;

        Assert.False(harness.Slot.TryRetireFromLifecycle());

        // The renderer may have latched its disposed state without releasing
        // its resources, so it stays owned and counted and is never reattached.
        Assert.True(harness.Slot.IsDisposalFaulted);
        Assert.True(latchedBeforeReport);
        Assert.Same(renderer, harness.Slot.Current);
        Assert.False(harness.Slot.IsRetirementPending);
        Assert.Equal(1, registry.ActiveRendererCount);
        Assert.Same(teardownFailure, Assert.Single(host.Faults));
        Assert.Equal(0, harness.ObservedRetirements);
        Assert.Equal(0, host.ReleasedCount);
    }

    [Fact]
    public void Retire_TeardownFailurePropagatesWithoutLatchingFault()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost();
        var harness = new SlotHarness(registry, host);
        var renderer = new FakeRenderer(host.Log, () => throw new InvalidOperationException("Teardown failed."));
        harness.Slot.Attach(renderer);

        Assert.Throws<InvalidOperationException>(harness.Slot.Retire);

        // Application shutdown aggregates the failure; the lifecycle path owns
        // the fault latch and the user-visible report.
        Assert.False(harness.Slot.IsDisposalFaulted);
        Assert.False(harness.Slot.IsRetirementPending);
        Assert.Same(renderer, harness.Slot.Current);
        Assert.Empty(host.Faults);
        Assert.Equal(1, registry.ActiveRendererCount);
    }

    [Fact]
    public async Task Retire_DrainFailureReportsFaultAndKeepsActiveCountLatched()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost();
        var harness = new SlotHarness(registry, host);
        var renderer = new FakeRenderer(
            host.Log,
            () => ValueTask.FromException(new InvalidOperationException("Drain failed.")));
        harness.Slot.Attach(renderer);
        bool latchedBeforeReport = false;
        host.OnFault = () => latchedBeforeReport = harness.Slot.IsDisposalFaulted;

        harness.Slot.Retire();
        await harness.Retirement;

        Assert.True(harness.Slot.IsDisposalFaulted);
        Assert.True(latchedBeforeReport);
        Assert.True(harness.Slot.IsRetirementPending);
        Assert.Equal(1, registry.ActiveRendererCount);
        Assert.Equal(0, host.ReleasedCount);
        Exception fault = Assert.Single(host.Faults);
        Assert.IsType<InvalidOperationException>(fault);
        Assert.Equal(
            $"Snapshot retirement failed with InvalidOperationException (HRESULT 0x{new InvalidOperationException().HResult:X8}).",
            fault.Message);
    }

    [Fact]
    public async Task Retire_ReleasesTrackingOnTheHostDispatcherWhenDrainCompletesElsewhere()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var dispatcher = new QueueDispatcher();
        var host = new RecordingHost { Dispatcher = dispatcher };
        var harness = new SlotHarness(registry, host);
        var renderer = new FakeRenderer(host.Log, () => ValueTask.CompletedTask);
        harness.Slot.Attach(renderer);

        harness.Slot.Retire();
        await WaitUntilAsync(() => dispatcher.Pending > 0);

        Assert.Equal(1, registry.ActiveRendererCount);
        Assert.Equal(0, host.ReleasedCount);

        dispatcher.RunPending();
        await harness.Retirement;

        Assert.Equal(0, registry.ActiveRendererCount);
        Assert.Equal(1, host.ReleasedCount);
        Assert.False(harness.Slot.IsRetirementPending);
    }

    [Fact]
    public async Task Retire_RejectedDispatcherKeepsActiveCountLatched()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost { Dispatcher = new QueueDispatcher { RejectWork = true } };
        var harness = new SlotHarness(registry, host);
        harness.Slot.Attach(new FakeRenderer(host.Log, () => ValueTask.CompletedTask));

        harness.Slot.Retire();
        await harness.Retirement;

        // The close path must see a latched count and skip shared-provider
        // teardown rather than treat a rejected completion as success.
        Assert.Equal(1, registry.ActiveRendererCount);
        Assert.True(harness.Slot.IsRetirementPending);
        Assert.Equal(0, host.ReleasedCount);
        Assert.Empty(host.Faults);
        Assert.False(await registry.WaitForRendererDisposalAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task Retire_MissingDispatcherKeepsActiveCountLatched()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost { Dispatcher = null };
        var harness = new SlotHarness(registry, host);
        harness.Slot.Attach(new FakeRenderer(host.Log, () => ValueTask.CompletedTask));

        harness.Slot.Retire();
        await harness.Retirement;

        Assert.Equal(1, registry.ActiveRendererCount);
        Assert.True(harness.Slot.IsRetirementPending);
        Assert.Equal(0, host.ReleasedCount);
        Assert.Empty(host.Faults);
    }

    [Fact]
    public async Task Retire_ReportsReleaseAfterTrackingIsCleared()
    {
        var registry = new MarkdownRendererLifetimeRegistry();
        var host = new RecordingHost();
        var harness = new SlotHarness(registry, host);
        host.OnReleased = () =>
        {
            // A replacement renderer may be created from this callback.
            Assert.Null(harness.Slot.Current);
            Assert.False(harness.Slot.IsRetirementPending);
            Assert.Equal(0, registry.ActiveRendererCount);
        };
        harness.Slot.Attach(new FakeRenderer(host.Log, () => ValueTask.CompletedTask));

        harness.Slot.Retire();
        await harness.Retirement;

        Assert.Equal(1, host.ReleasedCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met.");

            await Task.Yield();
        }
    }

    private sealed class SlotHarness
    {
        private readonly List<Task> _retirements = [];

        public SlotHarness(MarkdownRendererLifetimeRegistry registry, RecordingHost host)
        {
            Slot = new MarkdownRendererSlot<FakeRenderer>(registry, host, _retirements.Add);
        }

        public MarkdownRendererSlot<FakeRenderer> Slot { get; }

        public int ObservedRetirements => _retirements.Count;

        public Task Retirement => Assert.Single(_retirements);
    }

    private sealed class FakeRenderer : IAsyncDisposable
    {
        private readonly List<string> _log;
        private readonly Func<ValueTask> _dispose;

        public FakeRenderer(List<string> log, Func<ValueTask> dispose)
        {
            _log = log;
            _dispose = dispose;
        }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _log.Add("dispose");
            return _dispose();
        }
    }

    private sealed class RecordingHost : IMarkdownRendererSlotHost<FakeRenderer>
    {
        public List<string> Log { get; } = [];

        public List<Exception> Faults { get; } = [];

        public int ReleasedCount { get; private set; }

        public Action? OnReleased { get; set; }

        public IMarkdownUiDispatcher? Dispatcher { get; set; } = new InlineDispatcher();

        public void DetachRenderer(FakeRenderer renderer) => Log.Add("detach");

        public void OnRendererReleased()
        {
            ReleasedCount++;
            OnReleased?.Invoke();
        }

        public Action? OnFault { get; set; }

        public void OnRendererTeardownFaulted(Exception exception)
        {
            Faults.Add(exception);
            OnFault?.Invoke();
        }
    }

    private sealed class InlineDispatcher : IMarkdownUiDispatcher
    {
        public bool HasThreadAccess => true;

        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }

    private sealed class QueueDispatcher : IMarkdownUiDispatcher
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _queue = new();

        public bool RejectWork { get; init; }

        public bool HasThreadAccess => false;

        public int Pending
        {
            get
            {
                lock (_gate)
                    return _queue.Count;
            }
        }

        public bool TryEnqueue(Action callback)
        {
            if (RejectWork)
                return false;

            lock (_gate)
                _queue.Enqueue(callback);
            return true;
        }

        public void RunPending()
        {
            while (true)
            {
                Action callback;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                        return;

                    callback = _queue.Dequeue();
                }

                callback();
            }
        }
    }
}
