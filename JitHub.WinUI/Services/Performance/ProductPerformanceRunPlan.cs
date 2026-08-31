using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JitHub.Services;

public sealed record ProductPerformanceRunCase(
    ProductPerformanceFixture Fixture,
    ProductPerformanceRouteDefinition Route,
    int Iteration,
    string DataPartition,
    bool ResetCache,
    bool DisableNetwork,
    bool UseLargeAccountData)
{
    public ProductPerformanceRunCase CreateWarmup()
    {
        if (Fixture == ProductPerformanceFixture.Cold)
        {
            throw new InvalidOperationException("Cold fixtures do not have a cache warm-up run.");
        }

        return this with
        {
            Iteration = -1,
            ResetCache = true,
            DisableNetwork = false
        };
    }
}

public sealed record ProductPerformanceRunPlan(
    int SchemaVersion,
    int Iterations,
    IReadOnlyList<ProductPerformanceRunCase> Cases)
{
    public const int CurrentSchemaVersion = 1;

    public static ProductPerformanceRunPlan Create(
        int iterations = 10,
        IEnumerable<ProductPerformanceFixture>? fixtures = null,
        IEnumerable<string>? routes = null)
    {
        if (iterations < 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                "At least ten iterations are required for cached traversal and no-blanking p95 samples.");
        }

        ProductPerformanceFixture[] selectedFixtures =
            (fixtures ?? Enum.GetValues<ProductPerformanceFixture>())
            .Distinct()
            .OrderBy(static fixture => fixture)
            .ToArray();
        if (selectedFixtures.Length == 0)
        {
            throw new ArgumentException("At least one performance fixture must be selected.", nameof(fixtures));
        }

        ProductPerformanceFixture[] invalidFixtures = selectedFixtures
            .Where(static fixture => !Enum.IsDefined(fixture))
            .ToArray();
        if (invalidFixtures.Length > 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixtures),
                $"Unknown performance fixture value(s): {string.Join(", ", invalidFixtures.Select(static fixture => (int)fixture))}.");
        }
        HashSet<string>? selectedRoutes = routes is null
            ? null
            : new HashSet<string>(routes, StringComparer.Ordinal);

        ProductPerformanceRouteDefinition[] routeCatalog = ProductPerformanceGate.Routes
            .Where(route => selectedRoutes is null || selectedRoutes.Contains(route.Id))
            .OrderBy(static route => route.Id, StringComparer.Ordinal)
            .ToArray();
        if (routeCatalog.Length == 0)
        {
            throw new ArgumentException("At least one canonical route must be selected.", nameof(routes));
        }

        if (selectedRoutes is not null)
        {
            string[] unknown = selectedRoutes
                .Except(routeCatalog.Select(static route => route.Id), StringComparer.Ordinal)
                .OrderBy(static route => route, StringComparer.Ordinal)
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new ArgumentException(
                    $"Unknown canonical route(s): {string.Join(", ", unknown)}.",
                    nameof(routes));
            }
        }

        List<ProductPerformanceRunCase> cases = [];
        foreach (ProductPerformanceFixture fixture in selectedFixtures)
        {
            foreach (ProductPerformanceRouteDefinition route in routeCatalog)
            {
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    string partition = fixture switch
                    {
                        ProductPerformanceFixture.Cold => $"cold/{route.Id}/{iteration:D2}",
                        ProductPerformanceFixture.Warm => "warm/shared",
                        ProductPerformanceFixture.Offline => "offline/shared",
                        ProductPerformanceFixture.LargeAccount => "large-account/shared",
                        _ => throw new ArgumentOutOfRangeException(nameof(fixture))
                    };

                    cases.Add(new ProductPerformanceRunCase(
                        fixture,
                        route,
                        iteration,
                        partition,
                        ResetCache: fixture == ProductPerformanceFixture.Cold,
                        DisableNetwork: fixture == ProductPerformanceFixture.Offline,
                        UseLargeAccountData: fixture == ProductPerformanceFixture.LargeAccount));
                }
            }
        }

        return new ProductPerformanceRunPlan(
            CurrentSchemaVersion,
            iterations,
            new ReadOnlyCollection<ProductPerformanceRunCase>(cases));
    }
}

public static class ProductPerformanceMeasurementRequirements
{
    public static IReadOnlyList<(string Route, ProductPerformanceFixture Fixture, ProductPerformanceMetric Metric, int MinimumSamples)>
        Get(ProductPerformanceRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<(string Route, ProductPerformanceFixture Fixture, ProductPerformanceMetric Metric, int MinimumSamples)>
            requirements = [];

        foreach (ProductPerformanceFixture fixture in plan.Cases.Select(static item => item.Fixture).Distinct())
        {
            ProductPerformanceBudget startup = ProductPerformanceBudgets.Get(
                fixture,
                ProductPerformanceMetric.StartupToInteractive);
            requirements.Add((ProductPerformanceGate.ApplicationRoute, fixture, startup.Metric, startup.MinimumSamples));

            foreach (ProductPerformanceRouteDefinition route in plan.Cases
                         .Where(item => item.Fixture == fixture)
                         .Select(static item => item.Route)
                         .DistinctBy(static route => route.Id))
            {
                Add(route.Id, fixture, ProductPerformanceMetric.RouteToFirstDataContent);
                Add(route.Id, fixture, ProductPerformanceMetric.RouteToSettledDataContent);
                Add(route.Id, fixture, ProductPerformanceMetric.ContentBlanking);
                Add(route.Id, fixture, ProductPerformanceMetric.DispatcherStall);
                Add(route.Id, fixture, ProductPerformanceMetric.WorkingSet);

                if (fixture != ProductPerformanceFixture.Cold && route.SupportsCachedSelection)
                {
                    Add(route.Id, fixture, ProductPerformanceMetric.CachedSelection);
                }

                if (fixture != ProductPerformanceFixture.Cold && route.SupportsCachedRouteNavigation)
                {
                    Add(route.Id, fixture, ProductPerformanceMetric.CachedRouteNavigation);
                }

                if (fixture != ProductPerformanceFixture.Cold && route.SupportsScroll)
                {
                    Add(route.Id, fixture, ProductPerformanceMetric.ScrollFrame);
                }
            }
        }

        return requirements
            .OrderBy(static item => item.Fixture)
            .ThenBy(static item => item.Route, StringComparer.Ordinal)
            .ThenBy(static item => item.Metric)
            .ToArray();

        void Add(string route, ProductPerformanceFixture fixture, ProductPerformanceMetric metric)
        {
            ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(fixture, metric);
            requirements.Add((route, fixture, metric, budget.MinimumSamples));
        }
    }
}
