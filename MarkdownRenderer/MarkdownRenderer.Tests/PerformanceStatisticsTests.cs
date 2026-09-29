using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PerformanceStatisticsTests
{
    [Fact]
    public void MedianUsesTheTrueMiddleOfEvenPopulations()
    {
        Assert.Equal(5, PerformanceStatistics.Median([9, 1, 5]));
        Assert.Equal(50.5, PerformanceStatistics.Median([100, 1]));
        Assert.Equal(1, PerformanceStatistics.Percentile([1d, 100d], 0.50));
    }

    [Fact]
    public void TwoValueCenterIsSymmetricFiniteAndOverflowSafe()
    {
        Assert.Equal(90, PerformanceStatistics.TwoValueCenter(80, 100));
        Assert.Equal(90, PerformanceStatistics.TwoValueCenter(100, 80));
        Assert.Equal(double.MaxValue, PerformanceStatistics.TwoValueCenter(
            double.MaxValue,
            double.MaxValue));
        Assert.Equal(0, PerformanceStatistics.TwoValueCenter(
            -double.MaxValue,
            double.MaxValue));
        Assert.Equal(double.Epsilon, PerformanceStatistics.TwoValueCenter(
            double.Epsilon,
            double.Epsilon));
        Assert.True(double.IsNaN(PerformanceStatistics.TwoValueCenter(double.NaN, 1)));
        Assert.True(double.IsNaN(PerformanceStatistics.TwoValueCenter(1, double.PositiveInfinity)));
    }

    [Fact]
    public void MedianAndHodgesLehmannRejectInvalidPopulations()
    {
        Assert.True(double.IsNaN(PerformanceStatistics.Median(null)));
        Assert.True(double.IsNaN(PerformanceStatistics.Median([])));
        Assert.True(double.IsNaN(PerformanceStatistics.Median([1, double.NaN])));
        Assert.True(double.IsNaN(PerformanceStatistics.Median([1, double.NegativeInfinity])));
        Assert.True(double.IsNaN(PerformanceStatistics.HodgesLehmann(null)));
        Assert.True(double.IsNaN(PerformanceStatistics.RobustDispersionPercent(null)));
        Assert.Equal(7.75, PerformanceStatistics.HodgesLehmann([0, 1, 10, 20]));
        Assert.Equal(
            148.26,
            PerformanceStatistics.RobustDispersionPercent([0, 0, 10, 10]),
            10);
    }

    [Fact]
    public void TheilSenSlopeUsesEveryPairAndTheTrueMedian()
    {
        (int GlobalOrdinal, double Value)[] observations =
        [
            (0, 0),
            (1, 0),
            (2, 0),
            (3, 12),
        ];

        // The six slopes are 0, 0, 0, 4, 6, and 12. Their true median is 2;
        // a nearest-rank lower median would incorrectly return 0.
        Assert.Equal(2, PerformanceStatistics.TheilSenSlope(observations));
        Assert.Equal(6, PerformanceStatistics.ProjectedTheilSenDrift(observations));
    }

    [Fact]
    public void TheilSenSlopeSupportsIrregularGlobalOrdinalsAndResistsOneOutlier()
    {
        (int GlobalOrdinal, double Value)[] irregular =
        [
            (3, 100),
            (5, 110),
            (8, 125),
        ];
        (int GlobalOrdinal, double Value)[] outlier =
        [
            (0, 0),
            (1, 2),
            (2, 4),
            (3, 6),
            (4, 100),
        ];

        Assert.Equal(5, PerformanceStatistics.TheilSenSlope(irregular));
        Assert.Equal(25, PerformanceStatistics.ProjectedTheilSenDrift(irregular));
        Assert.Equal(2, PerformanceStatistics.TheilSenSlope(outlier));
        Assert.Equal(8, PerformanceStatistics.ProjectedTheilSenDrift(outlier));
    }

    [Fact]
    public void ProjectedTheilSenDriftPreservesDirectionAndUsesTheOrdinalSpan()
    {
        (int GlobalOrdinal, double Value)[] ascending =
        [
            (3, 100),
            (5, 110),
            (8, 125),
        ];
        (int GlobalOrdinal, double Value)[] descending =
        [
            (3, 125),
            (5, 115),
            (8, 100),
        ];

        Assert.Equal(5, PerformanceStatistics.TheilSenSlope(ascending));
        Assert.Equal(25, PerformanceStatistics.ProjectedTheilSenDrift(ascending));
        Assert.Equal(-5, PerformanceStatistics.TheilSenSlope(descending));
        Assert.Equal(-25, PerformanceStatistics.ProjectedTheilSenDrift(descending));
    }

    [Fact]
    public void OrderedTrendChangesWhenTheSameValuesMoveToDifferentOrdinals()
    {
        (int GlobalOrdinal, double Value)[] settling =
        [
            (0, 80),
            (1, 81),
            (2, 99),
            (3, 98),
            (4, 100),
        ];
        (int GlobalOrdinal, double Value)[] permuted =
        [
            (0, 80),
            (1, 99),
            (2, 100),
            (3, 81),
            (4, 98),
        ];
        double[] settlingValues = settling.Select(static item => item.Value).ToArray();
        double[] permutedValues = permuted.Select(static item => item.Value).ToArray();

        Assert.Equal(
            PerformanceStatistics.HodgesLehmann(settlingValues),
            PerformanceStatistics.HodgesLehmann(permutedValues));
        Assert.Equal(
            PerformanceStatistics.RobustDispersionPercent(settlingValues),
            PerformanceStatistics.RobustDispersionPercent(permutedValues));
        Assert.Equal(5.5, PerformanceStatistics.TheilSenSlope(settling));
        Assert.Equal(2d / 3d, PerformanceStatistics.TheilSenSlope(permuted), 12);
        Assert.Equal(22, PerformanceStatistics.ProjectedTheilSenDrift(settling));
        Assert.Equal(8d / 3d, PerformanceStatistics.ProjectedTheilSenDrift(permuted), 12);
    }

    [Fact]
    public void OrderedTrendRejectsZeroSpanInvalidOrdinalsAndInvalidValues()
    {
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope(null)));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope([])));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope([(0, 1)])));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope([(0, 1), (0, 2)])));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope([(-1, 1), (0, 2)])));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope([(0, 1), (2, 2), (1, 3)])));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope([(0, 1), (1, double.NaN)])));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope(
            [(0, double.NegativeInfinity), (1, 1)])));
        Assert.True(double.IsNaN(PerformanceStatistics.TheilSenSlope(
            [(0, -double.MaxValue), (1, double.MaxValue)])));

        Assert.True(double.IsNaN(PerformanceStatistics.ProjectedTheilSenDrift(null)));
        Assert.True(double.IsNaN(PerformanceStatistics.ProjectedTheilSenDrift([(0, 1)])));
        Assert.True(double.IsNaN(PerformanceStatistics.ProjectedTheilSenDrift([(0, 1), (0, 2)])));
    }
}
