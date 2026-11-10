using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ProgressiveSceneDeferralFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks when a performance session defers offscreen scenes: only the
/// progressive preset enables it, eager paths and diagnostics stay
/// unchanged, and disposed sessions and cross-engine documents keep the
/// right parser and registration lifetimes.
/// </summary>
public sealed class ProgressiveSceneDeferralTests
{
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
        await ((IMarkdownPerformanceSessionInternal)session).ReleaseDeferredSceneDocument(owner);
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
    public async Task DisposedSession_RetainsExactPublishedRegistrationUntilOwnerReleasesIt()
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

        session.Dispose();

        Assert.True(sessionInternal.HasRegisteredDeferredSceneWork(owner));
        Assert.True(sessionInternal.IsRegisteredDeferredSceneMarker(owner, marker));
        Assert.False(sessionInternal.IsRegisteredDeferredSceneMarker(owner, "math|unregistered"));
        Assert.True(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: [marker],
            isRegisteredMarker: visiblePlaceholderKey => sessionInternal.IsRegisteredDeferredSceneMarker(owner, visiblePlaceholderKey)));

        // A published replacement (including a diagnostic source fallback) no
        // longer exposes the deferred key, even though the retired registration
        // remains available until the owner releases its old plan.
        Assert.False(MarkdownDeferredSceneReadiness.HasPendingVisiblePlaceholder(
            hasRegisteredWork: true,
            visibleBandVerified: true,
            visiblePlaceholderKeys: Array.Empty<string>(),
            isRegisteredMarker: visiblePlaceholderKey => sessionInternal.IsRegisteredDeferredSceneMarker(owner, visiblePlaceholderKey)));

        await sessionInternal.ReleaseDeferredSceneDocument(owner);
        Assert.False(sessionInternal.HasRegisteredDeferredSceneWork(owner));
        Assert.False(sessionInternal.IsRegisteredDeferredSceneMarker(owner, marker));
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
            if (context.CanDeferOffscreenScenes)
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
