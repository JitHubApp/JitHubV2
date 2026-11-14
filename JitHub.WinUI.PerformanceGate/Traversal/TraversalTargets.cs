using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.Performance.Budgets;
using JitHub.Performance.Readiness;
using static JitHub.WinUI.PerformanceGate.Elements.RouteElements;
using static JitHub.WinUI.PerformanceGate.Routes.ProductPerformanceRouteProbe;
using static JitHub.WinUI.PerformanceGate.Routes.RouteProbeTimeouts;
using static JitHub.WinUI.PerformanceGate.Traversal.RepoCodeFixtureStatus;

namespace JitHub.WinUI.PerformanceGate.Traversal;

/// <summary>
/// Finds the item a cached traversal selects and waits for the app to report that exact traversal as ready.
/// </summary>
internal static class TraversalTargets
{
    internal static string GetExactTraversalIdentity(
        ProductPerformanceRouteDefinition route,
        AutomationElement target)
    {
        string automationId = target.AutomationId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(automationId))
        {
            return automationId;
        }

        string itemStatus = target.Properties.ItemStatus.ValueOrDefault?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(itemStatus))
        {
            return itemStatus;
        }

        throw new InvalidOperationException(
            $"Route '{route.Id}' traversal target did not expose a stable AutomationId or ItemStatus key.");
    }

    // This method stays whole because it is one polling loop on the traversal-ready marker that reacquires the root and
    // marker after transient UIA failures and returns only when the route, identity, and app timestamps line up, then
    // splits the measured time into input and render components from that same reading.
    internal static ProductPerformanceTraversalTiming WaitForExactTraversal(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route,
        AutomationElement marker,
        string expectedIdentity,
        long minimumStartedTimestamp,
        long interactionStartedTimestamp,
        Application application,
        UIA3Automation automation)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        string lastMarkerStatus = "<unread>";
        // The app stamps first-render and settled times itself. Give native input
        // an observer-free frame window before making a cross-process UIA call;
        // otherwise the probe can starve the UI thread whose latency it measures.
        Thread.Sleep(50);
        while (timeout.Elapsed < TransitionTimeout)
        {
            ProductPerformanceReadyStatus status;
            bool hasStatus;
            try
            {
                hasStatus = ProductPerformanceReadyStatus.TryParse(
                    marker.Properties.ItemStatus.ValueOrDefault,
                    out status);
                lastMarkerStatus = marker.Properties.ItemStatus.ValueOrDefault ?? "<empty>";
            }
            catch (COMException exception) when (
                IsTransientUiAutomationFailure(exception))
            {
                if (application.HasExited)
                {
                    throw new InvalidOperationException(
                        $"JitHub exited while measuring cached traversal for route '{route.Id}'.",
                        exception);
                }

                // WinUI can replace the root UIA provider together with a deferred content
                // subtree. Reacquire both providers by process identity before retrying.
                appRoot = WaitForAppRoot(application, automation);
                marker = FindVisibleForObservation(
                    appRoot,
                    $"ProductPerformanceTraversalReady_{route.Id}") ?? marker;
                Thread.Sleep(4);
                continue;
            }

            if (hasStatus &&
                string.Equals(status.Route, route.Id, StringComparison.Ordinal) &&
                string.Equals(status.Identity, expectedIdentity, StringComparison.Ordinal))
            {
                if (status.FirstRenderedTimestamp is long firstRenderedTimestamp &&
                    status.StartedTimestamp is long appStartedTimestamp &&
                    appStartedTimestamp >= minimumStartedTimestamp &&
                    appStartedTimestamp >= interactionStartedTimestamp &&
                    firstRenderedTimestamp >= appStartedTimestamp)
                {
                    AutomationElement trace = WaitForElement(appRoot, "ProductPerformanceTraversalTrace");
                    string traceStatus = trace.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
                    TimeSpan inputElapsed = Stopwatch.GetElapsedTime(
                        interactionStartedTimestamp,
                        appStartedTimestamp);
                    TimeSpan renderElapsed = Stopwatch.GetElapsedTime(
                        appStartedTimestamp,
                        firstRenderedTimestamp);
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(
                        interactionStartedTimestamp,
                        firstRenderedTimestamp);
                    Console.WriteLine(
                        $"  traversal trace {route.Id}: input=" +
                        $"{inputElapsed.TotalMilliseconds:0.##}, " +
                        $"render={renderElapsed.TotalMilliseconds:0.##}; " +
                        traceStatus);
                    return new ProductPerformanceTraversalTiming(
                        elapsed,
                        inputElapsed,
                        renderElapsed,
                        traceStatus);
                }
            }

            Thread.Sleep(4);
        }

        AutomationElement? timeoutTrace = FindVisibleForObservation(
            appRoot,
            "ProductPerformanceTraversalTrace");
        throw new TimeoutException(
            $"Cached traversal for route '{route.Id}' never committed exact identity '{expectedIdentity}'. " +
            $"Last marker: {lastMarkerStatus}. " +
            $"App trace: {timeoutTrace?.Properties.ItemStatus.ValueOrDefault ?? "unavailable"}");
    }

    internal static AutomationElement FindTraversalTarget(
        ProductPerformanceRouteDefinition route,
        AutomationElement selectionHost)
    {
        if (string.Equals(route.Id, "repo_code", StringComparison.Ordinal))
        {
            return FindRepoCodeSourceTraversalTarget(selectionHost);
        }

        if (route.Id is "gists" or "repo_pull_requests" or "repo_commits")
        {
            AutomationElement? secondVisibleItem = selectionHost
                .FindAllDescendants()
                .Where(static element => element.ControlType == ControlType.ListItem)
                .Where(IsVisible)
                .Skip(1)
                .FirstOrDefault();
            return secondVisibleItem ?? throw new InvalidOperationException(
                $"Route '{route.Id}' did not expose two cached rows for deterministic traversal.");
        }

        AutomationElement? target = FindUnselectedTraversalCandidates(selectionHost).FirstOrDefault();
        return target ?? throw new InvalidOperationException(
            $"Route '{route.Id}' did not expose an unselected cached item for traversal.");
    }

    private static AutomationElement FindRepoCodeSourceTraversalTarget(AutomationElement selectionHost)
    {
        AutomationElement? sourceFile = FindUnselectedTraversalCandidates(selectionHost)
            .FirstOrDefault(element =>
                element.ControlType == ControlType.TreeItem &&
                string.Equals(
                    element.Properties.ItemStatus.ValueOrDefault,
                    RepoCodeSourcePathStatus,
                    StringComparison.Ordinal));
        if (sourceFile is not null)
        {
            return sourceFile;
        }

        ExpandRepoCodeTreeItem(
            selectionHost,
            RepoCodeSourceDirectoryStatus,
            "Route 'repo_code' did not expose the deterministic src directory fixture.");
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            sourceFile = FindUnselectedTraversalCandidates(selectionHost)
                .FirstOrDefault(element =>
                    element.ControlType == ControlType.TreeItem &&
                    string.Equals(
                        element.Properties.ItemStatus.ValueOrDefault,
                        RepoCodeSourcePathStatus,
                        StringComparison.Ordinal));
            if (sourceFile is not null)
            {
                return sourceFile;
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException(
            "Route 'repo_code' did not realize the deterministic src/App.cs source fixture within 20 seconds.");
    }

    private static IEnumerable<AutomationElement> FindUnselectedTraversalCandidates(
        AutomationElement selectionHost) =>
        selectionHost
            .FindAllDescendants()
            .Where(static element =>
                element.ControlType is ControlType.ListItem or ControlType.TreeItem or ControlType.DataItem)
            .Where(IsVisible)
            .Where(static element => !IsSelected(element));

    internal static void ExpandRepoCodeTreeItem(
        AutomationElement scrollTarget,
        string itemStatus,
        string failureMessage)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            AutomationElement? item = scrollTarget
                .FindAllDescendants()
                .Where(static element => element.ControlType == ControlType.TreeItem)
                .Where(IsVisible)
                .FirstOrDefault(element => string.Equals(
                    element.Properties.ItemStatus.ValueOrDefault,
                    itemStatus,
                    StringComparison.Ordinal));
            if (item is null)
            {
                Thread.Sleep(5);
                continue;
            }

            if (item.Patterns.ExpandCollapse.IsSupported)
            {
                item.Patterns.ExpandCollapse.Pattern.Expand();
            }
            else
            {
                SelectOrInvoke(item);
            }

            return;
        }

        throw new TimeoutException(failureMessage);
    }

    internal static AutomationElement? FindTraversalElementByIdentity(
        AutomationElement selectionHost,
        string expectedIdentity)
    {
        foreach (AutomationElement candidate in selectionHost.FindAllDescendants())
        {
            try
            {
                if (candidate.ControlType is ControlType.ListItem or ControlType.TreeItem or ControlType.DataItem &&
                    IsVisible(candidate) &&
                    string.Equals(GetExactTraversalIdentityForComparison(candidate), expectedIdentity, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (COMException exception) when (IsTransientUiAutomationFailure(exception))
            {
            }
        }

        return null;
    }

    private static string GetExactTraversalIdentityForComparison(AutomationElement element)
    {
        string automationId = element.AutomationId?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(automationId)
            ? automationId
            : element.Properties.ItemStatus.ValueOrDefault?.Trim() ?? string.Empty;
    }

    internal sealed record ProductPerformanceTraversalTiming(
        TimeSpan Elapsed,
        TimeSpan Input,
        TimeSpan Render,
        string Trace);
}
