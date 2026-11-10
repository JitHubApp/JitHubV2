using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Checks;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;
using MarkdownRenderer.PerformanceHarness.Core.Events;
using MarkdownRenderer.PerformanceHarness.Core.Evidence;
using MarkdownRenderer.PerformanceHarness.Core.Measurement;
using MarkdownRenderer.PerformanceHarness.Core.MeasurementVisibility;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.PerformanceHarness.Measurements;
using MarkdownRenderer.PerformanceHarness.Measurements.FirstViewport;
using MarkdownRenderer.PerformanceHarness.Probes;
using MarkdownRenderer.PerformanceHarness.StageProfiles;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace MarkdownRenderer.PerformanceHarness.Hosting;

/// <summary>
/// Runs one harness invocation in the benchmark window: the measurement
/// preflight, every measurement in its frozen order, the report's verdict,
/// and the report, sidecar, console summary and process exit code.
/// </summary>
/// <remarks>
/// Exit codes: 0 when the run passed, 1 when a gate or the harness failed,
/// 2 when the report or stage-profile sidecar could not be written, and 4
/// when the measurement environment was invalidated so the renderer gates
/// were not evaluated.
/// </remarks>
internal sealed class PerformanceRun
{
    private const double MeasurementWidthDips = 1100;
    private const double MeasurementHeightDips = 760;

    private readonly Window _window;
    private readonly Grid _host;
    private readonly PerformanceOptions _options;
    private readonly PerformanceEventListener _events = new();
    private readonly MeasurementVisibilityInvalidation _measurementInvalidation = new();
    private readonly MonotonicUtcClock _utcClock = new();
    private readonly PerformanceReport _report;
    private readonly FirstViewportStageProfileRecorder _stageProfile;
    private readonly MeasurementRendererHost _rendererHost;
    private MeasurementVisibilityMonitor? _visibilityMonitor;
    private int _completionState;

    internal PerformanceRun(Window window, Grid host, PerformanceOptions options)
    {
        _window = window;
        _host = host;
        _options = options;
        _stageProfile = FirstViewportStageProfileRecorder.Create(options);
        _rendererHost = new MeasurementRendererHost(
            host,
            _events,
            _measurementInvalidation.CancellationToken);

        BuildArtifactHashes buildArtifacts = PerformanceEvidenceHashing.CaptureBuildArtifacts();
        _report = new PerformanceReport
        {
            StartedUtc = _utcClock.GetUtcNow(),
            IsReleaseEvidence = !options.Quick,
            BuildIdentity = MachineProbe.GetBuildIdentity(),
            BuildArtifacts = buildArtifacts,
            RuntimeConfiguration = PerformanceRuntimeContract.Capture(buildArtifacts.RuntimeConfig.Path),
            Machine = new MachineMetadata(),
            SampleRequirements = SampleRequirements.For(options),
        };
    }

    /// <summary>
    /// Runs the invocation to completion and exits the process with its exit
    /// code, unless <see cref="TryWriteFatalReport"/> completed it first.
    /// </summary>
    internal async Task RunAsync()
    {
        Debug.WriteLine("[PerfHarness] run started");
        _events.SetMeasurementEnvironmentAbortToken(_measurementInvalidation.CancellationToken);
        int exitCode;
        try
        {
            await MeasureAsync();
            exitCode = _report.Passed ? 0 : 1;
        }
        catch (OperationCanceledException) when (
            _measurementInvalidation.FirstUnqualifiedSample is not null)
        {
            exitCode = RecordInvalidEnvironment();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[PerfHarness] run failure: {exception}");
            _report.Failures.Add($"Harness failure: {exception}");
            TryCompleteVisibilityMonitoring();
            _report.Passed = false;
            exitCode = 1;
        }

        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
            return;

        if (_report.CompletedUtc == default)
        {
            // A failure before the normal freeze still needs an honest,
            // positive interval. Successful and post-freeze failure payloads
            // retain the exact timestamp that was evaluated above.
            _report.CompletedUtc = _utcClock.GetUtcNow();
        }
        exitCode = await WriteOutputsAsync(exitCode);

        await _rendererHost.DisposeRendererAsync();
        _events.Dispose();
        _measurementInvalidation.Dispose();
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// Writes an explicitly failed report for an unhandled WinUI exception,
    /// unless the run already completed. Returns true when it wrote one.
    /// </summary>
    internal bool TryWriteFatalReport(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
            return false;

        try
        {
            _report.Failures.Add($"Unhandled WinUI harness failure: {exception}");
            CompleteVisibilityMonitoring();
            _report.Passed = false;
            _report.CompletedUtc = _utcClock.GetUtcNow();
            Program.WriteReport(_options.OutputPath, _report);
            return true;
        }
        catch (Exception writeException)
        {
            Debug.WriteLine($"[PerfHarness] could not write fatal report: {writeException}");
            return false;
        }
        finally
        {
            _ = _rendererHost.DisposeRendererAsync();
            _events.Dispose();
            _measurementInvalidation.Dispose();
        }
    }

    private async Task MeasureAsync()
    {
        if (_stageProfile.ConfigurationError is not null)
            throw new InvalidOperationException(_stageProfile.ConfigurationError);

        IntPtr windowHandle = await PrepareMeasurementAsync();
        _report.SourceLookup = new SourceLookupMeasurement(_utcClock).Measure();
        Debug.WriteLine("[PerfHarness] source lookup complete");
        await new FirstViewportMeasurement(
            _options,
            _report,
            _events,
            _rendererHost,
            _utcClock,
            _measurementInvalidation.CancellationToken,
            _stageProfile).MeasureAsync();
        Debug.WriteLine("[PerfHarness] first viewport complete");
        _report.Scroll = await new WarmScrollMeasurement(
            _options,
            _report,
            _events,
            _rendererHost,
            _measurementInvalidation.CancellationToken).MeasureAsync();
        Debug.WriteLine("[PerfHarness] scroll complete");
        RecordObservedRefreshRate();
        await new RetainedMemoryMeasurement(_report, _events, _rendererHost).MeasureAsync();
        Debug.WriteLine("[PerfHarness] retained memory complete");
        _report.LifecyclePlateau = await new LifecyclePlateauMeasurement(
            _options,
            _report,
            _events,
            _rendererHost).MeasureAsync();
        Debug.WriteLine("[PerfHarness] lifecycle complete");
        _report.Cancellation = await new CancellationMeasurement(
            _options,
            _report,
            _events,
            _rendererHost).MeasureAsync();
        Debug.WriteLine("[PerfHarness] cancellation complete");
        _report.CompletionMachine = CaptureMachine(windowHandle, configureProcessPowerThrottling: false);
        CompleteVisibilityMonitoring();
        // The final visibility sample is part of the frozen interval too.
        // If foreground or coverage was lost immediately before teardown,
        // classify the run as an invalid environment rather than evaluating
        // its renderer measurements.
        _measurementInvalidation.CancellationToken.ThrowIfCancellationRequested();
        // Freeze the serialized report interval before evaluating its
        // evidence. Every recorded trial must be bounded by this exact,
        // positive interval; do not mutate it after the verdict is formed.
        _report.CompletedUtc = _utcClock.GetUtcNow();
        _report.Regression = RegressionEvaluator.Evaluate(_report, _options);
        new PerformanceReportSelfCheck(_report, _options, _events, _rendererHost).Validate();
    }

    /// <summary>
    /// Sizes the window, waits for a qualified desktop (release runs),
    /// captures the starting machine metadata, checks the display refresh
    /// rate (release runs), warms the runtime, and starts visibility
    /// monitoring (release runs). Returns the benchmark window handle.
    /// </summary>
    private async Task<IntPtr> PrepareMeasurementAsync()
    {
        await NormalizeMeasurementWindowAsync();
        double dpiScale = _host.XamlRoot?.RasterizationScale ?? 0;
        IntPtr windowHandle = WindowNative.GetWindowHandle(_window);
        if (!_options.Quick)
        {
            await MeasurementVisibilityReadinessWaiter.WaitUntilQualifiedAsync(
                () => MeasurementVisibilityMonitor.CaptureReadinessSample(windowHandle));
        }

        _report.Machine = MachineProbe.Capture(
            windowHandle,
            CanvasDevice.GetSharedDevice(),
            dpiScale,
            _host.ActualWidth,
            _host.ActualHeight,
            configureProcessPowerThrottling: true);

        if (!_options.Quick &&
            !MachineEvidenceValidator.IsConfiguredRefreshRateQualified(
                _report.Machine.ConfiguredRefreshRateHz))
        {
            throw new InvalidOperationException(
                $"Release measurement preflight requires a configured display refresh rate of at least " +
                $"{PerformanceMeasurementContract.MinimumConfiguredRefreshRateHz:N0} Hz " +
                $"(configured {_report.Machine.ConfiguredRefreshRateHz:N2} Hz).");
        }

        await WarmRuntimeAsync();
        Debug.WriteLine("[PerfHarness] runtime warm");
        if (!_options.Quick)
            StartVisibilityMonitoring(windowHandle);
        return windowHandle;
    }

    private void StartVisibilityMonitoring(IntPtr windowHandle)
    {
        MeasurementVisibilityMonitor visibilityMonitor =
            MeasurementVisibilityMonitor.Start(windowHandle, _measurementInvalidation);
        _visibilityMonitor = visibilityMonitor;
        if (!MeasurementVisibilityEvidenceValidator.IsQualifiedSample(
                visibilityMonitor.InitialSample))
        {
            _ = _measurementInvalidation.TryInvalidate(
                visibilityMonitor.InitialSample ?? new MeasurementVisibilitySample(),
                "the benchmark window did not qualify when measurement began");
            throw new OperationCanceledException(_measurementInvalidation.CancellationToken);
        }
    }

    private MachineMetadata CaptureMachine(IntPtr windowHandle, bool configureProcessPowerThrottling)
        => MachineProbe.Capture(
            windowHandle,
            CanvasDevice.GetSharedDevice(),
            _host.XamlRoot?.RasterizationScale ?? 0,
            _host.ActualWidth,
            _host.ActualHeight,
            configureProcessPowerThrottling);

    private void RecordObservedRefreshRate()
    {
        _report.Machine.ObservedRefreshRateHz =
            MachineEvidenceValidator.CalculateObservedRefreshRateHz(
                _report.Scroll);
        _report.Machine.IsAtLeast120Hz =
            MachineEvidenceValidator.IsConfiguredRefreshRateQualified(
                _report.Machine.ConfiguredRefreshRateHz) &&
            _report.Machine.ObservedRefreshRateHz >= Math.Max(
                115,
                _report.Machine.ConfiguredRefreshRateHz * 0.95);
    }

    private int RecordInvalidEnvironment()
    {
        MeasurementVisibilitySample sample =
            _measurementInvalidation.FirstUnqualifiedSample!;
        string reason = _measurementInvalidation.FirstViolationReason ??
            MeasurementVisibilityEvidenceValidator.DescribeUnqualifiedSample(sample);
        Debug.WriteLine($"[PerfHarness] measurement environment invalidated: {reason}");
        TryCompleteVisibilityMonitoring();
        _report.CompletedUtc = _utcClock.GetUtcNow();
        _report.Failures.Add(
            $"Invalid measurement environment: {reason} (source={sample.Source}, elapsedTicks={sample.ElapsedTicks}). " +
            "Renderer performance gates were not evaluated.");
        _report.Passed = false;
        return 4;
    }

    /// <summary>
    /// Writes the stage-profile sidecar (when enabled) and the report, prints
    /// the console summary, and returns the possibly updated exit code.
    /// </summary>
    private async Task<int> WriteOutputsAsync(int exitCode)
    {
        try
        {
            if (_stageProfile.IsEnabled)
            {
                try
                {
                    _stageProfile.WriteArtifact(_report, _options.OutputPath);
                    Console.WriteLine($"First-viewport stage profile: {_stageProfile.Path}");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Could not write first-viewport stage profile: {exception}");
                    _report.Failures.Add($"Could not write first-viewport stage profile artifact: {exception}");
                    _report.Passed = false;
                    exitCode = 2;
                }
            }

            await PerformanceReportWriter.WriteNewAsync(_options.OutputPath, _report);
            Console.WriteLine($"Performance report: {_options.OutputPath}");
            Console.WriteLine(exitCode == 4
                ? "Invalid measurement environment; renderer performance gates were not evaluated."
                : _options.Quick
                ? _report.Passed
                    ? "Quick performance smoke completed; release gates were not evaluated."
                    : "Quick performance smoke failed."
                : _report.Passed
                    ? "All release performance gates passed."
                    : "One or more release performance gates failed.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not write performance report: {exception}");
            exitCode = 2;
        }

        return exitCode;
    }

    private void TryCompleteVisibilityMonitoring()
    {
        try
        {
            CompleteVisibilityMonitoring();
        }
        catch (Exception visibilityException)
        {
            _report.Failures.Add(
                $"Could not finalize measurement visibility evidence: {visibilityException}");
        }
    }

    private void CompleteVisibilityMonitoring()
    {
        MeasurementVisibilityMonitor? monitor =
            Interlocked.Exchange(ref _visibilityMonitor, null);
        if (monitor is null)
            return;

        _report.MeasurementVisibility = monitor.Complete();
    }

    private async Task WarmRuntimeAsync()
    {
        MarkdownEngine engine = PerformanceEngineFactory.Create(100 * 1024);
        await _rendererHost.ReplaceRendererAsync(engine);
        string source = PerformanceDocumentFactory.Create(100 * 1024, -1);
        long sequence = _events.CurrentSequence;
        _rendererHost.Renderer!.Markdown = source;
        _ = await _events.WaitForFirstViewportAsync(
            sequence,
            100 * 1024,
            MeasurementRendererHost.ViewportTimeout);
        await _rendererHost.WaitForCompositionFramesAsync(8);
        await _rendererHost.DisposeRendererAsync();
        await _rendererHost.ForceCollectionAsync();
    }

    private async Task NormalizeMeasurementWindowAsync()
    {
        double scale = _host.XamlRoot?.RasterizationScale ?? 1.0;
        _window.AppWindow.Resize(new SizeInt32(
            (int)Math.Round(MeasurementWidthDips * scale),
            (int)Math.Round(MeasurementHeightDips * scale)));
        await _rendererHost.WaitForCompositionFramesAsync(4);
    }
}
