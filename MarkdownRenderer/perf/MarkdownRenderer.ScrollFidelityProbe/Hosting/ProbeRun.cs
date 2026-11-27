using System.Text.Json;
using MarkdownRenderer.ScrollFidelityProbe.Capture;
using MarkdownRenderer.ScrollFidelityProbe.Corpus;
using MarkdownRenderer.ScrollFidelityProbe.Reports;
using MarkdownRenderer.ScrollFidelityProbe.Scenarios;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.ScrollFidelityProbe.Hosting;

/// <summary>
/// Runs every selected scenario against a fresh renderer while capturing the
/// window, calibrates the blank-band threshold on settled frames, and writes
/// the report. The process exits when the report is written.
/// </summary>
internal sealed class ProbeRun
{
    private const int SettleAfterScenarioMilliseconds = 1500;
    private const int PauseBeforeScrollMilliseconds = 300;

    private static readonly IScrollScenario[] Catalog =
    [
        new WheelStormScenario(),
        new FlingScenario(),
        new DragScenario(),
        new JumpScenario(),
    ];

    private readonly Window _window;
    private readonly ProbeOptions _options;
    private readonly ProbeRendererHost _host;
    private readonly WindowFrameCapture _capture = new();
    private readonly CancellationTokenSource _abort = new(TimeSpan.FromMinutes(10));
    private readonly ProbeReport _report;

    internal ProbeRun(Window window, Grid host, ProbeOptions options)
    {
        _window = window;
        _options = options;
        _host = new ProbeRendererHost(host, options.Progressive);
        _report = new ProbeReport
        {
            Label = options.Label,
            StartedUtc = DateTimeOffset.UtcNow,
            PerformanceSession = options.Progressive,
        };
    }

    internal async Task RunAsync()
    {
        try
        {
            await MeasureAsync();
        }
        catch (Exception exception)
        {
            _report.Failures.Add(exception.ToString());
        }
        finally
        {
            _capture.Dispose();
            WriteReport();
            Application.Current.Exit();
        }
    }

    internal void WriteFailure(Exception exception)
    {
        _report.Failures.Add(exception.ToString());
        WriteReport();
    }

    private async Task MeasureAsync()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        string source = LongReadmeCorpus.Create(_options.CorpusKib * 1024);
        _report.CorpusUtf16Characters = source.Length;
        await Task.Delay(500, _abort.Token);
        _capture.Start(hwnd);

        var measured = new List<(IScrollScenario Scenario, double FirstRenderMs, List<FrameSample> Frames, ScrollRecorder Recorder)>();
        foreach (IScrollScenario scenario in Catalog.Where(item => _options.Scenarios.Contains(item.Name)))
        {
            (ScenarioContext context, double firstRenderMs) = await LoadAsync(source, hwnd);
            await Task.Delay(PauseBeforeScrollMilliseconds, _abort.Token);
            using var recorder = new ScrollRecorder(context.ScrollViewer);
            recorder.Start();
            _capture.BeginRecording();
            await scenario.RunAsync(context, recorder);
            await Task.Delay(SettleAfterScenarioMilliseconds, _abort.Token);
            List<FrameSample> frames = _capture.EndRecording();
            recorder.Dispose();
            measured.Add((scenario, firstRenderMs, frames, recorder));
        }

        (ScenarioContext calibration, _) = await LoadAsync(source, hwnd);
        int longest = await CalibrationSweep.MeasureLongestSettledRunAsync(calibration, _capture);
        int threshold = longest + Math.Max(8, (int)Math.Ceiling(6 * calibration.RasterizationScale));
        _report.CalibrationLongestUniformRunPx = longest;
        _report.BlankRunThresholdPx = threshold;
        _report.CapturedFrames = _capture.FramesSeen;
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            _report.PeakWorkingSetMb = process.PeakWorkingSet64 / 1048576.0;
            _report.PrivateMemoryMbAfterCalibration = process.PrivateMemorySize64 / 1048576.0;
        }

        await _host.DisposeRendererAsync();

        foreach (var (scenario, firstRenderMs, frames, recorder) in measured)
            _report.Scenarios.Add(ScenarioResultBuilder.Build(scenario, firstRenderMs, frames, recorder, threshold));
    }

    private async Task<(ScenarioContext Context, double FirstRenderMs)> LoadAsync(string source, nint hwnd)
    {
        double firstRenderMs = await _host.LoadFreshAsync(source, _abort.Token);
        double scale = _window.Content.XamlRoot?.RasterizationScale ?? 1.0;
        Rect region = _host.GetCaptureRegion(hwnd, scale);
        _capture.SetRegion(region);
        _report.RasterizationScale = scale;
        _report.ViewportWidthPx = (int)region.Width;
        _report.ViewportHeightPx = (int)region.Height;
        return (new ScenarioContext(_host, hwnd, scale, _abort.Token), firstRenderMs);
    }

    private void WriteReport()
    {
        string directory = Path.GetDirectoryName(_options.OutputPath)!;
        Directory.CreateDirectory(directory);
        string temporary = _options.OutputPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_report, ProbeReportJsonContext.Default.ProbeReport));
        File.Move(temporary, _options.OutputPath, overwrite: true);
    }
}
