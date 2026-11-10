using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using JitHub.Performance.Budgets;
using JitHub.Performance.Observation;
using JitHub.Performance.Readiness;
using static JitHub.WinUI.PerformanceGate.Elements.RouteElements;
using static JitHub.WinUI.PerformanceGate.Routes.RouteProbeTimeouts;

namespace JitHub.WinUI.PerformanceGate.Routes;

/// <summary>
/// Observes route content continuity and the app heartbeat while a route transition settles.
/// </summary>
internal static class RouteContentObservation
{
    internal static void WaitForRouteTransition(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route,
        ProductPerformanceContentTransitionTracker tracker,
        string description)
    {
        AutomationElement? routeRoot = null;
        AutomationElement? marker = null;
        ProductPerformanceContentObservation? lastObservation = null;
        Stopwatch timeout = Stopwatch.StartNew();
        while (!tracker.IsSettled && timeout.Elapsed < TransitionTimeout)
        {
            long observedTimestamp = Stopwatch.GetTimestamp();
            lastObservation = CaptureContentObservationCached(
                appRoot,
                route.RootAutomationId,
                route.ReadyAutomationId,
                ref routeRoot,
                ref marker);
            tracker.Observe(lastObservation, observedTimestamp);
            if (!tracker.IsSettled)
            {
                // Continuous cross-process UIA reads can delay the app's queued
                // settled marker, so leave an observer-free dispatcher window.
                Thread.Sleep(8);
            }
        }

        if (!tracker.IsSettled)
        {
            routeRoot = FindVisible(appRoot, route.RootAutomationId);
            marker = FindVisible(appRoot, route.ReadyAutomationId);
            string markerStatus = marker?.Properties.ItemStatus.ValueOrDefault ?? "<missing>";
            string observation = lastObservation is null
                ? "<none>"
                : $"identity='{lastObservation.Identity}', visible={lastObservation.IsVisible}, " +
                    $"busy={lastObservation.IsBusy}, frame={lastObservation.Heartbeat.Frame}, " +
                    $"first={lastObservation.FirstRenderedTimestamp?.ToString() ?? "<none>"}, " +
                    $"settled={lastObservation.SettledTimestamp?.ToString() ?? "<none>"}";
            throw new TimeoutException(
                $"The {description} did not commit route-specific data for three stable rendered frames. " +
                $"RootVisible={routeRoot is not null}; MarkerVisible={marker is not null}; " +
                $"MarkerStatus='{markerStatus}'; LastObservation={observation}.");
        }
    }

    internal static ProductPerformanceContentTransitionTracker CreateContinuityTracker(
        AutomationElement appRoot,
        string routeRootAutomationId,
        string contentAutomationId)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        ProductPerformanceContentTransitionTracker tracker = new(startedTimestamp);
        ObserveContinuity(
            appRoot,
            routeRootAutomationId,
            contentAutomationId,
            tracker,
            startedTimestamp);
        return tracker;
    }

    internal static void ObserveContinuity(
        AutomationElement appRoot,
        string routeRootAutomationId,
        string contentAutomationId,
        ProductPerformanceContentTransitionTracker tracker,
        long? observedTimestamp = null) =>
        tracker.Observe(
            CaptureContentObservation(
                appRoot,
                routeRootAutomationId,
                contentAutomationId),
            observedTimestamp ?? Stopwatch.GetTimestamp());

    private static ProductPerformanceContentObservation CaptureContentObservation(
        AutomationElement appRoot,
        string routeRootAutomationId,
        string contentAutomationId)
    {
        ProductPerformanceHeartbeat heartbeat = ReadHeartbeat(appRoot);
        AutomationElement? routeRoot = FindVisibleForObservation(appRoot, routeRootAutomationId);
        AutomationElement? marker = FindVisibleForObservation(appRoot, contentAutomationId);
        if (routeRoot is null || marker is null ||
            !ProductPerformanceReadyStatus.TryParse(
                marker.Properties.ItemStatus.ValueOrDefault,
                out ProductPerformanceReadyStatus status))
        {
            return new ProductPerformanceContentObservation(string.Empty, 0, false, false, heartbeat);
        }

        const string markerPrefix = "ProductPerformanceRouteReady_";
        string expectedRoute = contentAutomationId.StartsWith(markerPrefix, StringComparison.Ordinal)
            ? contentAutomationId[markerPrefix.Length..]
            : string.Empty;
        if (!string.Equals(status.Route, expectedRoute, StringComparison.Ordinal))
        {
            return new ProductPerformanceContentObservation(string.Empty, 0, false, false, heartbeat);
        }

        return new ProductPerformanceContentObservation(
            status.Identity,
            MeaningfulElementCount: 1,
            IsVisible: true,
            IsBusy: status.FirstRenderedTimestamp is not null && status.SettledTimestamp is null,
            heartbeat,
            status.StartedTimestamp,
            status.FirstRenderedTimestamp,
            status.SettledTimestamp);
    }

    private static ProductPerformanceContentObservation CaptureContentObservationCached(
        AutomationElement appRoot,
        string routeRootAutomationId,
        string contentAutomationId,
        ref AutomationElement? routeRoot,
        ref AutomationElement? marker)
    {
        ProductPerformanceHeartbeat heartbeat = ReadHeartbeat(appRoot);
        routeRoot ??= FindVisibleForObservation(appRoot, routeRootAutomationId);
        marker ??= FindVisibleForObservation(appRoot, contentAutomationId);
        if (routeRoot is null || marker is null)
        {
            return new ProductPerformanceContentObservation(string.Empty, 0, false, false, heartbeat);
        }

        ProductPerformanceReadyStatus status;
        try
        {
            if (!ProductPerformanceReadyStatus.TryParse(
                    marker.Properties.ItemStatus.ValueOrDefault,
                    out status))
            {
                return new ProductPerformanceContentObservation(string.Empty, 0, false, false, heartbeat);
            }
        }
        catch (COMException)
        {
            routeRoot = null;
            marker = null;
            return new ProductPerformanceContentObservation(string.Empty, 0, false, false, heartbeat);
        }

        const string markerPrefix = "ProductPerformanceRouteReady_";
        string expectedRoute = contentAutomationId.StartsWith(markerPrefix, StringComparison.Ordinal)
            ? contentAutomationId[markerPrefix.Length..]
            : string.Empty;
        if (!string.Equals(status.Route, expectedRoute, StringComparison.Ordinal))
        {
            return new ProductPerformanceContentObservation(string.Empty, 0, false, false, heartbeat);
        }

        return new ProductPerformanceContentObservation(
            status.Identity,
            MeaningfulElementCount: 1,
            IsVisible: true,
            IsBusy: status.FirstRenderedTimestamp is not null && status.SettledTimestamp is null,
            heartbeat,
            status.StartedTimestamp,
            status.FirstRenderedTimestamp,
            status.SettledTimestamp);
    }

    internal static ProductPerformanceHeartbeat ReadHeartbeat(AutomationElement appRoot)
    {
        string? itemStatus = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                itemStatus = appRoot.Properties.ItemStatus.ValueOrDefault;
                if (ProductPerformanceHeartbeat.TryParse(itemStatus, out ProductPerformanceHeartbeat heartbeat))
                {
                    return heartbeat;
                }
            }
            catch (COMException exception) when (
                IsTransientUiAutomationFailure(exception))
            {
            }

            Thread.Sleep(4);
        }

        throw new InvalidOperationException(
            $"The product performance heartbeat was unavailable or malformed: '{itemStatus ?? "<null>"}'.");
    }
}
