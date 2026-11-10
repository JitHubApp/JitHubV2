using System.Runtime.CompilerServices;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.CompletedScenes;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ProgressiveSceneDeferralFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the bindings of materialized and deferred scenes: they stay
/// scene-free, nested slots stay distinct, binding scopes restore after
/// rejected fragments, and deferred bindings keep only their marker and
/// ordinal.
/// </summary>
public sealed class ProgressiveSceneBindingTests
{
    [Fact]
    public async Task MaterializedBlockAndInlineMathKeepSceneFreeBindingsNotPlaceholderMarkers()
    {
        const string source = "$$\n\\frac{1}{2}\n$$\n\nbefore $x^2$ after";
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseMathematics().Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal internalSession = session;
        var owner = new object();
        MarkdownDocument document = Assert.IsType<MarkdownDocument>(await internalSession.ParseAndPrepareDocumentAsync(
            engine, null, source, new MarkdownExtensionRegistry(), owner, CancellationToken.None));
        MarkdownProgressiveScenePlan plan = GetRegisteredPlan(session, owner);
        string[] markers = plan.GetRegisteredMarkerSnapshot();
        Assert.Equal(2, markers.Length);
        plan.UpdateVisibleMarkers(markers);
        foreach (string marker in markers)
            await PublishBindingSceneAsync(plan, marker);

        using LayoutSnapshot snapshot = BuildLayout(document, FlowDirection.LeftToRight, internalSession, owner);
        VectorSceneBox block = Assert.Single(snapshot.Blocks.OfType<VectorSceneBox>());
        InlineVectorSceneRun inline = Assert.Single(EnumerateInlineContainers(snapshot.Blocks)
            .SelectMany(static container => container.Runs).OfType<InlineVectorSceneRun>());
        MarkdownDeferredSceneBinding blockBinding = Assert.IsType<MarkdownDeferredSceneBinding>(block.DeferredSceneBinding);
        MarkdownDeferredSceneBinding inlineBinding = Assert.IsType<MarkdownDeferredSceneBinding>(inline.DeferredSceneBinding);
        Assert.StartsWith(MarkdownSyntaxKinds.Block.Math + "|", blockBinding.Marker);
        Assert.StartsWith(MarkdownSyntaxKinds.Inline.Math + "|", inlineBinding.Marker);
        Assert.Equal(0, blockBinding.SceneOrdinal);
        Assert.Equal(0, inlineBinding.SceneOrdinal);
        Assert.True(internalSession.TryGetDeferredSceneResult(
            owner, blockBinding.Marker, out _, out object? blockProjectionIdentity));
        Assert.True(internalSession.TryGetDeferredSceneResult(
            owner, inlineBinding.Marker, out _, out object? inlineProjectionIdentity));
        Assert.IsType<MarkdownSceneProjectionSignature>(blockProjectionIdentity);
        Assert.IsType<MarkdownSceneProjectionSignature>(inlineProjectionIdentity);
        Assert.Same(blockProjectionIdentity, blockBinding.ProjectionIdentity);
        Assert.Same(inlineProjectionIdentity, inlineBinding.ProjectionIdentity);
        Assert.Null(inline.DeferredSceneMarker);
        Assert.False(Assert.Single(EnumerateInlineContainers(snapshot.Blocks)).HasDeferredSceneMarker);
        Assert.NotEqual(blockBinding.Marker, inlineBinding.Marker);

        session.Trim();
        Assert.Equal(blockBinding.Marker, block.DeferredSceneBinding!.Value.Marker);
        Assert.Equal(inlineBinding.Marker, inline.DeferredSceneBinding!.Value.Marker);
        Assert.Same(blockProjectionIdentity, block.DeferredSceneBinding!.Value.ProjectionIdentity);
        Assert.Same(inlineProjectionIdentity, inline.DeferredSceneBinding!.Value.ProjectionIdentity);
        Assert.Equal(MarkdownAccessibilityRole.Math, block.AccessibilityRole);
        Assert.Equal("x^2", inline.SemanticText);
        await internalSession.ReleaseDeferredSceneDocument(owner);
    }

    [Fact]
    public async Task NestedMaterializedSceneSlotsStayDistinctAndDoNotLeakIntoOtherContent()
    {
        const string source = "first\n\nsecond\n\nordinary";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(new NestedBindingSceneExtension(source.IndexOf("ordinary", StringComparison.Ordinal))).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal internalSession = session;
        var owner = new object();
        MarkdownDocument document = Assert.IsType<MarkdownDocument>(await internalSession.ParseAndPrepareDocumentAsync(
            engine, null, source, new MarkdownExtensionRegistry(), owner, CancellationToken.None));
        MarkdownProgressiveScenePlan plan = GetRegisteredPlan(session, owner);
        string[] markers = plan.GetRegisteredMarkerSnapshot();
        Assert.Equal(2, markers.Length);
        plan.UpdateVisibleMarkers(markers);
        foreach (string marker in markers)
            await PublishBindingSceneAsync(plan, marker);

        using LayoutSnapshot snapshot = BuildLayout(document, FlowDirection.LeftToRight, internalSession, owner);
        VectorSceneBox[] scenes = EnumerateBlockScenes(snapshot.Blocks).ToArray();
        Assert.Equal(5, scenes.Length);
        foreach (string marker in markers)
        {
            VectorSceneBox[] bound = scenes.Where(scene => scene.DeferredSceneBinding?.Marker == marker).ToArray();
            Assert.Equal(2, bound.Length);
            Assert.Equal(new[] { 0, 1 }, bound.Select(static scene => scene.DeferredSceneBinding!.Value.SceneOrdinal));
            Assert.All(bound, static scene => Assert.Null(scene.DeferredSceneBinding!.Value.ProjectionIdentity));
        }
        Assert.Null(scenes[^1].DeferredSceneBinding);
        Assert.All(scenes, static scene => Assert.Equal(MarkdownAccessibilityRole.Diagram, scene.AccessibilityRole));
        await internalSession.ReleaseDeferredSceneDocument(owner);
    }

    private static MarkdownProgressiveScenePlan GetRegisteredPlan(MarkdownPerformanceSession session, object owner)
    {
        // Read the already-published registration; do not invoke
        // control/dispatcher APIs for this fixture.
        Assert.True(session.ScenePlans.TryGetPlan(owner, out MarkdownProgressiveScenePlan? plan));
        return plan;
    }

    [Theory]
    [InlineData("nested|pending")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UnresolvedNestedInlinePlaceholderDoesNotConsumeOuterMaterializedSlot(string nestedMarker)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().Build();
        MarkdownDocument document = await engine.ParseAsync("ordinary text");
        LayoutBuilder builder = CreateLayoutBuilder(document, FlowDirection.LeftToRight);
        MarkdownLayoutContext context = builder.Context;
        using MarkdownLayoutConstructionScope ownership = context.Construction.Begin();
        var fragment = new MarkdownContentBuilder();
        AddScene(new Dictionary<string, string> { [MarkerAttribute] = nestedMarker });
        AddScene(attributes: null);
        AddScene(attributes: null);
        MarkdownContentFragment materializedFragment = fragment.Build();
        object projectionIdentity = MarkdownSceneProjectionSignature.Create(
            materializedFragment.Items[1], CancellationToken.None);
        object previousProjectionIdentity = MarkdownSceneProjectionSignature.Create(
            materializedFragment.Items[2], CancellationToken.None);
        MaterializedSceneScope sceneScope = builder.MaterializedScenes;
        sceneScope.ProjectionIdentity = previousProjectionIdentity;
        var runs = new List<InlineRun>();
        builder.DeclarativeInlines.AppendMaterializedSceneInline(
            "outer|ready", materializedFragment, projectionIdentity, runs);

        InlineVectorSceneRun[] scenes = runs.OfType<InlineVectorSceneRun>().ToArray();
        Assert.Equal(3, scenes.Length);
        Assert.Equal(nestedMarker, scenes[0].DeferredSceneMarker);
        Assert.Null(scenes[0].DeferredSceneBinding);
        for (int index = 1; index < scenes.Length; index++)
        {
            Assert.Null(scenes[index].DeferredSceneMarker);
            MarkdownDeferredSceneBinding binding = Assert.IsType<MarkdownDeferredSceneBinding>(
                scenes[index].DeferredSceneBinding);
            Assert.Equal("outer|ready", binding.Marker);
            Assert.Equal(index - 1, binding.SceneOrdinal);
            Assert.Same(projectionIdentity, binding.ProjectionIdentity);
        }
        Assert.Null(sceneScope.Marker);
        Assert.Equal(0, sceneScope.Ordinal);
        Assert.Same(previousProjectionIdentity, sceneScope.ProjectionIdentity);

        void AddScene(IReadOnlyDictionary<string, string>? attributes) =>
            fragment.AddVectorScene(new MarkdownVectorScene(20, 20, 10,
                    [MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20))]),
                new SourceSpan(0, 1), MarkdownStyleRole.Math, MarkdownAccessibilityRole.Math,
                "Scene", null, "scene", attributes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterializedBindingScopeRestoresAfterRejectedOrCanceledFragment(bool canceled)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().Build();
        MarkdownDocument document = await engine.ParseAsync("ordinary text");
        LayoutBuilder builder = CreateLayoutBuilder(document, FlowDirection.LeftToRight);
        MarkdownLayoutContext context = builder.Context;
        MaterializedSceneScope sceneScope = builder.MaterializedScenes;
        object previousProjectionIdentity = CreateProjectionIdentity();
        object suppliedProjectionIdentity = CreateProjectionIdentity();
        sceneScope.ProjectionIdentity = previousProjectionIdentity;
        var fragment = new MarkdownContentBuilder();
        using var cancellation = new CancellationTokenSource();
        if (canceled)
        {
            fragment.AddVectorScene(new MarkdownVectorScene(20, 20, 10,
                    [MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20))]),
                new SourceSpan(0, 1), MarkdownStyleRole.Diagram,
                MarkdownAccessibilityRole.Diagram, "Canceled scene", null, "scene");
            cancellation.Cancel();
            context.SetCancellationToken(cancellation.Token);
        }

        DeclarativeBlockBuilder declarativeBlocks = builder.DeclarativeBlocks;
        MarkdownContentFragment materializedFragment = fragment.Build();
        if (canceled)
        {
            Assert.Throws<OperationCanceledException>(() => declarativeBlocks.TryBuildMaterializedSceneBlock(
                "test|canceled", materializedFragment, suppliedProjectionIdentity));
        }
        else
        {
            Assert.Null(declarativeBlocks.TryBuildMaterializedSceneBlock(
                "test|empty", materializedFragment, suppliedProjectionIdentity));
        }
        Assert.Null(sceneScope.Marker);
        Assert.Equal(0, sceneScope.Ordinal);
        Assert.Same(previousProjectionIdentity, sceneScope.ProjectionIdentity);
        sceneScope.ProjectionIdentity = null;
        context.SetCancellationToken(CancellationToken.None);
        using LayoutSnapshot ordinary = builder.Build(document.ParsedDocument!, availableWidth: 640);
        Assert.Empty(EnumerateBlockScenes(ordinary.Blocks));
        Assert.Equal("ordinary text", ordinary.SemanticDocument.Text);
    }

    [Fact]
    public void DeferredBindingRetainsOnlyReadonlyMarkerAndOrdinal()
    {
        object identity = new();
        var binding = new MarkdownDeferredSceneBinding("scene|marker", 3, identity);
        Assert.Equal("scene|marker", binding.Marker);
        Assert.Equal(3, binding.SceneOrdinal);
        Assert.Same(identity, binding.ProjectionIdentity);
        // Storage is exactly the marker reference, the ordinal and the
        // identity reference; any further field would grow the struct.
        Assert.Equal(3 * IntPtr.Size, Unsafe.SizeOf<MarkdownDeferredSceneBinding>());

        Type identityType = typeof(MarkdownSceneProjectionSignature);
        Assert.True(identityType.IsValueType);
        Assert.True(identityType.IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute), inherit: false));
        // Four 64-bit digest words and nothing that can root an object graph.
        Assert.Equal(4 * sizeof(ulong), Unsafe.SizeOf<MarkdownSceneProjectionSignature>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<MarkdownSceneProjectionSignature>());
    }

    private static object CreateProjectionIdentity()
    {
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            new MarkdownVectorScene(20, 20, 10,
                [MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20))]),
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            "Scalar projection",
            null,
            "scalar projection text");
        return MarkdownSceneProjectionSignature.Create(
            Assert.Single(content.Build().Items), CancellationToken.None);
    }

    private static async Task PublishBindingSceneAsync(MarkdownProgressiveScenePlan plan, string marker)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(plan.TrySchedule(marker, static action => { action(); return true; }, () => published.TrySetResult()));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static IEnumerable<VectorSceneBox> EnumerateBlockScenes(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            if (block is VectorSceneBox vector)
                yield return vector;
            else if (block is StackBox stack)
                foreach (VectorSceneBox child in EnumerateBlockScenes(stack.Children))
                    yield return child;
        }
    }

    private sealed class NestedBindingSceneExtension(int ordinaryStart) : IMarkdownExtension
    {
        public string Id => "MarkdownRenderer.Tests.NestedBindings";

        public void Configure(MarkdownExtensionBuilder builder) =>
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder builder)
        {
            if (context.Node.SourceSpan.Start == ordinaryStart)
            {
                AddScene(builder, context.Node.SourceSpan);
            }
            else if (context.CanDeferOffscreenScenes)
            {
                builder.AddCodeBlock("pending", "test", context.Node.SourceSpan, MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = MarkdownSyntaxKinds.Block.Paragraph + "|" +
                            context.Node.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }
            else
            {
                builder.AddContainer(MarkdownContentKind.Container, MarkdownStyleRole.Body,
                    context.Node.SourceSpan, MarkdownAccessibilityRole.Group, children =>
                    {
                        AddScene(children, context.Node.SourceSpan);
                        children.AddContainer(MarkdownContentKind.Container, MarkdownStyleRole.Body,
                            context.Node.SourceSpan, MarkdownAccessibilityRole.Group, nested =>
                            {
                                nested.AddCodeBlock("separator", "test", context.Node.SourceSpan);
                                AddScene(nested, context.Node.SourceSpan);
                            });
                    });
            }
            return ValueTask.CompletedTask;
        }

        private static void AddScene(MarkdownContentBuilder builder, SourceSpan span) =>
            builder.AddVectorScene(new MarkdownVectorScene(20, 20, 10,
                    [MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 20, 20))]),
                span, MarkdownStyleRole.Diagram, MarkdownAccessibilityRole.Diagram, "Bound scene", null, "scene");
    }
}
