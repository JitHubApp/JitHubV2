using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Records whether the benchmark window stayed foreground, visible, and
/// unobstructed for the full measurement interval. A 250 ms poll is backed by
/// WinEvent hooks so brief foreground changes and window-covering events are
/// recorded immediately rather than inferred from the start/end snapshots.
/// </summary>
internal sealed class MeasurementVisibilityEvidence
{
    internal const string CurrentPolicy = "foreground-visible-unoccluded-v1";
    internal const int PollIntervalMilliseconds = 250;
    internal const int MaximumSampleGapMilliseconds = 1_000;

    public string Policy { get; init; } = CurrentPolicy;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public long StopwatchFrequency { get; init; }
    public long DurationTicks { get; init; }
    public int PollIntervalMs { get; init; } = PollIntervalMilliseconds;
    public int MaximumSampleGapMs { get; init; } = MaximumSampleGapMilliseconds;
    public bool ForegroundHookInstalled { get; init; }
    public bool DesktopSwitchHookInstalled { get; init; }
    public bool WindowEventHookInstalled { get; init; }
    public int ForegroundLossEvents { get; init; }
    public int DesktopSwitchEvents { get; init; }
    public int CaptureFailures { get; init; }
    public List<MeasurementVisibilitySample> Samples { get; init; } = [];
}

internal sealed class MeasurementVisibilitySample
{
    public long ElapsedTicks { get; init; }
    public string Source { get; init; } = string.Empty;
    public bool CaptureSucceeded { get; init; }
    public bool TargetWasForeground { get; init; }
    public bool TargetWasVisible { get; init; }
    public bool TargetWasMinimized { get; init; }
    public bool TargetWasCloaked { get; init; }
    public bool TargetWasWithinWorkArea { get; init; }
    public bool TargetWasUnoccluded { get; init; }
}

internal static class MeasurementVisibilityEvidenceValidator
{
    internal static bool IsValid(
        MeasurementVisibilityEvidence? evidence,
        DateTimeOffset reportStartedUtc,
        DateTimeOffset reportCompletedUtc,
        out string failure)
    {
        failure = string.Empty;
        if (evidence is null || evidence.Samples is null)
        {
            failure = "Measurement visibility evidence was missing.";
            return false;
        }

        if (evidence.Policy != MeasurementVisibilityEvidence.CurrentPolicy ||
            evidence.PollIntervalMs != MeasurementVisibilityEvidence.PollIntervalMilliseconds ||
            evidence.MaximumSampleGapMs != MeasurementVisibilityEvidence.MaximumSampleGapMilliseconds)
        {
            failure = "Measurement visibility evidence used an unknown or altered policy.";
            return false;
        }

        if (reportStartedUtc == default || reportCompletedUtc <= reportStartedUtc ||
            reportStartedUtc.Offset != TimeSpan.Zero || reportCompletedUtc.Offset != TimeSpan.Zero ||
            evidence.StartedUtc <= reportStartedUtc ||
            evidence.CompletedUtc < evidence.StartedUtc ||
            evidence.CompletedUtc > reportCompletedUtc ||
            evidence.StartedUtc.Offset != TimeSpan.Zero ||
            evidence.CompletedUtc.Offset != TimeSpan.Zero)
        {
            failure = "Measurement visibility timestamps did not fit inside the report interval.";
            return false;
        }

        if (evidence.StopwatchFrequency <= 0 || evidence.StopwatchFrequency > 1_000_000_000 ||
            evidence.DurationTicks <= 0 ||
            evidence.DurationTicks > evidence.StopwatchFrequency * 86_400L ||
            !evidence.ForegroundHookInstalled ||
            !evidence.DesktopSwitchHookInstalled ||
            !evidence.WindowEventHookInstalled ||
            evidence.ForegroundLossEvents != 0 ||
            evidence.DesktopSwitchEvents != 0 ||
            evidence.CaptureFailures != 0)
        {
            failure = "Visibility monitoring was unavailable or recorded foreground, desktop, or capture failures.";
            return false;
        }

        IReadOnlyList<MeasurementVisibilitySample> samples = evidence.Samples;
        if (samples.Count < 2 || samples.Count > 500_000)
        {
            failure = "Visibility evidence did not cover the measurement interval.";
            return false;
        }

        long maximumGapTicks = evidence.StopwatchFrequency *
            evidence.MaximumSampleGapMs / 1_000;
        long firstMaximumTicks = Math.Max(1, evidence.StopwatchFrequency / 2);
        long lastSampleMaximumAgeTicks = maximumGapTicks;
        long previousTicks = -1;
        foreach (MeasurementVisibilitySample sample in samples)
        {
            if (sample is null || !IsSafeSampleSource(sample.Source) || sample.ElapsedTicks < 0 ||
                sample.ElapsedTicks <= previousTicks ||
                sample.ElapsedTicks > evidence.DurationTicks)
            {
                failure = "Visibility samples had an unknown source, were unordered, or fell outside their monotonic interval.";
                return false;
            }

            if (!sample.CaptureSucceeded ||
                !sample.TargetWasForeground ||
                !sample.TargetWasVisible ||
                sample.TargetWasMinimized ||
                sample.TargetWasCloaked ||
                !sample.TargetWasWithinWorkArea ||
                !sample.TargetWasUnoccluded)
            {
                failure = "The benchmark window lost foreground, visibility, or unobstructed coverage during measurement.";
                return false;
            }

            if (previousTicks >= 0 && sample.ElapsedTicks - previousTicks > maximumGapTicks)
            {
                failure = "Visibility sampling left an uncovered gap in the measurement interval.";
                return false;
            }

            previousTicks = sample.ElapsedTicks;
        }

        if (samples[0].ElapsedTicks > firstMaximumTicks ||
            evidence.DurationTicks - samples[^1].ElapsedTicks > lastSampleMaximumAgeTicks)
        {
            failure = "Visibility samples did not bracket the complete measurement interval.";
            return false;
        }

        TimeSpan measuredDuration = TimeSpan.FromSeconds(
            (double)evidence.DurationTicks / evidence.StopwatchFrequency);
        TimeSpan recordedDuration = evidence.CompletedUtc - evidence.StartedUtc;
        if (Math.Abs((measuredDuration - recordedDuration).TotalMilliseconds) > 1_000)
        {
            failure = "Monotonic and UTC visibility durations disagreed by more than one second.";
            return false;
        }

        return true;
    }

    private static bool IsSafeSampleSource(string source)
        => source is "start" or "complete" or "poll" or "desktop-switch" or
            "foreground-event" or "window-event";
}

/// <summary>
/// Samples the active desktop without taking screenshots. The monitor only
/// keeps window geometry and visibility flags; it never records app content,
/// window titles, or pixels.
/// </summary>
internal sealed class MeasurementVisibilityMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemDesktopSwitch = 0x0020;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectReorder = 0x8004;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint WineventOutOfContext = 0;
    private const uint WmQuit = 0x0012;
    private const uint PeekMessageNoRemove = 0;
    private const uint DwmwaCloaked = 14;
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint MonitorDefaultToNearest = 2;

    private static readonly WinEventDelegate WinEventCallback = OnWinEvent;
    private static MeasurementVisibilityMonitor? s_activeMonitor;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _hooksReady = new(false);
    private readonly IntPtr _targetWindow;
    private readonly int _targetProcessId;
    private readonly Stopwatch _stopwatch = new();
    private readonly MonotonicUtcClock _utcClock = new();
    private readonly List<MeasurementVisibilitySample> _samples = [];
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

    private MeasurementVisibilityMonitor(IntPtr targetWindow)
    {
        _targetWindow = targetWindow;
        _ = GetWindowThreadProcessId(targetWindow, out uint targetProcessId);
        _targetProcessId = checked((int)targetProcessId);
    }

    private DateTimeOffset StartedUtc { get; set; }

    internal static MeasurementVisibilityMonitor Start(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero)
            throw new ArgumentException("A valid benchmark HWND is required.", nameof(targetWindow));
        var monitor = new MeasurementVisibilityMonitor(targetWindow);
        if (Interlocked.CompareExchange(ref s_activeMonitor, monitor, null) is not null)
        {
            monitor.Dispose();
            throw new InvalidOperationException("A desktop visibility monitor is already active.");
        }

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

        lock (monitor._gate)
        {
            monitor._stopwatch.Start();
            monitor.StartedUtc = monitor._utcClock.GetUtcNow();
            monitor._started = true;
            try
            {
                monitor.RecordSampleLocked("start");
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
        return monitor;
    }

    internal MeasurementVisibilityEvidence Complete()
    {
        uint eventThreadId;
        lock (_gate)
        {
            if (!_completed)
            {
                _timer?.Dispose();
                if (_started)
                {
                    RecordSampleLocked("complete");
                    _stopwatch.Stop();
                    _durationTicks = Math.Max(_stopwatch.ElapsedTicks, _lastSampleTicks);
                }
                _completedUtc = _utcClock.GetUtcNow();
                _completed = true;
                _ = Interlocked.CompareExchange(ref s_activeMonitor, null, this);
            }
            eventThreadId = _eventThreadId;
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

    private static void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        MeasurementVisibilityMonitor? monitor = Volatile.Read(ref s_activeMonitor);
        if (monitor is null || monitor._completed)
            return;

        try
        {
            monitor.ProcessWinEvent(eventType, hwnd, objectId, childId);
        }
        catch
        {
            // Do not allow an exception to cross the unmanaged callback
            // boundary. A release run is invalid if event capture failed.
            lock (monitor._gate)
            {
                if (monitor._started && !monitor._completed)
                    monitor._captureFailures++;
            }
        }
    }

    private void ProcessWinEvent(uint eventType, IntPtr hwnd, int objectId, int childId)
    {
        if (eventType == EventSystemDesktopSwitch)
        {
            lock (_gate)
            {
                if (!_started || _completed)
                    return;
                _desktopSwitchEvents++;
                RecordSampleLocked("desktop-switch");
            }
            return;
        }

        if (eventType == EventSystemForeground)
        {
            lock (_gate)
            {
                if (!_started || _completed)
                    return;
                bool isTargetForeground = IsTargetForeground(hwnd);
                if (!isTargetForeground)
                    _foregroundLossEvents++;
                RecordSampleLocked("foreground-event");
            }
            return;
        }

        if (objectId != ObjectIdWindow || childId != ChildIdSelf)
            return;

        bool canIntroduceOcclusion = eventType is
            EventObjectShow or EventObjectReorder or EventObjectLocationChange;
        if (hwnd == _targetWindow ||
            (canIntroduceOcclusion && MayOccludeTarget(hwnd)))
        {
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
                WinEventCallback,
                0,
                0,
                WineventOutOfContext);
            _hooks[1] = SetWinEventHook(
                EventSystemDesktopSwitch,
                EventSystemDesktopSwitch,
                IntPtr.Zero,
                WinEventCallback,
                0,
                0,
                WineventOutOfContext);
            _hooks[2] = SetWinEventHook(
                EventObjectShow,
                EventObjectLocationChange,
                IntPtr.Zero,
                WinEventCallback,
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
    }

    private void RecordSample(string source)
    {
        lock (_gate)
        {
            if (_started && !_completed)
                RecordSampleLocked(source);
        }
    }

    private void RecordSampleLocked(string source)
    {
        if (_samples.Count >= 500_000)
        {
            // A burst of desktop events must never grow benchmark evidence
            // without bound. The release validator rejects this capture.
            _captureFailures++;
            return;
        }

        long elapsedTicks = Math.Max(_stopwatch.ElapsedTicks, _lastSampleTicks + 1);
        bool captureSucceeded = false;
        bool targetWasForeground = false;
        bool targetWasVisible = false;
        bool targetWasMinimized = false;
        bool targetWasCloaked = false;
        bool targetWasWithinWorkArea = false;
        bool targetWasUnoccluded = false;
        try
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            targetWasForeground = IsTargetForeground(foregroundWindow);
            targetWasVisible = IsWindowVisible(_targetWindow);
            targetWasMinimized = IsIconic(_targetWindow);
            int cloakResult = DwmGetWindowAttribute(
                _targetWindow,
                DwmwaCloaked,
                out uint cloaked,
                sizeof(uint));
            if (cloakResult < 0)
                throw new InvalidOperationException("DWM could not report whether the benchmark window is cloaked.");
            targetWasCloaked = cloaked != 0;

            if (!TryGetVisibleFrameBounds(_targetWindow, out NativeRect targetRect))
                throw new InvalidOperationException("DWM could not report the benchmark window's visible frame bounds.");
            IntPtr monitor = MonitorFromWindow(_targetWindow, MonitorDefaultToNearest);
            var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
                throw new InvalidOperationException("Windows could not report the benchmark monitor work area.");

            NativeRect workArea = monitorInfo.WorkArea;
            targetWasWithinWorkArea = targetRect.Left >= workArea.Left &&
                targetRect.Top >= workArea.Top &&
                targetRect.Right <= workArea.Right &&
                targetRect.Bottom <= workArea.Bottom;
            targetWasUnoccluded = !IsOccludedByWindowAbove(targetRect);
            captureSucceeded = true;
        }
        catch
        {
            _captureFailures++;
        }

        _samples.Add(new MeasurementVisibilitySample
        {
            ElapsedTicks = elapsedTicks,
            Source = source,
            CaptureSucceeded = captureSucceeded,
            TargetWasForeground = targetWasForeground,
            TargetWasVisible = targetWasVisible,
            TargetWasMinimized = targetWasMinimized,
            TargetWasCloaked = targetWasCloaked,
            TargetWasWithinWorkArea = targetWasWithinWorkArea,
            TargetWasUnoccluded = targetWasUnoccluded,
        });
        _lastSampleTicks = elapsedTicks;
    }

    private bool IsTargetForeground(IntPtr foregroundWindow)
    {
        if (foregroundWindow == IntPtr.Zero)
            return false;

        _ = GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcessId);
        if (foregroundProcessId != (uint)_targetProcessId)
            return false;

        IntPtr targetRootOwner = GetAncestor(_targetWindow, GetAncestorRootOwner);
        IntPtr foregroundRootOwner = GetAncestor(foregroundWindow, GetAncestorRootOwner);
        return targetRootOwner != IntPtr.Zero && foregroundRootOwner == targetRootOwner;
    }

    private bool MayOccludeTarget(IntPtr candidate)
    {
        if (candidate == IntPtr.Zero || candidate == _targetWindow ||
            !IsWindowVisible(candidate) || IsIconic(candidate))
        {
            return false;
        }

        if (!TryGetVisibleFrameBounds(_targetWindow, out NativeRect targetRect) ||
            !TryGetVisibleFrameBounds(candidate, out NativeRect candidateRect))
        {
            return true;
        }
        if (!Intersects(targetRect, candidateRect))
            return false;

        return IsAboveTargetInZOrder(candidate);
    }

    private bool IsOccludedByWindowAbove(NativeRect targetRect)
    {
        bool foundTarget = false;
        bool occluded = false;
        bool inspectionFailed = false;
        _ = EnumWindows((window, _) =>
        {
            if (window == _targetWindow)
            {
                foundTarget = true;
                return false;
            }

            if (!IsWindowVisible(window) || IsIconic(window))
                return true;

            if (!TryGetVisibleFrameBounds(window, out NativeRect windowRect))
            {
                inspectionFailed = true;
                return false;
            }

            if (Intersects(targetRect, windowRect) && !IsCloaked(window))
            {
                occluded = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        // If the target is absent from the desktop z-order, we cannot prove
        // that the application owns a visible interactive surface.
        return !foundTarget || occluded || inspectionFailed;
    }

    private bool IsAboveTargetInZOrder(IntPtr candidate)
    {
        bool candidateBeforeTarget = false;
        _ = EnumWindows((window, _) =>
        {
            if (window == _targetWindow)
                return false;
            if (window == candidate)
            {
                candidateBeforeTarget = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return candidateBeforeTarget;
    }

    private static bool IsCloaked(IntPtr window)
        => DwmGetWindowAttribute(window, DwmwaCloaked, out uint cloaked, sizeof(uint)) >= 0 &&
           cloaked != 0;

    private static bool TryGetVisibleFrameBounds(IntPtr window, out NativeRect rect)
    {
        // GetWindowRect includes invisible resize borders. DWM's extended
        // frame bounds describe the rendered edge in physical desktop pixels,
        // which keeps the work-area and occlusion tests correct across DPI.
        return DwmGetWindowAttribute(
            window,
            DwmwaExtendedFrameBounds,
            out rect,
            Marshal.SizeOf<NativeRect>()) >= 0;
    }

    private static bool Intersects(NativeRect first, NativeRect second)
        => first.Left < second.Right && first.Right > second.Left &&
           first.Top < second.Bottom && first.Bottom > second.Top;

    private const uint GetAncestorRootOwner = 3;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out uint value,
        int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out NativeRect value,
        int valueSize);
}
