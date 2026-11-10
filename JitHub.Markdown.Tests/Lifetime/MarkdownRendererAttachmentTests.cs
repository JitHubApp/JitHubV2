using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JitHub.Markdown.Lifetime;
using JitHub.Markdown.Runtime;
using Xunit;

namespace JitHub.Markdown.Tests.Lifetime;

public sealed class MarkdownRendererAttachmentTests
{
    [Fact]
    public void EnsureRenderer_AttachesANewRendererBeforeConfiguringAddingAndApplyingIt()
    {
        var harness = new Harness();
        FakeRenderer? currentWhileConfiguring = null;
        harness.Host.OnConfigure = () => currentWhileConfiguring = harness.Slot.Current;

        harness.Attachment.EnsureRenderer();

        FakeRenderer renderer = Assert.Single(harness.Host.Created);
        Assert.Equal(["create", "configure", "add", "apply"], harness.Host.Log);
        Assert.Same(renderer, harness.Slot.Current);
        Assert.Same(renderer, currentWhileConfiguring);
        Assert.Equal(1, harness.Registry.ActiveRendererCount);
    }

    [Fact]
    public void EnsureRenderer_ReattachesTheCurrentRendererOnlyWhenItLeftTheTree()
    {
        var harness = new Harness();
        harness.Attachment.EnsureRenderer();
        harness.Host.Log.Clear();

        harness.Attachment.EnsureRenderer();
        Assert.Empty(harness.Host.Log);

        harness.Host.InTree.Clear();
        harness.Attachment.EnsureRenderer();

        Assert.Equal(["add", "apply"], harness.Host.Log);
        Assert.Single(harness.Host.Created);
    }

    [Fact]
    public void EnsureRenderer_AfterShutdownDoesNothing()
    {
        var harness = new Harness();
        harness.Host.IsShutdownRequested = true;

        harness.Attachment.EnsureRenderer();

        Assert.Empty(harness.Host.Log);
        Assert.Null(harness.Slot.Current);
    }

    [Fact]
    public void EnsureRenderer_AfterAFailedTeardownShowsTheFailureAndNeverReattachesOrCreates()
    {
        var harness = new Harness { Dispose = () => throw new InvalidOperationException("Teardown failed.") };
        harness.Attachment.EnsureRenderer();
        Assert.False(harness.Slot.TryRetireFromLifecycle());
        harness.Host.Log.Clear();

        harness.Attachment.EnsureRenderer();
        harness.Attachment.QueueCreation();
        harness.Host.RunPending();

        Assert.Equal(["failure"], harness.Host.Log);
        Assert.Single(harness.Host.Created);
    }

    [Fact]
    public void EnsureRenderer_WhileARetiredRendererDrainsDoesNotCreateAReplacement()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness { Dispose = () => new ValueTask(drain.Task) };
        harness.Attachment.EnsureRenderer();
        harness.Slot.Retire();
        harness.Host.Log.Clear();

        harness.Attachment.EnsureRenderer();
        harness.Attachment.QueueCreation();

        Assert.Empty(harness.Host.Log);
        Assert.Equal(0, harness.Host.PendingCount);
        Assert.Single(harness.Host.Created);
    }

    [Fact]
    public void QueueCreation_QueuesOneLowPriorityCreation()
    {
        var harness = new Harness();

        harness.Attachment.QueueCreation();
        harness.Attachment.QueueCreation();

        Assert.Equal(1, harness.Host.PendingCount);
        Assert.Empty(harness.Host.Log);

        harness.Host.RunPending();

        Assert.Single(harness.Host.Created);
        Assert.Equal(["create", "configure", "add", "apply"], harness.Host.Log);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void QueueCreation_CallbackRechecksShutdownAndLoaded(bool unload, bool shutdown)
    {
        var harness = new Harness();
        harness.Attachment.QueueCreation();

        harness.Host.IsLoaded = !unload;
        harness.Host.IsShutdownRequested = shutdown;
        harness.Host.RunPending();

        Assert.Empty(harness.Host.Created);
        Assert.Null(harness.Slot.Current);
    }

    [Fact]
    public void CancelQueuedCreation_LetsTheNextLoadQueueAgain()
    {
        var harness = new Harness();
        harness.Attachment.QueueCreation();

        harness.Attachment.CancelQueuedCreation();
        harness.Attachment.QueueCreation();

        Assert.Equal(2, harness.Host.PendingCount);
        harness.Host.RunPending();
        Assert.Single(harness.Host.Created);
    }

    [Fact]
    public async Task QueueCreation_CreatesInlineWhenTheHostCannotQueueWork()
    {
        var harness = new Harness();
        harness.Host.RejectWork = true;

        harness.Attachment.QueueCreation();

        Assert.Single(harness.Host.Created);
        Assert.Equal(0, harness.Host.PendingCount);

        // The queued flag was cleared, so the replacement requested when this
        // renderer is released is not swallowed.
        harness.Host.RejectWork = false;
        harness.Slot.Retire();
        await harness.Retirement;
        Assert.Equal(1, harness.Host.PendingCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void QueueCreation_IsSkippedWhenUnloadedShutDownOrAlreadyAttached(
        bool loaded,
        bool shutdown,
        bool attached)
    {
        var harness = new Harness();
        if (attached)
            harness.Attachment.EnsureRenderer();
        harness.Host.Log.Clear();
        harness.Host.IsLoaded = loaded;
        harness.Host.IsShutdownRequested = shutdown;

        harness.Attachment.QueueCreation();

        Assert.Equal(0, harness.Host.PendingCount);
        Assert.Empty(harness.Host.Log);
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    public async Task ReleasedRenderer_QueuesAReplacementOnlyWhileTheHostIsActive(
        bool loaded,
        bool shutdown,
        int expectedPending)
    {
        var harness = new Harness();
        harness.Attachment.EnsureRenderer();
        harness.Host.IsLoaded = loaded;
        harness.Host.IsShutdownRequested = shutdown;

        harness.Slot.Retire();
        await harness.Retirement;

        Assert.Equal(1, harness.Host.ReleasedCount);
        Assert.Equal(expectedPending, harness.Host.PendingCount);
    }

    private sealed class Harness
    {
        private readonly List<Task> _retirements = [];

        public Harness()
        {
            Registry = new MarkdownRendererLifetimeRegistry();
            Host = new FakeHost(this);
            Slot = new MarkdownRendererSlot<FakeRenderer>(Registry, Host, _retirements.Add);
            Attachment = new MarkdownRendererAttachment<FakeRenderer>(Slot, Host);
        }

        public Func<ValueTask> Dispose { get; init; } = static () => ValueTask.CompletedTask;

        public MarkdownRendererLifetimeRegistry Registry { get; }

        public FakeHost Host { get; }

        public MarkdownRendererSlot<FakeRenderer> Slot { get; }

        public MarkdownRendererAttachment<FakeRenderer> Attachment { get; }

        public Task Retirement => Assert.Single(_retirements);
    }

    private sealed class FakeRenderer(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }

    private sealed class FakeHost(Harness harness) :
        IMarkdownRendererSlotHost<FakeRenderer>,
        IMarkdownRendererAttachmentHost<FakeRenderer>
    {
        private readonly Queue<Action> _pending = new();

        public List<string> Log { get; } = [];

        public List<FakeRenderer> Created { get; } = [];

        public HashSet<FakeRenderer> InTree { get; } = [];

        public Action? OnConfigure { get; set; }

        public bool RejectWork { get; set; }

        public int ReleasedCount { get; private set; }

        public int PendingCount => _pending.Count;

        public bool IsShutdownRequested { get; set; }

        public bool IsLoaded { get; set; } = true;

        public IMarkdownUiDispatcher? Dispatcher { get; } = new InlineDispatcher();

        public void RunPending()
        {
            while (_pending.TryDequeue(out Action? callback))
                callback();
        }

        public bool TryEnqueueLowPriority(Action callback)
        {
            if (RejectWork)
                return false;

            _pending.Enqueue(callback);
            return true;
        }

        public bool IsRendererInTree(FakeRenderer renderer) => InTree.Contains(renderer);

        public FakeRenderer CreateRenderer()
        {
            Log.Add("create");
            var renderer = new FakeRenderer(harness.Dispose);
            Created.Add(renderer);
            return renderer;
        }

        public void ConfigureNewRenderer(FakeRenderer renderer)
        {
            Log.Add("configure");
            OnConfigure?.Invoke();
        }

        public void AddRendererToTree(FakeRenderer renderer)
        {
            Log.Add("add");
            InTree.Add(renderer);
        }

        public void ApplyRendererSettings() => Log.Add("apply");

        public void ShowRendererDisposalFailure() => Log.Add("failure");

        public void DetachRenderer(FakeRenderer renderer) => InTree.Remove(renderer);

        public void OnRendererReleased()
        {
            ReleasedCount++;
            harness.Attachment.OnRendererReleased();
        }

        public void OnRendererTeardownFaulted(Exception exception) => Log.Add("teardown-fault");
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
}
