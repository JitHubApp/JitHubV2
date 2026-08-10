using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceRunPlanTests
{
    [Fact]
    public void DefaultPlan_CoversEveryFixtureRouteAndTenIterations()
    {
        ProductPerformanceRunPlan plan = ProductPerformanceRunPlan.Create();

        Assert.Equal(4 * 14 * 10, plan.Cases.Count);
        Assert.Equal(10, plan.Iterations);
        Assert.Equal(Enum.GetValues<ProductPerformanceFixture>(), plan.Cases.Select(static item => item.Fixture).Distinct());
        Assert.Equal(
            ProductPerformanceGate.Routes.Select(static route => route.Id).OrderBy(static route => route),
            plan.Cases.Select(static item => item.Route.Id).Distinct().OrderBy(static route => route));
    }

    [Fact]
    public void Fixtures_EncodeColdWarmOfflineAndLargeAccountSemantics()
    {
        ProductPerformanceRunPlan plan = ProductPerformanceRunPlan.Create();
        ProductPerformanceRunCase cold = plan.Cases.First(static item => item.Fixture == ProductPerformanceFixture.Cold);
        ProductPerformanceRunCase warm = plan.Cases.First(static item => item.Fixture == ProductPerformanceFixture.Warm);
        ProductPerformanceRunCase offline = plan.Cases.First(static item => item.Fixture == ProductPerformanceFixture.Offline);
        ProductPerformanceRunCase large = plan.Cases.First(static item => item.Fixture == ProductPerformanceFixture.LargeAccount);

        Assert.True(cold.ResetCache);
        Assert.Contains(cold.Route.Id, cold.DataPartition);
        Assert.False(warm.ResetCache);
        Assert.Equal("warm/shared", warm.DataPartition);
        Assert.True(offline.DisableNetwork);
        Assert.Equal("offline/shared", offline.DataPartition);
        Assert.True(large.UseLargeAccountData);
        Assert.Equal("large-account/shared", large.DataPartition);
    }

    [Fact]
    public void Plan_RejectsUnderSampledAndUnknownRouteRequests()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductPerformanceRunPlan.Create(iterations: 9));
        Assert.Throws<ArgumentException>(() => ProductPerformanceRunPlan.Create(fixtures: []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductPerformanceRunPlan.Create(fixtures: [(ProductPerformanceFixture)999]));
        ArgumentException unknown = Assert.Throws<ArgumentException>(() =>
            ProductPerformanceRunPlan.Create(routes: ["home", "not_a_route"]));
        Assert.Contains("not_a_route", unknown.Message);
    }

    [Fact]
    public void Warmup_IsOnlineResetsPartitionAndPreservesFixtureShape()
    {
        ProductPerformanceRunCase offline = ProductPerformanceRunPlan.Create()
            .Cases.First(static item => item.Fixture == ProductPerformanceFixture.Offline);

        ProductPerformanceRunCase warmup = offline.CreateWarmup();

        Assert.Equal(-1, warmup.Iteration);
        Assert.True(warmup.ResetCache);
        Assert.False(warmup.DisableNetwork);
        Assert.Equal(offline.DataPartition, warmup.DataPartition);
        Assert.Equal(offline.Route, warmup.Route);
        Assert.Throws<InvalidOperationException>(() =>
            ProductPerformanceRunPlan.Create()
                .Cases.First(static item => item.Fixture == ProductPerformanceFixture.Cold)
                .CreateWarmup());
    }

    [Fact]
    public void Requirements_MatchEveryGateEvaluationDimension()
    {
        ProductPerformanceRunPlan plan = ProductPerformanceRunPlan.Create();
        var requirements = ProductPerformanceMeasurementRequirements.Get(plan);

        Assert.Equal(4, requirements.Count(static item => item.Route == ProductPerformanceGate.ApplicationRoute));
        Assert.Contains(requirements, static item =>
            item.Route == "repo_commits" &&
            item.Fixture == ProductPerformanceFixture.Warm &&
            item.Metric == ProductPerformanceMetric.CachedSelection &&
            item.MinimumSamples == 10);
        Assert.Contains(requirements, static item =>
            item.Route == "home" &&
            item.Fixture == ProductPerformanceFixture.Warm &&
            item.Metric == ProductPerformanceMetric.ContentBlanking &&
            item.MinimumSamples == 10);
        Assert.DoesNotContain(requirements, static item =>
            item.Route == "home" && item.Metric == ProductPerformanceMetric.CachedSelection);
        Assert.Contains(requirements, static item =>
            item.Route == "notifications" &&
            item.Fixture == ProductPerformanceFixture.Warm &&
            item.Metric == ProductPerformanceMetric.CachedRouteNavigation &&
            item.MinimumSamples == 10);
        Assert.DoesNotContain(requirements, static item =>
            item.Route == "notifications" && item.Metric == ProductPerformanceMetric.CachedSelection);
        Assert.DoesNotContain(requirements, static item =>
            item.Fixture == ProductPerformanceFixture.Cold && item.Metric == ProductPerformanceMetric.ScrollFrame);
    }
}
