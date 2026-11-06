using MarkdownRenderer.Extensions;
using System.Text;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Checks how the engine admits, deduplicates and cancels concurrent
/// parses: bounded unique work, shared parses across callers, concurrent
/// extension callbacks, and cancellation callbacks that reenter the engine.
/// </summary>
public sealed class MarkdownEngineParseAdmissionTests
{
    [Fact]
    public async Task ParseAsync_BoundsUniqueWorkAndStillDeduplicatesQueuedSources()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.bounded-async", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseCacheBudgetBytes(0)
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 3,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task<MarkdownRenderer.Document.MarkdownDocument> first = engine.ParseAsync("one");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownRenderer.Document.MarkdownDocument> second = engine.ParseAsync("two");
        Task<MarkdownRenderer.Document.MarkdownDocument> duplicate = engine.ParseAsync("two");
        Task<MarkdownRenderer.Document.MarkdownDocument> third = engine.ParseAsync("three");

        Assert.Equal(1, engine.ActiveParseCount);
        Assert.Equal(2, engine.QueuedParseCount);
        Assert.Equal(3, engine.OutstandingParseCount);

        release.TrySetResult();
        MarkdownRenderer.Document.MarkdownDocument[] documents = await Task.WhenAll(
            first, second, duplicate, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(documents[1], documents[2]);
        Assert.Equal(0, engine.OutstandingParseCount);
    }

    [Fact]
    public async Task ParseAsync_FullAdmissionRejectsAnotherUniqueSource()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.full-admission", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 2,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task first = engine.ParseAsync("one");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = engine.ParseAsync("two");
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ParseAsync("three"));

        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelingEveryQueuedWaiterImmediatelyReleasesAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.queued-cancellation", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 2,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task first = engine.ParseAsync("one");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task queued = engine.ParseAsync("two", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, engine.OutstandingParseCount);
        Assert.Equal(0, engine.QueuedParseCount);

        Task replacement = engine.ParseAsync("three");
        Assert.Equal(2, engine.OutstandingParseCount);
        release.TrySetResult();
        await Task.WhenAll(first, replacement).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AsyncExtensionCallbacksMayRunConcurrentlyAcrossDocuments()
    {
        int active = 0;
        int maximumActive = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.concurrent-async", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    int now = Interlocked.Increment(ref active);
                    if (now == 2)
                        Interlocked.Exchange(ref maximumActive, 2);
                    else
                        Interlocked.CompareExchange(ref maximumActive, 1, 0);
                    if (now == 2)
                        bothEntered.TrySetResult();
                    try
                    {
                        await release.Task.WaitAsync(context.CancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                })))
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 2,
                maximumOutstandingParseCount: 2,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task first = engine.ParseAsync("one");
        Task second = engine.ParseAsync("two");
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, maximumActive);
    }

    [Fact]
    public async Task ParseAsync_ZeroBudgetDeduplicatesOnlyConcurrentWork()
    {
        string source = CreateLargeMarkdown(5000);
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(0)
            .Build();

        var firstTask = engine.ParseAsync(source);
        var secondTask = engine.ParseAsync(source);
        var first = await firstTask;
        var second = await secondTask;
        var later = await engine.ParseAsync(source);

        Assert.Same(first, second);
        Assert.NotSame(first, later);
    }

    [Fact]
    public async Task ParseAsync_DeduplicatesEqualSourcesWithDifferentStringInstances()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.equal-source-deduplication", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseCacheBudgetBytes(0)
            .Build();
        string firstSource = new(['e', 'q', 'u', 'a', 'l']);
        string secondSource = new(firstSource.ToCharArray());
        Assert.NotSame(firstSource, secondSource);

        Task<MarkdownRenderer.Document.MarkdownDocument> first = engine.ParseAsync(firstSource);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownRenderer.Document.MarkdownDocument> second = engine.ParseAsync(secondSource);
        Assert.Equal(1, engine.OutstandingParseCount);

        release.TrySetResult();
        MarkdownRenderer.Document.MarkdownDocument[] documents = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(documents[0], documents[1]);
    }

    [Fact]
    public async Task ParseAsync_CancelingOneCallerDoesNotCancelSharedParse()
    {
        string source = CreateLargeMarkdown(5000);
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(8 * 1024 * 1024)
            .Build();
        using var cancellation = new CancellationTokenSource();

        var canceledCaller = engine.ParseAsync(source, cancellation.Token);
        var remainingCaller = engine.ParseAsync(source);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);
        var completed = await remainingCaller;
        var cached = await engine.ParseAsync(source);

        Assert.Same(completed, cached);
    }

    [Fact]
    public async Task ParseAsync_AbandonedWorkCannotPopulateCache()
    {
        string source = CreateLargeMarkdown(5000);
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(8 * 1024 * 1024)
            .Build();
        using var cancellation = new CancellationTokenSource();

        var abandoned = engine.ParseAsync(source, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveParseCount == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, engine.CompletedParseCount);

        var replacement = await engine.ParseAsync(source);
        var cached = await engine.ParseAsync(source);

        Assert.Same(replacement, cached);
    }

    [Fact]
    public async Task ParseAsync_CancellationCallbacksCanReenterEngineWithoutDeadlock()
    {
        var callbackRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reentrantParseStarted = new TaskCompletionSource<Task<MarkdownRenderer.Document.MarkdownDocument>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimaryParse = new ManualResetEventSlim();
        MarkdownEngine? engine = null;

        engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.reentrant-cancellation", builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, _) =>
                {
                    if (!string.Equals(context.Node.Literal, "primary", StringComparison.Ordinal))
                        return;

                    context.CancellationToken.Register(() =>
                    {
                        try
                        {
                            // ParseAsync enters the engine gate before returning.
                            // This callback therefore deadlocks if cancellation is
                            // signaled while that gate is held.
                            reentrantParseStarted.TrySetResult(engine!.ParseAsync("secondary"));
                        }
                        finally
                        {
                            releasePrimaryParse.Set();
                        }

                        throw new InvalidOperationException("Cancellation callback failures are isolated.");
                    });
                    callbackRegistered.TrySetResult();
                    if (!releasePrimaryParse.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Cancellation callback did not release the parser.");
                    context.CancellationToken.ThrowIfCancellationRequested();
                })))
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();

        using var cancellation = new CancellationTokenSource();
        Task<MarkdownRenderer.Document.MarkdownDocument> primary =
            engine.ParseAsync("primary", cancellation.Token);
        await callbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task cancel = Task.Run(cancellation.Cancel);
        Task<MarkdownRenderer.Document.MarkdownDocument> reentrant =
            await reentrantParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => primary.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("secondary", (await reentrant.WaitAsync(TimeSpan.FromSeconds(5))).Source);
        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveParseCount == 0,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ParseAsync_LastWaiterCancellationDoesNotWaitForExtensionCallbacks()
    {
        var callbackRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        using var parserRelease = new ManualResetEventSlim();

        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.blocking-cancellation", builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, _) =>
                {
                    using var registration = context.CancellationToken.Register(() =>
                    {
                        callbackEntered.TrySetResult();
                        if (!callbackRelease.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("Cancellation callback was not released.");
                        parserRelease.Set();
                    });
                    callbackRegistered.TrySetResult();
                    if (!parserRelease.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Parser was not released.");
                    context.CancellationToken.ThrowIfCancellationRequested();
                })))
            .WithParseCacheBudgetBytes(0)
            .Build();

        using var cancellation = new CancellationTokenSource();
        Task parse = engine.ParseAsync("active", cancellation.Token);
        await callbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task cancel = Task.Run(cancellation.Cancel);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => parse.WaitAsync(TimeSpan.FromSeconds(1)));
            await cancel.WaitAsync(TimeSpan.FromSeconds(1));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, engine.ActiveParseCount);
        }
        finally
        {
            callbackRelease.Set();
            parserRelease.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveParseCount == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, engine.CompletedParseCount);
    }

    private static string CreateLargeMarkdown(int sectionCount)
    {
        var builder = new StringBuilder(sectionCount * 32);
        for (int index = 0; index < sectionCount; index++)
        {
            builder.Append("## Section ");
            builder.Append(index);
            builder.Append("\n\nParagraph ");
            builder.Append(index);
            builder.Append(".\n\n");
        }

        return builder.ToString();
    }
}
