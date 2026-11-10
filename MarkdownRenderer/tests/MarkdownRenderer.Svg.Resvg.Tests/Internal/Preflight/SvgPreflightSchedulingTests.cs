using System.Buffers;
using System.Text;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Preflight;

public sealed class SvgPreflightSchedulingTests
{
    [Fact]
    public void PreCanceledInspectionDoesNotAllocateAFullSourceSnapshot()
    {
        var request = new MarkdownSvgOpenRequest(new byte[1024 * 1024]);
        var options = new ResvgMarkdownSvgRendererOptions();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Warm the exception path before measuring this thread's allocations.
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = SvgPreflight.InspectAsync(request, options, cancellation.Token);
        });

        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _ = SvgPreflight.InspectAsync(request, options, cancellation.Token);
            Assert.Fail("Canceled preflight was admitted.");
        }
        catch (OperationCanceledException)
        {
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024,
            $"Canceled inspection allocated {allocated} bytes for a 1 MiB caller source.");
    }

    [Fact]
    public async Task PoolThreadRunsInspectionInlineOnTheCallingThread()
    {
        var invocation = await Task.Factory.StartNew(
            () =>
            {
                int callerThreadId = Environment.CurrentManagedThreadId;
                bool invokedBeforeReturn = false;
                Task<(int ThreadId, bool IsThreadPoolThread)> inspection =
                    SvgPreflight.RunInspectCoreAsync(
                        () =>
                        {
                            invokedBeforeReturn = true;
                            return Task.FromResult((
                                Environment.CurrentManagedThreadId,
                                Thread.CurrentThread.IsThreadPoolThread));
                        },
                        CancellationToken.None);

                return (CallerThreadId: callerThreadId, InvokedBeforeReturn: invokedBeforeReturn, Inspection: inspection);
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        Assert.True(invocation.InvokedBeforeReturn);
        (int inspectionThreadId, bool isThreadPoolThread) = await invocation.Inspection;
        Assert.Equal(invocation.CallerThreadId, inspectionThreadId);
        Assert.True(isThreadPoolThread);
    }

    [Fact]
    public async Task NonPoolCallerDispatchesInspectionToTheThreadPool()
    {
        var completion = new TaskCompletionSource<(int CallerThreadId, Task<(int ThreadId, bool IsThreadPoolThread)> Inspection)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            int callerThreadId = Environment.CurrentManagedThreadId;
            Task<(int ThreadId, bool IsThreadPoolThread)> inspection =
                SvgPreflight.RunInspectCoreAsync(
                    () => Task.FromResult((
                        Environment.CurrentManagedThreadId,
                        Thread.CurrentThread.IsThreadPoolThread)),
                    CancellationToken.None);
            completion.TrySetResult((callerThreadId, inspection));
        })
        {
            IsBackground = true,
        };

        caller.Start();
        (int callerThreadId, Task<(int ThreadId, bool IsThreadPoolThread)> inspectionTask) =
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (int inspectionThreadId, bool isThreadPoolThread) =
            await inspectionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(callerThreadId, inspectionThreadId);
        Assert.True(isThreadPoolThread);
    }

    [Fact]
    public async Task NonPoolCallerSnapshotsBorrowedMemoryOnTheThreadPool()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>");
        byte[] original = (byte[])source.Clone();
        using var manager = new ThreadObservingMemoryManager(source);
        var inspectionStarted = new TaskCompletionSource<(
            int CallerThreadId,
            Task<SvgPreflightResult> Inspection)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            int callerThreadId = Environment.CurrentManagedThreadId;
            var request = new MarkdownSvgOpenRequest(manager.BorrowedMemory);
            Task<SvgPreflightResult> inspection = SvgPreflight.InspectAsync(
                request,
                new ResvgMarkdownSvgRendererOptions(),
                CancellationToken.None);
            inspectionStarted.TrySetResult((callerThreadId, inspection));
        })
        {
            IsBackground = true,
        };

        caller.Start();
        (int callerThreadId, Task<SvgPreflightResult> inspectionTask) =
            await inspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SvgPreflightResult result = await inspectionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(manager.AccessCount > 0);
        Assert.Equal(0, manager.NonPoolAccessCount);
        Assert.NotEqual(callerThreadId, manager.LastAccessThreadId);
        Assert.True(manager.LastAccessWasThreadPoolThread);
        Assert.Equal(original, source);
        Assert.Equal(original, result.Source.ReadAllBytes());
    }

    [Fact]
    public async Task PreCanceledInspectionNeverInvokesCoreOnEitherSchedulingPath()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var poolInvocation = await Task.Factory.StartNew(
            () =>
            {
                bool invoked = false;
                Task<int> inspection = SvgPreflight.RunInspectCoreAsync(
                    () =>
                    {
                        invoked = true;
                        return Task.FromResult(1);
                    },
                    cancellation.Token);
                return (Invoked: invoked, Inspection: inspection);
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        Assert.False(poolInvocation.Invoked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await poolInvocation.Inspection);

        var nonPoolCompletion = new TaskCompletionSource<(bool Invoked, Task<int> Inspection)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            bool invoked = false;
            Task<int> inspection = SvgPreflight.RunInspectCoreAsync(
                () =>
                {
                    invoked = true;
                    return Task.FromResult(1);
                },
                cancellation.Token);
            nonPoolCompletion.TrySetResult((invoked, inspection));
        })
        {
            IsBackground = true,
        };
        caller.Start();

        (bool invoked, Task<int> inspectionTask) =
            await nonPoolCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(invoked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await inspectionTask);
    }

    [Fact]
    public async Task InlinePoolDispatchReportsFailureThroughTheTaskLikeTaskRun()
    {
        using var cancellation = new CancellationTokenSource();

        var outcomes = await Task.Factory.StartNew(
            () =>
            {
                Task<int> canceled = SvgPreflight.RunInspectCoreAsync<int>(
                    () =>
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                        return Task.FromResult(1);
                    },
                    cancellation.Token);
                Task<int> faulted = SvgPreflight.RunInspectCoreAsync<int>(
                    () => throw new InvalidDataException("inline failure"),
                    CancellationToken.None);
                return (Canceled: canceled, Faulted: faulted);
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        Assert.True(outcomes.Canceled.IsCanceled);
        Assert.True(outcomes.Faulted.IsFaulted);
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await outcomes.Faulted);
        Assert.Equal("inline failure", failure.Message);
    }

    [Fact]
    public async Task PoolThreadFastPathStillRejectsActiveSvgContent()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        var request = new MarkdownSvgOpenRequest(Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"));

        MarkdownSvgException rejection = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await Task.Run(() => SvgPreflight.InspectAsync(request, options, CancellationToken.None)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejection.Reason);
    }

    private sealed class ThreadObservingMemoryManager(byte[] source) : MemoryManager<byte>
    {
        private int _accessCount;
        private int _nonPoolAccessCount;
        private int _lastAccessThreadId;
        private int _lastAccessWasThreadPoolThread;

        public int AccessCount => Volatile.Read(ref _accessCount);

        public int NonPoolAccessCount => Volatile.Read(ref _nonPoolAccessCount);

        public int LastAccessThreadId => Volatile.Read(ref _lastAccessThreadId);

        public ReadOnlyMemory<byte> BorrowedMemory => CreateMemory(source.Length);

        public bool LastAccessWasThreadPoolThread =>
            Volatile.Read(ref _lastAccessWasThreadPoolThread) != 0;

        public override Span<byte> GetSpan()
        {
            bool isThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            Volatile.Write(ref _lastAccessThreadId, Environment.CurrentManagedThreadId);
            Volatile.Write(ref _lastAccessWasThreadPoolThread, isThreadPoolThread ? 1 : 0);
            Interlocked.Increment(ref _accessCount);
            if (!isThreadPoolThread)
                Interlocked.Increment(ref _nonPoolAccessCount);
            return source;
        }

        public override MemoryHandle Pin(int elementIndex = 0) =>
            throw new NotSupportedException("The test memory manager does not support pinning.");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
