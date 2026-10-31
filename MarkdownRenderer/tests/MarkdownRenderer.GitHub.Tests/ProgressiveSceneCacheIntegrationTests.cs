using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using System.Runtime.CompilerServices;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ProgressiveSceneCacheIntegrationTests
{
    private const string MarkerAttribute = MarkdownContentAttributes.DeferredScene;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task PendingPublicationSurvivesTrimAndConsumedSceneRemainsUsable(long configuredBudget)
    {
        long budget = configuredBudget < 0 ? (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024 : configuredBudget;
        var extension = new CountingSceneExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive with
        {
            SceneCacheBudgetBytes = budget,
        });
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("scene");
        string marker = Marker(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document, (IMarkdownPerformanceSessionInternal)session, new object(), CancellationToken.None))!;
        plan.UpdateVisibleMarkers([marker]);
        await PublishAsync(plan, marker);

        // The cache does not own an unconsumed publication. Even an oversized
        // result or a zero-budget session must complete its first layout handoff.
        session.Trim();
        Assert.Equal(0, session.GetSnapshot().SceneCacheEstimatedBytes);
        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? first));
        MarkdownVectorScene scene = Assert.Single(first!.Items).VectorScene!;
        Assert.Equal(1, extension.RenderCount);
        Assert.InRange(session.GetSnapshot().SceneCacheEstimatedBytes, 0, budget);

        session.Trim();
        // Trimming reconstruction metadata may require later re-preparation,
        // but cannot mutate or dispose content held by the current layout.
        Assert.Equal(20, scene.Width);
        Assert.Single(scene.Commands);
        Assert.Equal(1, extension.RenderCount);
        Assert.Empty(plan.Diagnostics);
        await plan.Retire();
        Assert.Equal(0, session.GetSnapshot().SceneCacheEstimatedBytes);
        Assert.False(plan.TryGetResult(marker, out _));
        GC.KeepAlive(scene);
    }

    [Fact]
    public async Task TrimForgetsReuseWithoutStrandingAVisibleMarker()
    {
        var extension = new CountingSceneExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("scene");
        string marker = Marker(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document, (IMarkdownPerformanceSessionInternal)session, new object(), CancellationToken.None))!;
        plan.UpdateVisibleMarkers([marker]);
        await PublishAsync(plan, marker);
        (WeakReference<MarkdownContentFragment> fragment, MarkdownVectorScene heldScene) = ConsumeWithoutKeepingFragment(plan, marker);
        session.Trim();
        await plan.AdvanceGeneration();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(fragment.TryGetTarget(out _));
        Assert.False(plan.TryGetResult(marker, out _));
        Assert.Equal(20, heldScene.Width);
        await PublishAsync(plan, marker);
        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? replacement));
        Assert.NotSame(heldScene, Assert.Single(replacement!.Items).VectorScene);
        Assert.Equal(2, extension.RenderCount);
        Assert.Empty(plan.Diagnostics);
        GC.KeepAlive(heldScene);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<MarkdownContentFragment>, MarkdownVectorScene) ConsumeWithoutKeepingFragment(
        MarkdownProgressiveScenePlan plan, string marker)
    {
        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? fragment));
        return (new WeakReference<MarkdownContentFragment>(fragment!), Assert.Single(fragment!.Items).VectorScene!);
    }

    [Fact]
    public async Task LiveSceneMetadataIsRestoredByWorkerRatherThanUiLookup()
    {
        var extension = new CountingSceneExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive with
        {
            SceneCacheBudgetBytes = 0,
        });
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("scene");
        string marker = Marker(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document, (IMarkdownPerformanceSessionInternal)session, new object(), CancellationToken.None))!;
        plan.UpdateVisibleMarkers([marker]);
        await PublishAsync(plan, marker);
        (WeakReference<MarkdownContentFragment> fragment, MarkdownVectorScene heldScene) = ConsumeWithoutKeepingFragment(plan, marker);
        await plan.AdvanceGeneration();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(fragment.TryGetTarget(out _));
        Assert.True(session.CompletedSceneCache.MetadataCount > 0);
        // Lookup is a cheap cache/weak-result read: no content-tree allocation
        // is allowed on the layout thread even while reconstruction can succeed.
        Assert.False(plan.TryGetResult(marker, out _));
        await PublishAsync(plan, marker);
        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? replacement));
        Assert.Same(heldScene, Assert.Single(replacement!.Items).VectorScene);
        Assert.Equal(1, extension.RenderCount);
        Assert.Equal(0, session.GetSnapshot().SceneCacheEstimatedBytes);
        Assert.Empty(plan.Diagnostics);
        GC.KeepAlive(heldScene);
    }

    [Fact]
    public async Task LeavingBandDoesNotKeepUnconsumedPublicationsOutsideTheAggregateCache()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(new CountingSceneExtension()).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("scene");
        string marker = Marker(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document, (IMarkdownPerformanceSessionInternal)session, new object(), CancellationToken.None))!;
        plan.UpdateVisibleMarkers([marker]);
        await PublishAsync(plan, marker);
        Assert.Equal(0, session.GetSnapshot().SceneCacheEstimatedBytes);
        plan.UpdateVisibleMarkers([]);
        Assert.True(session.GetSnapshot().SceneCacheEstimatedBytes > 0);
        Assert.True(plan.TryGetResult(marker, out _));
        Assert.True(session.GetSnapshot().SceneCacheHits > 0);
        await plan.Retire();
        Assert.Equal(0, session.GetSnapshot().SceneCacheEstimatedBytes);
    }

    [Fact]
    public async Task RetiringOnePlanRemovesOnlyItsSessionCacheEntries()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(new CountingSceneExtension()).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("scene");
        string marker = Marker(document);
        using MarkdownProgressiveScenePlan first = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document, (IMarkdownPerformanceSessionInternal)session, new object(), CancellationToken.None))!;
        using MarkdownProgressiveScenePlan second = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document, (IMarkdownPerformanceSessionInternal)session, new object(), CancellationToken.None))!;
        await PublishAsync(first, marker);
        await PublishAsync(second, marker);
        Assert.True(first.TryGetResult(marker, out _));
        long onePlanWeight = session.GetSnapshot().SceneCacheEstimatedBytes;
        Assert.True(second.TryGetResult(marker, out _));
        Assert.Equal(2 * onePlanWeight, session.GetSnapshot().SceneCacheEstimatedBytes);
        await first.Retire();
        Assert.Equal(onePlanWeight, session.GetSnapshot().SceneCacheEstimatedBytes);
        Assert.True(second.TryGetResult(marker, out _));
        await second.Retire();
        Assert.Equal(0, session.GetSnapshot().SceneCacheEstimatedBytes);
    }

    private static string Marker(MarkdownDocument document) => Assert.Single(
        document.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items)).Attributes[MarkerAttribute];

    private static async Task PublishAsync(MarkdownProgressiveScenePlan plan, string marker)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(plan.TrySchedule(marker, static action => { action(); return true; }, () => published.TrySetResult()));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class CountingSceneExtension : IMarkdownExtension
    {
        private int _renderCount;
        public string Id => "MarkdownRenderer.Tests.SceneCache";
        public int RenderCount => Volatile.Read(ref _renderCount);
        public void Configure(MarkdownExtensionBuilder builder) =>
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder builder)
        {
            if (context.CanDeferOffscreenScenes)
            {
                builder.AddCodeBlock("scene", null, context.Node.SourceSpan, MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = MarkdownSyntaxKinds.Block.Paragraph + "|scene",
                    });
                return ValueTask.CompletedTask;
            }
            Interlocked.Increment(ref _renderCount);
            var scene = new MarkdownVectorScene(20, 20, 10,
                [MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20))]);
            builder.AddVectorScene(scene, context.Node.SourceSpan, MarkdownStyleRole.Diagram,
                MarkdownAccessibilityRole.Diagram, "Scene", "Description", "scene");
            return ValueTask.CompletedTask;
        }
    }
}
