using System;
using System.Diagnostics;
using System.Threading;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Diagnostic-only probe for the first layout build captured by a performance
/// session. Its timer performs no XAML/UIA writes; it only updates the
/// diagnostics accumulator from the dispatcher thread.
/// </summary>
internal sealed class InitialLayoutDispatcherProbe : IDisposable
{
    private readonly WeakReference<PipelineTimings> _timings;
    private readonly MarkdownLayoutDiagnostics _diagnostics;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly DispatcherQueueHandler _sentinelHandler;
    private DispatcherQueueTimer? _heartbeatTimer;
    private int _stopped;

    internal InitialLayoutDispatcherProbe(
        PipelineTimings timings,
        DispatcherQueue? dispatcherQueue,
        MarkdownLayoutDiagnostics diagnostics)
    {
        _timings = new WeakReference<PipelineTimings>(timings);
        _diagnostics = diagnostics;
        _dispatcherQueue = dispatcherQueue;
        _sentinelHandler = OnSentinelDispatched;
        if (dispatcherQueue is null)
            return;

        try
        {
            DispatcherQueueTimer timer = dispatcherQueue.CreateTimer();
            _heartbeatTimer = timer;
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.IsRepeating = true;
            timer.Tick += OnHeartbeatTick;
            timer.Start();
            diagnostics.StartDispatcherHeartbeat(Stopwatch.GetTimestamp());
        }
        catch
        {
            // Diagnostics must not change layout behavior if the optional
            // dispatcher timer cannot be created or started.
            StopHeartbeatTimer();
        }
    }

    internal void EnqueueWorkerCompletionSentinel(long workerCompletionTimestamp)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return;

        bool enqueued = false;
        try
        {
            enqueued = _dispatcherQueue?.TryEnqueue(
                DispatcherQueuePriority.Normal,
                _sentinelHandler) == true;
        }
        catch
        {
            // A failed diagnostic enqueue is recorded, not a layout failure.
        }

        _diagnostics.RecordDispatcherSentinelEnqueue(workerCompletionTimestamp, enqueued);
    }

    internal MarkdownInitialLayoutDispatcherDiagnosticsSnapshot StopAndCapture()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            StopHeartbeatTimer();
            _diagnostics.RecordDispatcherHeartbeat(Stopwatch.GetTimestamp());
        }

        return _diagnostics.CaptureDispatcher(freezeHeartbeat: true);
    }

    public void Dispose() => StopAndCapture();

    private void OnHeartbeatTick(DispatcherQueueTimer sender, object args) =>
        _diagnostics.RecordDispatcherHeartbeat(Stopwatch.GetTimestamp());

    private void OnSentinelDispatched()
    {
        _diagnostics.RecordDispatcherSentinelDispatched(Stopwatch.GetTimestamp());
        if (_timings.TryGetTarget(out PipelineTimings? timings))
            timings.OnInitialDispatcherSentinelDispatched(_diagnostics);
    }

    private void StopHeartbeatTimer()
    {
        if (_heartbeatTimer is not { } timer)
            return;

        _heartbeatTimer = null;
        try
        {
            timer.Stop();
            timer.Tick -= OnHeartbeatTick;
        }
        catch
        {
            // Stopping an optional diagnostic timer must remain best-effort.
        }
    }
}
