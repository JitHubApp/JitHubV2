using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ProgressiveSceneDeferralFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how a deferred scene plan matches markers: the exact registered
/// syntax kind, visible marker readiness, the emitting and containing
/// renderer, duplicate source spans and materialized fallback ranges.
/// </summary>
public sealed class ProgressiveScenePlanMarkerTests
{
    [Fact]
    public async Task ScenePlanMatchesMarkerToTheExactRegisteredSyntaxKind()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(new MarkerExtension(MarkdownSyntaxKinds.Block.Math, duplicateSpans: false))
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("plain paragraph");

        MarkdownProgressiveScenePlan? plan = await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None);

        Assert.Null(plan);
    }

    [Fact]
    public async Task DeferredSceneReadiness_UsesRegisteredVisibleMarkersAndFailsClosedWhenBandIsUnknown()
    {
        const string source = "$$\n\\frac{1}{2}\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal sessionInternal = session;
        var owner = new object();
        MarkdownDocument document = Assert.IsType<MarkdownDocument>(
            await sessionInternal.ParseAndPrepareDocumentAsync(
                engine,
                null,
                source,
                new MarkdownExtensionRegistry(),
                owner,
                CancellationToken.None));
        string marker = GetSingleMarker(document);

        Assert.True(sessionInternal.HasRegisteredDeferredSceneWork(owner));
        Assert.True(sessionInternal.IsRegisteredDeferredSceneMarker(owner, marker));
        Assert.False(sessionInternal.IsRegisteredDeferredSceneMarker(owner, "math|not-registered"));
        Assert.True(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: false,
            visiblePlaceholderKeys: null,
            isRegisteredMarker: visiblePlaceholderKey => sessionInternal.IsRegisteredDeferredSceneMarker(owner, visiblePlaceholderKey)));
        Assert.True(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: [marker],
            isRegisteredMarker: visiblePlaceholderKey => sessionInternal.IsRegisteredDeferredSceneMarker(owner, visiblePlaceholderKey)));
        Assert.False(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: ["math|unregistered"],
            isRegisteredMarker: visiblePlaceholderKey => sessionInternal.IsRegisteredDeferredSceneMarker(owner, visiblePlaceholderKey)));
        Assert.False(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: Array.Empty<string>(),
            isRegisteredMarker: visiblePlaceholderKey => sessionInternal.IsRegisteredDeferredSceneMarker(owner, visiblePlaceholderKey)));
    }

    [Fact]
    public async Task ScenePlanReplaysOnlyTheCallbackThatEmittedTheMarker()
    {
        var extension = new ComposedDeferredExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(extension)
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("plain paragraph");
        string[] markers = GetMarkers(document);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        int publicationCount = 0;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Equal(2, markers.Length);
        Assert.Equal(1, extension.FallThroughCallCount);
        Assert.Equal(1, extension.ProducerCallCount);
        Assert.Equal(0, extension.TrailingCallCount);
        foreach (string marker in markers)
        {
            Assert.True(plan.TrySchedule(
                marker,
                static action =>
                {
                    action();
                    return true;
                },
                () =>
                {
                    if (Interlocked.Increment(ref publicationCount) == markers.Length)
                        published.TrySetResult();
                }));
        }
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, extension.FallThroughCallCount);
        Assert.Equal(3, extension.ProducerCallCount);
        Assert.Equal(0, extension.TrailingCallCount);
        foreach (string marker in markers)
        {
            Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? fragment));
            Assert.Equal("materialized-by-producer", Assert.Single(fragment!.Items).Text);
        }
    }

    [Fact]
    public async Task NestedDeferredMarker_StillCapturesItsContainingRenderer()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(new NestedMarkerExtension())
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("plain paragraph");
        MarkdownContent nestedFallback = Assert.Single(
            Assert.Single(document.ExtensionBlockNodeContent.Values).Items)
            .Children.Single();
        string marker = Assert.IsType<string>(nestedFallback.Attributes[MarkerAttribute]);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(plan.TrySchedule(
            marker,
            static action =>
            {
                action();
                return true;
            },
            () => published.TrySetResult()));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? result));
        Assert.Equal("materialized-from-nested-producer", Assert.Single(result!.Items).Text);
    }

    [Fact]
    public async Task ScenePlanKeepsDistinctMarkersWithDuplicateSourceSpans()
    {
        Assert.False(MarkdownRebuildDispatchPolicy.AdvancesDeferredSceneGeneration(sceneMaterialized: true));
        Assert.True(MarkdownRebuildDispatchPolicy.AdvancesDeferredSceneGeneration(sceneMaterialized: false));

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(new MarkerExtension(MarkdownSyntaxKinds.Block.Paragraph, duplicateSpans: true))
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal sessionInternal = session;
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("plain paragraph");
        MarkdownContent[] fallbacks = Assert.Single(document.ExtensionBlockNodeContent.Values).Items.ToArray();
        Assert.Collection(
            fallbacks,
            first => Assert.Equal(new SourceSpan(0, "plain paragraph".Length), first.SourceSpan),
            second => Assert.Equal(new SourceSpan(0, "plain paragraph".Length), second.SourceSpan));

        string firstMarker = fallbacks[0].Attributes[MarkerAttribute];
        string secondMarker = fallbacks[1].Attributes[MarkerAttribute];
        Assert.NotEqual(firstMarker, secondMarker);

        var owner = new object();
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            sessionInternal,
            owner,
            CancellationToken.None))!;
        int publicationCount = 0;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onMaterialized = () =>
        {
            if (MarkdownRebuildDispatchPolicy.AdvancesDeferredSceneGeneration(sceneMaterialized: true))
                sessionInternal.AdvanceDeferredSceneGeneration(owner);
            if (Interlocked.Increment(ref publicationCount) == 2)
                published.TrySetResult();
        };

        Assert.True(plan.TrySchedule(firstMarker, firstPublication.TrySetResult, onMaterialized));
        Action first = await firstPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(plan.TrySchedule(secondMarker, secondPublication.TrySetResult, onMaterialized));

        // Simulate the ordered UI dispatcher: the first scene's publication
        // triggers its non-invalidating materialization rebuild before the
        // sibling is allowed to commit. On the one-slot/x86 lane, acknowledging
        // the first also admits the sibling's preparation.
        first();
        Action second = await secondPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        second();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(plan.TryGetResult(firstMarker, out _));
        Assert.True(plan.TryGetResult(secondMarker, out _));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public async Task ScenePlanPublishesMaterializedFallbackAndItsExactDiagnosticRange()
    {
        const string tex = @"\notacommand{sample}";
        string source = "$$\n" + tex + "\n$$";
        var diagnosticRange = new SourceSpan(source.IndexOf(tex, StringComparison.Ordinal), tex.Length);
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
        var queuedPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                queuedPublication.TrySetResult(action);
                return true;
            },
            () => published.TrySetResult()));

        Action publication = await queuedPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        publication();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? result));
        Assert.Equal(source, Assert.Single(result!.Items).Text);
        MarkdownContent fallback = Assert.Single(result.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, fallback.Kind);
        Assert.False(fallback.Attributes.ContainsKey(MarkerAttribute));
        Assert.True(plan.IsRegisteredMarker(marker));
        Assert.True(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: [marker],
            isRegisteredMarker: plan.IsRegisteredMarker));
        Assert.False(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: Array.Empty<string>(),
            isRegisteredMarker: plan.IsRegisteredMarker));
        MarkdownDiagnostic diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal("MATH100", diagnostic.Code);
        Assert.Equal(diagnosticRange, diagnostic.SourceSpan);
    }

    private sealed class ComposedDeferredExtension : IMarkdownExtension
    {
        private int _fallThroughCallCount;
        private int _producerCallCount;
        private int _trailingCallCount;

        public string Id => "MarkdownRenderer.GitHub.Tests.ComposedDeferred";

        internal int FallThroughCallCount => Volatile.Read(ref _fallThroughCallCount);
        internal int ProducerCallCount => Volatile.Read(ref _producerCallCount);
        internal int TrailingCallCount => Volatile.Read(ref _trailingCallCount);

        public void Configure(MarkdownExtensionBuilder builder)
        {
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, FallThroughAsync);
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, ProduceAsync);
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, TrailingAsync);
        }

        private ValueTask FallThroughAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            Interlocked.Increment(ref _fallThroughCallCount);
            return ValueTask.CompletedTask;
        }

        private ValueTask ProduceAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            Interlocked.Increment(ref _producerCallCount);
            if (!context.CanDeferOffscreenScenes)
            {
                content.AddText("materialized-by-producer", context.Node.SourceSpan);
                return ValueTask.CompletedTask;
            }

            for (int token = 1; token <= 2; token++)
            {
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = string.Concat(
                            MarkdownSyntaxKinds.Block.Paragraph,
                            "|",
                            token.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    });
            }
            return ValueTask.CompletedTask;
        }

        private ValueTask TrailingAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            Interlocked.Increment(ref _trailingCallCount);
            content.AddText("should-not-be-appended", context.Node.SourceSpan);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NestedMarkerExtension : IMarkdownExtension
    {
        private const string MarkerKind = MarkdownSyntaxKinds.Block.Paragraph;

        public string Id => "MarkdownRenderer.GitHub.Tests.NestedMarker";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlockAsync(MarkerKind, RenderAsync);

        private ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            if (!context.CanDeferOffscreenScenes)
            {
                content.AddText("materialized-from-nested-producer", context.Node.SourceSpan);
                return ValueTask.CompletedTask;
            }

            content.AddContainer(
                MarkdownContentKind.Container,
                MarkdownStyleRole.Body,
                context.Node.SourceSpan,
                MarkdownAccessibilityRole.Group,
                children => children.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = string.Concat(MarkerKind, "|nested"),
                    }));
            return ValueTask.CompletedTask;
        }
    }
}
