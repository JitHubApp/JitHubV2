using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Diagnostics.Internal;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Owns the benchmark window's renderer slot: the one <see cref="MarkdownScrollView"/>
/// under measurement, the engine it owns, its render-failure count, and the
/// composition-frame, scroll-frame and collection waits every measurement
/// uses between its timed intervals. All members run on the UI thread.
/// </summary>
internal sealed class MeasurementRendererHost
{
    internal static readonly TimeSpan ViewportTimeout = TimeSpan.FromMinutes(3);
    private const double ScrollResetToleranceDips = 0.5;

    private readonly Grid _host;
    private readonly PerformanceEventListener _events;
    private readonly CancellationToken _measurementAbort;
    private MarkdownScrollView? _renderer;
    private MarkdownEngine? _ownedRendererEngine;
    private int _renderFailures;

    internal MeasurementRendererHost(
        Grid host,
        PerformanceEventListener events,
        CancellationToken measurementAbort)
    {
        _host = host;
        _events = events;
        _measurementAbort = measurementAbort;
    }

    /// <summary>The renderer under measurement, or null between measurements.</summary>
    internal MarkdownScrollView? Renderer => _renderer;

    /// <summary>The number of <see cref="MarkdownScrollView.RenderFailed"/> events seen so far.</summary>
    internal int RenderFailures => _renderFailures;

    /// <summary>
    /// Disposes the current renderer and installs a blank one bound to
    /// <paramref name="engine"/>, waiting for its empty first viewport.
    /// </summary>
    internal async Task ReplaceRendererAsync(
        MarkdownEngine engine,
        bool takeEngineOwnership = true)
    {
        await DisposeRendererAsync();
        await WaitForCompositionFramesAsync(2);

        long sequence = _events.CurrentSequence;
        var renderer = new MarkdownScrollView
        {
            Engine = engine,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        renderer.RenderFailed += OnRenderFailed;
        _renderer = renderer;
        _ownedRendererEngine = takeEngineOwnership ? engine : null;
        _host.Children.Add(renderer);
        _ = await _events.WaitForFirstViewportAsync(sequence, 0, ViewportTimeout);
    }

    /// <summary>
    /// Installs a renderer the caller constructed with its source already set.
    /// The caller keeps ownership of the renderer's engine.
    /// </summary>
    internal void AttachRenderer(MarkdownScrollView renderer)
    {
        renderer.RenderFailed += OnRenderFailed;
        _renderer = renderer;
        _ownedRendererEngine = null;
        _host.Children.Add(renderer);
    }

    internal async Task DisposeRendererAsync()
    {
        MarkdownEngine? ownedEngine = _ownedRendererEngine;
        _ownedRendererEngine = null;
        if (_renderer is null)
        {
            ownedEngine?.Dispose();
            return;
        }

        MarkdownScrollView renderer = _renderer;
        renderer.RenderFailed -= OnRenderFailed;
        _host.Children.Remove(renderer);
        renderer.Dispose();
        _renderer = null;
        try
        {
            await renderer.DrainRetiredSnapshotsAsync();
        }
        finally
        {
            ownedEngine?.Dispose();
        }
    }

    private void OnRenderFailed(object? sender, MarkdownRenderFailedEventArgs e)
        => _renderFailures++;

    /// <summary>
    /// Scrolls the current renderer one step per composition frame and records
    /// the interval between consecutive frames. Each frame is tagged with its
    /// logical frame id so the event ledger can attribute renderer work to it.
    /// </summary>
    internal async Task RunScrollFramesAsync(
        long firstFrameId,
        int frameCount,
        ICollection<double> intervalsMilliseconds,
        ICollection<long>? intervalElapsedTicks = null)
    {
        if (_renderer is null)
            throw new InvalidOperationException("Scroll trace requires a renderer.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long previousTimestamp = 0;
        int frame = 0;
        double position = 0;
        double direction = 1;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            if (previousTimestamp != 0)
            {
                long elapsedTicks = now - previousTimestamp;
                intervalElapsedTicks?.Add(elapsedTicks);
                intervalsMilliseconds.Add(elapsedTicks * 1000.0 / Stopwatch.Frequency);
            }

            if (frame >= frameCount)
            {
                CompositionTarget.Rendering -= handler;
                MarkdownPerformanceEventSource.SetLogicalFrameId(0);
                completion.TrySetResult();
                return;
            }

            previousTimestamp = now;
            MarkdownPerformanceEventSource.SetLogicalFrameId(firstFrameId + frame);
            if (!_renderer.AutomationScrolling.TryGetMetrics(out _, out double viewportHeight, out double extentHeight))
            {
                CompositionTarget.Rendering -= handler;
                completion.TrySetException(new InvalidOperationException("Renderer did not expose scroll metrics."));
                return;
            }

            double maximum = Math.Max(1, extentHeight - viewportHeight);
            position += direction * 96;
            if (position >= maximum)
            {
                position = maximum;
                direction = -1;
            }
            else if (position <= 0)
            {
                position = 0;
                direction = 1;
            }
            _renderer.AutomationScrolling.SetVerticalPercent(position * 100.0 / maximum);
            frame++;
        };

        CompositionTarget.Rendering += handler;
        try
        {
            await completion.Task.WaitAsync(
                TimeSpan.FromMinutes(2),
                _measurementAbort);
            await WaitForCompositionFramesAsync(4);
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
            MarkdownPerformanceEventSource.SetLogicalFrameId(0);
        }
    }

    internal Task WaitForScrollTopAsync(string operation)
        => WaitUntilAsync(
            () => _renderer is not null &&
                  _renderer.AutomationScrolling.TryGetMetrics(
                      out double verticalOffset,
                      out _,
                      out _) &&
                  double.IsFinite(verticalOffset) &&
                  Math.Abs(verticalOffset) <= ScrollResetToleranceDips,
            ViewportTimeout,
            $"{operation} to reach offset zero");

    internal async Task WaitForCompositionFramesAsync(int count)
    {
        for (int index = 0; index < count; index++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object>? handler = null;
            handler = (_, _) =>
            {
                CompositionTarget.Rendering -= handler;
                completion.TrySetResult();
            };
            CompositionTarget.Rendering += handler;
            try
            {
                await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    _measurementAbort);
            }
            finally
            {
                CompositionTarget.Rendering -= handler;
            }
        }
    }

    internal async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            _measurementAbort.ThrowIfCancellationRequested();
            if (condition())
                return;
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                throw new TimeoutException($"Timed out waiting for {description}.");
            await Task.Delay(10, _measurementAbort);
        }
    }

    /// <summary>
    /// Trims the shared Win2D device and forces two full blocking compacting
    /// collections, so a memory checkpoint sees only retained objects.
    /// </summary>
    internal async Task ForceCollectionAsync()
    {
        await WaitForCompositionFramesAsync(3);
        try
        {
            CanvasDevice.GetSharedDevice().Trim();
        }
        catch
        {
            // Trim only releases cached device memory before a checkpoint. A
            // lost or unavailable shared device has nothing to trim, and the
            // checkpoint that follows still measures what is retained.
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// Forces two full blocking compacting collections between composition
    /// frames, so a timed interval does not start with pending garbage.
    /// </summary>
    internal async Task ForceManagedCollectionAsync()
    {
        await WaitForCompositionFramesAsync(3);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        await WaitForCompositionFramesAsync(3);
    }

    internal static (long Managed, long Private) CaptureMemory()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return (GC.GetTotalMemory(forceFullCollection: false), process.PrivateMemorySize64);
    }
}
