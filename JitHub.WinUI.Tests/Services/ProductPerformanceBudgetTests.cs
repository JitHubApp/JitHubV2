using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceBudgetTests
{
    [Fact]
    public void Catalog_CoversEveryRequiredFixtureAndMetric()
    {
        ProductPerformanceFixture[] fixtures = Enum.GetValues<ProductPerformanceFixture>();
        ProductPerformanceMetric[] universallyRequired =
        [
            ProductPerformanceMetric.StartupToInteractive,
            ProductPerformanceMetric.RouteToFirstDataContent,
            ProductPerformanceMetric.RouteToSettledDataContent,
            ProductPerformanceMetric.ContentBlanking,
            ProductPerformanceMetric.DispatcherStall,
            ProductPerformanceMetric.WorkingSet
        ];

        foreach (ProductPerformanceFixture fixture in fixtures)
        {
            foreach (ProductPerformanceMetric metric in universallyRequired)
            {
                ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(fixture, metric);
                Assert.True(
                    metric == ProductPerformanceMetric.ContentBlanking
                        ? budget.Maximum == 0
                        : budget.Maximum > 0);
                Assert.True(budget.MinimumSamples >= 3);
            }
        }

        foreach (ProductPerformanceFixture fixture in fixtures.Where(fixture => fixture != ProductPerformanceFixture.Cold))
        {
            Assert.Equal(
                50,
                ProductPerformanceBudgets.Get(fixture, ProductPerformanceMetric.CachedSelection).Maximum,
                0);
            Assert.Equal(
                150,
                ProductPerformanceBudgets.Get(fixture, ProductPerformanceMetric.CachedRouteNavigation).Maximum,
                0);
            Assert.True(ProductPerformanceBudgets.Get(fixture, ProductPerformanceMetric.ScrollFrame).MinimumSamples >= 30);
        }
    }

    [Fact]
    public void CachedWarmRoute_UsesOriginalPerceivedPerformanceBudget()
    {
        ProductPerformanceBudget route = ProductPerformanceBudgets.Get(
            ProductPerformanceFixture.Warm,
            ProductPerformanceMetric.RouteToSettledDataContent);
        ProductPerformanceBudget traversal = ProductPerformanceBudgets.Get(
            ProductPerformanceFixture.Warm,
            ProductPerformanceMetric.CachedSelection);

        Assert.Equal(150, route.Maximum, 0);
        Assert.Equal(50, traversal.Maximum, 0);
    }

    [Fact]
    public void Evaluate_UsesP95AndRejectsUnderSampledRuns()
    {
        ProductPerformanceBudget budget = new(
            ProductPerformanceFixture.Warm,
            ProductPerformanceMetric.RouteToSettledDataContent,
            Maximum: 150,
            Unit: ProductPerformanceBudgets.Milliseconds,
            MinimumSamples: 5);

        ProductPerformanceEvaluation underSampled = ProductPerformanceBudgets.Evaluate(
            budget,
            CreateMeasurements(budget, 30, 40, 50));
        ProductPerformanceEvaluation passing = ProductPerformanceBudgets.Evaluate(
            budget,
            CreateMeasurements(budget, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120));
        ProductPerformanceEvaluation failing = ProductPerformanceBudgets.Evaluate(
            budget,
            CreateMeasurements(budget, 30, 40, 50, 60, 70, 80, 90, 100, 220, 300));

        Assert.False(underSampled.Passed);
        Assert.Contains("required samples", underSampled.Detail);
        Assert.True(passing.Passed);
        Assert.False(failing.Passed);
        Assert.Contains("exceeds", failing.Detail);
    }

    [Fact]
    public void Gate_CoversEveryCanonicalRouteWithoutCrossRouteAveraging()
    {
        Assert.Equal(14, ProductPerformanceGate.Routes.Count);
        Assert.Contains(ProductPerformanceGate.Routes, route => route.Id == "home" && !route.SupportsCachedSelection);
        Assert.Contains(ProductPerformanceGate.Routes, route => route.Id == "repo_commits" && route.SupportsCachedSelection);
        Assert.Contains(ProductPerformanceGate.Routes, route => route.Id == "notifications" && route.SupportsCachedRouteNavigation);

        ProductPerformanceMeasurement[] measurements = CreatePassingWarmGateMeasurements().ToArray();
        ProductPerformanceGateResult passing = ProductPerformanceGate.Evaluate(
            measurements,
            [ProductPerformanceFixture.Warm]);

        Assert.True(passing.Passed);

        ProductPerformanceMeasurement[] withOneSlowRoute =
        [
            .. measurements,
            .. CreateMeasurements(
                ProductPerformanceBudgets.Get(ProductPerformanceFixture.Warm, ProductPerformanceMetric.RouteToSettledDataContent),
                "settings",
                400, 400, 400)
        ];
        ProductPerformanceGateResult failing = ProductPerformanceGate.Evaluate(
            withOneSlowRoute,
            [ProductPerformanceFixture.Warm]);

        Assert.False(failing.Passed);
        Assert.Contains(
            failing.Evaluations,
            evaluation =>
                evaluation.Route == "settings" &&
                evaluation.Evaluation.Budget.Metric == ProductPerformanceMetric.RouteToSettledDataContent &&
                !evaluation.Evaluation.Passed);
    }

    [Fact]
    public void Gate_FocusedRoutesEvaluateOnlyRequestedCanonicalSet()
    {
        ProductPerformanceMeasurement[] measurements = CreatePassingWarmGateMeasurements().ToArray();

        ProductPerformanceGateResult focused = ProductPerformanceGate.Evaluate(
            measurements,
            [ProductPerformanceFixture.Warm],
            ["settings"]);

        Assert.True(focused.Passed);
        Assert.Contains(focused.Evaluations, static evaluation => evaluation.Route == ProductPerformanceGate.ApplicationRoute);
        Assert.Contains(focused.Evaluations, static evaluation => evaluation.Route == "settings");
        Assert.DoesNotContain(focused.Evaluations, static evaluation => evaluation.Route == "home");
        Assert.Throws<ArgumentException>(() => ProductPerformanceGate.Evaluate(
            measurements,
            [ProductPerformanceFixture.Warm],
            ["not_a_route"]));
    }

    private static IEnumerable<ProductPerformanceMeasurement> CreatePassingWarmGateMeasurements()
    {
        ProductPerformanceFixture fixture = ProductPerformanceFixture.Warm;
        foreach (ProductPerformanceMeasurement measurement in CreateMeasurements(
                     ProductPerformanceBudgets.Get(fixture, ProductPerformanceMetric.StartupToInteractive),
                     ProductPerformanceGate.ApplicationRoute,
                     500, 600, 700))
        {
            yield return measurement;
        }

        foreach (ProductPerformanceRouteDefinition route in ProductPerformanceGate.Routes)
        {
            foreach (ProductPerformanceMetric metric in new[]
                     {
                        ProductPerformanceMetric.RouteToFirstDataContent,
                        ProductPerformanceMetric.RouteToSettledDataContent,
                        ProductPerformanceMetric.ContentBlanking,
                        ProductPerformanceMetric.DispatcherStall,
                        ProductPerformanceMetric.WorkingSet
                     })
            {
                ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(fixture, metric);
                foreach (ProductPerformanceMeasurement measurement in CreateMeasurements(
                             budget,
                             route.Id,
                             Enumerable.Repeat(budget.Maximum * 0.5, budget.MinimumSamples).ToArray()))
                {
                    yield return measurement;
                }
            }

            if (route.SupportsCachedSelection)
            {
                ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(fixture, ProductPerformanceMetric.CachedSelection);
                foreach (ProductPerformanceMeasurement measurement in CreateMeasurements(
                             budget,
                             route.Id,
                             Enumerable.Repeat(budget.Maximum * 0.5, budget.MinimumSamples).ToArray()))
                {
                    yield return measurement;
                }
            }

            if (route.SupportsCachedRouteNavigation)
            {
                ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(fixture, ProductPerformanceMetric.CachedRouteNavigation);
                foreach (ProductPerformanceMeasurement measurement in CreateMeasurements(
                             budget,
                             route.Id,
                             Enumerable.Repeat(budget.Maximum * 0.5, budget.MinimumSamples).ToArray()))
                {
                    yield return measurement;
                }
            }

            if (route.SupportsScroll)
            {
                ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(fixture, ProductPerformanceMetric.ScrollFrame);
                foreach (ProductPerformanceMeasurement measurement in CreateMeasurements(
                             budget,
                             route.Id,
                             Enumerable.Repeat(budget.Maximum * 0.5, budget.MinimumSamples).ToArray()))
                {
                    yield return measurement;
                }
            }
        }
    }

    private static IEnumerable<ProductPerformanceMeasurement> CreateMeasurements(
        ProductPerformanceBudget budget,
        params double[] values) =>
        CreateMeasurements(budget, "fixture", values);

    private static IEnumerable<ProductPerformanceMeasurement> CreateMeasurements(
        ProductPerformanceBudget budget,
        string route,
        params double[] values) =>
        values.Select(value => new ProductPerformanceMeasurement(
            budget.Fixture,
            budget.Metric,
            value,
            Route: route,
            DateTimeOffset.UnixEpoch));
}
