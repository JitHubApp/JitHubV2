using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownParsePipelineDiagnosticsTests
{
    [Fact]
    public void AuditStageIdsAreStableForTheWinUiRecorder()
    {
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4, 5 },
            new[]
            {
                (int)MarkdownParsePipelineStage.Begin,
                (int)MarkdownParsePipelineStage.EngineParseAndCache,
                (int)MarkdownParsePipelineStage.LegacyParseAndDocument,
                (int)MarkdownParsePipelineStage.ProgressiveScenePlan,
                (int)MarkdownParsePipelineStage.StyleRoleDemand,
                (int)MarkdownParsePipelineStage.SessionTotal,
            });
    }

    [Fact]
    public async Task AuditOptInReportsPreparationStagesAndDisabledSessionEmitsNothing()
    {
        var events = new List<ParsePipelineEvent>();
        using MarkdownEngine engine = new MarkdownEngineBuilder().Build();
        const string source = "# Heading\n\nBody text.";
        object owner = new();
        int ownerIdentity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner);
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive)
        {
            ParseStageDiagnosticRecorder = (owner, stage, ticks) =>
                events.Add(new ParsePipelineEvent(owner, (MarkdownParsePipelineStage)stage, ticks)),
        };

        MarkdownRenderer.Document.MarkdownDocument? document =
            await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                engine,
                null,
                source,
                new MarkdownExtensionRegistry(),
                owner,
                CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal(source, document!.Source);
        Assert.All(events, item => Assert.Equal(ownerIdentity, item.OwnerIdentity));
        Assert.Equal(
            new[]
            {
                MarkdownParsePipelineStage.Begin,
                MarkdownParsePipelineStage.EngineParseAndCache,
                MarkdownParsePipelineStage.ProgressiveScenePlan,
                MarkdownParsePipelineStage.StyleRoleDemand,
                MarkdownParsePipelineStage.SessionTotal,
            },
            events.Select(static item => item.Stage));
        Assert.Equal(0, events[0].ElapsedStopwatchTicks);
        Assert.All(events.Skip(1), item => Assert.True(item.ElapsedStopwatchTicks >= 0));

        await session.DisposeAsync();
        Assert.Null(session.ParseStageDiagnosticRecorder);

        await using var disabledSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownRenderer.Document.MarkdownDocument? uninstrumented =
            await ((IMarkdownPerformanceSessionInternal)disabledSession).ParseAndPrepareDocumentAsync(
                engine,
                null,
                source,
                new MarkdownExtensionRegistry(),
                new object(),
                CancellationToken.None);

        Assert.NotNull(uninstrumented);
        Assert.Equal(5, events.Count);
    }

    private readonly record struct ParsePipelineEvent(
        int OwnerIdentity,
        MarkdownParsePipelineStage Stage,
        long ElapsedStopwatchTicks);
}
