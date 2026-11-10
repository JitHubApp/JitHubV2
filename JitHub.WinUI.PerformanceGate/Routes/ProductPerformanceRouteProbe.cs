using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.Services;
using static JitHub.WinUI.PerformanceGate.Elements.RouteElements;
using static JitHub.WinUI.PerformanceGate.Routes.RouteContentObservation;
using static JitHub.WinUI.PerformanceGate.Routes.RouteProbeTimeouts;
using static JitHub.WinUI.PerformanceGate.Scrolling.ScrollMeasurement;
using static JitHub.WinUI.PerformanceGate.Traversal.TraversalPointerActivation;
using static JitHub.WinUI.PerformanceGate.Traversal.TraversalTargets;

namespace JitHub.WinUI.PerformanceGate.Routes;

/// <summary>
/// Runs one product performance run case against a launched app: startup to interactive, the timed route, cached selection, dispatcher and scroll samples, blanking and working set.
/// </summary>
internal sealed class ProductPerformanceRouteProbe
{
    private const string AppRootAutomationId = "JitHubMainWindowRoot";

    private readonly string _appPath;
    private readonly string _dataRoot;
    private readonly string _repository;

    public ProductPerformanceRouteProbe(string appPath, string dataRoot, string repository)
    {
        _appPath = Path.GetFullPath(appPath);
        _dataRoot = Path.GetFullPath(dataRoot);
        _repository = repository;
    }

    // This method stays whole because it is one timed run case: reset the cache partition, launch and time startup to
    // interactive, run untimed warm navigation, time the target route, measure cached selection before or after
    // dispatcher and scroll depending on route, total blanking and working set, then close and archive diagnostics.
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
            Window appWindow = WaitForWindow(application, automation);
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
                    MeasureCachedSelection(runCase, application, automation, appRoot, appWindow);
                measurements.Add(selectionMeasurement);
                blankingOccurrences += selectionBlanking;
            }

            measurements.AddRange(MeasureDispatcherAndScroll(
                runCase,
                appRoot,
                continuity,
                new IntPtr(appWindow.Properties.NativeWindowHandle.ValueOrDefault)));
            if (runCase.Fixture != ProductPerformanceFixture.Cold &&
                runCase.Route.SupportsTraversal &&
                !measureSelectionBeforeScroll)
            {
                (ProductPerformanceMeasurement selectionMeasurement, int selectionBlanking) =
                    MeasureCachedSelection(runCase, application, automation, appRoot, appWindow);
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
            ArchiveDiagnostics(runCase, partitionRoot);
        }
    }

    private void ArchiveDiagnostics(ProductPerformanceRunCase runCase, string partitionRoot)
    {
        string sourcePath = Path.Combine(
            partitionRoot,
            "Local",
            "Diagnostics",
            "v1",
            "diagnostics.ndjson");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        string iterationName = runCase.Iteration < 0
            ? "warmup"
            : $"iteration-{runCase.Iteration + 1:D2}";
        string destinationDirectory = Path.Combine(
            _dataRoot,
            "diagnostics",
            runCase.Fixture.ToString().ToLowerInvariant(),
            runCase.Route.Id);
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(
            sourcePath,
            Path.Combine(destinationDirectory, $"{iterationName}.ndjson"),
            overwrite: true);
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

    internal static ProductPerformanceMeasurement Measure(
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
            catch (COMException exception) when (IsTransientUiAutomationFailure(exception))
            {
                // A startup element can disappear between enumeration and property access.
                // Retry the fresh desktop tree instead of failing the entire benchmark run.
            }
            catch (Win32Exception exception) when (IsTransientUiAutomationTimeout(exception))
            {
                // FlaUI wraps a temporarily unresponsive provider as Win32 timeout 1460.
                // The outer 20-second deadline remains authoritative.
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The JitHub window did not become interactive within 20 seconds.");
    }

    internal static AutomationElement WaitForAppRoot(
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
            catch (COMException exception) when (IsTransientUiAutomationFailure(exception))
            {
            }
            catch (Win32Exception exception) when (IsTransientUiAutomationTimeout(exception))
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
        CommitTextValue(
            appRoot,
            "ProductPerformanceRouteInput",
            route.Id,
            "performance route");
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

    private (ProductPerformanceMeasurement Measurement, int BlankingOccurrences) MeasureCachedSelection(
        ProductPerformanceRunCase runCase,
        Application application,
        UIA3Automation automation,
        AutomationElement appRoot,
        Window appWindow)
    {
        ProductPerformanceRouteDefinition route = runCase.Route;
        AutomationElement selectionHost = WaitForElement(appRoot, route.SelectionAutomationId!);
        AutomationElement target = FindTraversalTarget(route, selectionHost);

        string expectedIdentity = GetExactTraversalIdentity(route, target);
        CommitTextValue(
            appRoot,
            "ProductPerformanceRouteInput",
            route.Id,
            "performance traversal route");
        CommitTextValue(
            appRoot,
            "ProductPerformanceTraversalInput",
            expectedIdentity,
            "performance traversal identity");
        Func<long> activateTraversalTarget = PrepareTraversalActivation(
            route,
            target,
            expectedIdentity,
            selectionHost,
            new IntPtr(appWindow.Properties.NativeWindowHandle.ValueOrDefault));
        AutomationElement traversalMarker = WaitForElement(
            appRoot,
            $"ProductPerformanceTraversalReady_{route.Id}");
        long traversalArmRequestedTimestamp = Stopwatch.GetTimestamp();
        SelectOrInvoke(WaitForElement(appRoot, "ProductPerformanceArmTraversalButton"));
        // Arming mutates a hidden UIA marker. Let that observer-only work drain
        // before timing native input so it cannot steal the user's first frame.
        Thread.Sleep(50);
        long traversalActivationStartedTimestamp = activateTraversalTarget();
        ProductPerformanceTraversalTiming selectionTiming = WaitForExactTraversal(
            appRoot,
            route,
            traversalMarker,
            expectedIdentity,
            traversalArmRequestedTimestamp,
            traversalActivationStartedTimestamp,
            application,
            automation);
        AppendTraversalObservation(runCase, expectedIdentity, selectionTiming);

        return (
            Measure(
                runCase,
                route.Id,
                route.SupportsCachedSelection
                    ? ProductPerformanceMetric.CachedSelection
                    : ProductPerformanceMetric.CachedRouteNavigation,
                selectionTiming.Elapsed.TotalMilliseconds,
                DateTimeOffset.UtcNow),
            0);
    }

    private void AppendTraversalObservation(
        ProductPerformanceRunCase runCase,
        string identity,
        ProductPerformanceTraversalTiming timing)
    {
        string path = Path.Combine(_dataRoot, "traversal-observations.ndjson");
        var observation = new
        {
            recordedAt = DateTimeOffset.UtcNow,
            fixture = runCase.Fixture.ToString(),
            route = runCase.Route.Id,
            iteration = runCase.Iteration,
            warmup = runCase.Iteration < 0,
            identity,
            inputMilliseconds = timing.Input.TotalMilliseconds,
            renderMilliseconds = timing.Render.TotalMilliseconds,
            totalMilliseconds = timing.Elapsed.TotalMilliseconds,
            trace = timing.Trace
        };

        using FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4 * 1024,
            FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, observation);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

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
}
