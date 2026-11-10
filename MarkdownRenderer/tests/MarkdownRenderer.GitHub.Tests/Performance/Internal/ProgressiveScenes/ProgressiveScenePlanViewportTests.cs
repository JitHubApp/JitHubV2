using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ProgressiveSceneDeferralFixture;

namespace MarkdownRenderer.GitHub.Tests.Performance.Internal.ProgressiveScenes;

/// <summary>
/// Checks that a deferred scene plan cancels or rejects scene work that
/// leaves the viewport, including queued work that has not started, and
/// allows the scene to re-enter.
/// </summary>
public sealed class ProgressiveScenePlanViewportTests
{
    [Fact]
    public async Task ScenePlanCancelsWorkThatLeavesTheViewportAndAllowsReentry()
    {
        var extension = new ViewportCancellationExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(extension)
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("deferred scene");
        string marker = GetSingleMarker(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int publicationCount = 0;

        plan.UpdateVisibleMarkers([marker]);
        Assert.True(plan.TrySchedule(
            marker,
            static action =>
            {
                action();
                return true;
            },
            () =>
            {
                if (Interlocked.Increment(ref publicationCount) == 1)
                    published.TrySetResult();
            },
            () => canceled.TrySetResult()));

        await extension.FirstRenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        plan.UpdateVisibleMarkers(Array.Empty<string>());
        await extension.FirstRenderCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(plan.TryGetResult(marker, out _));
        Assert.Equal(0, publicationCount);

        plan.UpdateVisibleMarkers([marker]);
        Assert.True(plan.TrySchedule(
            marker,
            static action =>
            {
                action();
                return true;
            },
            () =>
            {
                if (Interlocked.Increment(ref publicationCount) == 1)
                    published.TrySetResult();
            }));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, extension.RenderCount);
        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? result));
        Assert.Equal("materialized after reentry", Assert.Single(result!.Items).Text);
    }

    [Fact]
    public async Task ScenePlanRejectsQueuedResultWhenSceneLeavesViewportBeforePublication()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(new MarkerExtension(MarkdownSyntaxKinds.Block.Paragraph, duplicateSpans: false))
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("deferred scene");
        string marker = GetSingleMarker(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        var queuedPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var republished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledWorkRemoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool publishedOffscreen = false;

        plan.UpdateVisibleMarkers([marker]);
        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                queuedPublication.TrySetResult(action);
                return true;
            },
            () => publishedOffscreen = true,
            () => canceledWorkRemoved.TrySetResult()));

        Action publication = await queuedPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The rendered result is already queued for the UI, but the viewport
        // changes before that callback gets a chance to publish it.
        plan.UpdateVisibleMarkers(Array.Empty<string>());
        publication();

        Assert.False(publishedOffscreen);
        Assert.False(plan.TryGetResult(marker, out _));

        plan.UpdateVisibleMarkers([marker]);
        Func<Action, bool> enqueueInline = static action =>
        {
            action();
            return true;
        };
        Action markRepublished = () => republished.TrySetResult();
        bool rescheduled = plan.TrySchedule(
            marker,
            enqueueInline,
            markRepublished);
        if (!rescheduled)
        {
            // If token cancellation won the queued envelope, its dispatch is
            // a no-op and the producer removes the old active entry in its
            // asynchronous completion path. Wait for the retry notification,
            // which runs only after that entry is removed, before rescheduling.
            await canceledWorkRemoved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(plan.TrySchedule(marker, enqueueInline, markRepublished));
        }
        await republished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? result));
        Assert.Equal("materialized", Assert.Single(result!.Items).Text);
    }

    [Fact]
    public async Task ScenePlanCancelsAQueuedSceneBeforeItCanStartWhenViewportJumps()
    {
        var extension = new QueuedViewportCancellationExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(extension)
            .Build();
        using var session = new MarkdownPerformanceSession(new MarkdownPerformanceOptions
        {
            DeferOffscreenScenes = true,
            MaxConcurrentScenePreparations = 1,
        });
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(
            "first scene\n\nsecond scene");
        string[] markers = GetMarkers(document);
        Assert.Equal(2, markers.Length);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        plan.UpdateVisibleMarkers([markers[0]]);
        Assert.True(plan.TrySchedule(
            markers[0],
            static action =>
            {
                action();
                return true;
            },
            () => firstPublished.TrySetResult()));
        await extension.FirstRenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        plan.UpdateVisibleMarkers([markers[0], markers[1]]);
        Assert.True(plan.TrySchedule(
            markers[1],
            static action =>
            {
                action();
                return true;
            },
            () => secondPublished.TrySetResult(),
            () => staleCanceled.TrySetResult()));
        plan.UpdateVisibleMarkers([markers[0]]);
        await staleCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, extension.SecondRenderCount);

        extension.ReleaseFirstRender.TrySetResult();
        await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        plan.UpdateVisibleMarkers([markers[1]]);
        Assert.True(plan.TrySchedule(
            markers[1],
            static action =>
            {
                action();
                return true;
            },
            () => secondPublished.TrySetResult()));
        await secondPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, extension.SecondRenderCount);
    }

    private sealed class ViewportCancellationExtension : IMarkdownExtension
    {
        private int _renderCount;

        internal TaskCompletionSource FirstRenderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstRenderCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RenderCount => Volatile.Read(ref _renderCount);

        public string Id => "MarkdownRenderer.GitHub.Tests.ViewportCancellation";

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
                        [MarkerAttribute] = string.Concat(MarkdownSyntaxKinds.Block.Paragraph, "|viewport"),
                    });
                return;
            }

            if (Interlocked.Increment(ref _renderCount) == 1)
            {
                FirstRenderStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstRenderCanceled.TrySetResult();
                    throw;
                }
            }

            content.AddText("materialized after reentry", context.Node.SourceSpan);
        }
    }

    private sealed class QueuedViewportCancellationExtension : IMarkdownExtension
    {
        private int _secondRenderCount;

        internal TaskCompletionSource FirstRenderStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstRender { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int SecondRenderCount => Volatile.Read(ref _secondRenderCount);

        public string Id => "MarkdownRenderer.GitHub.Tests.QueuedViewportCancellation";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private async ValueTask RenderAsync(
            MarkdownExtensionContext context,
            MarkdownContentBuilder content)
        {
            if (context.CanDeferOffscreenScenes)
            {
                string token = context.Node.SourceSpan.Start == 0 ? "first" : "second";
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = string.Concat(MarkdownSyntaxKinds.Block.Paragraph, "|", token),
                    });
                return;
            }

            if (context.Node.SourceSpan.Start == 0)
            {
                FirstRenderStarted.TrySetResult();
                await ReleaseFirstRender.Task.WaitAsync(context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                Interlocked.Increment(ref _secondRenderCount);
            }

            content.AddText("materialized", context.Node.SourceSpan);
        }
    }
}
