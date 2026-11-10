using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Math;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ProgressiveSceneDeferralFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks that a deferred scene plan rejects queued results after a
/// generation change or disposal, and drains provider work before a
/// generation retires.
/// </summary>
public sealed class ProgressiveScenePlanGenerationTests
{
    [Fact]
    public async Task ScenePlanRejectsQueuedResultAfterGenerationChanges()
    {
        const string tex = @"\notacommand{sample}";
        string source = "$$\n" + tex + "\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        string marker = GetSingleMarker(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        await AssertQueuedResultRejectedAfterGenerationChangeAsync(plan, marker);

        var rescheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(plan.TrySchedule(
            marker,
            static action =>
            {
                action();
                return true;
            },
            () => rescheduled.TrySetResult()));
        await rescheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(plan.TryGetResult(marker, out _));
        Assert.Equal("MATH100", Assert.Single(plan.Diagnostics).Code);
    }

    [Fact]
    public async Task ScenePlanRejectsQueuedResultsAcrossRepeatedGenerations()
    {
        const int iterations = 100;
        const string tex = @"\notacommand{sample}";
        string formula = "$$\n" + tex + "\n$$";
        string source = string.Join("\n\n", Enumerable.Repeat(formula, iterations));
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        string[] markers = GetMarkers(document);
        Assert.Equal(iterations, markers.Length);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;

        foreach (string marker in markers)
            await AssertQueuedResultRejectedAfterGenerationChangeAsync(plan, marker);

        Assert.Empty(plan.Diagnostics);
    }

    private static async Task AssertQueuedResultRejectedAfterGenerationChangeAsync(
        MarkdownProgressiveScenePlan plan,
        string marker)
    {
        var queuedPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnqueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool published = false;
        int retryCount = 0;
        Action? publication = null;
        Task? generationRetirement = null;

        try
        {
            Assert.True(plan.TrySchedule(
                marker,
                action =>
                {
                    queuedPublication.TrySetResult(action);
                    releaseEnqueue.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    return true;
                },
                () => published = true,
                () => Interlocked.Increment(ref retryCount)));

            publication = await queuedPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
            generationRetirement = plan.AdvanceGeneration();
            // Keep the queue delegate held until this check: cancellation callbacks are async,
            // and otherwise the old producer may retire/remove its active marker first.
            Assert.False(plan.TrySchedule(marker, static _ => true, static () => { }));
        }
        finally
        {
            // The worker is deliberately blocked in the queue delegate; always release it even
            // when an assertion fails so this test cannot strand a scene-preparation worker.
            releaseEnqueue.TrySetResult();
        }

        publication!();
        await generationRetirement!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(published);
        Assert.False(plan.TryGetResult(marker, out _));
        Assert.Equal(1, retryCount);
    }

    [Fact]
    public async Task ScenePlanGenerationAndDisposalDrainsNonCooperativeProviderWork()
    {
        var extension = new NonCooperativeDeferredExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(extension)
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("deferred scene");
        string marker = GetSingleMarker(document);
        MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;

        Assert.True(plan.TrySchedule(marker, static _ => true, static () => { }));
        await extension.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task generationRetirement = plan.AdvanceGeneration();
        Task planRetirement = plan.Retire();
        Assert.False(generationRetirement.IsCompleted);
        Assert.False(planRetirement.IsCompleted);

        extension.ReleaseRender.TrySetResult();
        await generationRetirement.WaitAsync(TimeSpan.FromSeconds(10));
        await planRetirement.WaitAsync(TimeSpan.FromSeconds(10));
        plan.Dispose();
    }

    [Fact]
    public async Task ScenePlanRetirementWaitsForPriorGenerationDrainAfterWorkLeavesActiveMap()
    {
        var extension = new CancellableDeferredExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(extension)
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("deferred scene");
        string marker = GetSingleMarker(document);
        MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(plan.TrySchedule(
            marker,
            static _ => true,
            static () => { },
            () =>
            {
                retryEntered.TrySetResult();
                releaseRetry.Task.GetAwaiter().GetResult();
            }));
        await extension.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task generationRetirement = plan.AdvanceGeneration();
        Task? planRetirement = null;
        try
        {
            // The provider observes cancellation and drops out of `_active`,
            // then deliberately blocks in the retry callback before its task
            // completes. The earlier generation drain still owns that tail.
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(generationRetirement.IsCompleted);
            planRetirement = plan.Retire();
            Assert.False(planRetirement.IsCompleted);
        }
        finally
        {
            releaseRetry.TrySetResult();
        }

        await generationRetirement.WaitAsync(TimeSpan.FromSeconds(10));
        await (planRetirement ?? plan.Retire()).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ScenePlanRejectsQueuedResultAfterDisposal()
    {
        const string tex = @"\frac{1}{2}";
        string source = "$$\n" + tex + "\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        string marker = GetSingleMarker(document);
        MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        var queuedPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool published = false;

        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                queuedPublication.TrySetResult(action);
                return true;
            },
            () => published = true));

        Action publication = await queuedPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        plan.Dispose();
        publication();

        Assert.False(published);
        Assert.False(plan.TryGetResult(marker, out _));
        Assert.Empty(plan.Diagnostics);
    }

    private sealed class NonCooperativeDeferredExtension : IMarkdownExtension
    {
        internal TaskCompletionSource RenderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRender { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "MarkdownRenderer.GitHub.Tests.NonCooperativeDeferred";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private async ValueTask RenderAsync(
            MarkdownExtensionContext context,
            MarkdownContentBuilder content)
        {
            if (context.CanDeferOffscreenScenes)
            {
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = string.Concat(MarkdownSyntaxKinds.Block.Paragraph, "|non-cooperative"),
                    });
                return;
            }

            RenderStarted.TrySetResult();
            // Deliberately ignores cancellation: owner retirement must wait
            // until the extension has really stopped using its captured engine.
            await ReleaseRender.Task;
            content.AddText("completed", context.Node.SourceSpan);
        }
    }

    private sealed class CancellableDeferredExtension : IMarkdownExtension
    {
        internal TaskCompletionSource RenderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "MarkdownRenderer.GitHub.Tests.CancellableDeferred";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private async ValueTask RenderAsync(
            MarkdownExtensionContext context,
            MarkdownContentBuilder content)
        {
            if (context.CanDeferOffscreenScenes)
            {
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = string.Concat(MarkdownSyntaxKinds.Block.Paragraph, "|cancellable"),
                    });
                return;
            }

            RenderStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            content.AddText("unreachable", context.Node.SourceSpan);
        }
    }
}
