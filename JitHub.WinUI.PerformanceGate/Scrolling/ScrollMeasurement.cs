using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using JitHub.Services;
using static JitHub.WinUI.PerformanceGate.Elements.RouteElements;
using static JitHub.WinUI.PerformanceGate.Native.NativePointerInput;
using static JitHub.WinUI.PerformanceGate.Routes.ProductPerformanceRouteProbe;
using static JitHub.WinUI.PerformanceGate.Routes.RouteContentObservation;
using static JitHub.WinUI.PerformanceGate.Routes.RouteProbeTimeouts;
using static JitHub.WinUI.PerformanceGate.Traversal.RepoCodeFixtureStatus;
using static JitHub.WinUI.PerformanceGate.Traversal.TraversalPointerActivation;
using static JitHub.WinUI.PerformanceGate.Traversal.TraversalTargets;

namespace JitHub.WinUI.PerformanceGate.Scrolling;

/// <summary>
/// Measures dispatcher heartbeat latency and scroll completion on a route's scrollable surface.
/// </summary>
internal static class ScrollMeasurement
{
    // This method stays whole because each of 30 samples measures a dispatcher heartbeat advance and then a scroll, and
    // the scroll retry path resets its starting offset, heartbeat frame, probe sequence, and transition tracker
    // together before sending a native wheel event every 250 ms.
    internal static IEnumerable<ProductPerformanceMeasurement> MeasureDispatcherAndScroll(
        ProductPerformanceRunCase runCase,
        AutomationElement appRoot,
        ProductPerformanceContentTransitionTracker continuity,
        IntPtr appWindowHandle)
    {
        List<ProductPerformanceMeasurement> measurements = [];
        AutomationElement? scrollElement = null;
        if (runCase.Fixture != ProductPerformanceFixture.Cold && runCase.Route.SupportsScroll)
        {
            AutomationElement scrollTarget = WaitForElement(appRoot, runCase.Route.ScrollAutomationId!);
            PrepareScrollableSurface(runCase.Route, scrollTarget);
            scrollElement = ResolveVerticalScrollElement(appRoot, runCase.Route);
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
                int scrollAttempts = 1;
                double lastObservedOffset = initialOffset;
                ProductPerformanceHeartbeat lastObservedHeartbeat = initialScrollHeartbeat;
                ProductPerformanceScrollStatus? lastObservedStatus = initialScrollStatus;

                if (hasAppScrollProbe)
                {
                    // The app records ViewChanging-to-CompositionTarget.Rendering timestamps
                    // internally. Leave one observer-free render window before cross-process
                    // UIA polling so the benchmark does not stall the frame it is measuring.
                    Thread.Sleep(50);
                }

                Stopwatch timeout = Stopwatch.StartNew();
                Stopwatch attemptTimeout = Stopwatch.StartNew();
                while (!scrollTransition.IsCompleted && timeout.Elapsed < ObservableTimeout)
                {
                    ProductPerformanceHeartbeat currentHeartbeat = ReadHeartbeat(appRoot);
                    lastObservedHeartbeat = currentHeartbeat;
                    if (currentHeartbeat.Frame > initialScrollHeartbeat.Frame)
                    {
                        long observedTimestamp = Stopwatch.GetTimestamp();
                        ProductPerformanceScrollStatus? scrollStatus = ReadScrollStatus(appRoot, runCase.Route);
                        lastObservedStatus = scrollStatus;
                        if (scrollStatus is ProductPerformanceScrollStatus renderedScroll &&
                            renderedScroll.Sequence > initialScrollSequence &&
                            renderedScroll.StartedTimestamp >= scrollStartedTimestamp)
                        {
                            scrollTransition.ObserveRenderedInterval(
                                renderedScroll.StartedTimestamp,
                                renderedScroll.RenderedTimestamp);
                        }

                        double currentOffset = ReadVerticalScrollPercent(appRoot, runCase.Route, ref scrollElement);
                        lastObservedOffset = currentOffset;
                        scrollTransition.Observe(currentOffset, currentHeartbeat, observedTimestamp);
                        ObserveContinuity(
                            appRoot,
                            runCase.Route.RootAutomationId,
                            runCase.Route.ReadyAutomationId,
                            continuity);
                    }

                    if (!scrollTransition.IsCompleted)
                    {
                        if (attemptTimeout.Elapsed >= TimeSpan.FromMilliseconds(250))
                        {
                            scrollElement = ResolveVerticalScrollElement(appRoot, runCase.Route);
                            initialOffset = ReadVerticalScrollPercent(appRoot, runCase.Route, ref scrollElement);
                            initialScrollHeartbeat = ReadHeartbeat(appRoot);
                            initialScrollStatus = ReadScrollStatus(appRoot, runCase.Route);
                            hasAppScrollProbe = initialScrollStatus is not null;
                            initialScrollSequence = initialScrollStatus?.Sequence ?? 0;
                            scrollStartedTimestamp = SendNativeWheelOverElement(
                                scrollElement,
                                appWindowHandle,
                                scrollUp: initialOffset >= 10);
                            scrollAttempts++;
                            scrollTransition = new ProductPerformanceScrollTransitionTracker(
                                scrollStartedTimestamp,
                                initialOffset,
                                initialScrollHeartbeat.Frame);
                            if (hasAppScrollProbe)
                            {
                                Thread.Sleep(50);
                            }
                            attemptTimeout.Restart();
                        }

                        Thread.Sleep(1);
                    }
                }

                if (!scrollTransition.IsCompleted)
                {
                    string scrollElementState = DescribeScrollElement(scrollElement);
                    throw new TimeoutException(
                        $"Route '{runCase.Route.Id}' scroll did not produce both a scroll-offset change and a rendered frame. " +
                        $"Attempts={scrollAttempts}; initialOffset={initialOffset:0.###}; " +
                        $"lastOffset={lastObservedOffset:0.###}; " +
                        $"frame={initialScrollHeartbeat.Frame}->{lastObservedHeartbeat.Frame}; " +
                        $"probeSequence={initialScrollSequence}->{lastObservedStatus?.Sequence ?? -1}; " +
                        $"scrollElement={scrollElementState}.");
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

    private static long SendNativeWheelOverElement(
        AutomationElement scrollElement,
        IntPtr appWindowHandle,
        bool scrollUp)
    {
        ActivateWindowForPointerInput(appWindowHandle);
        Rectangle visibleBounds = Rectangle.Intersect(
            scrollElement.BoundingRectangle,
            GetWindowBounds(appWindowHandle));
        if (visibleBounds.Width <= 1 || visibleBounds.Height <= 1)
        {
            throw new InvalidOperationException(
                "The measured scroll surface is outside the JitHub window.");
        }

        SendNativePointerMove(new Point(
            visibleBounds.Left + visibleBounds.Width / 2,
            visibleBounds.Top + visibleBounds.Height / 2));
        Thread.Sleep(20);
        long startedTimestamp = Stopwatch.GetTimestamp();
        SendNativeWheel(scrollUp);
        return startedTimestamp;
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

    private static AutomationElement? FindVerticallyScrollableElement(AutomationElement root)
    {
        IEnumerable<AutomationElement> candidates = new[] { root }.Concat(root.FindAllDescendants());
        foreach (AutomationElement candidate in candidates)
        {
            try
            {
                if (candidate.Patterns.Scroll.IsSupported &&
                    IsVisible(candidate) &&
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

    private static string DescribeScrollElement(AutomationElement element)
    {
        try
        {
            var scroll = element.Patterns.Scroll.Pattern;
            Rectangle bounds = element.BoundingRectangle;
            return $"id={element.AutomationId ?? "<none>"}," +
                $"name={element.Name ?? "<none>"}," +
                $"type={element.ControlType}," +
                $"offscreen={element.Properties.IsOffscreen.ValueOrDefault}," +
                $"bounds={bounds.Left:0.#},{bounds.Top:0.#},{bounds.Width:0.#},{bounds.Height:0.#}," +
                $"percent={scroll.VerticalScrollPercent.ValueOrDefault:0.###}," +
                $"view={scroll.VerticalViewSize.ValueOrDefault:0.###}";
        }
        catch (Exception exception)
        {
            return $"unavailable:{exception.GetType().Name}";
        }
    }

    private static AutomationElement? WaitForVerticallyScrollableElement(
        AutomationElement root,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            AutomationElement? scrollElement = FindVerticallyScrollableElement(root);
            if (scrollElement is not null)
            {
                return scrollElement;
            }

            Thread.Sleep(5);
        }
        while (stopwatch.Elapsed < timeout);

        return null;
    }
}
