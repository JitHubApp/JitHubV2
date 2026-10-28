using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ProgressiveSceneDeferralTests
{
    private const string MarkerAttribute = MarkdownContentAttributes.DeferredScene;

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
    public async Task ProgressiveMathParseDefersInlineSceneUntilItsMarkerIsScheduled()
    {
        const string formula = @"$\frac{1}{2}$";
        const string source = "before " + formula + " after";
        var range = new SourceSpan(source.IndexOf(formula, StringComparison.Ordinal), formula.Length);
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var owner = new object();

        MarkdownDocument progressive = await engine.ParseForProgressivePresentationAsync(source);
        Assert.True(progressive.TryGetInlineExtensionContent(range, out MarkdownContentFragment? fragment));
        MarkdownContent placeholder = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, placeholder.Kind);
        Assert.NotNull(placeholder.VectorScene);
        Assert.Equal(@"\frac{1}{2}", placeholder.SemanticText);
        Assert.Equal("Mathematical expression", placeholder.AccessibilityName);
        Assert.Equal(formula, placeholder.AccessibilityDescription);
        Assert.Equal(MarkdownAccessibilityRole.Math, placeholder.AccessibilityRole);
        Assert.Contains(placeholder.VectorScene!.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.TextStyle?.FontFamily == "Consolas");
        Assert.Equal(range, placeholder.SourceSpan);
        string marker = Assert.IsType<string>(placeholder.Attributes[MarkerAttribute]);
        Assert.StartsWith(MarkdownSyntaxKinds.Inline.Math + "|", marker);

        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            progressive,
            (IMarkdownPerformanceSessionInternal)session,
            owner,
            CancellationToken.None))!;
        Assert.True(plan.IsRegisteredMarker(marker));
        Assert.False(plan.TryGetResult(marker, out _));
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

        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? materialized));
        MarkdownContent scene = Assert.Single(materialized!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, scene.Kind);
        Assert.Equal(MarkdownAccessibilityRole.Math, scene.AccessibilityRole);
        Assert.Equal(range, scene.SourceSpan);
        Assert.Empty(plan.Diagnostics);

        MarkdownDocument eager = await engine.ParseAsync(source);
        Assert.True(eager.TryGetInlineExtensionContent(range, out MarkdownContentFragment? eagerFragment));
        Assert.Equal(
            MarkdownContentKind.VectorScene,
            Assert.Single(eagerFragment!.Items).Kind);
        Assert.NotSame(progressive, eager);
    }

    [Fact]
    public async Task DeferredAndEagerInlineMathKeepAtomicBufferSourceSemanticsAcrossListsTablesRtlAndLinks()
    {
        const string source = "- list $x+y$\n\n| expression |\n| --- |\n| [$\\frac{1}{2}$](https://example.test/math \"fraction\") |\n\nمرحبا $x$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        MarkdownDocument progressive = await engine.ParseForProgressivePresentationAsync(source);
        MarkdownDocument eager = await engine.ParseAsync(source);

        using LayoutSnapshot pendingSnapshot = BuildLayout(progressive, FlowDirection.RightToLeft);
        using LayoutSnapshot materializedSnapshot = BuildLayout(eager, FlowDirection.RightToLeft);
        InlineVectorSceneRun[] pendingMath = EnumerateInlineContainers(pendingSnapshot.Blocks)
            .SelectMany(static box => box.Runs)
            .OfType<InlineVectorSceneRun>()
            .Where(static run => run.ElementKey == MarkdownElementKeys.Math)
            .ToArray();
        InlineVectorSceneRun[] materializedMath = EnumerateInlineContainers(materializedSnapshot.Blocks)
            .SelectMany(static box => box.Runs)
            .OfType<InlineVectorSceneRun>()
            .Where(static run => run.ElementKey == MarkdownElementKeys.Math)
            .ToArray();

        Assert.Equal(3, pendingMath.Length);
        Assert.Equal(pendingMath.Length, materializedMath.Length);
        Assert.All(pendingMath, static run =>
        {
            Assert.Equal("\uFFFC", run.Text);
            Assert.Equal(1, run.RenderedLength);
            Assert.Equal(run.Text, run.LayoutText);
            Assert.False(string.IsNullOrWhiteSpace(run.AccessibleText));
            Assert.NotNull(run.DeferredSceneMarker);
        });

        foreach (InlineVectorSceneRun pending in pendingMath)
        {
            InlineVectorSceneRun realized = Assert.Single(
                materializedMath,
                run => run.SourceSpan == pending.SourceSpan);
            Assert.Equal(1, realized.RenderedLength);
            Assert.Equal("\uFFFC", realized.Text);
            Assert.Equal(pending.AccessibleText, realized.AccessibleText);
            Assert.Null(realized.DeferredSceneMarker);

            InlineContainerBox pendingBox = Assert.Single(
                EnumerateInlineContainers(pendingSnapshot.Blocks),
                box => box.Runs.Contains(pending));
            int start = pendingBox.GetBufferCharOffset(new DocumentPosition(
                pendingBox.BlockIndex,
                pending.InlineIndex,
                0));
            int end = pendingBox.GetBufferCharOffset(new DocumentPosition(
                pendingBox.BlockIndex,
                pending.InlineIndex,
                pending.RenderedLength));
            Assert.Equal(1, end - start);
            var selectedFormula = new DocumentRange(
                new DocumentPosition(pendingBox.BlockIndex, pending.InlineIndex, 0),
                new DocumentPosition(pendingBox.BlockIndex, pending.InlineIndex, 1));
            Assert.True(pendingSnapshot.SourceMap.TryMapRange(selectedFormula, out SourceSpan mappedSpan));
            Assert.Equal(pending.SourceSpan, mappedSpan);
            Assert.Equal(source.Substring(pending.SourceSpan.Start, pending.SourceSpan.Length),
                pendingSnapshot.SourceMap.Slice(selectedFormula));
        }

        Assert.Equal(pendingSnapshot.SemanticDocument.Text, materializedSnapshot.SemanticDocument.Text);
        Assert.Equal(3, EnumerateSemanticNodes(pendingSnapshot.SemanticDocument.Root)
            .Count(static node => node.Role == MarkdownSemanticRole.Math));
        MarkdownSemanticNode[] semanticMath = EnumerateSemanticNodes(pendingSnapshot.SemanticDocument.Root)
            .Where(static node => node.Role == MarkdownSemanticRole.Math)
            .ToArray();
        Assert.All(semanticMath, static node =>
            Assert.Equal(MarkdownAccessibilityRole.Math, node.AccessibilityRole));

        InlineVectorSceneRun linkedMath = Assert.Single(pendingMath, static run => run.IsLinked);
        Assert.Equal("https://example.test/math", linkedMath.LinkUrl);
        Assert.Equal("fraction", linkedMath.LinkTitle);
        MarkdownSemanticNode linkedSemantic = Assert.Single(
            semanticMath,
            node => ReferenceEquals(node.InlineRun, linkedMath));
        Assert.Equal("fraction", linkedSemantic.HelpText);
    }

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
        // Test-only read of the already-published registration; do not add a
        // shipping test hook or invoke control/dispatcher APIs for this fixture.
        var field = typeof(MarkdownPerformanceSession).GetField("_scenePlans",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<Dictionary<object, MarkdownProgressiveScenePlan>>(field!.GetValue(session))[owner];
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
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        MarkdownLayoutContext context = Assert.IsType<MarkdownLayoutContext>(
            typeof(LayoutBuilder).GetField("_context", flags)!.GetValue(builder));
        using MarkdownLayoutConstructionScope ownership = context.BeginConstructionScope();
        var fragment = new MarkdownContentBuilder();
        AddScene(new Dictionary<string, string> { [MarkerAttribute] = nestedMarker });
        AddScene(attributes: null);
        AddScene(attributes: null);
        MarkdownContentFragment materializedFragment = fragment.Build();
        object projectionIdentity = MarkdownSceneProjectionSignature.Create(
            materializedFragment.Items[1], CancellationToken.None);
        object previousProjectionIdentity = MarkdownSceneProjectionSignature.Create(
            materializedFragment.Items[2], CancellationToken.None);
        FieldInfo projectionIdentityField = typeof(LayoutBuilder).GetField(
            "_materializedSceneProjectionIdentity", flags)!;
        projectionIdentityField.SetValue(builder, previousProjectionIdentity);
        var runs = new List<InlineRun>();
        typeof(LayoutBuilder).GetMethod("AppendMaterializedSceneInline", flags)!.Invoke(
            builder, new object[] { "outer|ready", materializedFragment, projectionIdentity, runs });

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
        Assert.Null(typeof(LayoutBuilder).GetField("_materializedSceneMarker", flags)!.GetValue(builder));
        Assert.Equal(0, typeof(LayoutBuilder).GetField("_materializedSceneOrdinal", flags)!.GetValue(builder));
        Assert.Same(previousProjectionIdentity, projectionIdentityField.GetValue(builder));

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
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        MarkdownLayoutContext context = Assert.IsType<MarkdownLayoutContext>(
            typeof(LayoutBuilder).GetField("_context", flags)!.GetValue(builder));
        FieldInfo projectionIdentityField = typeof(LayoutBuilder).GetField(
            "_materializedSceneProjectionIdentity", flags)!;
        object previousProjectionIdentity = CreateProjectionIdentity();
        object suppliedProjectionIdentity = CreateProjectionIdentity();
        projectionIdentityField.SetValue(builder, previousProjectionIdentity);
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

        var materialize = typeof(LayoutBuilder).GetMethod("TryBuildMaterializedSceneBlock", flags)!;
        MarkdownContentFragment materializedFragment = fragment.Build();
        if (canceled)
        {
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                materialize.Invoke(builder, new object[] { "test|canceled", materializedFragment, suppliedProjectionIdentity }));
            Assert.IsType<OperationCanceledException>(exception.InnerException);
        }
        else
        {
            Assert.Null(materialize.Invoke(builder, new object[] { "test|empty", materializedFragment, suppliedProjectionIdentity }));
        }
        Assert.Null(typeof(LayoutBuilder).GetField("_materializedSceneMarker", flags)!.GetValue(builder));
        Assert.Equal(0, Assert.IsType<int>(typeof(LayoutBuilder)
            .GetField("_materializedSceneOrdinal", flags)!.GetValue(builder)));
        Assert.Same(previousProjectionIdentity, projectionIdentityField.GetValue(builder));
        projectionIdentityField.SetValue(builder, null);
        context.SetCancellationToken(CancellationToken.None);
        using LayoutSnapshot ordinary = builder.Build(document.ParsedDocument!, availableWidth: 640);
        Assert.Empty(EnumerateBlockScenes(ordinary.Blocks));
        Assert.Equal("ordinary text", ordinary.SemanticDocument.Text);
    }

    [Fact]
    public void DeferredBindingRetainsOnlyReadonlyMarkerAndOrdinal()
    {
        var fields = typeof(MarkdownDeferredSceneBinding).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        Assert.Equal(3, fields.Length);
        Assert.All(fields, static field => Assert.True(field.IsInitOnly));
        Assert.Contains(fields, static field => field.Name == "Marker" && field.FieldType == typeof(string));
        Assert.Contains(fields, static field => field.Name == "SceneOrdinal" && field.FieldType == typeof(int));
        Assert.Contains(fields, static field => field.Name == "ProjectionIdentity" && field.FieldType == typeof(object));

        Type identityType = typeof(MarkdownSceneProjectionSignature);
        Assert.True(identityType.IsValueType);
        Assert.True(identityType.IsDefined(typeof(System.Runtime.CompilerServices.IsReadOnlyAttribute), inherit: false));
        FieldInfo[] identityFields = identityType.GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        Assert.Equal(4, identityFields.Length);
        Assert.All(identityFields, static field =>
        {
            Assert.True(field.IsInitOnly);
            Assert.Equal(typeof(ulong), field.FieldType);
        });
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

    private static LayoutSnapshot BuildLayout(
        MarkdownDocument document,
        FlowDirection flowDirection,
        IMarkdownPerformanceSessionInternal? session = null,
        object? owner = null)
        => CreateLayoutBuilder(document, flowDirection, session, owner)
            .Build(document.ParsedDocument!, availableWidth: 640);

    private static LayoutBuilder CreateLayoutBuilder(
        MarkdownDocument document,
        FlowDirection flowDirection,
        IMarkdownPerformanceSessionInternal? session = null,
        object? owner = null)
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
            flowDirection)
        {
            PerformanceSession = session,
            PerformanceDocumentOwner = owner,
        };
        return new LayoutBuilder(context, semanticDocument: document);
    }

    private static IEnumerable<InlineContainerBox> EnumerateInlineContainers(
        IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            switch (block)
            {
                case InlineContainerBox inline:
                    yield return inline;
                    break;
                case ListItemBox listItem:
                    foreach (InlineContainerBox nested in EnumerateInlineContainers([listItem.Marker, listItem.Content]))
                        yield return nested;
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        yield return cell;
                    break;
                case StackBox stack:
                    foreach (InlineContainerBox nested in EnumerateInlineContainers(stack.Children))
                        yield return nested;
                    break;
            }
        }
    }

    private static IEnumerable<MarkdownSemanticNode> EnumerateSemanticNodes(MarkdownSemanticNode root)
    {
        yield return root;
        foreach (MarkdownSemanticNode child in root.Children)
        foreach (MarkdownSemanticNode descendant in EnumerateSemanticNodes(child))
            yield return descendant;
    }

    [Fact]
    public async Task DeferredInlineMathFailureKeepsExactSourceAndPublishesDiagnostic()
    {
        const string tex = @"\notacommand{sample}";
        const string formula = "$" + tex + "$";
        const string source = "before " + formula + " after";
        var formulaRange = new SourceSpan(source.IndexOf(formula, StringComparison.Ordinal), formula.Length);
        var diagnosticRange = new SourceSpan(source.IndexOf(tex, StringComparison.Ordinal), tex.Length);
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);

        MarkdownDocument progressive = await engine.ParseForProgressivePresentationAsync(source);
        Assert.True(progressive.TryGetInlineExtensionContent(formulaRange, out MarkdownContentFragment? fragment));
        MarkdownContent placeholder = Assert.Single(fragment!.Items);
        string marker = Assert.IsType<string>(placeholder.Attributes[MarkerAttribute]);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            progressive,
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

        Assert.True(plan.TryGetResult(marker, out MarkdownContentFragment? fallbackFragment));
        MarkdownContent fallback = Assert.Single(fallbackFragment!.Items);
        Assert.Equal(MarkdownContentKind.Text, fallback.Kind);
        Assert.Equal(formula, fallback.Text);
        Assert.Equal(MarkdownStyleRole.InlineCode, fallback.StyleRole);
        Assert.Equal(formulaRange, fallback.SourceSpan);
        MarkdownDiagnostic diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal("MATH100", diagnostic.Code);
        Assert.Equal(diagnosticRange, diagnostic.SourceSpan);

        MarkdownDocument eager = await engine.ParseAsync(source);
        Assert.Contains(eager.Diagnostics, static item => item.Code == "MATH100");
        Assert.True(eager.TryGetInlineExtensionContent(formulaRange, out MarkdownContentFragment? eagerFragment));
        Assert.Equal(formula, Assert.Single(eagerFragment!.Items).Text);
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
            if (!context.CanDeferOffscreenScenes)
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
