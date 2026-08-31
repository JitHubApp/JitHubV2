using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceGateContractTests
{
    [Fact]
    public void RouteCatalog_HasExecutableMarkersForEveryCanonicalRoute()
    {
        Assert.Equal(14, ProductPerformanceGate.Routes.Count);
        Assert.Equal(ProductPerformanceGate.Routes.Count, ProductPerformanceGate.Routes.Select(static route => route.Id).Distinct().Count());

        foreach (ProductPerformanceRouteDefinition route in ProductPerformanceGate.Routes)
        {
            Assert.False(string.IsNullOrWhiteSpace(route.LaunchPage));
            Assert.False(string.IsNullOrWhiteSpace(route.RootAutomationId));
            Assert.Equal($"ProductPerformanceRouteReady_{route.Id}", route.ReadyAutomationId);
            Assert.Equal(route.SupportsTraversal, !string.IsNullOrWhiteSpace(route.SelectionAutomationId));
            Assert.Equal(route.SupportsTraversal, !string.IsNullOrWhiteSpace(route.SelectionDestinationRootAutomationId));
            Assert.Equal(route.SupportsTraversal, !string.IsNullOrWhiteSpace(route.SelectionDestinationContentAutomationId));
            Assert.Equal(route.SupportsScroll, !string.IsNullOrWhiteSpace(route.ScrollAutomationId));
        }

        ProductPerformanceRouteDefinition repoCode = Assert.Single(
            ProductPerformanceGate.Routes,
            static route => route.Id == "repo_code");
        Assert.Equal("ProductPerformanceRouteReady_repo_code", repoCode.ReadyAutomationId);
        Assert.Equal("RepoCodeEditor", repoCode.SelectionDestinationContentAutomationId);

        foreach (string routeId in new[] { "stars", "repo_manage", "repo_search" })
        {
            ProductPerformanceRouteDefinition repositoryRoute = Assert.Single(
                ProductPerformanceGate.Routes,
                route => route.Id == routeId);
            Assert.Equal("RepoCodeFileTree", repositoryRoute.SelectionDestinationContentAutomationId);
        }
    }

    [Fact]
    public void Gate_EnforcesCachedPageTraversalAndZeroBlankingAtP95()
    {
        Assert.Equal(150, ProductPerformanceBudgets.Get(ProductPerformanceFixture.Warm, ProductPerformanceMetric.RouteToSettledDataContent).Maximum);
        Assert.Equal(150, ProductPerformanceBudgets.Get(ProductPerformanceFixture.Offline, ProductPerformanceMetric.RouteToSettledDataContent).Maximum);
        Assert.Equal(150, ProductPerformanceBudgets.Get(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.RouteToSettledDataContent).Maximum);
        Assert.Equal(50, ProductPerformanceBudgets.Get(ProductPerformanceFixture.Warm, ProductPerformanceMetric.CachedSelection).Maximum);
        Assert.Equal(50, ProductPerformanceBudgets.Get(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.CachedSelection).Maximum);
        Assert.Equal(150, ProductPerformanceBudgets.Get(ProductPerformanceFixture.Warm, ProductPerformanceMetric.CachedRouteNavigation).Maximum);
        Assert.Equal(0, ProductPerformanceBudgets.Get(ProductPerformanceFixture.Warm, ProductPerformanceMetric.ContentBlanking).Maximum);

        ProductPerformanceBudget noBlanking = ProductPerformanceBudgets.Get(
            ProductPerformanceFixture.Warm,
            ProductPerformanceMetric.ContentBlanking);
        ProductPerformanceEvaluation passing = ProductPerformanceBudgets.Evaluate(
            noBlanking,
            Enumerable.Range(0, noBlanking.MinimumSamples)
                .Select(_ => Measurement(noBlanking, "home", 0)));
        ProductPerformanceEvaluation failing = ProductPerformanceBudgets.Evaluate(
            noBlanking,
            Enumerable.Range(0, noBlanking.MinimumSamples)
                .Select(index => Measurement(noBlanking, "home", index == 0 ? 1 : 0)));

        Assert.True(passing.Passed);
        Assert.False(failing.Passed);
        Assert.Contains("exceeds", failing.Detail);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Evaluation_RejectsInvalidMeasurements(double invalidValue)
    {
        ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(
            ProductPerformanceFixture.Warm,
            ProductPerformanceMetric.RouteToFirstDataContent);
        ProductPerformanceMeasurement[] measurements = Enumerable.Range(0, budget.MinimumSamples)
            .Select(index => Measurement(budget, "home", index == 0 ? invalidValue : 1))
            .ToArray();

        ProductPerformanceEvaluation evaluation = ProductPerformanceBudgets.Evaluate(budget, measurements);

        Assert.False(evaluation.Passed);
        Assert.Contains("negative or non-finite", evaluation.Detail);
    }

    [Fact]
    public void EmptyOrPartialInput_FailsDeterministically()
    {
        ProductPerformanceGateResult empty = ProductPerformanceGate.Evaluate(
            [],
            [ProductPerformanceFixture.Warm]);

        Assert.False(empty.Passed);
        Assert.NotEmpty(empty.Failures);
        Assert.All(empty.Failures, static failure => Assert.Contains("required samples", failure.Evaluation.Detail));
    }

    private static ProductPerformanceMeasurement Measurement(
        ProductPerformanceBudget budget,
        string route,
        double value) =>
        new(budget.Fixture, budget.Metric, value, route, DateTimeOffset.UnixEpoch);
}
