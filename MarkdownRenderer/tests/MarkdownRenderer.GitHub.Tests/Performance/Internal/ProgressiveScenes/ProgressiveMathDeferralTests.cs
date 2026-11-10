using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ProgressiveSceneDeferralFixture;

namespace MarkdownRenderer.GitHub.Tests.Performance.Internal.ProgressiveScenes;

/// <summary>
/// Checks progressive parsing of math and Mermaid scenes: block and inline
/// scenes are deferred until scheduled, keep their atomic source semantics,
/// and fall back to their exact source on failure or before
/// materialization.
/// </summary>
public sealed class ProgressiveMathDeferralTests
{
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
        InlineVectorSceneRun[] pendingMath =
            InlineMathPlaceholderAssertions.FindMathRuns(EnumerateInlineContainers(pendingSnapshot.Blocks));
        InlineVectorSceneRun[] materializedMath =
            InlineMathPlaceholderAssertions.FindMathRuns(EnumerateInlineContainers(materializedSnapshot.Blocks));

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
            InlineMathPlaceholderAssertions.AssertRealizedTwinAndAtomicSourceMapping(
                pending, materializedMath, EnumerateInlineContainers(pendingSnapshot.Blocks), pendingSnapshot, source);
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
}
