using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ProgressiveSceneGraphRefillTests
{
    private const string MarkerAttribute = MarkdownContentAttributes.DeferredScene;

    [Fact]
    public async Task SingleBlockGraphRefillsFromRetainedCacheWithoutChangingLayoutOrProjections()
    {
        const string source = "stable graph source";
        var extension = new GraphRefillSceneExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal internalSession = session;
        object owner = new();
        var fixture = await PrepareBlockAsync(engine, internalSession, owner, source);

        try
        {
            using LayoutSnapshot snapshot = BuildLazySnapshot(fixture.Document, internalSession, owner);
            VectorSceneBox box = Assert.Single(EnumerateSceneBoxes(snapshot.Blocks));
            MarkdownDeferredSceneBinding binding = Assert.IsType<MarkdownDeferredSceneBinding>(
                box.DeferredSceneBinding);
            Assert.Equal(fixture.Marker, binding.Marker);
            Assert.Equal(0, binding.SceneOrdinal);
            Assert.True(box.IsNativeResourcesPrepared);
            Assert.True(session.GetSnapshot().SceneCacheEstimatedBytes > 0);
            MarkdownVectorScene cachedScene = box.Scene;

            long layoutRevision = snapshot.LayoutRevision;
            string semanticText = snapshot.SemanticDocument.Text;
            SemanticNodeProjection[] semanticNodes = CaptureSemanticNodes(snapshot.SemanticDocument.Root);
            (bool Mapped, SourceSpan Span, string Slice) sourceProjection = CaptureSourceProjection(snapshot, box);
            Rect[] selectionProjection = CaptureSelectionProjection(box);
            Assert.NotEmpty(selectionProjection);
            Rect semanticBounds = box.GetSemanticBounds(1);
            LazyLayoutBand sceneBand = BandFor(box);
            LazyLayoutBand parkedBand = ParkedBandFor(box);

            LazyLayoutCommit leave = snapshot.EnsureMeasuredBand(parkedBand, CancellationToken.None);

            Assert.True(leave.NativeSceneResourcesChanged);
            Assert.True(box.IsGraphDetached);
            Assert.False(box.IsNativeResourcesPrepared);
            Assert.Equal(layoutRevision, snapshot.LayoutRevision);

            LazyLayoutCommit reentry = snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None);
            Assert.True(reentry.SceneGraphRebindRequired);
            Assert.True(box.IsGraphDetached);
            Assert.True(await internalSession.PrepareDeferredSceneBandAsync(
                owner, snapshot, sceneBand, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None).SceneGraphRebindRequired);

            Assert.False(box.IsGraphDetached);
            Assert.True(box.IsNativeResourcesPrepared);
            Assert.Same(cachedScene, box.Scene);
            Assert.Equal(1, extension.MaterializedRenderCount);
            Assert.Equal(layoutRevision, snapshot.LayoutRevision);
            Assert.Equal(semanticText, snapshot.SemanticDocument.Text);
            Assert.Equal(semanticNodes, CaptureSemanticNodes(snapshot.SemanticDocument.Root));
            Assert.Equal(sourceProjection, CaptureSourceProjection(snapshot, box));
            Assert.Equal(selectionProjection, CaptureSelectionProjection(box));
            Assert.Equal(semanticBounds, box.GetSemanticBounds(1));
            Assert.True(snapshot.IsBandMeasured(sceneBand));
        }
        finally
        {
            await internalSession.ReleaseDeferredSceneDocument(owner);
        }
    }

    [Fact]
    public async Task TrimmedDetachedGraphCanBeCollectedAndProviderRendersEquivalentReplacement()
    {
        const string source = "trimmed graph source";
        var extension = new GraphRefillSceneExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal internalSession = session;
        object owner = new();
        var fixture = await PrepareBlockAsync(engine, internalSession, owner, source);

        try
        {
            using LayoutSnapshot snapshot = BuildLazySnapshot(fixture.Document, internalSession, owner);
            VectorSceneBox box = Assert.Single(EnumerateSceneBoxes(snapshot.Blocks));
            WeakReference<MarkdownVectorScene> originalScene = Assert.IsType<WeakReference<MarkdownVectorScene>>(
                extension.LastMaterializedScene);
            SemanticNodeProjection[] semanticNodes = CaptureSemanticNodes(snapshot.SemanticDocument.Root);
            LazyLayoutBand sceneBand = BandFor(box);

            snapshot.EnsureMeasuredBand(ParkedBandFor(box), CancellationToken.None);
            Assert.True(box.IsGraphDetached);
            session.Trim();
            ForceCollection();
            Assert.False(originalScene.TryGetTarget(out _));
            Assert.Equal(0, session.GetSnapshot().SceneCacheEstimatedBytes);

            LazyLayoutCommit reentry = snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None);
            Assert.True(reentry.SceneGraphRebindRequired);
            await internalSession.PrepareDeferredSceneBandAsync(
                owner, snapshot, sceneBand, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None).SceneGraphRebindRequired);

            Assert.False(box.IsGraphDetached);
            Assert.True(box.IsNativeResourcesPrepared);
            Assert.Equal(2, extension.MaterializedRenderCount);
            Assert.NotNull(extension.LastMaterializedScene);
            Assert.Equal(semanticNodes, CaptureSemanticNodes(snapshot.SemanticDocument.Root));
            Assert.True(snapshot.IsBandMeasured(sceneBand));
        }
        finally
        {
            await internalSession.ReleaseDeferredSceneDocument(owner);
        }
    }

    [Fact]
    public async Task ChangedProviderProjectionPublishesMdp0002AndKeepsBandUnready()
    {
        const string source = "nondeterministic graph source";
        var extension = new GraphRefillSceneExtension(changeProjectionOnSecondRender: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal internalSession = session;
        object owner = new();
        var fixture = await PrepareBlockAsync(engine, internalSession, owner, source);

        try
        {
            using LayoutSnapshot snapshot = BuildLazySnapshot(fixture.Document, internalSession, owner);
            VectorSceneBox box = Assert.Single(EnumerateSceneBoxes(snapshot.Blocks));
            LazyLayoutBand sceneBand = BandFor(box);
            long layoutRevision = snapshot.LayoutRevision;

            snapshot.EnsureMeasuredBand(ParkedBandFor(box), CancellationToken.None);
            Assert.True(box.IsGraphDetached);
            session.Trim();
            ForceCollection();
            Assert.False(Assert.IsType<WeakReference<MarkdownVectorScene>>(extension.LastMaterializedScene)
                .TryGetTarget(out _));

            LazyLayoutCommit reentry = snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None);
            Assert.True(reentry.SceneGraphRebindRequired);
            await internalSession.PrepareDeferredSceneBandAsync(
                owner, snapshot, sceneBand, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None).SceneGraphRebindRequired);

            MarkdownDiagnostic diagnostic = Assert.Single(fixture.Plan.Diagnostics);
            Assert.Equal("MDP0002", diagnostic.Code);
            Assert.Equal(box.SourceRange, diagnostic.SourceSpan);
            Assert.True(box.GraphRebindFailed);
            Assert.True(box.IsGraphDetached);
            IMarkdownVectorSceneResidencyPolicy policy = Assert.IsAssignableFrom<IMarkdownVectorSceneResidencyPolicy>(
                snapshot.VectorSceneResidencyPolicy);
            Assert.Same(box, Assert.Single(policy.GraphStatusChanges));
            Assert.True(snapshot.TryBeginPaint());
            try
            {
                Assert.True(snapshot.HasFailedScenesInBandUnderPaint(sceneBand));
            }
            finally
            {
                snapshot.EndPaint();
            }
            policy.ClearGraphStatusChanges();
            Assert.Empty(policy.GraphStatusChanges);
            Assert.False(box.IsNativeResourcesPrepared);
            Assert.True(snapshot.IsBandWorkSettled(sceneBand));
            Assert.False(snapshot.IsBandMeasured(sceneBand));
            Assert.Equal(layoutRevision, snapshot.LayoutRevision);
            Assert.Equal(2, extension.MaterializedRenderCount);

            // A typed terminal projection failure settles scheduling without
            // passing the native-readiness gate. Repeated warm checks and band
            // commits must not restart the provider for the same failed box.
            Assert.True(snapshot.IsBandWorkSettled(sceneBand));
            Assert.False(snapshot.IsBandMeasured(sceneBand));
            Assert.False(snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None).SceneGraphRebindRequired);
            Assert.True(snapshot.IsBandWorkSettled(sceneBand));
            Assert.False(snapshot.IsBandMeasured(sceneBand));
            Assert.False(snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None).SceneGraphRebindRequired);
            Assert.True(snapshot.IsBandWorkSettled(sceneBand));
            Assert.False(snapshot.IsBandMeasured(sceneBand));
            Assert.Equal(2, extension.MaterializedRenderCount);
        }
        finally
        {
            await internalSession.ReleaseDeferredSceneDocument(owner);
        }
    }

    [Fact]
    public async Task MultiSceneBlockAndInlineMathKeepTheirCommandGraphsWhenLeavingBand()
    {
        const string blockSource = "two scene leaves";
        var extension = new GraphRefillSceneExtension(sceneCount: 2);
        using MarkdownEngine blockEngine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var blockSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal blockInternal = blockSession;
        object blockOwner = new();
        var blockFixture = await PrepareBlockAsync(blockEngine, blockInternal, blockOwner, blockSource);

        try
        {
            using LayoutSnapshot snapshot = BuildLazySnapshot(blockFixture.Document, blockInternal, blockOwner);
            VectorSceneBox[] boxes = EnumerateSceneBoxes(snapshot.Blocks).ToArray();
            Assert.Equal(2, boxes.Length);
            Assert.Equal(new[] { 0, 1 }, boxes.Select(static box => box.DeferredSceneBinding!.Value.SceneOrdinal));
            MarkdownVectorScene[] scenes = boxes.Select(static box => box.Scene).ToArray();
            double bottom = boxes.Max(static box => box.Bounds.Bottom);

            snapshot.EnsureMeasuredBand(new LazyLayoutBand(bottom + 256, bottom + 512), CancellationToken.None);

            Assert.All(boxes, static box =>
            {
                Assert.False(box.IsGraphDetached);
                Assert.False(box.IsNativeResourcesPrepared);
            });
            Assert.Equal(1, extension.MaterializedRenderCount);
            blockSession.Trim();
            for (int index = 0; index < boxes.Length; index++)
                Assert.Same(scenes[index], boxes[index].Scene);
        }
        finally
        {
            await blockInternal.ReleaseDeferredSceneDocument(blockOwner);
        }

        const string inlineSource = "before $x^2$ after";
        using MarkdownEngine inlineEngine = new MarkdownEngineBuilder().UseMathematics().Build();
        using var inlineSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal inlineInternal = inlineSession;
        object inlineOwner = new();
        MarkdownDocument inlineDocument = Assert.IsType<MarkdownDocument>(
            await inlineInternal.ParseAndPrepareDocumentAsync(
                inlineEngine, null, inlineSource, new MarkdownExtensionRegistry(), inlineOwner, CancellationToken.None));
        MarkdownProgressiveScenePlan inlinePlan = GetRegisteredPlan(inlineSession, inlineOwner);
        string inlineMarker = GetOnlyInlineMarker(inlineDocument);
        inlinePlan.UpdateVisibleMarkers([inlineMarker]);
        await PublishAsync(inlinePlan, inlineMarker);

        try
        {
            using LayoutSnapshot snapshot = BuildLazySnapshot(inlineDocument, inlineInternal, inlineOwner);
            InlineContainerBox container = Assert.Single(EnumerateInlineContainers(snapshot.Blocks));
            InlineVectorSceneRun run = Assert.Single(container.Runs.OfType<InlineVectorSceneRun>());
            MarkdownVectorScene scene = run.Scene;
            LazyLayoutBand inlineBand = new(container.Bounds.Top, container.Bounds.Bottom);
            Assert.Equal(inlineMarker, run.DeferredSceneBinding?.Marker);
            Assert.True(run.IsNativeResourcesPrepared);

            snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(container.Bounds.Bottom + 256, container.Bounds.Bottom + 512),
                CancellationToken.None);

            Assert.Same(scene, run.Scene);
            Assert.False(run.IsNativeResourcesPrepared);
            Assert.True(snapshot.LayoutRevision > 0);
            Assert.False(snapshot.IsBandMeasured(inlineBand));
        }
        finally
        {
            await inlineInternal.ReleaseDeferredSceneDocument(inlineOwner);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefillIgnoresStaleBandOrGenerationAfterProviderThatIgnoresCancellation(bool cancelGeneration)
    {
        const string source = "gated graph source";
        var extension = new GraphRefillSceneExtension(holdSecondRender: true);
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseExtension(extension).Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        IMarkdownPerformanceSessionInternal internalSession = session;
        object owner = new();
        var fixture = await PrepareBlockAsync(engine, internalSession, owner, source);

        try
        {
            using LayoutSnapshot snapshot = BuildLazySnapshot(fixture.Document, internalSession, owner);
            VectorSceneBox box = Assert.Single(EnumerateSceneBoxes(snapshot.Blocks));
            WeakReference<MarkdownVectorScene> originalScene = Assert.IsType<WeakReference<MarkdownVectorScene>>(
                extension.LastMaterializedScene);
            LazyLayoutBand sceneBand = BandFor(box);
            LazyLayoutBand parkedBand = ParkedBandFor(box);

            snapshot.EnsureMeasuredBand(parkedBand, CancellationToken.None);
            session.Trim();
            ForceCollection();
            Assert.False(originalScene.TryGetTarget(out _));

            Assert.True(snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None).SceneGraphRebindRequired);
            Task<bool> refill = internalSession.PrepareDeferredSceneBandAsync(
                owner, snapshot, sceneBand, CancellationToken.None);
            await extension.SecondRenderStarted.WaitAsync(TimeSpan.FromSeconds(10));

            Task? generationDrain = null;
            if (cancelGeneration)
            {
                generationDrain = internalSession.AdvanceDeferredSceneGeneration(owner);
                Assert.False(generationDrain.IsCompleted);
            }
            else
            {
                snapshot.EnsureMeasuredBand(parkedBand, CancellationToken.None);
            }

            extension.ReleaseSecondRender();
            if (cancelGeneration)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await refill.WaitAsync(TimeSpan.FromSeconds(10)));
                await generationDrain!.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                Assert.True(await refill.WaitAsync(TimeSpan.FromSeconds(10)));
            }

            Assert.True(box.IsGraphDetached);
            Assert.Empty(fixture.Plan.Diagnostics);

            LazyLayoutCommit retry = snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None);
            Assert.True(retry.SceneGraphRebindRequired);
            await internalSession.PrepareDeferredSceneBandAsync(
                owner, snapshot, sceneBand, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(snapshot.EnsureMeasuredBand(sceneBand, CancellationToken.None).SceneGraphRebindRequired);
            Assert.False(box.IsGraphDetached);
            Assert.True(box.IsNativeResourcesPrepared);
            Assert.Equal(3, extension.MaterializedRenderCount);
            Assert.Empty(fixture.Plan.Diagnostics);
        }
        finally
        {
            extension.ReleaseSecondRender();
            await internalSession.ReleaseDeferredSceneDocument(owner);
        }
    }

    private static async Task<(MarkdownDocument Document, MarkdownProgressiveScenePlan Plan, string Marker)> PrepareBlockAsync(
        MarkdownEngine engine,
        IMarkdownPerformanceSessionInternal session,
        object owner,
        string source)
    {
        MarkdownDocument document = Assert.IsType<MarkdownDocument>(await session.ParseAndPrepareDocumentAsync(
            engine, null, source, new MarkdownExtensionRegistry(), owner, CancellationToken.None));
        string marker = GetOnlyBlockMarker(document);
        MarkdownProgressiveScenePlan plan = GetRegisteredPlan((MarkdownPerformanceSession)session, owner);
        plan.UpdateVisibleMarkers([marker]);
        await PublishAsync(plan, marker);
        return (document, plan, marker);
    }

    private static string GetOnlyBlockMarker(MarkdownDocument document) => Assert.Single(
        document.ExtensionBlockNodeContent.Values
            .SelectMany(static fragment => fragment.Items),
        static content => content.Attributes.ContainsKey(MarkerAttribute))
        .Attributes[MarkerAttribute];

    private static string GetOnlyInlineMarker(MarkdownDocument document) => Assert.Single(
        document.ExtensionInlineNodeContent.Values
            .SelectMany(static fragment => fragment.Items),
        static content => content.Attributes.ContainsKey(MarkerAttribute))
        .Attributes[MarkerAttribute];

    private static MarkdownProgressiveScenePlan GetRegisteredPlan(MarkdownPerformanceSession session, object owner)
    {
        FieldInfo field = typeof(MarkdownPerformanceSession).GetField(
            "_scenePlans", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<Dictionary<object, MarkdownProgressiveScenePlan>>(field.GetValue(session))[owner];
    }

    private static async Task PublishAsync(MarkdownProgressiveScenePlan plan, string marker)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(plan.TrySchedule(
            marker,
            static action => { action(); return true; },
            () => published.TrySetResult()));
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static LayoutSnapshot BuildLazySnapshot(
        MarkdownDocument document,
        IMarkdownPerformanceSessionInternal session,
        object owner)
    {
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Math,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.List,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
            MarkdownElementKeys.Diagram,
        ];
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            keys.ToDictionary(static key => key, _ => style, StringComparer.Ordinal),
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(document.Source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight)
        {
            PerformanceSession = session,
            PerformanceDocumentOwner = owner,
        };
        return new LayoutBuilder(context, semanticDocument: document).BuildLazy(
            document.ParsedDocument!,
            availableWidth: 640,
            viewportTop: 0,
            viewportHeight: 256,
            overscan: 0,
            CancellationToken.None);
    }

    private static IEnumerable<VectorSceneBox> EnumerateSceneBoxes(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            switch (block)
            {
                case VectorSceneBox scene:
                    yield return scene;
                    break;
                case StackBox stack:
                    foreach (VectorSceneBox child in EnumerateSceneBoxes(stack.Children))
                        yield return child;
                    break;
                case ListItemBox listItem:
                    foreach (VectorSceneBox child in EnumerateSceneBoxes([listItem.Marker, listItem.Content]))
                        yield return child;
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                    foreach (VectorSceneBox child in EnumerateSceneBoxes([cell]))
                        yield return child;
                    break;
            }
        }
    }

    private static IEnumerable<InlineContainerBox> EnumerateInlineContainers(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            switch (block)
            {
                case InlineContainerBox inline:
                    yield return inline;
                    break;
                case StackBox stack:
                    foreach (InlineContainerBox child in EnumerateInlineContainers(stack.Children))
                        yield return child;
                    break;
                case ListItemBox listItem:
                    foreach (InlineContainerBox child in EnumerateInlineContainers([listItem.Marker, listItem.Content]))
                        yield return child;
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        yield return cell;
                    break;
            }
        }
    }

    private static LazyLayoutBand BandFor(VectorSceneBox box) =>
        new(box.Bounds.Top, box.Bounds.Bottom);

    private static LazyLayoutBand ParkedBandFor(VectorSceneBox box) =>
        new(box.Bounds.Bottom + 256, box.Bounds.Bottom + 512);

    private static (bool Mapped, SourceSpan Span, string Slice) CaptureSourceProjection(
        LayoutSnapshot snapshot,
        VectorSceneBox box)
    {
        var range = new DocumentRange(
            new DocumentPosition(box.BlockIndex, 0, 0),
            new DocumentPosition(box.BlockIndex, 0, 1));
        bool mapped = snapshot.SourceMap.TryMapRange(range, out SourceSpan span);
        return (mapped, span, snapshot.SourceMap.Slice(range));
    }

    private static Rect[] CaptureSelectionProjection(VectorSceneBox box) => box.GetSelectionRects(new DocumentRange(
        new DocumentPosition(box.BlockIndex, 0, 0),
        new DocumentPosition(box.BlockIndex, 0, 1))).ToArray();

    private static SemanticNodeProjection[] CaptureSemanticNodes(MarkdownSemanticNode root)
    {
        var nodes = new List<SemanticNodeProjection>();
        AddSemanticNode(root, nodes);
        return nodes.ToArray();
    }

    private static void AddSemanticNode(MarkdownSemanticNode node, List<SemanticNodeProjection> nodes)
    {
        nodes.Add(new SemanticNodeProjection(
            node.Role,
            node.TextStart,
            node.TextEnd,
            node.AccessibilityRole,
            node.AccessibilityName,
            node.AccessibilityDescription,
            node.HelpText,
            node.AutomationId,
            node.VectorSemanticIndex,
            node.VectorSemanticRole,
            node.VectorSemanticFlags,
            node.Bounds,
            node.Children.Count));
        foreach (MarkdownSemanticNode child in node.Children)
            AddSemanticNode(child, nodes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private readonly record struct SemanticNodeProjection(
        MarkdownSemanticRole Role,
        int TextStart,
        int TextEnd,
        MarkdownAccessibilityRole AccessibilityRole,
        string? AccessibilityName,
        string? AccessibilityDescription,
        string? HelpText,
        string? AutomationId,
        int VectorSemanticIndex,
        MarkdownVectorSemanticRole? VectorSemanticRole,
        MarkdownVectorSemanticFlags VectorSemanticFlags,
        Rect Bounds,
        int ChildCount);

    private sealed class GraphRefillSceneExtension(
        int sceneCount = 1,
        bool changeProjectionOnSecondRender = false,
        bool holdSecondRender = false) : IMarkdownExtension
    {
        private int _materializedRenderCount;
        private WeakReference<MarkdownVectorScene>? _lastMaterializedScene;
        private readonly TaskCompletionSource _secondRenderStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondRender =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int MaterializedRenderCount => Volatile.Read(ref _materializedRenderCount);
        internal WeakReference<MarkdownVectorScene>? LastMaterializedScene => Volatile.Read(ref _lastMaterializedScene);
        internal Task SecondRenderStarted => _secondRenderStarted.Task;

        public string Id => "MarkdownRenderer.GitHub.Tests.GraphRefill";

        public void Configure(MarkdownExtensionBuilder builder) =>
            builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private async ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            if (context.CanDeferOffscreenScenes)
            {
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "scene",
                    context.Node.SourceSpan,
                    MarkdownStyleRole.CodeBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [MarkerAttribute] = MarkdownSyntaxKinds.Block.Paragraph + "|graph-refill",
                    });
                return;
            }

            int render = Interlocked.Increment(ref _materializedRenderCount);
            if (holdSecondRender && render == 2)
            {
                _secondRenderStarted.TrySetResult();
                // Intentionally ignore the callback token. The plan must drain
                // this producer and discard its late graph after generation change.
                await _releaseSecondRender.Task.ConfigureAwait(false);
            }

            SourceSpan span = context.Node.SourceSpan;
            float width = changeProjectionOnSecondRender && render >= 2 ? 21 : 20;
            for (int index = 0; index < sceneCount; index++)
            {
                MarkdownVectorScene scene = CreateScene(
                    span,
                    width + index,
                    sceneCount == 1 ? "Stable node" : $"Stable node {index}");
                Volatile.Write(ref _lastMaterializedScene, new WeakReference<MarkdownVectorScene>(scene));
                content.AddVectorScene(
                    scene,
                    span,
                    MarkdownStyleRole.Diagram,
                    MarkdownAccessibilityRole.Diagram,
                    accessibilityName: "Stable diagram",
                    accessibilityDescription: "Stable diagram description",
                    semanticText: "Stable diagram semantic text");
            }
        }

        internal void ReleaseSecondRender() => _releaseSecondRender.TrySetResult();

        private static MarkdownVectorScene CreateScene(SourceSpan span, float width, string semanticName)
        {
            var bounds = new MarkdownVectorRectangle(1, 2, System.Math.Min(8, width - 2), 6);
            MarkdownVectorPaintStyle paint = MarkdownVectorPaintStyle.CreateSemantic(
                MarkdownVectorPaintRole.Foreground,
                MarkdownVectorPaintRole.Link,
                fillArgb: 0xFF336699,
                strokeArgb: 0xFF112233,
                strokeWidth: 1,
                opacity: 0.9f);
            MarkdownVectorSemanticItem[] semantics =
            [
                new MarkdownVectorSemanticItem(
                    1,
                    MarkdownVectorSemanticRole.Diagram,
                    "Stable root",
                    "Stable root description",
                    -1,
                    span,
                    new MarkdownVectorRectangle(0, 0, width, 16)),
                new MarkdownVectorSemanticItem(
                    2,
                    MarkdownVectorSemanticRole.Node,
                    semanticName,
                    "Stable node description",
                    0,
                    span,
                    bounds,
                    MarkdownVectorSemanticFlags.Focusable |
                    MarkdownVectorSemanticFlags.Selectable |
                    MarkdownVectorSemanticFlags.Linked),
            ];
            MarkdownVectorLinkAction[] links =
            [
                new MarkdownVectorLinkAction(1, "https://example.test/stable", "activate", external: true),
            ];
            MarkdownVectorCommand[] commands =
            [
                MarkdownVectorCommand.DrawRectangle(bounds, paint, semanticIndex: 1),
            ];
            return new MarkdownVectorScene(
                width,
                16,
                12,
                commands,
                referenceFontSize: 12,
                viewport: new MarkdownVectorRectangle(0, 0, width, 16),
                semantics,
                links);
        }
    }
}
