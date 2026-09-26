using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class CancellationMeasurementPlanTests
{
    [Fact]
    public void PlanPrependsOneDistinctWarmupAndPreservesEveryRecordedIteration()
    {
        const int requiredRecordedIterations = 5;

        IReadOnlyList<CancellationMeasurementIteration> plan =
            CancellationMeasurementPlan.CreateTrial(0, requiredRecordedIterations);

        Assert.Equal(requiredRecordedIterations + 1, plan.Count);
        Assert.Equal(
            CancellationMeasurementPlan.WarmupIterationsPerTrial,
            plan.Count(item => !item.IsRecorded));
        Assert.False(plan[0].IsRecorded);
        Assert.Equal(CancellationMeasurementPlan.FirstRecordedSourceSeed - 1, plan[0].SourceSeed);
        Assert.Equal(int.MinValue, plan[0].WinnerOrdinal);

        CancellationMeasurementIteration[] recorded = plan.Where(item => item.IsRecorded).ToArray();
        Assert.Equal(requiredRecordedIterations, recorded.Length);
        Assert.Equal(
            Enumerable.Range(
                CancellationMeasurementPlan.FirstRecordedSourceSeed,
                requiredRecordedIterations),
            recorded.Select(item => item.SourceSeed));
        Assert.Equal(
            Enumerable.Range(0, requiredRecordedIterations),
            recorded.Select(item => item.WinnerOrdinal));
    }

    [Fact]
    public void PlanRejectsMissingRecordedEvidence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CancellationMeasurementPlan.CreateTrial(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CancellationMeasurementPlan.CreateTrial(-1, 1));
    }

    [Fact]
    public void TrialsUseGloballyDistinctSeedsAndWinnerOrdinals()
    {
        CancellationMeasurementIteration[] iterations = Enumerable.Range(0, 5)
            .SelectMany(ordinal => CancellationMeasurementPlan.CreateTrial(ordinal, 40))
            .ToArray();

        Assert.Equal(iterations.Length, iterations.Select(item => item.SourceSeed).Distinct().Count());
        Assert.Equal(iterations.Length, iterations.Select(item => item.WinnerOrdinal).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    public void WarmupEvidencePassesWithinTheAbsoluteBudget(double elapsedMilliseconds)
    {
        Assert.True(CancellationMeasurementPlan.IsWarmupEvidencePassing(
            CancellationMeasurementPlan.WarmupIterationsPerTrial,
            elapsedMilliseconds,
            staleCommits: 0));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, -0.001, 0)]
    [InlineData(1, 16.001, 0)]
    [InlineData(1, double.NaN, 0)]
    [InlineData(1, double.PositiveInfinity, 0)]
    [InlineData(1, 1, 1)]
    public void WarmupEvidenceRejectsMissingSlowNonFiniteOrStaleResults(
        int warmupIterations,
        double elapsedMilliseconds,
        int staleCommits)
    {
        Assert.False(CancellationMeasurementPlan.IsWarmupEvidencePassing(
            warmupIterations,
            elapsedMilliseconds,
            staleCommits));
    }
}
