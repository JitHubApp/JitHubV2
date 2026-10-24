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

    [Fact]
    public void SnapshotRetirementCollectsBeforeDisposalAndPublishesTaskArrayBeforeThrowing()
    {
        string source = File.ReadAllText(FindLayoutSnapshotSource());
        int start = source.IndexOf("private void DisposeCoreNoLock(", StringComparison.Ordinal);
        int end = source.IndexOf("/// <summary>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string method = source[start..end];

        int collect = method.IndexOf("cleanup.TryCollectRetirementTasks(block, retirementTasks);", StringComparison.Ordinal);
        int dispose = method.IndexOf("cleanup.TryDispose(block);", collect, StringComparison.Ordinal);
        int publishTasks = method.IndexOf("pendingRetirementTasks = retirementTasks.ToArray();", dispose, StringComparison.Ordinal);
        int throwFailures = method.IndexOf("cleanup.ThrowIfAny(\"disposing markdown layout snapshot resources\");", publishTasks, StringComparison.Ordinal);

        Assert.True(collect >= 0 && dispose > collect);
        Assert.True(publishTasks > dispose && throwFailures > publishTasks);
        Assert.Contains("cleanup.TryDispose(residencyPolicy);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("List<Exception>? failures", method, StringComparison.Ordinal);
    }

    private static string FindLayoutSnapshotSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Layout",
                "LayoutSnapshot.cs");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "Layout",
                "LayoutSnapshot.cs");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException("Could not locate LayoutSnapshot.cs from the test base directory.");
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
