using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotRetirementCleanupTests
{
    [Fact]
    public async Task RetirementAttemptsEveryThrowingRootAndDrainsAllCollectedBarriers()
    {
        const int rootCount = 100;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocks = new CleanupProbeBox[rootCount];
        for (int index = 0; index < blocks.Length; index++)
        {
            blocks[index] = new CleanupProbeBox(
                index == rootCount - 1 ? barrier.Task : Task.CompletedTask,
                new InvalidOperationException($"collect {index}"),
                new InvalidOperationException($"dispose {index}"))
            {
                BlockIndex = index,
            };
        }

        var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 640,
            height: 0);
        Task retirement = snapshot.Retire();

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => blocks.All(static block => block.CollectCount == 1 && block.DisposeCount == 1),
                TimeSpan.FromSeconds(5)));
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            barrier.TrySetResult();
        }
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => retirement.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(rootCount, blocks.Sum(static block => block.CollectCount));
        Assert.Equal(rootCount, blocks.Sum(static block => block.DisposeCount));
        Assert.Equal(9, failure.InnerExceptions.Count);
        Assert.Contains("omitted", failure.InnerExceptions[^1].Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CleanupProbeBox(
        Task barrier,
        Exception collectFailure,
        Exception disposeFailure) : BlockBox
    {
        private int _collectCount;
        private int _disposeCount;

        internal int CollectCount => Volatile.Read(ref _collectCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, 1);
            return 1;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }

        public override void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            throw disposeFailure;
        }

        internal override void CollectRetirementTasks(List<Task> pendingTasks)
        {
            Interlocked.Increment(ref _collectCount);
            pendingTasks.Add(barrier);
            throw collectFailure;
        }
    }
}
