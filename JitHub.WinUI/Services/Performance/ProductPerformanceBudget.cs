using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JitHub.Services;

public enum ProductPerformanceFixture
{
    Cold,
    Warm,
    Offline,
    LargeAccount
}

public enum ProductPerformanceMetric
{
    StartupToInteractive,
    RouteToFirstDataContent,
    RouteToSettledDataContent,
    CachedSelection,
    CachedRouteNavigation,
    ContentBlanking,
    ScrollFrame,
    DispatcherStall,
    WorkingSet
}

public enum ProductPerformanceTraversalKind
{
    None,
    CachedInPlace,
    CachedCrossRoute
}

public sealed record ProductPerformanceBudget(
    ProductPerformanceFixture Fixture,
    ProductPerformanceMetric Metric,
    double Maximum,
    string Unit,
    int MinimumSamples = 3);

public sealed record ProductPerformanceMeasurement(
    ProductPerformanceFixture Fixture,
    ProductPerformanceMetric Metric,
    double Value,
    string Route,
    DateTimeOffset RecordedAt);

public sealed record ProductPerformanceEvaluation(
    ProductPerformanceBudget Budget,
    int SampleCount,
    double Median,
    double Percentile95,
    double MaximumObserved,
    bool Passed,
    string Detail);

public sealed record ProductPerformanceRouteDefinition(
    string Id,
    string LaunchPage,
    string RootAutomationId,
    string ReadyAutomationId,
    string? SelectionAutomationId,
    string? SelectionDestinationRootAutomationId,
    string? SelectionDestinationContentAutomationId,
    string? ScrollAutomationId,
    ProductPerformanceTraversalKind TraversalKind,
    bool SupportsScroll)
{
    public bool SupportsTraversal => TraversalKind != ProductPerformanceTraversalKind.None;

    public bool SupportsCachedSelection => TraversalKind == ProductPerformanceTraversalKind.CachedInPlace;

    public bool SupportsCachedRouteNavigation => TraversalKind == ProductPerformanceTraversalKind.CachedCrossRoute;
}

public sealed record ProductPerformanceRouteEvaluation(
    string Route,
    ProductPerformanceEvaluation Evaluation);

public sealed record ProductPerformanceGateResult(
    IReadOnlyList<ProductPerformanceRouteEvaluation> Evaluations)
{
    public bool Passed => Evaluations.Count > 0 && Evaluations.All(evaluation => evaluation.Evaluation.Passed);

    public IReadOnlyList<ProductPerformanceRouteEvaluation> Failures =>
        Evaluations.Where(static evaluation => !evaluation.Evaluation.Passed).ToArray();
}

public static class ProductPerformanceBudgets
{
    public const string Milliseconds = "ms";
    public const string Mebibytes = "MiB";

    private static readonly ReadOnlyCollection<ProductPerformanceBudget> AllBudgets =
        Array.AsReadOnly(
        new ProductPerformanceBudget[]
        {
            new(ProductPerformanceFixture.Cold, ProductPerformanceMetric.StartupToInteractive, 2_500, Milliseconds),
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.StartupToInteractive, 1_500, Milliseconds),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.StartupToInteractive, 1_500, Milliseconds),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.StartupToInteractive, 2_500, Milliseconds),

            new(ProductPerformanceFixture.Cold, ProductPerformanceMetric.RouteToSettledDataContent, 500, Milliseconds),
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.RouteToSettledDataContent, 150, Milliseconds),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.RouteToSettledDataContent, 150, Milliseconds),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.RouteToSettledDataContent, 150, Milliseconds),

            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.CachedSelection, 50, Milliseconds, MinimumSamples: 10),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.CachedSelection, 50, Milliseconds, MinimumSamples: 10),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.CachedSelection, 50, Milliseconds, MinimumSamples: 10),

            // Cross-workspace timing is stamped inside the app at the routed input
            // event, excluding UI Automation transport overhead from the metric.
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.CachedRouteNavigation, 150, Milliseconds, MinimumSamples: 10),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.CachedRouteNavigation, 150, Milliseconds, MinimumSamples: 10),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.CachedRouteNavigation, 150, Milliseconds, MinimumSamples: 10),

            new(ProductPerformanceFixture.Cold, ProductPerformanceMetric.RouteToFirstDataContent, 500, Milliseconds),
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.RouteToFirstDataContent, 150, Milliseconds),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.RouteToFirstDataContent, 150, Milliseconds),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.RouteToFirstDataContent, 150, Milliseconds),

            new(ProductPerformanceFixture.Cold, ProductPerformanceMetric.ContentBlanking, 0, "occurrences"),
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.ContentBlanking, 0, "occurrences", MinimumSamples: 10),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.ContentBlanking, 0, "occurrences", MinimumSamples: 10),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.ContentBlanking, 0, "occurrences", MinimumSamples: 10),

            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.ScrollFrame, 33.4, Milliseconds, MinimumSamples: 30),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.ScrollFrame, 33.4, Milliseconds, MinimumSamples: 30),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.ScrollFrame, 33.4, Milliseconds, MinimumSamples: 30),

            new(ProductPerformanceFixture.Cold, ProductPerformanceMetric.DispatcherStall, 100, Milliseconds, MinimumSamples: 10),
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.DispatcherStall, 50, Milliseconds, MinimumSamples: 30),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.DispatcherStall, 50, Milliseconds, MinimumSamples: 30),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.DispatcherStall, 75, Milliseconds, MinimumSamples: 30),

            new(ProductPerformanceFixture.Cold, ProductPerformanceMetric.WorkingSet, 768, Mebibytes),
            new(ProductPerformanceFixture.Warm, ProductPerformanceMetric.WorkingSet, 640, Mebibytes),
            new(ProductPerformanceFixture.Offline, ProductPerformanceMetric.WorkingSet, 640, Mebibytes),
            new(ProductPerformanceFixture.LargeAccount, ProductPerformanceMetric.WorkingSet, 896, Mebibytes)
        });

    public static IReadOnlyList<ProductPerformanceBudget> All => AllBudgets;

    public static ProductPerformanceBudget Get(
        ProductPerformanceFixture fixture,
        ProductPerformanceMetric metric) =>
        AllBudgets.Single(budget => budget.Fixture == fixture && budget.Metric == metric);

    public static ProductPerformanceEvaluation Evaluate(
        ProductPerformanceBudget budget,
        IEnumerable<ProductPerformanceMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(measurements);

        ProductPerformanceMeasurement[] matching = measurements
            .Where(measurement =>
                measurement.Fixture == budget.Fixture &&
                measurement.Metric == budget.Metric)
            .ToArray();

        ProductPerformanceMeasurement? invalid = matching.FirstOrDefault(static measurement =>
            !double.IsFinite(measurement.Value) || measurement.Value < 0);
        if (invalid is not null)
        {
            return new ProductPerformanceEvaluation(
                budget,
                matching.Length,
                Median: 0,
                Percentile95: 0,
                MaximumObserved: 0,
                Passed: false,
                Detail: "A measurement was negative or non-finite.");
        }

        double[] samples = matching
            .Select(static measurement => measurement.Value)
            .OrderBy(value => value)
            .ToArray();

        if (samples.Length < budget.MinimumSamples)
        {
            return new ProductPerformanceEvaluation(
                budget,
                samples.Length,
                Median: Percentile(samples, 0.5),
                Percentile95: Percentile(samples, 0.95),
                MaximumObserved: samples.Length == 0 ? 0 : samples[^1],
                Passed: false,
                Detail: $"Only {samples.Length} of {budget.MinimumSamples} required samples were recorded.");
        }

        double median = Percentile(samples, 0.5);
        double percentile95 = Percentile(samples, 0.95);
        double maximum = samples[^1];
        bool passed = percentile95 <= budget.Maximum;
        return new ProductPerformanceEvaluation(
            budget,
            samples.Length,
            median,
            percentile95,
            maximum,
            passed,
            passed
                ? $"p95 {percentile95:0.##} {budget.Unit} is within the {budget.Maximum:0.##} {budget.Unit} budget."
                : $"p95 {percentile95:0.##} {budget.Unit} exceeds the {budget.Maximum:0.##} {budget.Unit} budget.");
    }

    private static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
    {
        if (sortedSamples.Count == 0)
        {
            return 0;
        }

        if (sortedSamples.Count == 1)
        {
            return sortedSamples[0];
        }

        double position = (sortedSamples.Count - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedSamples[lower];
        }

        double fraction = position - lower;
        return sortedSamples[lower] + ((sortedSamples[upper] - sortedSamples[lower]) * fraction);
    }
}

public static class ProductPerformanceGate
{
    public const string ApplicationRoute = "app";

    private static readonly ReadOnlyCollection<ProductPerformanceRouteDefinition> CanonicalRoutes =
        Array.AsReadOnly(
        new ProductPerformanceRouteDefinition[]
        {
            new("home", "home", "DashboardMainRailScrollViewer", "ProductPerformanceRouteReady_home", null, null, null, "DashboardMainRailScrollViewer", ProductPerformanceTraversalKind.None, true),
            new("settings", "settings", "SettingsPageTitle", "ProductPerformanceRouteReady_settings", null, null, null, null, ProductPerformanceTraversalKind.None, false),
            new("profile", "profile", "ProfileModeSelector", "ProductPerformanceRouteReady_profile", null, null, null, null, ProductPerformanceTraversalKind.None, false),
            new("my_issues", "my-issues", "MyIssuesList", "ProductPerformanceRouteReady_my_issues", "MyIssuesList", "MyIssuesList", "MyIssuesDetailHost", "MyIssuesList", ProductPerformanceTraversalKind.CachedInPlace, true),
            new("my_pull_requests", "my-pull-requests", "MyPullRequestsList", "ProductPerformanceRouteReady_my_pull_requests", "MyPullRequestsList", "MyPullRequestsList", "MyPullRequestsDetailHost", "MyPullRequestsList", ProductPerformanceTraversalKind.CachedInPlace, true),
            new("stars", "stars", "StarsList", "ProductPerformanceRouteReady_stars", "StarsList", "RepoCodeAdaptiveWorkspace", "RepoCodeFileTree", "StarsList", ProductPerformanceTraversalKind.CachedCrossRoute, true),
            new("gists", "gists", "GistsList", "ProductPerformanceRouteReady_gists", "GistsList", "GistsList", "GistsDetailPane", "GistsList", ProductPerformanceTraversalKind.CachedInPlace, true),
            new("notifications", "notifications", "NotificationsList", "ProductPerformanceRouteReady_notifications", "NotificationsList", "ShellRoot", "ShellContentFrame", "NotificationsList", ProductPerformanceTraversalKind.CachedCrossRoute, true),
            new("repo_manage", "repositories", "RepositoryLibraryList", "ProductPerformanceRouteReady_repo_manage", "RepositoryLibraryList", "RepoCodeAdaptiveWorkspace", "RepoCodeFileTree", "RepositoryLibraryList", ProductPerformanceTraversalKind.CachedCrossRoute, true),
            new("repo_search", "home", "RepoSearchResultsList", "ProductPerformanceRouteReady_repo_search", "RepoSearchResultsList", "RepoCodeAdaptiveWorkspace", "RepoCodeFileTree", "RepoSearchResultsList", ProductPerformanceTraversalKind.CachedCrossRoute, true),
            new("repo_code", "repo-code", "RepoCodeAdaptiveWorkspace", "ProductPerformanceRouteReady_repo_code", "RepoCodeFileTreeHost", "RepoCodeAdaptiveWorkspace", "RepoCodeEditor", "RepoCodeFileTreeHost", ProductPerformanceTraversalKind.CachedInPlace, true),
            new("repo_issues", "repo-issues", "RepoIssuesList", "ProductPerformanceRouteReady_repo_issues", "RepoIssuesList", "RepoIssuesList", "RepoIssuesDetailHost", "RepoIssuesList", ProductPerformanceTraversalKind.CachedInPlace, true),
            new("repo_pull_requests", "repo-pull-requests", "RepoPullRequestsList", "ProductPerformanceRouteReady_repo_pull_requests", "RepoPullRequestsList", "RepoPullRequestsList", "RepoPullRequestsDetailHost", "RepoPullRequestsContentScrollViewer", ProductPerformanceTraversalKind.CachedInPlace, true),
            new("repo_commits", "repo-commits", "RepoCommitsList", "ProductPerformanceRouteReady_repo_commits", "RepoCommitsList", "RepoCommitsList", "RepoCommitsDetailHost", "RepoCommitsDiffViewer", ProductPerformanceTraversalKind.CachedInPlace, true)
        });

    public static IReadOnlyList<ProductPerformanceRouteDefinition> Routes => CanonicalRoutes;

    public static ProductPerformanceGateResult Evaluate(
        IEnumerable<ProductPerformanceMeasurement> measurements,
        IEnumerable<ProductPerformanceFixture>? fixtures = null,
        IEnumerable<string>? routes = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ProductPerformanceMeasurement[] samples = measurements.ToArray();
        ProductPerformanceFixture[] fixtureSet = (fixtures ?? Enum.GetValues<ProductPerformanceFixture>()).Distinct().ToArray();
        HashSet<string>? requestedRoutes = routes is null
            ? null
            : new HashSet<string>(routes, StringComparer.Ordinal);
        ProductPerformanceRouteDefinition[] routeSet = CanonicalRoutes
            .Where(route => requestedRoutes is null || requestedRoutes.Contains(route.Id))
            .ToArray();
        if (routeSet.Length == 0)
        {
            throw new ArgumentException("At least one canonical route must be evaluated.", nameof(routes));
        }

        if (requestedRoutes is not null)
        {
            string[] unknown = requestedRoutes
                .Except(routeSet.Select(static route => route.Id), StringComparer.Ordinal)
                .OrderBy(static route => route, StringComparer.Ordinal)
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new ArgumentException($"Unknown canonical route(s): {string.Join(", ", unknown)}.", nameof(routes));
            }
        }
        List<ProductPerformanceRouteEvaluation> evaluations = [];

        foreach (ProductPerformanceFixture fixture in fixtureSet)
        {
            EvaluateRouteMetric(
                evaluations,
                samples,
                ApplicationRoute,
                fixture,
                ProductPerformanceMetric.StartupToInteractive);

            foreach (ProductPerformanceRouteDefinition route in routeSet)
            {
                EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.RouteToFirstDataContent);
                EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.RouteToSettledDataContent);
                EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.ContentBlanking);
                EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.DispatcherStall);
                EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.WorkingSet);

                if (fixture != ProductPerformanceFixture.Cold && route.SupportsCachedSelection)
                {
                    EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.CachedSelection);
                }

                if (fixture != ProductPerformanceFixture.Cold && route.SupportsCachedRouteNavigation)
                {
                    EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.CachedRouteNavigation);
                }

                if (fixture != ProductPerformanceFixture.Cold && route.SupportsScroll)
                {
                    EvaluateRouteMetric(evaluations, samples, route.Id, fixture, ProductPerformanceMetric.ScrollFrame);
                }
            }
        }

        return new ProductPerformanceGateResult(evaluations.AsReadOnly());
    }

    private static void EvaluateRouteMetric(
        ICollection<ProductPerformanceRouteEvaluation> evaluations,
        IReadOnlyCollection<ProductPerformanceMeasurement> samples,
        string route,
        ProductPerformanceFixture fixture,
        ProductPerformanceMetric metric)
    {
        ProductPerformanceBudget budget = ProductPerformanceBudgets.Get(fixture, metric);
        ProductPerformanceEvaluation evaluation = ProductPerformanceBudgets.Evaluate(
            budget,
            samples.Where(sample => string.Equals(sample.Route, route, StringComparison.Ordinal)));
        evaluations.Add(new ProductPerformanceRouteEvaluation(route, evaluation));
    }
}
