using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Cycles one renderer through alternating sources, widths and themes with a
/// synthetic device reset every 25 cycles, and compares retained memory at
/// 80% and 100% of the cycles to show that it plateaus.
/// </summary>
internal sealed class LifecyclePlateauMeasurement
{
    private const int SourceBytes = 100 * 1024;

    private readonly PerformanceOptions _options;
    private readonly PerformanceReport _report;
    private readonly PerformanceEventListener _events;
    private readonly MeasurementRendererHost _rendererHost;

    internal LifecyclePlateauMeasurement(
        PerformanceOptions options,
        PerformanceReport report,
        PerformanceEventListener events,
        MeasurementRendererHost rendererHost)
    {
        _options = options;
        _report = report;
        _events = events;
        _rendererHost = rendererHost;
    }

    internal async Task<LifecyclePlateauResult> MeasureAsync()
    {
        string first = PerformanceDocumentFactory.Create(SourceBytes, 1001);
        string second = PerformanceDocumentFactory.Create(SourceBytes, 1002);
        await _rendererHost.ReplaceRendererAsync(PerformanceEngineFactory.Create(SourceBytes));
        long initialSequence = _events.CurrentSequence;
        _rendererHost.Renderer!.Markdown = first;
        _ = await _events.WaitForFirstViewportAsync(
            initialSequence,
            SourceBytes,
            MeasurementRendererHost.ViewportTimeout);

        int checkpointCycle = Math.Max(1, (int)Math.Ceiling(_options.LifecycleCycles * 0.8));
        long checkpointManaged = 0;
        long checkpointPrivate = 0;
        int resets = 0;
        int failuresBefore = _rendererHost.RenderFailures;

        for (int cycle = 1; cycle <= _options.LifecycleCycles; cycle++)
        {
            long sequence = _events.CurrentSequence;
            if (await RunCycleAsync(cycle, first, second))
                resets++;

            _ = await _events.WaitForFirstViewportAsync(
                sequence,
                SourceBytes,
                MeasurementRendererHost.ViewportTimeout);
            if (cycle != checkpointCycle && cycle != _options.LifecycleCycles)
                continue;

            await _rendererHost.WaitForCompositionFramesAsync(5);
            await _rendererHost.ForceCollectionAsync();
            (long managed, long privateBytes) = MeasurementRendererHost.CaptureMemory();
            if (cycle == checkpointCycle)
            {
                checkpointManaged = managed;
                checkpointPrivate = privateBytes;
            }
            if (cycle == _options.LifecycleCycles)
            {
                return CreateResult(
                    cycle,
                    resets,
                    checkpointManaged,
                    managed,
                    checkpointPrivate,
                    privateBytes,
                    failuresBefore);
            }
        }

        throw new InvalidOperationException("Lifecycle plateau loop did not produce a final checkpoint.");
    }

    /// <summary>
    /// Changes the source, width and theme for one cycle, and every 25 cycles
    /// performs a synthetic device reset. Returns true when it reset.
    /// </summary>
    private async Task<bool> RunCycleAsync(int cycle, string first, string second)
    {
        MarkdownScrollView renderer = _rendererHost.Renderer!;
        renderer.Markdown = (cycle & 1) == 0 ? first : second;
        renderer.Width = (cycle & 1) == 0 ? 980 : 940;
        renderer.RequestedTheme = (cycle & 1) == 0 ? ElementTheme.Light : ElementTheme.Dark;
        if (cycle % 25 != 0)
            return false;

        // Release every snapshot-owned Win2D/DirectWrite object, trim
        // the shared device, and rebuild. Win2D exposes no supported
        // synthetic DXGI-loss trigger; physically induced device-loss
        // evidence remains a separate hardware-lab requirement.
        renderer.PrepareForSyntheticDeviceReset();
        await renderer.DrainRetiredSnapshotsAsync();
        CanvasDevice.GetSharedDevice().Trim();
        renderer.RequestRebuild();
        return true;
    }

    private LifecyclePlateauResult CreateResult(
        int cycle,
        int resets,
        long checkpointManaged,
        long managed,
        long checkpointPrivate,
        long privateBytes,
        int failuresBefore)
    {
        double managedGrowth = PerformanceStatistics.GrowthPercent(managed, checkpointManaged);
        double privateGrowth = PerformanceStatistics.GrowthPercent(privateBytes, checkpointPrivate);
        double growth = Math.Max(managedGrowth, privateGrowth);
        int renderFailures = _rendererHost.RenderFailures;
        return new LifecyclePlateauResult
        {
            CompletedCycles = cycle,
            RequiredCycles = _options.LifecycleCycles,
            SyntheticDeviceResets = resets,
            Checkpoint80ManagedBytes = checkpointManaged,
            Checkpoint100ManagedBytes = managed,
            Checkpoint80PrivateBytes = checkpointPrivate,
            Checkpoint100PrivateBytes = privateBytes,
            ManagedGrowthPercent = managedGrowth,
            PrivateGrowthPercent = privateGrowth,
            GrowthPercent = growth,
            RenderFailures = renderFailures - failuresBefore,
            Passed = cycle >= _report.SampleRequirements.LifecycleCyclesRequired &&
                     resets >= 4 &&
                     growth <= 5 &&
                     renderFailures == failuresBefore,
        };
    }
}
