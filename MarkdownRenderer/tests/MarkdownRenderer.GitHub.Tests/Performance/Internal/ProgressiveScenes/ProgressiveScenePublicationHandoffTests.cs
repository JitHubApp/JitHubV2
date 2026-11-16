using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Performance.Internal.ProgressiveScenes;

/// <remarks>
/// This file is longer than 500 lines because each publication handoff race
/// (cancellation, reentrant dispatch, callback failure, late dispatcher
/// refusal) needs its own scripted dispatcher, and the shared scene extension,
/// publication queue and cleanup recorders those races use live beside them.
/// </remarks>
public sealed class ProgressiveScenePublicationHandoffTests
{
    private const string MarkerAttribute = MarkdownContentAttributes.DeferredScene;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SharedSceneSlotsBoundPreparedGraphsQueuedAcrossPlans(int requestedSlots)
    {
        int slots = IntPtr.Size == 4 ? 1 : requestedSlots;
        var extension = new HandoffSceneExtension(holdRenderUntilReleased: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive with
        {
            MaxConcurrentScenePreparations = slots,
        });
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(
            "first\n\nsecond\n\nthird\n\nfourth");
        string[] markers = GetMarkers(document);
        Assert.Equal(4, markers.Length);
        object firstOwner = new();
        object secondOwner = new();
        using MarkdownProgressiveScenePlan first = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            firstOwner,
            CancellationToken.None))!;
        using MarkdownProgressiveScenePlan second = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            secondOwner,
            CancellationToken.None))!;
        using var publications = new PublicationQueue();
        int published = 0;

        try
        {
            first.UpdateVisibleMarkers([markers[0], markers[1]]);
            second.UpdateVisibleMarkers([markers[2], markers[3]]);
            Assert.True(first.TrySchedule(markers[0], publications.Enqueue, () => Interlocked.Increment(ref published)));
            Assert.True(first.TrySchedule(markers[1], publications.Enqueue, () => Interlocked.Increment(ref published)));
            Assert.True(second.TrySchedule(markers[2], publications.Enqueue, () => Interlocked.Increment(ref published)));
            Assert.True(second.TrySchedule(markers[3], publications.Enqueue, () => Interlocked.Increment(ref published)));

            await extension.WaitForStartedAsync(slots);
            extension.Release(slots);
            await publications.WaitForCountAsync(slots);

            // A prepared result must continue to own a session scene slot while
            // its dispatcher action is pending. Thus at most the configured
            // number of graph-bearing publication envelopes can queue even
            // when multiple plans share the session.
            await Task.Delay(75);
            Assert.Equal(slots, extension.StartedCount);
            Assert.Equal(slots, extension.MaximumActiveRenders);

            int dispatched = 0;
            while (dispatched < markers.Length)
            {
                Action callback = await publications.TakeAsync();
                callback();
                dispatched++;

                int nextStart = System.Math.Min(markers.Length, dispatched + slots);
                await extension.WaitForStartedAsync(nextStart);
                if (nextStart > extension.ReleasedRenderCount)
                {
                    extension.Release(1);
                }
            }

            Assert.Equal(markers.Length, Volatile.Read(ref published));
            await first.AdvanceGeneration();
            await second.AdvanceGeneration();
        }
        finally
        {
            await first.Retire();
            await second.Retire();
            await session.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("viewport")]
    [InlineData("generation")]
    [InlineData("owner")]
    [InlineData("session")]
    public async Task CancellationBeforeDispatchClearsStrongPayloadButLeavesLateActionHarmless(string cancellation)
    {
        var extension = new HandoffSceneExtension(holdRenderUntilReleased: false, reportDiagnostic: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("queued scene");
        string marker = Assert.Single(GetMarkers(document));
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        using var publications = new PublicationQueue();
        int published = 0;
        plan.UpdateVisibleMarkers([marker]);

        Assert.True(plan.TrySchedule(marker, publications.Enqueue, () => Interlocked.Increment(ref published)));
        Action lateCallback = await publications.TakeAsync();
        WeakReference<MarkdownVectorScene> scene = extension.LastScene;

        switch (cancellation)
        {
            case "viewport":
                plan.UpdateVisibleMarkers(Array.Empty<string>());
                await plan.AdvanceGeneration();
                break;
            case "generation":
                await plan.AdvanceGeneration();
                break;
            case "owner":
                await plan.Retire();
                break;
            case "session":
                await session.DisposeAsync();
                break;
            default:
                throw new InvalidOperationException("Unknown cancellation case.");
        }

        AssertCollected(scene);
        lateCallback();
        Assert.Equal(0, Volatile.Read(ref published));
        Assert.Empty(plan.Diagnostics);
        Assert.False(plan.TryGetResult(marker, out _));

        await plan.Retire();
        await session.DisposeAsync();
        GC.KeepAlive(document);
        GC.KeepAlive(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementWaitsForPublicationAlreadyExecuting(bool disposeSession)
    {
        var extension = new HandoffSceneExtension(holdRenderUntilReleased: false, reportDiagnostic: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("executing scene");
        string marker = Assert.Single(GetMarkers(document));
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        using var publications = new PublicationQueue();
        var callbackStarted = NewSignal();
        using var releaseCallback = new ManualResetEventSlim();
        int published = 0;
        plan.UpdateVisibleMarkers([marker]);

        Assert.True(plan.TrySchedule(
            marker,
            publications.Enqueue,
            () =>
            {
                Interlocked.Increment(ref published);
                callbackStarted.TrySetResult();
                Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
            }));
        Action callback = await publications.TakeAsync();
        Task callbackTask = Task.Run(callback);
        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // If the UI callback has atomically started, cancellation may not
            // acknowledge the handoff early: it owns the graph until its finally.
            Task retirement = disposeSession
                ? session.DisposeAsync().AsTask()
                : plan.Retire();
            await Task.Delay(50);
            Assert.False(retirement.IsCompleted);

            releaseCallback.Set();
            await callbackTask.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseCallback.Set();
            await callbackTask.WaitAsync(TimeSpan.FromSeconds(5));
            await plan.Retire();
            await session.DisposeAsync();
        }
        Assert.Equal(1, Volatile.Read(ref published));
        Assert.Empty(plan.Diagnostics);
        Assert.False(plan.TryGetResult(marker, out _));

        GC.KeepAlive(document);
        GC.KeepAlive(engine);
    }

    [Fact]
    public async Task ReentrantSynchronousPublicationAndDuplicateDispatcherCallCommitOnlyOnce()
    {
        var extension = new HandoffSceneExtension(holdRenderUntilReleased: false, reportDiagnostic: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("reentrant scene");
        string marker = Assert.Single(GetMarkers(document));
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        plan.UpdateVisibleMarkers([marker]);
        var retireRequested = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        int published = 0;

        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                action();
                action();
                return true;
            },
            () =>
            {
                Interlocked.Increment(ref published);
                retireRequested.TrySetResult(plan.Retire());
            }));

        Task retirement = await retireRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref published));
        Assert.False(plan.TryGetResult(marker, out _));
        await session.DisposeAsync();
        GC.KeepAlive(document);
        GC.KeepAlive(engine);
    }

    [Fact]
    public async Task PublicationCallbackFailureIsAcknowledgedAndObservedByRetirement()
    {
        var extension = new HandoffSceneExtension(holdRenderUntilReleased: false);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("failing publication");
        string marker = Assert.Single(GetMarkers(document));
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        using var publications = new PublicationQueue();
        plan.UpdateVisibleMarkers([marker]);

        Assert.True(plan.TrySchedule(
            marker,
            publications.Enqueue,
            static () => throw new InvalidOperationException("publication callback failed")));
        Action callback = await publications.TakeAsync();

        // Dispatch is expected to be invoked later by a UI dispatcher. The
        // failure must complete the producer's handoff task rather than escape
        // this callback and become an unobserved UI-thread exception.
        callback();

        Exception? failure = null;
        try
        {
            await plan.Retire();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.Contains(
            EnumerateExceptions(failure!),
            static exception => exception is InvalidOperationException { Message: "publication callback failed" });
        Assert.False(plan.TryGetResult(marker, out _));

        try
        {
            await session.DisposeAsync();
        }
        catch
        {
            // The same fault remains observable through the session's
            // retirement barrier; this test asserts the plan-level boundary.
        }
        GC.KeepAlive(document);
        GC.KeepAlive(engine);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DispatcherRejectionOrThrowAfterCallbackStartsWaitsForCallback(
        bool throws,
        bool disposeSession)
    {
        var extension = new HandoffSceneExtension(holdRenderUntilReleased: false);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("dispatch handoff race");
        string marker = Assert.Single(GetMarkers(document));
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        plan.UpdateVisibleMarkers([marker]);

        using var race = new LateDispatcherRace(throws);
        Assert.True(plan.TrySchedule(marker, race.EnqueueAfterCallbackStarts, race.RunPublicationCallback));

        Task? retirement = null;
        Task? callbackExecution = null;
        var failures = new HandoffCleanupFailures();
        try
        {
            await race.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await race.DispatcherDecided.Task.WaitAsync(TimeSpan.FromSeconds(10));
            callbackExecution = await race.CallbackTask.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The dispatcher has rejected/thrown, but DispatchCore already
            // changed the envelope to Executing. Neither owner nor session
            // retirement may release the prepared graph before this callback
            // acknowledges completion.
            retirement = disposeSession
                ? session.DisposeAsync().AsTask()
                : plan.Retire();
            await Task.Delay(50);
            Assert.False(retirement.IsCompleted);
        }
        finally
        {
            race.ReleaseCallback.Set();
            await ReleaseAndRetireRecordingFailuresAsync(
                callbackExecution,
                retirement,
                () => disposeSession ? session.DisposeAsync().AsTask() : plan.Retire(),
                plan,
                session,
                failures);
        }

        AssertFaultReportedOnlyByPlanBarrier(throws, disposeSession, failures, race.EnqueueFailure);
        Assert.Null(failures.Session);
        Assert.Null(failures.CallbackEscape);
        Assert.False(plan.TryGetResult(marker, out _));
        GC.KeepAlive(document);
        GC.KeepAlive(engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrThrowingDispatcherDropsPreparedGraph(bool throws)
    {
        var extension = new HandoffSceneExtension(holdRenderUntilReleased: false);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("unaccepted scene");
        string marker = Assert.Single(GetMarkers(document));
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        plan.UpdateVisibleMarkers([marker]);
        Action? lateCallback = null;
        var enqueueAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                lateCallback = action;
                enqueueAttempted.TrySetResult();
                if (throws)
                    throw new InvalidOperationException("dispatcher enqueue failed");
                return false;
            },
            static () => { }));
        await enqueueAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        WeakReference<MarkdownVectorScene> scene = extension.LastScene;

        if (throws)
        {
            Exception? failure = null;
            try
            {
                await plan.Retire();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Assert.NotNull(failure);
            Assert.Contains(
                EnumerateExceptions(failure!),
                static exception => exception is InvalidOperationException { Message: "dispatcher enqueue failed" });
        }
        else
        {
            await plan.Retire();
        }

        AssertCollected(scene);
        lateCallback!();
        Assert.False(plan.TryGetResult(marker, out _));
        await session.DisposeAsync();
        GC.KeepAlive(document);
        GC.KeepAlive(engine);
    }

    private static string[] GetMarkers(MarkdownDocument document) => document.ExtensionBlockNodeContent.Values
        .SelectMany(static fragment => fragment.Items)
        .Where(static content => content.Attributes.ContainsKey(MarkerAttribute))
        .OrderBy(static content => content.SourceSpan.Start)
        .Select(static content => (string)content.Attributes[MarkerAttribute])
        .ToArray();

    // Waits for the released callback, then the chosen retirement (started here
    // if the scenario did not reach it), the plan's own barrier and the session,
    // in that order, recording each one's failure.
    private static async Task ReleaseAndRetireRecordingFailuresAsync(
        Task? callbackExecution,
        Task? retirement,
        Func<Task> startRetirement,
        MarkdownProgressiveScenePlan plan,
        MarkdownPerformanceSession session,
        HandoffCleanupFailures failures)
    {
        if (callbackExecution is not null)
        {
            try
            {
                await callbackExecution.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception)
            {
                failures.CallbackEscape = exception;
            }
        }

        retirement ??= startRetirement();
        try
        {
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            failures.Retirement = exception;
        }

        try
        {
            await plan.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            failures.Plan = exception;
        }
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception)
        {
            failures.Session = exception;
        }
    }

    private static void AssertFaultReportedOnlyByPlanBarrier(
        bool throws,
        bool disposeSession,
        HandoffCleanupFailures failures,
        InvalidOperationException enqueueFailure)
    {
        if (throws)
        {
            // This test creates a plan directly rather than registering it
            // with the session, so the session barrier must wait for the
            // active lease but only the plan's own barrier reports its fault.
            Assert.NotNull(failures.Plan);
            Assert.Contains(
                EnumerateExceptions(failures.Plan!),
                exception => exception is InvalidOperationException &&
                    exception.Message == enqueueFailure.Message);
            if (disposeSession)
            {
                Assert.Null(failures.Retirement);
            }
            else
            {
                Assert.NotNull(failures.Retirement);
                Assert.Contains(
                    EnumerateExceptions(failures.Retirement!),
                    exception => exception is InvalidOperationException &&
                        exception.Message == enqueueFailure.Message);
            }
        }
        else
        {
            Assert.Null(failures.Plan);
            Assert.Null(failures.Retirement);
        }
    }

    private sealed class HandoffCleanupFailures
    {
        internal Exception? Retirement { get; set; }
        internal Exception? Plan { get; set; }
        internal Exception? Session { get; set; }
        internal Exception? CallbackEscape { get; set; }
    }

    /// <summary>
    /// A dispatcher that starts the publication callback on the thread pool,
    /// waits until the callback has entered, and only then rejects (or throws),
    /// so the plan sees a refusal after its callback already began executing.
    /// </summary>
    private sealed class LateDispatcherRace(bool throws) : IDisposable
    {
        private readonly ManualResetEventSlim _callbackEntered = new();

        internal ManualResetEventSlim ReleaseCallback { get; } = new();
        internal TaskCompletionSource CallbackStarted { get; } = NewSignal();
        internal TaskCompletionSource DispatcherDecided { get; } = NewSignal();
        internal TaskCompletionSource<Task> CallbackTask { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException EnqueueFailure { get; } =
            new("dispatcher enqueue failed after callback started");

        internal bool EnqueueAfterCallbackStarts(Action action)
        {
            Task callbackTask = Task.Run(action);
            CallbackTask.TrySetResult(callbackTask);
            if (!_callbackEntered.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The publication callback did not enter before the dispatcher decision.");

            DispatcherDecided.TrySetResult();
            if (throws)
                throw EnqueueFailure;
            return false;
        }

        internal void RunPublicationCallback()
        {
            CallbackStarted.TrySetResult();
            _callbackEntered.Set();
            if (!ReleaseCallback.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The publication callback release barrier was not signaled.");
        }

        public void Dispose()
        {
            ReleaseCallback.Dispose();
            _callbackEntered.Dispose();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCollected(WeakReference<MarkdownVectorScene> reference)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!reference.TryGetTarget(out _))
                return;
            Thread.Sleep(10);
        }

        Assert.False(reference.TryGetTarget(out _), "A canceled dispatcher closure retained the prepared vector graph.");
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out Exception? current))
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private sealed class PublicationQueue : IDisposable
    {
        private readonly ConcurrentQueue<Action> _actions = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal bool Enqueue(Action action)
        {
            _actions.Enqueue(action);
            Interlocked.Increment(ref _count);
            _available.Release();
            return true;
        }

        internal async Task<Action> TakeAsync()
        {
            await _available.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(_actions.TryDequeue(out Action? action));
            Interlocked.Decrement(ref _count);
            return action!;
        }

        internal async Task WaitForCountAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Count < count && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            Assert.True(Count >= count, $"Expected {count} queued publication callbacks, observed {Count}.");
        }

        public void Dispose() => _available.Dispose();
    }

    private sealed class HandoffSceneExtension(
        bool holdRenderUntilReleased,
        bool reportDiagnostic = false) : IMarkdownExtension
    {
        private readonly SemaphoreSlim _renderReleases = new(0);
        private readonly object _startGate = new();
        private readonly List<(int Count, TaskCompletionSource Completion)> _startWaiters = new();
        private WeakReference<MarkdownVectorScene>? _lastScene;
        private int _started;
        private int _activeRenders;
        private int _maximumActiveRenders;
        private int _releasedRenderCount;

        public string Id => "MarkdownRenderer.Tests.ScenePublicationHandoff";

        internal int StartedCount => Volatile.Read(ref _started);
        internal int MaximumActiveRenders => Volatile.Read(ref _maximumActiveRenders);
        internal int ReleasedRenderCount => Volatile.Read(ref _releasedRenderCount);
        internal WeakReference<MarkdownVectorScene> LastScene =>
            Volatile.Read(ref _lastScene) ?? throw new InvalidOperationException("No scene has been rendered.");

        public void Configure(MarkdownExtensionBuilder builder) =>
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        internal void Release(int count)
        {
            for (int index = 0; index < count; index++)
                _renderReleases.Release();
            Interlocked.Add(ref _releasedRenderCount, count);
        }

        internal Task WaitForStartedAsync(int count)
        {
            TaskCompletionSource completion;
            lock (_startGate)
            {
                if (_started >= count)
                    return Task.CompletedTask;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _startWaiters.Add((count, completion));
            }
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private async ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder builder)
        {
            string marker = MarkdownSyntaxKinds.Block.Paragraph + "|" +
                context.Node.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (context.CanDeferOffscreenScenes)
            {
                builder.AddCodeBlock(
                    context.Node.Literal ?? "scene",
                    "scene",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = marker,
                    });
                return;
            }

            int active = Interlocked.Increment(ref _activeRenders);
            UpdateMaximum(active);
            SignalStarted();
            try
            {
                if (holdRenderUntilReleased)
                    await _renderReleases.WaitAsync(context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
                var scene = new MarkdownVectorScene(
                    20,
                    20,
                    10,
                    [MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20))]);
                Interlocked.Exchange(ref _lastScene, new WeakReference<MarkdownVectorScene>(scene));
                if (reportDiagnostic)
                {
                    context.ReportDiagnostic(
                        "TESTSCENE001",
                        MarkdownDiagnosticSeverity.Warning,
                        "Unpublished handoff diagnostic.",
                        context.Node.SourceSpan);
                }
                builder.AddVectorScene(
                    scene,
                    context.Node.SourceSpan,
                    MarkdownStyleRole.Diagram,
                    MarkdownAccessibilityRole.Diagram,
                    "Scene",
                    "Description",
                    "scene");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRenders);
            }
        }

        private void SignalStarted()
        {
            TaskCompletionSource[] ready;
            lock (_startGate)
            {
                int count = ++_started;
                ready = _startWaiters
                    .Where(waiter => waiter.Count <= count)
                    .Select(static waiter => waiter.Completion)
                    .ToArray();
                _startWaiters.RemoveAll(waiter => waiter.Count <= count);
            }
            foreach (TaskCompletionSource waiter in ready)
                waiter.TrySetResult();
        }

        private void UpdateMaximum(int active)
        {
            int current;
            while (active > (current = Volatile.Read(ref _maximumActiveRenders)) &&
                   Interlocked.CompareExchange(ref _maximumActiveRenders, active, current) != current)
            {
            }
        }
    }
}
