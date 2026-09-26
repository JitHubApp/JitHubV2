using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class FirstViewportMeasurementContractTests
{
    [Fact]
    public void WilliamsDesignBalancesPeriodsAndFirstOrderCarryover()
    {
        int rows = PerformanceMeasurementContract.ReleaseFirstViewportTrials;
        int conditions = PerformanceMeasurementContract.FirstViewportConditionCount;
        Assert.Equal(conditions, rows);

        var periodCounts = new int[conditions, conditions];
        var carryovers = new Dictionary<(int Previous, int Current), int>();
        for (int row = 0; row < rows; row++)
        {
            int[] sequence = Enumerable.Range(0, conditions)
                .Select(position => PerformanceMeasurementContract.GetFirstViewportCondition(row, position))
                .ToArray();
            Assert.Equal(Enumerable.Range(0, conditions).Order(), sequence.Order());

            for (int position = 0; position < sequence.Length; position++)
            {
                periodCounts[position, sequence[position]]++;
                if (position == 0)
                    continue;

                var pair = (sequence[position - 1], sequence[position]);
                carryovers[pair] = carryovers.GetValueOrDefault(pair) + 1;
            }
        }

        for (int position = 0; position < conditions; position++)
        {
            for (int condition = 0; condition < conditions; condition++)
                Assert.Equal(1, periodCounts[position, condition]);
        }

        Assert.Equal(conditions * (conditions - 1), carryovers.Count);
        Assert.All(carryovers, static pair => Assert.Equal(1, pair.Value));
    }

    [Fact]
    public void SchemaElevenFreezesHonestModesAndTrialShapedPopulations()
    {
        Assert.Equal(11, PerformanceMeasurementContract.SchemaVersion);
        Assert.Equal(6, PerformanceMeasurementContract.ReleaseFirstViewportTrials);
        Assert.Equal(3, PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials);
        Assert.Equal("cache-disabled", PerformanceMeasurementContract.FirstViewportCacheDisabledMode);
        Assert.Equal("cache-hit", PerformanceMeasurementContract.FirstViewportCacheHitMode);
        Assert.Contains("fresh-engine", PerformanceMeasurementContract.FirstViewportTrialStartPolicy);
        Assert.Contains("one-settling-presentation", PerformanceMeasurementContract.FirstViewportTrialStartPolicy);
        Assert.Contains("williams", PerformanceMeasurementContract.FirstViewportSchedulePolicy);
        Assert.Contains("theil-sen", PerformanceMeasurementContract.FirstViewportStationarityPolicy);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(6, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 6)]
    public void WilliamsDesignRejectsOutOfRangeCoordinates(int row, int position)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            PerformanceMeasurementContract.GetFirstViewportCondition(row, position));
}
