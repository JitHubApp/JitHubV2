using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Engine;
using MarkdownRenderer.Theming;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Xunit;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Tests;

/// <summary>The highlighter host over any provider: worker scheduling, sharing, caching, atomic fallback and disposal.</summary>
public sealed class TextMateHighlighterTests
{
    [Fact]
    public async Task Highlighter_InvokesSynchronousProviderOnWorkerThread()
    {
        var provider = new RecordingProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);
        Exception? failure = null;
        bool callerWasThreadPool = true;

        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                callerWasThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                _ = highlighter.HighlightAsync(CreateRequest("csharp", "var value = 1;"), CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        thread.Join();

        Assert.Null(failure);
        Assert.False(callerWasThreadPool);
        Assert.True(provider.WasThreadPoolThread);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task IdenticalConcurrentRequests_AreDeduplicatedWithoutSharingCallerCancellation()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);
        using var firstCancellation = new CancellationTokenSource();

        Task<CodeBlockHighlightResult?> first = highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;"), firstCancellation.Token).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<CodeBlockHighlightResult?> second = highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;"), CancellationToken.None).AsTask();
        await firstCancellation.CancelAsync();

        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(provider.WorkCancellation.IsCancellationRequested);

        provider.Release.TrySetResult();
        CodeBlockHighlightResult? result = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Single(result.Spans);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task LastCanceledWaiter_CancelsSharedProviderWorkPromptly()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);
        using var cancellation = new CancellationTokenSource();

        Task<CodeBlockHighlightResult?> work = highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;"), cancellation.Token).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await cancellation.CancelAsync();

        Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(2)));
        await provider.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CompletedResult_IsCachedAndWeightedBudgetEvictsLeastRecentlyUsedEntry()
    {
        var provider = new CountingProvider();
        var options = new TextMateHighlighterOptions(cacheBudgetBytes: 330);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider, options);

        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "a"), CancellationToken.None);
        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "a"), CancellationToken.None);
        Assert.Equal(1, provider.InvocationCount);

        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "second"), CancellationToken.None);
        _ = await highlighter.HighlightAsync(CreateRequest("csharp", "a"), CancellationToken.None);
        Assert.Equal(3, provider.InvocationCount);
    }

    [Theory]
    [InlineData("12345", 4, 10, 10)]
    [InlineData("a\nb\nc", 20, 2, 10)]
    [InlineData("12345", 20, 10, 4)]
    public async Task InputOutsideBudget_FallsBackWithoutInvokingProvider(
        string code,
        int maximumCodeLength,
        int maximumLineCount,
        int maximumLineLength)
    {
        var provider = new CountingProvider();
        var options = new TextMateHighlighterOptions(
            maximumCodeLength,
            maximumLineCount,
            maximumLineLength,
            maximumSpanCount: 10,
            cacheBudgetBytes: 0);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider, options);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", code), CancellationToken.None);

        Assert.Same(CodeBlockHighlightResult.Empty, result);
        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task ExcessiveProviderOutput_FallsBackAtomically()
    {
        var provider = new StaticProvider(new TextMateGrammarHighlightResult(
        [
            new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
            new TextMateGrammarHighlightSpan(1, 1, 0xFF00FF00),
        ]));
        var options = new TextMateHighlighterOptions(maximumSpanCount: 1);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider, options);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "ab"), CancellationToken.None);

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task OutOfRangeProviderSpan_FallsBackAtomically()
    {
        var provider = new StaticProvider(new TextMateGrammarHighlightResult(
        [
            new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
            new TextMateGrammarHighlightSpan(2, 2, 0xFF00FF00),
        ]));
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "abc"), CancellationToken.None);

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task OverlappingProviderSpans_FallBackAtomically()
    {
        var provider = new StaticProvider(new TextMateGrammarHighlightResult(
        [
            new TextMateGrammarHighlightSpan(0, 2, 0xFFFF0000),
            new TextMateGrammarHighlightSpan(1, 2, 0xFF00FF00),
        ]));
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "abc"), CancellationToken.None);

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task ProviderFailure_FallsBackToUnhighlightedCode()
    {
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(new ThrowingProvider());

        CodeBlockHighlightResult? result = await highlighter.HighlightAsync(
            CreateRequest("csharp", "var value = 1;"), CancellationToken.None);

        Assert.Same(CodeBlockHighlightResult.Empty, result);
    }

    [Fact]
    public async Task DisposingOwnedHighlighterCancelsWorkThenDisposesProvider()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);
        Task<CodeBlockHighlightResult?> work = highlighter.HighlightAsync(
            CreateRequest("csharp", "value"), CancellationToken.None).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        highlighter.Dispose();

        await provider.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => provider.IsDisposed, TimeSpan.FromSeconds(2)));
        Assert.Throws<ObjectDisposedException>(() => _ = highlighter.Revision);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            highlighter.HighlightAsync(CreateRequest("csharp", "later"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DisposeDoesNotBlockOnCancellationCallbacksOrDisposeTheirTokenEarly()
    {
        var provider = new BlockingCancellationProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);
        Task<CodeBlockHighlightResult?> work = highlighter.HighlightAsync(
            CreateRequest("csharp", "value"), CancellationToken.None).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        highlighter.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await provider.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(provider.IsDisposed);
        Assert.False(work.IsCompleted);

        provider.ReleaseCallback.Set();
        Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => provider.IsDisposed, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DisposeDoesNotBlockBehindProviderRevisionAndDefersOwnedProviderDisposal()
    {
        var provider = new BlockingRevisionProvider();
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);
        Task<int> revision = Task.Run(() => highlighter.Revision);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        highlighter.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(provider.IsDisposed);

        provider.Release.Set();
        Assert.Equal(7, await revision.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => provider.IsDisposed, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ProviderRevisionMayDisposeOwnerReentrantlyWithoutDeadlock()
    {
        TextMateCodeBlockSyntaxHighlighter? highlighter = null;
        var provider = new ReentrantRevisionProvider(() => highlighter!.Dispose());
        highlighter = new TextMateCodeBlockSyntaxHighlighter(
            provider,
            options: null,
            ownsProvider: true);

        Assert.Equal(11, highlighter.Revision);
        Assert.True(provider.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = highlighter.Revision);
    }

    [Fact]
    public void DisposingBorrowingHighlighterLeavesProviderAlive()
    {
        var provider = new ControlledProvider(observeCancellation: true);
        var highlighter = new TextMateCodeBlockSyntaxHighlighter(provider);

        highlighter.Dispose();

        Assert.False(highlighter.OwnsProvider);
        Assert.False(provider.IsDisposed);
        provider.Dispose();
    }

    private static CodeBlockHighlightRequest CreateRequest(string language, string code) =>
        new(language, code, MarkdownThemeVariant.Dark);

    private sealed class RecordingProvider : ITextMateGrammarProvider
    {
        public bool WasThreadPoolThread { get; private set; }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            WasThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            return ValueTask.FromResult<TextMateGrammarHighlightResult?>(
                TextMateGrammarHighlightResult.Empty);
        }
    }

    private sealed class CountingProvider : ITextMateGrammarProvider
    {
        public int InvocationCount { get; private set; }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult<TextMateGrammarHighlightResult?>(
                new TextMateGrammarHighlightResult(
                [
                    new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
                ]));
        }
    }

    private sealed class StaticProvider(TextMateGrammarHighlightResult result)
        : ITextMateGrammarProvider
    {
        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TextMateGrammarHighlightResult?>(result);
    }

    private sealed class ThrowingProvider : ITextMateGrammarProvider
    {
        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic provider failure");
    }

    private sealed class ControlledProvider(bool observeCancellation) : ITextMateGrammarProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public CancellationToken WorkCancellation { get; private set; }

        public async ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            WorkCancellation = cancellationToken;
            Started.TrySetResult();
            try
            {
                if (observeCancellation)
                    await Release.Task.WaitAsync(cancellationToken);
                else
                    await Release.Task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }

            return new TextMateGrammarHighlightResult(
            [
                new TextMateGrammarHighlightSpan(0, 1, 0xFFFF0000),
            ]);
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class BlockingCancellationProvider : ITextMateGrammarProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseCallback { get; } = new();

        public bool IsDisposed { get; private set; }

        public async ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                CallbackStarted.TrySetResult();
                if (!ReleaseCallback.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Cancellation callback was not released.");
                throw new InvalidOperationException("Synthetic cancellation callback failure.");
            });
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TextMateGrammarHighlightResult.Empty;
        }

        public void Dispose()
        {
            IsDisposed = true;
            ReleaseCallback.Dispose();
        }
    }

    private sealed class BlockingRevisionProvider : ITextMateGrammarProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public bool IsDisposed { get; private set; }

        public int Revision
        {
            get
            {
                Started.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The revision checkpoint was not released.");
                return 7;
            }
        }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TextMateGrammarHighlightResult?>(TextMateGrammarHighlightResult.Empty);

        public void Dispose()
        {
            IsDisposed = true;
            Release.Dispose();
        }
    }

    private sealed class ReentrantRevisionProvider(Action onRevision) : ITextMateGrammarProvider, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public int Revision
        {
            get
            {
                onRevision();
                return 11;
            }
        }

        public ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
            TextMateGrammarHighlightRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<TextMateGrammarHighlightResult?>(TextMateGrammarHighlightResult.Empty);

        public void Dispose() => IsDisposed = true;
    }
}
