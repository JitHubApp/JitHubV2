using System.Diagnostics;
using static MarkdownRenderer.PerformanceHarness.DesktopVisibilityNativeMethods;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Samples the active desktop without taking screenshots. The monitor only
/// keeps window geometry and visibility flags; it never records app content,
/// window titles, or pixels.
/// </summary>
public sealed class MeasurementVisibilityMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _hooksReady = new(false);
    private readonly IntPtr _targetWindow;
    private readonly TargetWindowVisibilityProbe _probe;
    // Each monitor routes its own hooks through its own delegate, so no
    // process-wide registration is needed to find the active monitor. The
    // field keeps the marshaled callback alive while the hooks exist.
    private readonly WinEventDelegate _winEventCallback;
    private readonly Stopwatch _stopwatch = new();
    private readonly MonotonicUtcClock _utcClock = new();
    private readonly List<MeasurementVisibilitySample> _samples = [];
    private readonly MeasurementVisibilityInvalidation _invalidation;
    private Timer? _timer;
    private Thread? _eventThread;
    private readonly IntPtr[] _hooks = new IntPtr[3];
    private int _foregroundLossEvents;
    private int _desktopSwitchEvents;
    private int _captureFailures;
    private uint _eventThreadId;
    private long _lastSampleTicks = -1;
    private long _durationTicks;
    private DateTimeOffset _completedUtc;
    private volatile bool _started;
    private volatile bool _completed;

    private MeasurementVisibilityMonitor(
        IntPtr targetWindow,
        MeasurementVisibilityInvalidation invalidation)
    {
        _targetWindow = targetWindow;
        _invalidation = invalidation;
        _probe = new TargetWindowVisibilityProbe(targetWindow);
        _winEventCallback = OnWinEvent;
    }

    private DateTimeOffset StartedUtc { get; set; }

    public MeasurementVisibilitySample? InitialSample
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count > 0 && _samples[0].Source == "start"
                    ? _samples[0]
                    : null;
            }
        }
    }

    public static MeasurementVisibilityMonitor Start(
        IntPtr targetWindow,
        MeasurementVisibilityInvalidation invalidation)
    {
        if (targetWindow == IntPtr.Zero)
            throw new ArgumentException("A valid benchmark HWND is required.", nameof(targetWindow));
        ArgumentNullException.ThrowIfNull(invalidation);
        var monitor = new MeasurementVisibilityMonitor(targetWindow, invalidation);

        // WinEvent callbacks are dispatched to the thread that installed the
        // out-of-context hooks. Keep that thread separate from the WinUI UI
        // thread so window enumeration and DWM queries cannot perturb the
        // very UI timings this harness is measuring.
        try
        {
            monitor._eventThread = new Thread(monitor.RunEventHookMessageLoop)
            {
                IsBackground = true,
                Name = "Markdown performance visibility monitor",
            };
            monitor._eventThread.Start();
            // Hook installation belongs to setup, not the measured interval.
            // A timeout leaves the corresponding hook flags false and makes
            // release evidence fail closed without aborting report creation.
            _ = monitor._hooksReady.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            lock (monitor._gate)
                monitor._captureFailures++;
        }

        MeasurementVisibilitySample? startSample = null;
        lock (monitor._gate)
        {
            monitor._stopwatch.Start();
            monitor.StartedUtc = monitor._utcClock.GetUtcNow();
            monitor._started = true;
            try
            {
                startSample = monitor.RecordSampleLocked("start");
                monitor._timer = new Timer(
                    static state => ((MeasurementVisibilityMonitor)state!).RecordSample("poll"),
                    monitor,
                    MeasurementVisibilityEvidence.PollIntervalMilliseconds,
                    MeasurementVisibilityEvidence.PollIntervalMilliseconds);
            }
            catch
            {
                // Preserve a failing-but-serializable report if monitoring
                // resources cannot be allocated; do not abort before the
                // harness can write release diagnostics.
                monitor._captureFailures++;
            }
        }
        if (startSample is not null)
            _ = invalidation.TryInvalidate(startSample);
        return monitor;
    }

    public static MeasurementVisibilitySample CaptureReadinessSample(IntPtr targetWindow)
    {
        return new TargetWindowVisibilityProbe(targetWindow).Capture("preflight", elapsedTicks: 0);
    }

    public MeasurementVisibilityEvidence Complete()
    {
        uint eventThreadId;
        Timer? timer = null;
        MeasurementVisibilitySample? completionSample = null;
        lock (_gate)
        {
            if (!_completed)
            {
                timer = _timer;
                _timer = null;
                if (_started)
                {
                    completionSample = RecordSampleLocked("complete");
                    _stopwatch.Stop();
                    _durationTicks = Math.Max(_stopwatch.ElapsedTicks, _lastSampleTicks);
                }
                _completedUtc = _utcClock.GetUtcNow();
                _completed = true;
            }
            eventThreadId = _eventThreadId;
        }

        if (completionSample is not null)
            _ = _invalidation.TryInvalidate(completionSample);
        if (timer is not null)
        {
            using var callbacksDrained = new ManualResetEvent(initialState: false);
            if (timer.Dispose(callbacksDrained) &&
                !callbacksDrained.WaitOne(TimeSpan.FromSeconds(2)))
            {
                lock (_gate)
                    _captureFailures++;
            }
        }

        if (eventThreadId != 0)
            _ = PostThreadMessage(eventThreadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        // Shutdown is outside the measured interval. Waiting here avoids
        // leaving desktop hooks or a message-pump thread behind.
        _ = _eventThread?.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            return new MeasurementVisibilityEvidence
            {
                StartedUtc = StartedUtc,
                CompletedUtc = _completedUtc,
                StopwatchFrequency = Stopwatch.Frequency,
                DurationTicks = _durationTicks,
                ForegroundHookInstalled = _hooks[0] != IntPtr.Zero,
                DesktopSwitchHookInstalled = _hooks[1] != IntPtr.Zero,
                WindowEventHookInstalled = _hooks[2] != IntPtr.Zero,
                ForegroundLossEvents = _foregroundLossEvents,
                DesktopSwitchEvents = _desktopSwitchEvents,
                CaptureFailures = _captureFailures,
                Samples = [.. _samples],
            };
        }
    }

    public void Dispose()
    {
        _ = Complete();
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_completed)
            return;

        try
        {
            ProcessWinEvent(eventType, hwnd, objectId, childId);
        }
        catch
        {
            // Do not allow an exception to cross the unmanaged callback
            // boundary. A release run is invalid if event capture failed.
            lock (_gate)
            {
                if (_started && !_completed)
                    _captureFailures++;
            }
        }
    }

    private void ProcessWinEvent(uint eventType, IntPtr hwnd, int objectId, int childId)
    {
        if (eventType == EventSystemDesktopSwitch)
        {
            MeasurementVisibilitySample? sample;
            lock (_gate)
            {
                if (!_started || _completed)
                    return;
                _desktopSwitchEvents++;
                sample = RecordSampleLocked("desktop-switch");
            }
            if (sample is not null)
                _ = _invalidation.TryInvalidate(
                    sample,
                    "the active desktop changed during measurement");
            return;
        }

        if (eventType == EventSystemForeground)
        {
            MeasurementVisibilitySample? sample;
            string? irreversibleViolationReason;
            lock (_gate)
            {
                if (!_started || _completed)
                    return;
                bool isTargetForeground = _probe.IsTargetForeground(hwnd);
                if (!isTargetForeground)
                    _foregroundLossEvents++;
                irreversibleViolationReason = isTargetForeground
                    ? null
                    : "the benchmark window lost foreground";
                sample = RecordSampleLocked("foreground-event");
            }
            if (sample is not null)
                _ = _invalidation.TryInvalidate(sample, irreversibleViolationReason);
            return;
        }

        if (objectId != ObjectIdWindow || childId != ChildIdSelf)
            return;

        bool canIntroduceOcclusion = eventType is
            EventObjectShow or EventObjectReorder or EventObjectLocationChange;
        bool mayOcclude = canIntroduceOcclusion && _probe.MayOccludeTarget(hwnd);
        if (hwnd == _targetWindow || mayOcclude)
        {
            // The event is only a trigger to take a fresh sample. A window
            // moving through an overlapping rectangle does not itself prove
            // that the benchmark lost coverage; the captured unoccluded
            // predicate remains authoritative for this event class.
            RecordSample("window-event");
        }
    }

    private void RunEventHookMessageLoop()
    {
        // Force creation of this thread's message queue before publishing its
        // ID so Complete can always post WM_QUIT after the measured interval.
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PeekMessageNoRemove);
        _eventThreadId = GetCurrentThreadId();
        try
        {
            _hooks[0] = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                WineventOutOfContext);
            _hooks[1] = SetWinEventHook(
                EventSystemDesktopSwitch,
                EventSystemDesktopSwitch,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                WineventOutOfContext);
            _hooks[2] = SetWinEventHook(
                EventObjectShow,
                EventObjectLocationChange,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                WineventOutOfContext);
        }
        catch
        {
            lock (_gate)
                _captureFailures++;
        }
        finally
        {
            _hooksReady.Set();
        }

        try
        {
            int messageResult = 0;
            while (!_completed && (messageResult = GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0)) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }

            if (messageResult < 0)
            {
                lock (_gate)
                {
                    if (_started && !_completed)
                        _captureFailures++;
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                if (_started && !_completed)
                    _captureFailures++;
            }
        }

        foreach (IntPtr hook in _hooks)
        {
            if (hook != IntPtr.Zero)
                _ = UnhookWinEvent(hook);
        }
        GC.KeepAlive(_winEventCallback);
    }

    private void RecordSample(string source, string? irreversibleViolationReason = null)
    {
        MeasurementVisibilitySample? sample;
        lock (_gate)
        {
            if (!_started || _completed)
                return;
            sample = RecordSampleLocked(source);
        }
        if (sample is not null)
            _ = _invalidation.TryInvalidate(sample, irreversibleViolationReason);
    }

    private MeasurementVisibilitySample? RecordSampleLocked(string source)
    {
        if (_samples.Count >= 500_000)
        {
            // A burst of desktop events must never grow benchmark evidence
            // without bound. The release validator rejects this capture.
            _captureFailures++;
            return null;
        }

        long elapsedTicks = Math.Max(_stopwatch.ElapsedTicks, _lastSampleTicks + 1);
        MeasurementVisibilitySample sample = _probe.Capture(source, elapsedTicks);
        if (!sample.CaptureSucceeded)
            _captureFailures++;
        _samples.Add(sample);
        _lastSampleTicks = elapsedTicks;
        return sample;
    }
}
