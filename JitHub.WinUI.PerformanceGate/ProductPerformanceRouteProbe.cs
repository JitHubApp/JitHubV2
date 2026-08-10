using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.Services;

internal sealed class ProductPerformanceRouteProbe
{
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ObservableTimeout = TimeSpan.FromSeconds(2);
    private const string AppRootAutomationId = "JitHubMainWindowRoot";
    private const string RepoCodeSourceDirectoryStatus = "path:src";
    private const string RepoCodeGeneratedDirectoryStatus = "path:src/generated";
    private const string RepoCodeSourcePathStatus = "path:src/App.cs";
    private readonly string _appPath;
    private readonly string _dataRoot;
    private readonly string _repository;

    public ProductPerformanceRouteProbe(string appPath, string dataRoot, string repository)
    {
        _appPath = Path.GetFullPath(appPath);
        _dataRoot = Path.GetFullPath(dataRoot);
        _repository = repository;
    }

    public IReadOnlyList<ProductPerformanceMeasurement> Run(ProductPerformanceRunCase runCase)
    {
        string partitionRoot = Path.Combine(
            _dataRoot,
            runCase.DataPartition.Replace('/', Path.DirectorySeparatorChar));
        if (runCase.ResetCache && Directory.Exists(partitionRoot))
        {
            Directory.Delete(partitionRoot, recursive: true);
        }

        Directory.CreateDirectory(partitionRoot);
        ProcessStartInfo startInfo = CreateStartInfo(runCase, partitionRoot);
        long startupStartedTimestamp = Stopwatch.GetTimestamp();
        using Application application = Application.Launch(startInfo);
        using UIA3Automation automation = new();

        try
        {
            _ = WaitForWindow(application, automation);
            AutomationElement appRoot = WaitForAppRoot(application, automation);
            _ = WaitForElement(appRoot, "ShellRoot");
            TimeSpan startupElapsed = WaitForInteractiveTimestamp(appRoot, startupStartedTimestamp);

            // Route timing is deliberately armed only after the existing shell is interactive.
            // Waiting for Home here is an untimed precondition, not part of startup or target navigation.
            WaitForRouteReady(appRoot, ProductPerformanceGate.Routes.Single(static route => route.Id == "home"));
            if (runCase.Fixture != ProductPerformanceFixture.Cold &&
                !string.Equals(runCase.Route.Id, "home", StringComparison.Ordinal))
            {
                _ = NavigateRouteAndWait(appRoot, runCase.Route);
                NavigateRouteAndWait(
                    appRoot,
                    ProductPerformanceGate.Routes.Single(route =>
                        route.Id == (string.Equals(runCase.Route.Id, "settings", StringComparison.Ordinal)
                            ? "home"
                            : "settings")));
            }
            else if (string.Equals(runCase.Route.Id, "home", StringComparison.Ordinal))
            {
                NavigateRouteAndWait(
                    appRoot,
                    ProductPerformanceGate.Routes.Single(static route => route.Id == "settings"));
            }

            ProductPerformanceContentTransitionTracker routeTransition = NavigateRouteAndWait(
                appRoot,
                runCase.Route);

            DateTimeOffset recordedAt = DateTimeOffset.UtcNow;
            List<ProductPerformanceMeasurement> measurements =
            [
                Measure(runCase, ProductPerformanceGate.ApplicationRoute, ProductPerformanceMetric.StartupToInteractive, startupElapsed.TotalMilliseconds, recordedAt),
                Measure(runCase, runCase.Route.Id, ProductPerformanceMetric.RouteToFirstDataContent, routeTransition.FirstDataContent!.Value.TotalMilliseconds, recordedAt),
                Measure(runCase, runCase.Route.Id, ProductPerformanceMetric.RouteToSettledDataContent, routeTransition.SettledDataContent!.Value.TotalMilliseconds, recordedAt)
            ];

            int blankingOccurrences = routeTransition.BlankingFrameCount;
            ProductPerformanceContentTransitionTracker continuity = CreateContinuityTracker(
                appRoot,
                runCase.Route.RootAutomationId,
                runCase.Route.ReadyAutomationId);
            _ = WaitForElement(appRoot, runCase.Route.RootAutomationId);
            bool measureSelectionBeforeScroll =
                runCase.Fixture != ProductPerformanceFixture.Cold &&
                runCase.Route.SupportsTraversal &&
                string.Equals(runCase.Route.Id, "repo_code", StringComparison.Ordinal);
            if (measureSelectionBeforeScroll)
            {
                (ProductPerformanceMeasurement selectionMeasurement, int selectionBlanking) =
                    MeasureCachedSelection(runCase, appRoot);
                measurements.Add(selectionMeasurement);
                blankingOccurrences += selectionBlanking;
            }

            measurements.AddRange(MeasureDispatcherAndScroll(runCase, appRoot, continuity));
            if (runCase.Fixture != ProductPerformanceFixture.Cold &&
                runCase.Route.SupportsTraversal &&
                !measureSelectionBeforeScroll)
            {
                (ProductPerformanceMeasurement selectionMeasurement, int selectionBlanking) =
                    MeasureCachedSelection(runCase, appRoot);
                measurements.Add(selectionMeasurement);
                blankingOccurrences += selectionBlanking;
            }

            blankingOccurrences += continuity.BlankingFrameCount;
            measurements.Add(Measure(
                runCase,
                runCase.Route.Id,
                ProductPerformanceMetric.ContentBlanking,
                blankingOccurrences,
                DateTimeOffset.UtcNow));

            using Process process = Process.GetProcessById(application.ProcessId);
            process.Refresh();
            measurements.Add(Measure(
                runCase,
                runCase.Route.Id,
                ProductPerformanceMetric.WorkingSet,
                process.WorkingSet64 / (1024d * 1024d),
                DateTimeOffset.UtcNow));

            return measurements;
        }
        finally
        {
            CloseApplication(application);
        }
    }

    private ProcessStartInfo CreateStartInfo(ProductPerformanceRunCase runCase, string partitionRoot)
    {
        ProcessStartInfo info = new(_appPath)
        {
            WorkingDirectory = Path.GetDirectoryName(_appPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--page=home");
        info.ArgumentList.Add("--theme=dark");
        info.ArgumentList.Add($"--repo={_repository}");
        info.Environment["JITHUB_PREVIEW_REPOSITORY"] = _repository;

        info.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = partitionRoot;
        info.Environment["JITHUB_PREVIEW_PAGE"] = "home";
        info.Environment["JITHUB_PREVIEW_THEME"] = "dark";
        info.Environment["JITHUB_PERFORMANCE_FIXTURE"] = runCase.Fixture.ToString();
        info.Environment["JITHUB_PREVIEW_SCENARIO"] = runCase.UseLargeAccountData
            ? "performance-large-account"
            : "performance-route";
        if (runCase.UseLargeAccountData)
        {
            info.Environment["JITHUB_AUTOMATION_LARGE_COMMIT"] = "1";
        }

        if (runCase.DisableNetwork)
        {
            info.Environment["HTTP_PROXY"] = "http://127.0.0.1:9";
            info.Environment["HTTPS_PROXY"] = "http://127.0.0.1:9";
            info.Environment["ALL_PROXY"] = "http://127.0.0.1:9";
            info.Environment["NO_PROXY"] = string.Empty;
        }

        return info;
    }

    private static ProductPerformanceMeasurement Measure(
        ProductPerformanceRunCase runCase,
        string route,
        ProductPerformanceMetric metric,
        double value,
        DateTimeOffset recordedAt) =>
        new(runCase.Fixture, metric, value, route, recordedAt);

    private static Window WaitForWindow(Application application, UIA3Automation automation)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            if (application.HasExited)
            {
                throw new InvalidOperationException(
                    "The JitHub process exited before its main window became interactive.");
            }

            try
            {
                Window? window = application.GetMainWindow(automation, TimeSpan.FromMilliseconds(500));
                if (window is not null && IsVisible(window))
                {
                    return window;
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (COMException exception) when (
                exception.HResult == unchecked((int)0x80040201))
            {
                // A startup element can disappear between enumeration and property access.
                // Retry the fresh desktop tree instead of failing the entire benchmark run.
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The JitHub window did not become interactive within 20 seconds.");
    }

    private static AutomationElement WaitForElement(AutomationElement root, string automationId)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            AutomationElement? element = FindVisible(root, automationId);
            if (element is not null)
            {
                return element;
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException($"Automation element '{automationId}' did not become visible within 20 seconds.");
    }

    private static AutomationElement WaitForAppRoot(
        Application application,
        UIA3Automation automation)
    {
        AutomationElement desktop = automation.GetDesktop();
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            if (application.HasExited)
            {
                throw new InvalidOperationException(
                    "The JitHub process exited before its application root became interactive.");
            }

            try
            {
                AutomationElement? appRoot = desktop
                    .FindAllDescendants(condition => condition.ByAutomationId(AppRootAutomationId))
                    .FirstOrDefault(candidate =>
                        candidate.Properties.ProcessId.ValueOrDefault == application.ProcessId &&
                        IsVisible(candidate));
                if (appRoot is not null)
                {
                    return appRoot;
                }
            }
            catch (InvalidOperationException)
            {
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException(
            $"Automation element '{AppRootAutomationId}' for process {application.ProcessId} " +
            "did not become visible within 20 seconds.");
    }

    private static ProductPerformanceContentTransitionTracker NavigateRouteAndWait(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route)
    {
        AutomationElement routeInput = WaitForElement(appRoot, "ProductPerformanceRouteInput");
        CommitTextValue(routeInput, route.Id, "performance route");
        AutomationElement navigate = WaitForElement(appRoot, "ProductPerformanceNavigateButton");
        long routeStartedTimestamp = Stopwatch.GetTimestamp();
        ProductPerformanceContentTransitionTracker transition =
            new(routeStartedTimestamp, requiredStableFrames: 3);
        SelectOrInvoke(navigate);
        WaitForRouteTransition(appRoot, route, transition, $"route '{route.Id}'");
        return transition;
    }

    private static void WaitForRouteReady(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        ProductPerformanceContentTransitionTracker transition =
            new(startedTimestamp, requiredStableFrames: 3);
        WaitForRouteTransition(appRoot, route, transition, $"route precondition '{route.Id}'");
    }

    private static (ProductPerformanceMeasurement Measurement, int BlankingOccurrences) MeasureCachedSelection(
        ProductPerformanceRunCase runCase,
        AutomationElement appRoot)
    {
        ProductPerformanceRouteDefinition route = runCase.Route;
        AutomationElement selectionHost = WaitForElement(appRoot, route.SelectionAutomationId!);
        AutomationElement target = FindTraversalTarget(route, selectionHost);

        string expectedIdentity = GetExactTraversalIdentity(route, target);
        AutomationElement routeInput = WaitForElement(appRoot, "ProductPerformanceRouteInput");
        CommitTextValue(routeInput, route.Id, "performance traversal route");
        AutomationElement traversalInput = WaitForElement(appRoot, "ProductPerformanceTraversalInput");
        CommitTextValue(traversalInput, expectedIdentity, "performance traversal identity");
        SelectOrInvoke(WaitForElement(appRoot, "ProductPerformanceArmTraversalButton"));
        AutomationElement traversalMarker = WaitForElement(
            appRoot,
            $"ProductPerformanceTraversalReady_{route.Id}");
        Action activateTraversalTarget = PrepareTraversalActivation(target);
        long selectionStartedTimestamp = Stopwatch.GetTimestamp();
        activateTraversalTarget();
        TimeSpan selectionElapsed = WaitForExactTraversal(
            appRoot,
            route,
            traversalMarker,
            expectedIdentity,
            selectionStartedTimestamp);

        return (
            Measure(
                runCase,
                route.Id,
                route.SupportsCachedSelection
                    ? ProductPerformanceMetric.CachedSelection
                    : ProductPerformanceMetric.CachedRouteNavigation,
                selectionElapsed.TotalMilliseconds,
                DateTimeOffset.UtcNow),
            0);
    }

    private static string GetExactTraversalIdentity(
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

    private static TimeSpan WaitForExactTraversal(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route,
        AutomationElement marker,
        string expectedIdentity,
        long startedTimestamp)
    {
        long lastFrame = -1;
        int stableFrames = 0;
        TimeSpan? firstRenderedMatch = null;
        Stopwatch timeout = Stopwatch.StartNew();
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
            }
            catch (COMException exception) when (
                IsTransientUiAutomationFailure(exception))
            {
                // A deferred WinUI subtree can briefly replace its UIA provider. The marker is
                // stable in the visual tree, so reacquire its provider and retry next frame.
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
                    appStartedTimestamp >= startedTimestamp &&
                    firstRenderedTimestamp >= appStartedTimestamp)
                {
                    AutomationElement trace = WaitForElement(appRoot, "ProductPerformanceTraversalTrace");
                    Console.WriteLine(
                        $"  traversal trace {route.Id}: {trace.Properties.ItemStatus.ValueOrDefault}");
                    return Stopwatch.GetElapsedTime(appStartedTimestamp, firstRenderedTimestamp);
                }

                ProductPerformanceHeartbeat heartbeat = ReadHeartbeat(appRoot);
                if (heartbeat.Frame > lastFrame)
                {
                    stableFrames++;
                    lastFrame = heartbeat.Frame;
                    firstRenderedMatch ??= Stopwatch.GetElapsedTime(startedTimestamp);
                }

                if (stableFrames >= 2)
                {
                    return firstRenderedMatch ?? Stopwatch.GetElapsedTime(startedTimestamp);
                }
            }
            else
            {
                stableFrames = 0;
                lastFrame = -1;
            }

            Thread.Sleep(4);
        }

        throw new TimeoutException(
            $"Cached traversal for route '{route.Id}' never committed exact identity '{expectedIdentity}'.");
    }

    private static AutomationElement FindTraversalTarget(
        ProductPerformanceRouteDefinition route,
        AutomationElement selectionHost)
    {
        if (string.Equals(route.Id, "repo_code", StringComparison.Ordinal))
        {
            return FindRepoCodeSourceTraversalTarget(selectionHost);
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

    private static IEnumerable<ProductPerformanceMeasurement> MeasureDispatcherAndScroll(
        ProductPerformanceRunCase runCase,
        AutomationElement appRoot,
        ProductPerformanceContentTransitionTracker continuity)
    {
        List<ProductPerformanceMeasurement> measurements = [];
        AutomationElement? scrollElement = null;
        if (runCase.Fixture != ProductPerformanceFixture.Cold && runCase.Route.SupportsScroll)
        {
            AutomationElement scrollTarget = WaitForElement(appRoot, runCase.Route.ScrollAutomationId!);
            PrepareScrollableSurface(runCase.Route, scrollTarget);
            scrollElement = FindVerticallyScrollableElement(scrollTarget)
                ?? throw new InvalidOperationException(
                    $"Route '{runCase.Route.Id}' did not expose a vertically scrollable UIA element under " +
                    $"'{runCase.Route.ScrollAutomationId}' for its {runCase.Fixture} fixture.");
        }

        for (int sample = 0; sample < 30; sample++)
        {
            ProductPerformanceHeartbeat initialHeartbeat = ReadHeartbeat(appRoot);
            TimeSpan dispatcherElapsed = WaitForHeartbeatAdvance(
                appRoot,
                initialHeartbeat,
                requireFrameAdvance: true,
                continuity,
                runCase.Route.RootAutomationId,
                runCase.Route.ReadyAutomationId,
                ObservableTimeout);
            measurements.Add(Measure(
                runCase,
                runCase.Route.Id,
                ProductPerformanceMetric.DispatcherStall,
                dispatcherElapsed.TotalMilliseconds,
                DateTimeOffset.UtcNow));

            if (scrollElement is not null)
            {
                scrollElement = ResolveVerticalScrollElement(appRoot, runCase.Route);
                double initialOffset = ReadVerticalScrollPercent(appRoot, runCase.Route, ref scrollElement);
                ProductPerformanceHeartbeat initialScrollHeartbeat = ReadHeartbeat(appRoot);
                ProductPerformanceScrollStatus? initialScrollStatus = ReadScrollStatus(appRoot, runCase.Route);
                bool hasAppScrollProbe = initialScrollStatus is not null;
                long initialScrollSequence = initialScrollStatus?.Sequence ?? 0;
                // Exercise rendered scrolling without driving virtualized lists into pagination.
                ScrollAmount amount = initialOffset >= 10
                    ? ScrollAmount.SmallDecrement
                    : ScrollAmount.SmallIncrement;
                long scrollStartedTimestamp = Stopwatch.GetTimestamp();
                ScrollVertically(appRoot, runCase.Route, ref scrollElement, amount);
                ProductPerformanceScrollTransitionTracker scrollTransition =
                    new(scrollStartedTimestamp, initialOffset, initialScrollHeartbeat.Frame);

                Stopwatch timeout = Stopwatch.StartNew();
                Stopwatch attemptTimeout = Stopwatch.StartNew();
                while (!scrollTransition.IsCompleted && timeout.Elapsed < ObservableTimeout)
                {
                    ProductPerformanceHeartbeat currentHeartbeat = ReadHeartbeat(appRoot);
                    if (currentHeartbeat.Frame <= initialScrollHeartbeat.Frame)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    long observedTimestamp = Stopwatch.GetTimestamp();
                    ProductPerformanceScrollStatus? scrollStatus = ReadScrollStatus(appRoot, runCase.Route);
                    if (scrollStatus is ProductPerformanceScrollStatus renderedScroll &&
                        renderedScroll.Sequence > initialScrollSequence &&
                        renderedScroll.StartedTimestamp >= scrollStartedTimestamp)
                    {
                        scrollTransition.ObserveRenderedInterval(
                            renderedScroll.StartedTimestamp,
                            renderedScroll.RenderedTimestamp);
                    }

                    if (!hasAppScrollProbe)
                    {
                        double currentOffset = ReadVerticalScrollPercent(appRoot, runCase.Route, ref scrollElement);
                        scrollTransition.Observe(currentOffset, currentHeartbeat, observedTimestamp);
                    }
                    ObserveContinuity(
                        appRoot,
                        runCase.Route.RootAutomationId,
                        runCase.Route.ReadyAutomationId,
                        continuity);
                    if (!scrollTransition.IsCompleted)
                    {
                        if (attemptTimeout.Elapsed >= TimeSpan.FromMilliseconds(250))
                        {
                            scrollElement = ResolveVerticalScrollElement(appRoot, runCase.Route);
                            initialOffset = ReadVerticalScrollPercent(appRoot, runCase.Route, ref scrollElement);
                            initialScrollHeartbeat = ReadHeartbeat(appRoot);
                            amount = initialOffset >= 10
                                ? ScrollAmount.SmallDecrement
                                : ScrollAmount.SmallIncrement;
                            initialScrollStatus = ReadScrollStatus(appRoot, runCase.Route);
                            hasAppScrollProbe = initialScrollStatus is not null;
                            initialScrollSequence = initialScrollStatus?.Sequence ?? 0;
                            scrollStartedTimestamp = Stopwatch.GetTimestamp();
                            ScrollVertically(appRoot, runCase.Route, ref scrollElement, amount);
                            scrollTransition = new ProductPerformanceScrollTransitionTracker(
                                scrollStartedTimestamp,
                                initialOffset,
                                initialScrollHeartbeat.Frame);
                            attemptTimeout.Restart();
                        }

                        Thread.Sleep(1);
                    }
                }

                if (!scrollTransition.IsCompleted)
                {
                    throw new TimeoutException(
                        $"Route '{runCase.Route.Id}' scroll did not produce both a scroll-offset change and a rendered frame.");
                }

                measurements.Add(Measure(
                    runCase,
                    runCase.Route.Id,
                    ProductPerformanceMetric.ScrollFrame,
                    scrollTransition.Completed!.Value.TotalMilliseconds,
                    DateTimeOffset.UtcNow));
            }
        }

        return measurements;
    }

    private static void PrepareScrollableSurface(
        ProductPerformanceRouteDefinition route,
        AutomationElement scrollTarget)
    {
        if (!string.Equals(route.Id, "repo_code", StringComparison.Ordinal) ||
            FindVerticallyScrollableElement(scrollTarget) is not null)
        {
            return;
        }

        ExpandRepoCodeTreeItem(
            scrollTarget,
            RepoCodeSourceDirectoryStatus,
            "Route 'repo_code' did not expose the deterministic src directory needed to measure tree scrolling.");
        ExpandRepoCodeTreeItem(
            scrollTarget,
            RepoCodeGeneratedDirectoryStatus,
            "Route 'repo_code' did not realize the deterministic src/generated directory needed to measure tree scrolling.");

        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            if (FindVerticallyScrollableElement(scrollTarget) is not null)
            {
                return;
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException(
            "Route 'repo_code' did not expose a scrollable file tree after expanding the deterministic src fixture.");
    }

    private static void ExpandRepoCodeTreeItem(
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

    private static AutomationElement ResolveVerticalScrollElement(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            AutomationElement? scrollTarget = FindVisibleForObservation(appRoot, route.ScrollAutomationId!);
            AutomationElement? scrollElement = scrollTarget is null
                ? null
                : FindVerticallyScrollableElement(scrollTarget);
            if (scrollElement is not null)
            {
                return scrollElement;
            }

            // Virtualized section hydration can briefly replace the scroll peer.
            // Wait for the stable peer instead of treating that transient as a route failure.
            Thread.Sleep(5);
        }

        throw new InvalidOperationException(
            $"Route '{route.Id}' no longer exposes a vertically scrollable UIA element under " +
            $"'{route.ScrollAutomationId}'.");
    }

    private static ProductPerformanceScrollStatus? ReadScrollStatus(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route)
    {
        AutomationElement? statusHost = FindVisibleForObservation(appRoot, route.ScrollAutomationId!);
        return statusHost is not null &&
            ProductPerformanceScrollStatus.TryParse(
                statusHost.Properties.ItemStatus.ValueOrDefault,
                out ProductPerformanceScrollStatus status)
                ? status
                : null;
    }

    private static double ReadVerticalScrollPercent(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route,
        ref AutomationElement scrollElement)
    {
        const int transientEventFailure = unchecked((int)0x80040201);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return scrollElement.Patterns.Scroll.Pattern.VerticalScrollPercent.ValueOrDefault;
            }
            catch (COMException exception) when (exception.HResult == transientEventFailure && attempt < 9)
            {
                scrollElement = ResolveVerticalScrollElement(appRoot, route);
                Thread.Sleep(5);
            }
            catch (FlaUI.Core.Exceptions.PatternNotSupportedException) when (attempt < 9)
            {
                scrollElement = ResolveVerticalScrollElement(appRoot, route);
                Thread.Sleep(5);
            }
            catch (InvalidOperationException) when (attempt < 9)
            {
                scrollElement = ResolveVerticalScrollElement(appRoot, route);
                Thread.Sleep(5);
            }
        }
    }

    private static void ScrollVertically(
        AutomationElement appRoot,
        ProductPerformanceRouteDefinition route,
        ref AutomationElement scrollElement,
        ScrollAmount amount)
    {
        const int transientEventFailure = unchecked((int)0x80040201);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                scrollElement.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, amount);
                return;
            }
            catch (COMException exception) when (exception.HResult == transientEventFailure && attempt < 9)
            {
                scrollElement = ResolveVerticalScrollElement(appRoot, route);
                Thread.Sleep(5);
            }
            catch (FlaUI.Core.Exceptions.PatternNotSupportedException) when (attempt < 9)
            {
                scrollElement = ResolveVerticalScrollElement(appRoot, route);
                Thread.Sleep(5);
            }
            catch (InvalidOperationException) when (attempt < 9)
            {
                scrollElement = ResolveVerticalScrollElement(appRoot, route);
                Thread.Sleep(5);
            }
        }
    }

    private static TimeSpan WaitForHeartbeatAdvance(
        AutomationElement appRoot,
        ProductPerformanceHeartbeat initial,
        bool requireFrameAdvance,
        ProductPerformanceContentTransitionTracker continuity,
        string routeRootAutomationId,
        string contentAutomationId,
        TimeSpan timeout)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            ProductPerformanceHeartbeat current = ReadHeartbeat(appRoot);
            if (current.Dispatcher > initial.Dispatcher &&
                (!requireFrameAdvance || current.Frame > initial.Frame))
            {
                TimeSpan response = Stopwatch.GetElapsedTime(startedTimestamp);
                ObserveContinuity(
                    appRoot,
                    routeRootAutomationId,
                    contentAutomationId,
                    continuity);
                return response;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The product dispatcher/composition heartbeat did not advance.");
    }

    private static void WaitForRouteTransition(
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

    private static ProductPerformanceContentTransitionTracker CreateContinuityTracker(
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

    private static void ObserveContinuity(
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

    private static AutomationElement? FindVerticallyScrollableElement(AutomationElement root)
    {
        IEnumerable<AutomationElement> candidates = new[] { root }.Concat(root.FindAllDescendants());
        foreach (AutomationElement candidate in candidates)
        {
            try
            {
                if (candidate.Patterns.Scroll.IsSupported &&
                    candidate.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault)
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static ProductPerformanceHeartbeat ReadHeartbeat(AutomationElement appRoot)
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

    private static bool IsTransientUiAutomationFailure(COMException exception) =>
        exception.HResult is unchecked((int)0x80040201) or unchecked((int)0x8000FFFF);

    private static TimeSpan WaitForInteractiveTimestamp(
        AutomationElement appRoot,
        long startupStartedTimestamp)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            ProductPerformanceHeartbeat heartbeat = ReadHeartbeat(appRoot);
            if (heartbeat.InteractiveTimestamp is long interactiveTimestamp &&
                interactiveTimestamp >= startupStartedTimestamp)
            {
                return Stopwatch.GetElapsedTime(startupStartedTimestamp, interactiveTimestamp);
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException("The application did not publish its interactive startup timestamp.");
    }

    private static AutomationElement? FindVisible(AutomationElement root, string automationId)
    {
        try
        {
            AutomationElement? element = root.AutomationId == automationId
                ? root
                : root.FindFirstDescendant(condition => condition.ByAutomationId(automationId));
            return element is not null && IsVisible(element) ? element : null;
        }
        catch
        {
            return null;
        }
    }

    private static AutomationElement? FindVisibleForObservation(AutomationElement root, string automationId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            AutomationElement? element = FindVisible(root, automationId);
            if (element is not null)
            {
                return element;
            }

            Thread.Sleep(1);
        }

        return null;
    }

    private static bool IsVisible(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            Rectangle bounds = element.BoundingRectangle;
            return element.Properties.IsOffscreen.ValueOrDefault != true && bounds.Width > 1 && bounds.Height > 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSelected(AutomationElement element)
    {
        try
        {
            return element.Patterns.SelectionItem.IsSupported &&
                element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;
        }
        catch
        {
            return false;
        }
    }

    private static string GetElementName(AutomationElement element)
    {
        try
        {
            return element.Name?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SelectOrInvoke(AutomationElement element)
    {
        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
            return;
        }

        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        element.Click();
    }

    private static void CommitTextValue(AutomationElement element, string value, string description)
    {
        TextBox textBox = element.AsTextBox();
        textBox.Text = value;

        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (string.Equals(textBox.Text, value, StringComparison.Ordinal))
            {
                // Let WinUI process the Value-pattern update before the adjacent
                // command is invoked. This setup delay is outside measured time.
                Thread.Sleep(16);
                return;
            }

            Thread.Sleep(2);
        }

        throw new TimeoutException($"The {description} input did not commit '{value}'.");
    }

    private static Action PrepareTraversalActivation(AutomationElement element)
    {
        if (element.ControlType == ControlType.TreeItem)
        {
            AutomationElement? label = element
                .FindAllDescendants()
                .Where(static descendant => descendant.ControlType == ControlType.Text)
                .FirstOrDefault(IsVisible);
            if (label is not null)
            {
                Rectangle labelBounds = label.BoundingRectangle;
                Point clickPoint = new(
                    labelBounds.Left + labelBounds.Width / 2,
                    labelBounds.Top + labelBounds.Height / 2);
                SendNativePointerMove(clickPoint);
                Thread.Sleep(75);
                return SendNativeClick;
            }

            element.Focus();
            return static () => Keyboard.Press(VirtualKeyShort.ENTER);
        }

        Rectangle bounds = element.BoundingRectangle;
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            throw new InvalidOperationException("The cached traversal target has no clickable bounds.");
        }

        // List rows can contain legitimate nested actions such as author links.
        // Activate a neutral point near the row's leading/top edge so the probe
        // measures row selection rather than whichever child happens to be centered.
        int horizontalInset = Math.Min(12, Math.Max(2, bounds.Width / 4));
        int verticalInset = Math.Min(12, Math.Max(2, bounds.Height / 4));
        Point target = element.ControlType is ControlType.ListItem or ControlType.DataItem
            ? new Point(bounds.Left + horizontalInset, bounds.Top + verticalInset)
            : new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        SendNativePointerMove(target);
        Thread.Sleep(75);
        return SendNativeClick;
    }

    private static void SendNativePointerMove(Point target)
    {
        int virtualLeft = GetSystemMetrics(76);
        int virtualTop = GetSystemMetrics(77);
        int virtualWidth = Math.Max(2, GetSystemMetrics(78));
        int virtualHeight = Math.Max(2, GetSystemMetrics(79));
        int normalizedX = (int)Math.Round((target.X - virtualLeft) * 65_535d / (virtualWidth - 1));
        int normalizedY = (int)Math.Round((target.Y - virtualTop) * 65_535d / (virtualHeight - 1));
        NativeInput[] inputs =
        [
            new()
            {
                Type = 0,
                Mouse = new NativeMouseInput
                {
                    X = normalizedX,
                    Y = normalizedY,
                    Flags = 0x0001 | 0x4000 | 0x8000
                }
            }
        ];

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            throw new InvalidOperationException("Could not position the native cached-traversal pointer.");
        }
    }

    private static void SendNativeClick()
    {
        NativeInput[] inputs =
        [
            new() { Type = 0, Mouse = new NativeMouseInput { Flags = 0x0002 } },
            new() { Type = 0, Mouse = new NativeMouseInput { Flags = 0x0004 } }
        ];

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            throw new InvalidOperationException("Could not deliver native cached-traversal click.");
        }
    }

    private static void CloseApplication(Application application)
    {
        try
        {
            application.Close();
        }
        catch
        {
        }

        try
        {
            using Process process = Process.GetProcessById(application.ProcessId);
            if (!process.WaitForExit(3_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeMouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
