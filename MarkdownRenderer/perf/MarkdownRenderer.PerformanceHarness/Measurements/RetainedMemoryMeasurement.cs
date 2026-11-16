using MarkdownRenderer.PerformanceHarness.Core.Events;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.PerformanceHarness.Hosting;

namespace MarkdownRenderer.PerformanceHarness.Measurements;

/// <summary>
/// Measures the managed and private bytes a renderer retains after showing
/// each first-viewport source size, against a budget of eight bytes per
/// UTF-16 byte plus 32 MiB.
/// </summary>
internal sealed class RetainedMemoryMeasurement
{
    private readonly PerformanceReport _report;
    private readonly PerformanceEventListener _events;
    private readonly MeasurementRendererHost _rendererHost;

    internal RetainedMemoryMeasurement(
        PerformanceReport report,
        PerformanceEventListener events,
        MeasurementRendererHost rendererHost)
    {
        _report = report;
        _events = events;
        _rendererHost = rendererHost;
    }

    internal static long GetBudgetBytes(int sourceBytes)
        => checked(sourceBytes * 8L + 32L * 1024 * 1024);

    internal async Task MeasureAsync()
    {
        foreach (int sourceBytes in MeasuredSourceSizes.FirstViewport)
        {
            await _rendererHost.ReplaceRendererAsync(PerformanceEngineFactory.Create(sourceBytes));
            await _rendererHost.ForceCollectionAsync();
            (long managedBefore, long privateBefore) = MeasurementRendererHost.CaptureMemory();
            // Allocate the caller's exact source after the baseline so the
            // renderer-retained total includes the UTF-16 input itself.
            string source = PerformanceDocumentFactory.Create(sourceBytes, sourceBytes + 1);

            long sequence = _events.CurrentSequence;
            _rendererHost.Renderer!.Markdown = source;
            _ = await _events.WaitForFirstViewportAsync(
                sequence,
                sourceBytes,
                MeasurementRendererHost.ViewportTimeout);
            await _rendererHost.WaitForCompositionFramesAsync(5);
            await _rendererHost.ForceCollectionAsync();
            (long managedAfter, long privateAfter) = MeasurementRendererHost.CaptureMemory();

            long managedDelta = Math.Max(0, managedAfter - managedBefore);
            long privateDelta = Math.Max(0, privateAfter - privateBefore);
            long budget = GetBudgetBytes(sourceBytes);
            _report.RetainedMemory.Add(new RetainedMemoryResult
            {
                Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
                SourceUtf16Bytes = sourceBytes,
                ManagedDeltaBytes = managedDelta,
                PrivateDeltaBytes = privateDelta,
                BudgetBytes = budget,
                Passed = managedDelta <= budget && privateDelta <= budget,
            });
            await _rendererHost.DisposeRendererAsync();
            await _rendererHost.ForceCollectionAsync();
        }
    }
}
