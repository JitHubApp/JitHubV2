using JitHub.Services.Layout;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ResponsiveUniformGridLayoutPolicyTests
{
    [Theory]
    [InlineData(0, 5, 1, 0)]
    [InlineData(219, 5, 1, 219)]
    [InlineData(220, 5, 1, 220)]
    [InlineData(451, 5, 1, 451)]
    [InlineData(452, 5, 2, 220)]
    [InlineData(684, 5, 3, 220)]
    [InlineData(1148, 5, 5, 220)]
    public void Calculate_UsesStableEqualWidthColumns(
        double width,
        int itemCount,
        int expectedColumns,
        double expectedItemWidth)
    {
        ResponsiveUniformGridMetrics result = ResponsiveUniformGridLayoutPolicy.Calculate(
            width,
            itemCount,
            minimumItemWidth: 220,
            spacing: 12);

        Assert.Equal(expectedColumns, result.Columns);
        Assert.Equal(expectedItemWidth, result.ItemWidth, precision: 6);
    }

    [Fact]
    public void Calculate_HandlesEmptyInfiniteAndInvalidConstraintsWithoutDivision()
    {
        Assert.Equal(
            new ResponsiveUniformGridMetrics(0, 0),
            ResponsiveUniformGridLayoutPolicy.Calculate(0, 0, 0, 0));
        Assert.Equal(
            new ResponsiveUniformGridMetrics(5, 220),
            ResponsiveUniformGridLayoutPolicy.Calculate(double.PositiveInfinity, 5, 220, 12));
        Assert.Equal(
            new ResponsiveUniformGridMetrics(1, 0),
            ResponsiveUniformGridLayoutPolicy.Calculate(double.NaN, 5, double.NaN, double.NaN));
    }
}
