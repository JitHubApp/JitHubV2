using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ProgressiveSceneDeferralTests
{
    private const string MarkerAttribute = "renderer.internal.deferred-scene";

    [Fact]
    public void OffscreenSceneDeferral_IsEnabledOnlyByTheProgressivePreset()
    {
        Assert.True(MarkdownPerformanceOptions.Progressive.DeferOffscreenScenes);
        Assert.False(new MarkdownPerformanceOptions().DeferOffscreenScenes);
        Assert.False((MarkdownPerformanceOptions.Progressive with
        {
            DeferOffscreenScenes = false,
        }).DeferOffscreenScenes);
    }

    [Fact]
    public async Task SessionDiagnosticsQuery_DoesNotMutateEagerDocumentDiagnostics()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var owner = new object();

        MarkdownDocument document = Assert.IsType<MarkdownDocument>(
            await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                engine,
                null,
                "$$\n\\notacommand{x}\n$$",
                new MarkdownExtensionRegistry(),
                owner,
                CancellationToken.None));

        Assert.Empty(document.Diagnostics);
        MarkdownContent fallback = Assert.Single(
            document.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items));
        Assert.Equal(MarkdownContentKind.CodeBlock, fallback.Kind);
        Assert.True(fallback.Attributes.ContainsKey(MarkerAttribute));
        Assert.Empty(session.GetDeferredSceneDiagnostics(document));
        ((IMarkdownPerformanceSessionInternal)session).ReleaseDeferredSceneDocument(owner);
        Assert.Empty(session.GetDeferredSceneDiagnostics(document));
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public async Task SessionParseAndPrepare_UsesEagerPathWhenDeferralIsNotEnabled()
    {
        string source = "$$\n\\frac{1}{2}\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(new MarkdownPerformanceOptions());

        MarkdownDocument document = Assert.IsType<MarkdownDocument>(
            await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                engine,
                null,
                source,
                new MarkdownExtensionRegistry(),
                new object(),
                CancellationToken.None));

        MarkdownContent rendered = Assert.Single(
            document.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items));
        Assert.Equal(MarkdownContentKind.VectorScene, rendered.Kind);
        Assert.False(rendered.Attributes.ContainsKey(MarkerAttribute));
    }

    [Fact]
    public async Task DisposedSession_ReparsesReusableProgressiveDocumentEagerly()
    {
        const string tex = @"\frac{1}{2}";
        string source = "$$\n" + tex + "\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        MarkdownDocument progressive = await engine.ParseForProgressivePresentationAsync(source);
        Assert.Equal(MarkdownContentKind.CodeBlock, Assert.Single(
            progressive.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items)).Kind);

        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        session.Dispose();
        MarkdownDocument prepared = Assert.IsType<MarkdownDocument>(
            await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                engine,
                progressive,
                source,
                new MarkdownExtensionRegistry(),
                new object(),
                CancellationToken.None));

        Assert.NotSame(progressive, prepared);
        MarkdownContent eager = Assert.Single(
            prepared.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items));
        Assert.Equal(MarkdownContentKind.VectorScene, eager.Kind);
        Assert.False(eager.Attributes.ContainsKey(MarkerAttribute));
    }

    [Fact]
    public async Task SessionOrchestratesLegacyParserFallbackForNullEngine()
    {
        const string source = "legacy parser source";
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var parser = new StubLegacyParser();
        var registry = new MarkdownExtensionRegistry();

        MarkdownDocument document = Assert.IsType<MarkdownDocument>(
            await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                engine: null,
                document: null,
                source,
                registry,
                parser,
                CancellationToken.None));

        Assert.Equal(source, document.Source);
        Assert.Equal(source, parser.LastSource);
        Assert.Same(registry, parser.LastRegistry);
        Assert.Equal(1, parser.CallCount);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public async Task ProgressiveMathParseDefersBlockScene_ButPublicParseRemainsEagerAndCacheIsolated()
    {
        const string tex = @"\frac{1}{2}";
        string source = "$$\n" + tex + "\n$$";
        var range = new SourceSpan(0, source.Length);
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();

        MarkdownDocument progressive = await engine.ParseForProgressivePresentationAsync(source);
        Assert.Empty(progressive.Diagnostics);
        Assert.True(progressive.TryGetBlockExtensionContent(range, out MarkdownContentFragment? deferredFragment));
        MarkdownContent deferred = Assert.Single(deferredFragment!.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, deferred.Kind);
        Assert.Equal(source, deferred.Text);
        Assert.Equal("tex", deferred.Language);
        string marker = Assert.IsType<string>(deferred.Attributes[MarkerAttribute]);
        Assert.StartsWith(MarkdownSyntaxKinds.Block.Math + "|", marker);

        MarkdownDocument publicDocument = await engine.ParseAsync(source);
        Assert.NotSame(progressive, publicDocument);
        Assert.True(publicDocument.TryGetBlockExtensionContent(range, out MarkdownContentFragment? eagerFragment));
        MarkdownContent eager = Assert.Single(eagerFragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, eager.Kind);
        Assert.False(eager.Attributes.ContainsKey(MarkerAttribute));

        MarkdownDocument progressiveAgain = await engine.ParseForProgressivePresentationAsync(source);
        Assert.Same(progressive, progressiveAgain);
    }

    [Fact]
    public async Task MermaidProgressiveParseUsesSourceFallbackUntilMaterialized()
    {
        const string diagram = "flowchart TD\nA --> B";
        string source = $"```mermaid\n{diagram}\n```";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .Build();

        MarkdownDocument progressive = await engine.ParseForProgressivePresentationAsync(source);
        MarkdownContent deferred = Assert.Single(
            progressive.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items));
        Assert.Equal(MarkdownContentKind.CodeBlock, deferred.Kind);
        Assert.Equal("mermaid", deferred.Language);
        Assert.Equal(diagram, deferred.Text);
        string marker = Assert.IsType<string>(deferred.Attributes[MarkerAttribute]);
        Assert.StartsWith(MarkdownSyntaxKinds.Block.FencedCode + "|", marker);
        Assert.Empty(progressive.Diagnostics);

        MarkdownDocument eager = await engine.ParseAsync(source);
        Assert.Contains(
            eager.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items),
            static content => content.Kind == MarkdownContentKind.VectorScene);
    }

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
        Assert.True(plan.TrySchedule(secondMarker, secondPublication.TrySetResult, onMaterialized));
        Action first = await firstPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Action second = await secondPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Simulate the ordered UI dispatcher: the first scene's publication
        // triggers its non-invalidating materialization rebuild before the
        // already-queued sibling is allowed to commit.
        first();
        second();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(plan.TryGetResult(firstMarker, out _));
        Assert.True(plan.TryGetResult(secondMarker, out _));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public async Task CrossEngineProgressiveDocumentUsesItsProducerLifetime()
    {
        const string source = "plain paragraph";
        var resources = new List<OwnedDeferredSceneResource>();
        MarkdownEngineBuilder builder = new MarkdownEngineBuilder()
            .UseOwnedExtensionFactory(
                "MarkdownRenderer.GitHub.Tests.OwnedDeferredScene",
                () =>
                {
                    var resource = new OwnedDeferredSceneResource();
                    resources.Add(resource);
                    return new MarkdownOwnedExtension(new OwnedDeferredSceneExtension(resource), resource);
                });
        MarkdownEngine producerEngine = builder.Build();
        using MarkdownEngine consumerEngine = builder.Build();
        OwnedDeferredSceneResource producerResource = Assert.IsType<OwnedDeferredSceneResource>(resources[0]);
        OwnedDeferredSceneResource consumerResource = Assert.IsType<OwnedDeferredSceneResource>(resources[1]);

        MarkdownDocument produced = await producerEngine.ParseForProgressivePresentationAsync(source);
        string marker = GetSingleMarker(produced);
        producerEngine.Dispose();
        Assert.True(producerResource.IsDisposed);

        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal sessionInternal = session;
        var consumerOwner = new object();
        MarkdownDocument adopted = Assert.IsType<MarkdownDocument>(
            await sessionInternal.ParseAndPrepareDocumentAsync(
                consumerEngine,
                produced,
                source,
                new MarkdownExtensionRegistry(),
                consumerOwner,
                CancellationToken.None));
        Assert.Same(produced, adopted);

        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            adopted,
            sessionInternal,
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

        Assert.Equal(0, producerResource.DeferredCallbackCount);
        Assert.Equal(0, consumerResource.DeferredCallbackCount);
        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? fallback));
        Assert.Equal(MarkdownContentKind.CodeBlock, Assert.Single(fallback!.Items).Kind);
        Assert.Equal("MDP0001", Assert.Single(plan.Diagnostics).Code);
    }

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
        var queuedPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool published = false;
        int retryCount = 0;

        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                queuedPublication.TrySetResult(action);
                return true;
            },
            () => published = true,
            () => Interlocked.Increment(ref retryCount)));

        Action publication = await queuedPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        plan.AdvanceGeneration();
        Assert.False(plan.TrySchedule(marker, static _ => true, static () => { }));
        publication();

        Assert.False(published);
        Assert.False(plan.TryGetResult(marker, out _));
        Assert.Equal(1, retryCount);

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
        MarkdownDiagnostic diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal("MATH100", diagnostic.Code);
        Assert.Equal(diagnosticRange, diagnostic.SourceSpan);
    }

    private static string GetSingleMarker(MarkdownDocument document)
        => Assert.IsType<string>(Assert.Single(
            document.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items))
            .Attributes[MarkerAttribute]);

    private static string[] GetMarkers(MarkdownDocument document)
        => document.ExtensionBlockNodeContent.Values
            .SelectMany(static fragment => fragment.Items)
            .Where(static content => content.Attributes.ContainsKey(MarkerAttribute))
            .Select(content => Assert.IsType<string>(content.Attributes[MarkerAttribute]))
            .ToArray();

    private sealed class StubLegacyParser : IMarkdownPerformanceLegacyParser
    {
        internal string? LastSource { get; private set; }
        internal MarkdownExtensionRegistry? LastRegistry { get; private set; }
        internal int CallCount { get; private set; }

        public Task<ParsedMarkdown?> ParseLegacyMarkdownAsync(
            string source,
            MarkdownExtensionRegistry registry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSource = source;
            LastRegistry = registry;
            CallCount++;
            return Task.FromResult<ParsedMarkdown?>(
                new ParsedMarkdown(source, Markdig.Markdown.Parse(source)));
        }
    }

    private sealed class MarkerExtension(string markerKind, bool duplicateSpans) : IMarkdownExtension
    {
        private int _nextToken;

        public string Id => "MarkdownRenderer.GitHub.Tests.Marker";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            if (!context.ShouldDeferOffscreenScenes)
            {
                content.AddText("materialized", context.Node.SourceSpan);
                return ValueTask.CompletedTask;
            }

            int count = duplicateSpans ? 2 : 1;
            for (int i = 0; i < count; i++)
            {
                string marker = string.Concat(markerKind, "|", Interlocked.Increment(ref _nextToken)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = marker,
                    });
            }

            return ValueTask.CompletedTask;
        }
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
            if (!context.ShouldDeferOffscreenScenes)
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
            if (!context.ShouldDeferOffscreenScenes)
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

    private sealed class OwnedDeferredSceneResource : IDisposable
    {
        private int _isDisposed;
        private int _deferredCallbackCount;

        internal bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;
        internal int DeferredCallbackCount => Volatile.Read(ref _deferredCallbackCount);

        internal void OnDeferredCallback()
        {
            Interlocked.Increment(ref _deferredCallbackCount);
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(OwnedDeferredSceneResource));
        }

        public void Dispose() => Interlocked.Exchange(ref _isDisposed, 1);
    }

    private sealed class OwnedDeferredSceneExtension(OwnedDeferredSceneResource resource) : IMarkdownExtension
    {
        private const string MarkerKind = MarkdownSyntaxKinds.Block.Paragraph;

        public string Id => "MarkdownRenderer.GitHub.Tests.OwnedDeferredScene";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlockAsync(MarkerKind, RenderAsync);

        private ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            if (context.ShouldDeferOffscreenScenes)
            {
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = string.Concat(MarkerKind, "|owned"),
                    });
                return ValueTask.CompletedTask;
            }

            resource.OnDeferredCallback();
            content.AddText("materialized-by-origin", context.Node.SourceSpan);
            return ValueTask.CompletedTask;
        }
    }
}
