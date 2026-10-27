using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ProgressiveSceneDiagnosticAggregationTests
{
    private const string DeferredSceneMarkerAttribute = "renderer.internal.deferred-scene";

    [Fact]
    public async Task SceneDiagnosticsAggregateInSourceOrderAndSuccessfulPublicationPreservesSnapshot()
    {
        const string source =
            "$$\n\\frac{1}{2}\n$$\n\n" +
            "$$\n\\notacommand{first error}\n$$\n\n" +
            "$$\n\\notacommand{second error}\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        string[] markers = document.ExtensionBlockNodeContent.Values
            .SelectMany(static fragment => fragment.Items)
            .Where(static content => content.Attributes.ContainsKey(DeferredSceneMarkerAttribute))
            .OrderBy(static content => content.SourceSpan.Start)
            .Select(content => Assert.IsType<string>(content.Attributes[DeferredSceneMarkerAttribute]))
            .ToArray();
        Assert.Equal(3, markers.Length);

        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        // Complete errors sequentially in reverse source order. A preparation
        // slot now includes its queued publication, so this must also work on
        // the x86/one-slot lane without holding an undrained success in front.
        Action laterError = await QueuePublicationAsync(plan, markers[2]);
        laterError();
        Action earlierError = await QueuePublicationAsync(plan, markers[1]);
        earlierError();

        IReadOnlyList<MarkdownDiagnostic> diagnostics = plan.Diagnostics;
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("MATH100", diagnostics[0].Code);
        Assert.Equal("MATH100", diagnostics[1].Code);
        Assert.True(diagnostics[0].SourceSpan.Start < diagnostics[1].SourceSpan.Start);

        // A valid scene completion has no diagnostics and must not allocate or
        // publish a replacement diagnostics snapshot on the UI callback path.
        Action successfulPublication = await QueuePublicationAsync(plan, markers[0]);
        successfulPublication();
        Assert.Same(diagnostics, plan.Diagnostics);
        Assert.Equal(2, plan.Diagnostics.Count);
    }

    [Fact]
    public async Task BulkSceneErrorsStaySourceSortedWithoutRebuildingFromCompletedDescriptors()
    {
        const int errorCount = 48;
        string source = string.Join(
            "\n\n",
            Enumerable.Range(0, errorCount).Select(static index => $"$$\n\\notacommand{{error-{index}}}\n$$"));
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        string[] markers = GetMarkers(document);
        Assert.Equal(errorCount, markers.Length);

        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;

        foreach (string marker in markers.Reverse())
        {
            Action publication = await QueuePublicationAsync(plan, marker);
            publication();
        }

        await plan.AdvanceGeneration().WaitAsync(TimeSpan.FromSeconds(10));
        IReadOnlyList<MarkdownDiagnostic> diagnostics = plan.Diagnostics;
        Assert.Equal(errorCount, diagnostics.Count);
        Assert.All(diagnostics, static diagnostic => Assert.Equal("MATH100", diagnostic.Code));
        Assert.Equal(
            diagnostics.OrderBy(static diagnostic => diagnostic.SourceSpan.Start),
            diagnostics);
    }

    [Fact]
    public async Task RepeatedMarkerPublicationReplacesItsDiagnosticContribution()
    {
        var extension = new ReplacingDiagnosticExtension();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(extension)
            .Build();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { SceneCacheBudgetBytes = 0 });
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync("replacement target");
        string marker = Assert.Single(GetMarkers(document));
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;

        IReadOnlyList<MarkdownDiagnostic> firstSnapshot =
            await QueuePublishAndAdvanceAsync(plan, marker);
        Assert.Equal("replacement 1", Assert.Single(firstSnapshot).Message);
        ConsumeCompletedResult(plan, marker);
        session.Trim();

        bool rescheduled = false;
        var queuedReplacement = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int attempt = 0; attempt < 8 && !rescheduled; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            rescheduled = plan.TrySchedule(
                marker,
                action =>
                {
                    queuedReplacement.TrySetResult(action);
                    return true;
                },
                static () => { });
            if (!rescheduled)
                await Task.Delay(10);
        }
        Assert.True(rescheduled, "The zero-budget weak result should be re-prepared after its handoff is consumed.");

        Action replacementPublication = await queuedReplacement.Task.WaitAsync(TimeSpan.FromSeconds(10));
        replacementPublication();
        await plan.AdvanceGeneration().WaitAsync(TimeSpan.FromSeconds(10));
        IReadOnlyList<MarkdownDiagnostic> replacementSnapshot = plan.Diagnostics;
        Assert.NotSame(firstSnapshot, replacementSnapshot);
        Assert.Equal("replacement 2", Assert.Single(replacementSnapshot).Message);
    }

    [Fact]
    public async Task RetirementCancelsDiagnosticPublicationWhenDispatcherQueueIsNeverDrained()
    {
        const string tex = @"\notacommand{queued}";
        string source = "$$\n" + tex + "\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        string marker = Assert.Single(GetMarkers(document));
        MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        Action queuedPublication = await QueuePublicationAsync(plan, marker);

        await plan.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(plan.Diagnostics);

        // This callback was accepted by the fake dispatcher but never drained
        // until after cancellation and teardown had completed.
        queuedPublication();
        Assert.Empty(plan.Diagnostics);
        plan.Dispose();
    }

    [Fact]
    public async Task RetirementFromPublicationCallbackWaitsForProducerAcknowledgement()
    {
        const string tex = @"\notacommand{retire in callback}";
        string source = "$$\n" + tex + "\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        string marker = Assert.Single(GetMarkers(document));
        MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;
        var queuedPublication = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? retirement = null;
        bool retirementCompletedInsideCallback = true;

        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                queuedPublication.TrySetResult(action);
                return true;
            },
            () =>
            {
                retirement = plan.Retire();
                retirementCompletedInsideCallback = retirement.IsCompleted;
            }));

        Action publication = await queuedPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        publication();

        Assert.False(retirementCompletedInsideCallback,
            "Retirement must include the producer that still owns the serialized diagnostic publication gate.");
        await (retirement ?? throw new InvalidOperationException("The publication callback did not start retirement."))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public async Task CanceledQueuedDiagnosticCannotOverwriteLaterMarkerContribution()
    {
        const string firstTex = @"\notacommand{first}";
        const string secondTex = @"\notacommand{second}";
        string source = "$$\n" + firstTex + "\n$$\n\n$$\n" + secondTex + "\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownDocument document = await engine.ParseForProgressivePresentationAsync(source);
        MarkdownContent[] markedContent = document.ExtensionBlockNodeContent.Values
            .SelectMany(static fragment => fragment.Items)
            .Where(static content => content.Attributes.ContainsKey(DeferredSceneMarkerAttribute))
            .OrderBy(static content => content.SourceSpan.Start)
            .ToArray();
        string firstMarker = Assert.IsType<string>(markedContent[0].Attributes[DeferredSceneMarkerAttribute]);
        string secondMarker = Assert.IsType<string>(markedContent[1].Attributes[DeferredSceneMarkerAttribute]);
        using MarkdownProgressiveScenePlan plan = (await MarkdownProgressiveScenePlan.TryCreateAsync(
            document,
            (IMarkdownPerformanceSessionInternal)session,
            new object(),
            CancellationToken.None))!;

        plan.UpdateVisibleMarkers([firstMarker]);
        Action stalePublication = await QueuePublicationAsync(plan, firstMarker);
        plan.UpdateVisibleMarkers(Array.Empty<string>());

        plan.UpdateVisibleMarkers([secondMarker]);
        Action currentPublication = await QueuePublicationAsync(plan, secondMarker);
        currentPublication();
        await plan.AdvanceGeneration().WaitAsync(TimeSpan.FromSeconds(10));

        IReadOnlyList<MarkdownDiagnostic> currentSnapshot = plan.Diagnostics;
        MarkdownDiagnostic currentDiagnostic = Assert.Single(currentSnapshot);
        Assert.Equal(source.IndexOf(secondTex, StringComparison.Ordinal), currentDiagnostic.SourceSpan.Start);

        // The first callback remained queued after its cancellation, but its
        // active identity and publication version are stale and cannot replace
        // the later marker's immutable contribution root.
        stalePublication();
        Assert.Same(currentSnapshot, plan.Diagnostics);
        Assert.Equal(currentDiagnostic, Assert.Single(plan.Diagnostics));
    }

    private static async Task<Action> QueuePublicationAsync(
        MarkdownProgressiveScenePlan plan,
        string marker)
    {
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(plan.TrySchedule(
            marker,
            action =>
            {
                queued.TrySetResult(action);
                return true;
            },
            static () => { }));
        return await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<IReadOnlyList<MarkdownDiagnostic>> QueuePublishAndAdvanceAsync(
        MarkdownProgressiveScenePlan plan,
        string marker)
    {
        Action publication = await QueuePublicationAsync(plan, marker);
        publication();
        publication = null!;
        await plan.AdvanceGeneration().WaitAsync(TimeSpan.FromSeconds(10));
        return plan.Diagnostics;
    }

    private static string[] GetMarkers(MarkdownDocument document)
        => document.ExtensionBlockNodeContent.Values
            .SelectMany(static fragment => fragment.Items)
            .Where(static content => content.Attributes.ContainsKey(DeferredSceneMarkerAttribute))
            .OrderBy(static content => content.SourceSpan.Start)
            .Select(content => Assert.IsType<string>(content.Attributes[DeferredSceneMarkerAttribute]))
            .ToArray();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ConsumeCompletedResult(MarkdownProgressiveScenePlan plan, string marker)
        => Assert.True(plan.TryGetResult(marker, out _));

    private sealed class ReplacingDiagnosticExtension : IMarkdownExtension
    {
        private int _renderCount;

        public string Id => "MarkdownRenderer.GitHub.Tests.ReplacingDiagnostic";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, RenderAsync);

        private ValueTask RenderAsync(MarkdownExtensionContext context, MarkdownContentBuilder content)
        {
            if (context.ShouldDeferOffscreenScenes)
            {
                content.AddCodeBlock(
                    context.Node.Literal ?? string.Empty,
                    "test",
                    context.Node.SourceSpan,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [DeferredSceneMarkerAttribute] = string.Concat(
                            MarkdownSyntaxKinds.Block.Paragraph,
                            "|replacement"),
                    });
                return ValueTask.CompletedTask;
            }

            int renderCount = Interlocked.Increment(ref _renderCount);
            context.ReportDiagnostic(
                "TST0001",
                MarkdownDiagnosticSeverity.Error,
                $"replacement {renderCount}",
                context.Node.SourceSpan);
            content.AddCodeBlock(
                $"render {renderCount}",
                "test",
                context.Node.SourceSpan,
                attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DeferredSceneMarkerAttribute] = string.Concat(
                        MarkdownSyntaxKinds.Block.Paragraph,
                        "|non-reconstructible-result"),
                });
            return ValueTask.CompletedTask;
        }
    }
}
