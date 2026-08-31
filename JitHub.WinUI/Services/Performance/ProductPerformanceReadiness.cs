using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace JitHub.Services;

public sealed record ProductPerformanceRouteCommit(
    string Route,
    string Identity,
    long CommittedTimestamp,
    long? StartedTimestamp = null);

public sealed record ProductPerformanceTraversalStart(
    string Route,
    string Identity,
    string ExpectedDestinationRoute,
    long StartedTimestamp,
    long Generation = 0);

public sealed record ProductPerformanceTraversalStage(
    string Stage,
    long StartedTimestamp,
    long RecordedTimestamp,
    long Generation = 0);

public readonly record struct ProductPerformanceReadyStatus(
    string Route,
    string Identity,
    long? StartedTimestamp = null,
    long? FirstRenderedTimestamp = null,
    long? SettledTimestamp = null)
{
    public static bool TryParse(string? value, out ProductPerformanceReadyStatus status)
    {
        status = default;
        const string routeToken = "ready;route=";
        const string identityToken = ";identity=";
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(routeToken, StringComparison.Ordinal))
        {
            return false;
        }

        int identityIndex = value.IndexOf(identityToken, routeToken.Length, StringComparison.Ordinal);
        if (identityIndex <= routeToken.Length || identityIndex + identityToken.Length >= value.Length)
        {
            return false;
        }

        string[] metadata = value[..identityIndex].Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? route = metadata
            .FirstOrDefault(static part => part.StartsWith("route=", StringComparison.Ordinal))?["route=".Length..];
        long? startedTimestamp = ParseTimestamp(metadata, "started_ticks=");
        long? firstRenderedTimestamp = ParseTimestamp(metadata, "first_ticks=");
        long? settledTimestamp = ParseTimestamp(metadata, "settled_ticks=");
        string identity = value[(identityIndex + identityToken.Length)..];
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        status = new ProductPerformanceReadyStatus(
            route,
            identity,
            startedTimestamp,
            firstRenderedTimestamp,
            settledTimestamp);
        return true;
    }

    private static long? ParseTimestamp(IEnumerable<string> metadata, string prefix)
    {
        string? value = metadata.FirstOrDefault(part => part.StartsWith(prefix, StringComparison.Ordinal));
        return value is not null &&
            long.TryParse(value[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp) &&
            timestamp > 0
                ? timestamp
                : null;
    }
}

public static class ProductPerformanceReadiness
{
    private static long _applicationInteractiveTimestamp;
    private static long _traversalGeneration;
    private static TraversalContext? _activeTraversal;
    public const string ReadyPrefix = "ready";

    public static event EventHandler<ProductPerformanceRouteCommit>? RouteCommitted;

    public static event EventHandler<ProductPerformanceRouteCommit>? TraversalCommitted;

    public static event EventHandler<ProductPerformanceTraversalStart>? TraversalStarted;

    public static event EventHandler<ProductPerformanceTraversalStage>? TraversalStageRecorded;

    public static event EventHandler? TraversalMeasurementArmed;

    public static event EventHandler? TraversalMeasurementCompleted;

    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JITHUB_PERFORMANCE_FIXTURE"));

    public static long? ApplicationInteractiveTimestamp
    {
        get
        {
            long timestamp = Volatile.Read(ref _applicationInteractiveTimestamp);
            return timestamp > 0 ? timestamp : null;
        }
    }

    public static void CommitApplicationInteractive() =>
        Interlocked.CompareExchange(
            ref _applicationInteractiveTimestamp,
            Stopwatch.GetTimestamp(),
            comparand: 0);

    public static void CommitRoute(string route, string identity)
    {
        if (!IsEnabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        string normalizedIdentity = string.IsNullOrWhiteSpace(identity) ? "empty" : identity.Trim();
        RouteCommitted?.Invoke(
            null,
            new ProductPerformanceRouteCommit(
                route.Trim(),
                normalizedIdentity,
                Stopwatch.GetTimestamp()));
    }

    public static void CommitTraversal(string route, string identity, long? startedTimestamp = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        TraversalContext? traversal = Volatile.Read(ref _activeTraversal);
        if (traversal is not null &&
            (startedTimestamp is null || startedTimestamp == traversal.StartedTimestamp))
        {
            Interlocked.CompareExchange(ref _activeTraversal, null, traversal);
        }

        try
        {
            TraversalCommitted?.Invoke(
                null,
                new ProductPerformanceRouteCommit(
                    route.Trim(),
                    identity.Trim(),
                    Stopwatch.GetTimestamp(),
                    startedTimestamp ?? traversal?.StartedTimestamp));
        }
        finally
        {
            TraversalMeasurementCompleted?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void ArmTraversalMeasurement()
    {
        if (IsEnabled)
        {
            TraversalMeasurementArmed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void BeginTraversal(
        string route,
        string identity,
        string expectedDestinationRoute)
    {
        if (!IsEnabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDestinationRoute);
        long startedTimestamp = Stopwatch.GetTimestamp();
        long generation = Interlocked.Increment(ref _traversalGeneration);
        var traversal = new TraversalContext(startedTimestamp, generation);
        Interlocked.Exchange(ref _activeTraversal, traversal);
        TraversalStarted?.Invoke(
            null,
            new ProductPerformanceTraversalStart(
                route.Trim(),
                identity.Trim(),
                expectedDestinationRoute.Trim(),
                startedTimestamp,
                generation));
    }

    public static void CancelTraversal(long? startedTimestamp = null)
    {
        TraversalContext? traversal = Volatile.Read(ref _activeTraversal);
        if (traversal is null ||
            (startedTimestamp is not null && startedTimestamp != traversal.StartedTimestamp))
        {
            return;
        }

        if (ReferenceEquals(
                Interlocked.CompareExchange(ref _activeTraversal, null, traversal),
                traversal))
        {
            TraversalMeasurementCompleted?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void RecordTraversalStage(string stage)
    {
        if (!IsEnabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        TraversalContext? traversal = Volatile.Read(ref _activeTraversal);
        if (traversal is null)
        {
            return;
        }

        TraversalStageRecorded?.Invoke(
            null,
            new ProductPerformanceTraversalStage(
                stage.Trim(),
                traversal.StartedTimestamp,
                Stopwatch.GetTimestamp(),
                traversal.Generation));
    }

    public static string CountIdentity(int count) =>
        $"count={Math.Max(0, count).ToString(CultureInfo.InvariantCulture)}";

    public static string FormatStatus(string route, string identity) =>
        $"{ReadyPrefix};route={route};identity={identity}";

    public static string FormatStatus(
        string route,
        string identity,
        long startedTimestamp,
        long firstRenderedTimestamp,
        long? settledTimestamp)
    {
        string settled = settledTimestamp is long timestamp
            ? $";settled_ticks={timestamp.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        return $"{ReadyPrefix};route={route};started_ticks={startedTimestamp.ToString(CultureInfo.InvariantCulture)};" +
            $"first_ticks={firstRenderedTimestamp.ToString(CultureInfo.InvariantCulture)}" +
            $"{settled};identity={identity}";
    }

    private sealed record TraversalContext(long StartedTimestamp, long Generation);
}
