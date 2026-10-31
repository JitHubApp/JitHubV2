using System.Diagnostics;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.PerformanceHarness;

using Math = System.Math;

public sealed class PerformanceEventListener : IMarkdownPerformanceSink, IDisposable
{
    private readonly object _gate = new();
    private readonly List<PipelineEvent> _pipelines = [];
    private readonly List<FirstViewportEvent> _firstViewports = [];
    private readonly List<SupersessionEvent> _supersessions = [];
    private readonly List<CancellationEvent> _cancellations = [];
    private readonly Dictionary<long, FrameWork> _frames = [];
    private readonly List<string> _providerErrors = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _sequence;
    private CancellationToken _measurementEnvironmentAbortToken;
    private FirstViewportStageProfileCapture? _stageProfileCapture;
    private FirstViewportStageProfileObservation? _stageProfileObservation;
    private bool _disposed;

    public PerformanceEventListener()
    {
        MarkdownPerformanceEventSource.SetInProcessSink(this);
    }

    public long CurrentSequence
    {
        get { lock (_gate) return _sequence; }
    }

    public void SetMeasurementEnvironmentAbortToken(CancellationToken cancellationToken)
        => _measurementEnvironmentAbortToken = cancellationToken;

    public void BeginStageProfilePresentation(long afterSequence, long sourceUtf16Bytes)
    {
        var capture = new FirstViewportStageProfileCapture(
            afterSequence,
            sourceUtf16Bytes,
            Environment.CurrentManagedThreadId,
            ThreadCpuClock.ReadCurrentThreadTicks(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
        lock (_gate)
        {
            if (_disposed)
                return;

            _stageProfileObservation = null;
            _stageProfileCapture = capture;
        }
    }

    public bool TryConsumeStageProfileMetrics(
        FirstViewportEvent presentation,
        out FirstViewportStageProfileThreadMetrics metrics)
    {
        lock (_gate)
        {
            FirstViewportStageProfileObservation? observation = _stageProfileObservation;
            _stageProfileObservation = null;
            if (!observation.HasValue)
            {
                metrics = default;
                return false;
            }

            FirstViewportStageProfileObservation value = observation.Value;
            if (value.Sequence != presentation.Sequence ||
                value.Generation != presentation.Generation ||
                value.SourceUtf16Bytes != presentation.SourceUtf16Bytes)
            {
                metrics = default;
                return false;
            }

            metrics = value.Metrics;
            return true;
        }
    }

    public IReadOnlyList<FirstViewportEvent> FirstViewports
    {
        get { lock (_gate) return _firstViewports.ToArray(); }
    }

    public IReadOnlyList<string> ProviderErrors
    {
        get { lock (_gate) return _providerErrors.ToArray(); }
    }

    public void BitmapCacheCleanupFailed(int hresult)
    {
        lock (_gate)
        {
            // One sanitized health failure is enough to fail qualification.
            // An eviction storm must not retain one string per failed bitmap.
            if (_providerErrors.Count == 0)
                _providerErrors.Add($"Bitmap cache cleanup failed (HResult=0x{hresult:X8}).");
        }
    }

    public void PipelineStarted(long generation, long sourceUtf16Bytes)
    {
        lock (_gate)
        {
            _pipelines.Add(new PipelineEvent(++_sequence, generation, sourceUtf16Bytes));
            SignalWaitersNoLock();
        }
    }

    public void FirstViewportPresented(
        long generation,
        long sourceUtf16Bytes,
        long elapsedStopwatchTicks)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            long sequence = ++_sequence;
            if (_stageProfileCapture is not null)
                CaptureStageProfileMetricsNoLock(sequence, generation, sourceUtf16Bytes);
            _firstViewports.Add(new FirstViewportEvent(
                sequence,
                generation,
                sourceUtf16Bytes,
                ToMilliseconds(elapsedStopwatchTicks)));
            SignalWaitersNoLock();
        }
    }

    private void CaptureStageProfileMetricsNoLock(
        long sequence,
        long generation,
        long sourceUtf16Bytes)
    {
        FirstViewportStageProfileCapture? capture = _stageProfileCapture;
        if (capture is null ||
            sequence <= capture.AfterSequence ||
            sourceUtf16Bytes != capture.SourceUtf16Bytes)
        {
            return;
        }

        _stageProfileCapture = null;
        bool sameManagedThread = Environment.CurrentManagedThreadId == capture.ManagedThreadId;
        long cpuCompleted = sameManagedThread
            ? ThreadCpuClock.ReadCurrentThreadTicks()
            : -1;
        double elapsedCpuMilliseconds = sameManagedThread
            ? ThreadCpuClock.ElapsedMilliseconds(capture.CpuStartedTicks, cpuCompleted)
            : -1;

        var metrics = new FirstViewportStageProfileThreadMetrics(
            elapsedCpuMilliseconds >= 0 ? elapsedCpuMilliseconds : null,
            CollectionDelta(capture.Gen0Collections, GC.CollectionCount(0)),
            CollectionDelta(capture.Gen1Collections, GC.CollectionCount(1)),
            CollectionDelta(capture.Gen2Collections, GC.CollectionCount(2)));
        _stageProfileObservation = new FirstViewportStageProfileObservation(
            sequence,
            generation,
            sourceUtf16Bytes,
            metrics);
    }

    private static int? CollectionDelta(int before, int after)
        => after >= before ? after - before : null;

    public void ScrollWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes)
    {
        lock (_gate)
        {
            _sequence++;
            AddFrameWork(logicalFrameId, elapsedStopwatchTicks, allocatedBytes, paint: false);
        }
    }

    public void PaintWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes)
    {
        lock (_gate)
        {
            _sequence++;
            AddFrameWork(logicalFrameId, elapsedStopwatchTicks, allocatedBytes, paint: true);
        }
    }

    public void PipelineSuperseded(long generation)
    {
        lock (_gate)
        {
            _supersessions.Add(new SupersessionEvent(++_sequence, generation));
            SignalWaitersNoLock();
        }
    }

    public void SupersededPipelineStopped(long generation, long elapsedStopwatchTicks)
    {
        lock (_gate)
        {
            _cancellations.Add(new CancellationEvent(
                ++_sequence,
                generation,
                ToMilliseconds(elapsedStopwatchTicks)));
            SignalWaitersNoLock();
        }
    }

    public void ScrollAllocationBreakdown(
        long logicalFrameId,
        long lazyLayoutBytes,
        long adornerBytes,
        long realizationBytes,
        long highlightingBytes)
    {
        lock (_gate)
        {
            _sequence++;
            AddScrollAllocationBreakdown(
                logicalFrameId,
                lazyLayoutBytes,
                adornerBytes,
                realizationBytes,
                highlightingBytes);
        }
    }

    public void PaintAllocationBreakdown(
        long logicalFrameId,
        long schedulingBytes,
        long platformSessionBytes,
        long snapshotPaintBytes,
        long interactivePaintBytes)
    {
        lock (_gate)
        {
            _sequence++;
            if (logicalFrameId <= 0)
                return;
            if (!_frames.TryGetValue(logicalFrameId, out FrameWork? work))
            {
                work = new FrameWork();
                _frames.Add(logicalFrameId, work);
            }
            work.PaintSchedulingAllocatedBytes += Math.Max(0, schedulingBytes);
            work.PaintPlatformSessionAllocatedBytes += Math.Max(0, platformSessionBytes);
            work.SnapshotPaintAllocatedBytes += Math.Max(0, snapshotPaintBytes);
            work.InteractivePaintAllocatedBytes += Math.Max(0, interactivePaintBytes);
        }
    }

    public void SnapshotPaintAllocationBreakdown(
        long logicalFrameId,
        long inlineBytes,
        long tableBytes,
        long codeBytes,
        long otherBytes)
    {
        lock (_gate)
        {
            _sequence++;
            if (logicalFrameId <= 0)
                return;
            if (!_frames.TryGetValue(logicalFrameId, out FrameWork? work))
            {
                work = new FrameWork();
                _frames.Add(logicalFrameId, work);
            }
            work.InlinePaintAllocatedBytes += Math.Max(0, inlineBytes);
            work.TablePaintAllocatedBytes += Math.Max(0, tableBytes);
            work.CodePaintAllocatedBytes += Math.Max(0, codeBytes);
            work.OtherPaintAllocatedBytes += Math.Max(0, otherBytes);
        }
    }

    public void CodeActionRealization(long logicalFrameId, long realizedCount, long offscreenCount)
    {
        lock (_gate)
        {
            _sequence++;
            if (logicalFrameId <= 0)
                return;
            if (!_frames.TryGetValue(logicalFrameId, out FrameWork? work))
            {
                work = new FrameWork();
                _frames.Add(logicalFrameId, work);
            }
            work.MaximumRealizedCodeActions = Math.Max(work.MaximumRealizedCodeActions, realizedCount);
            work.MaximumOffscreenCodeActions = Math.Max(work.MaximumOffscreenCodeActions, offscreenCount);
        }
    }

    public void PrepareFrameCapture(long firstFrameId, int frameCount)
    {
        lock (_gate)
        {
            for (long frameId = firstFrameId; frameId < firstFrameId + frameCount; frameId++)
                _frames[frameId] = new FrameWork();
        }
    }

    public void Dispose()
    {
        MarkdownPerformanceEventSource.SetInProcessSink(null);
        lock (_gate)
        {
            _disposed = true;
            _stageProfileCapture = null;
            _stageProfileObservation = null;
        }
    }

    private void SignalWaitersNoLock()
    {
        TaskCompletionSource changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    public Task<PipelineEvent> WaitForPipelineAsync(
        long afterSequence,
        long sourceUtf16Bytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => WaitForAsync(
            () => _pipelines.FirstOrDefault(
                item => item.Sequence > afterSequence && item.SourceUtf16Bytes == sourceUtf16Bytes),
            static item => item.Sequence != 0,
            timeout,
            $"pipeline start for {sourceUtf16Bytes:N0} UTF-16 bytes",
            cancellationToken);

    public Task<FirstViewportEvent> WaitForFirstViewportAsync(
        long afterSequence,
        long sourceUtf16Bytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => WaitForAsync(
            () => _firstViewports.FirstOrDefault(
                item => item.Sequence > afterSequence && item.SourceUtf16Bytes == sourceUtf16Bytes),
            static item => item.Sequence != 0,
            timeout,
            $"first viewport for {sourceUtf16Bytes:N0} UTF-16 bytes",
            cancellationToken);

    public Task<CancellationEvent> WaitForCancellationAsync(
        long afterSequence,
        long generation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => WaitForAsync(
            () => _cancellations.FirstOrDefault(
                item => item.Sequence > afterSequence && item.Generation == generation),
            static item => item.Sequence != 0,
            timeout,
            $"cancellation of generation {generation}",
            cancellationToken);

    public Task<SupersessionEvent> WaitForSupersessionAsync(
        long afterSequence,
        long generation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => WaitForAsync(
            () => _supersessions.FirstOrDefault(
                item => item.Sequence > afterSequence && item.Generation == generation),
            static item => item.Sequence != 0,
            timeout,
            $"supersession of generation {generation}",
            cancellationToken);

    public IReadOnlyDictionary<long, FrameWork> SnapshotFrames(long firstFrameId, long lastFrameId)
    {
        lock (_gate)
        {
            var result = new Dictionary<long, FrameWork>();
            for (long frameId = firstFrameId; frameId <= lastFrameId; frameId++)
            {
                if (_frames.TryGetValue(frameId, out FrameWork? work))
                    result.Add(frameId, work with { });
            }
            return result;
        }
    }

    private async Task<T> WaitForAsync<T>(
        Func<T> find,
        Func<T, bool> found,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        CancellationToken environmentAbortToken = _measurementEnvironmentAbortToken;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            environmentAbortToken);
        timeoutSource.CancelAfter(timeout);

        while (true)
        {
            timeoutSource.Token.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                T value = find();
                if (found(value))
                    return value;
                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(timeoutSource.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                !environmentAbortToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }
        }
    }

    private void AddFrameWork(long frameId, long elapsedTicks, long allocatedBytes, bool paint)
    {
        if (frameId <= 0)
            return;

        if (!_frames.TryGetValue(frameId, out FrameWork? work))
        {
            work = new FrameWork();
            _frames.Add(frameId, work);
        }

        long nonNegativeElapsedTicks = Math.Max(0, elapsedTicks);
        work.UiThreadWorkElapsedTicks = checked(
            work.UiThreadWorkElapsedTicks + nonNegativeElapsedTicks);
        work.ElapsedMilliseconds += ToMilliseconds(nonNegativeElapsedTicks);
        work.AllocatedBytes += Math.Max(0, allocatedBytes);
        if (paint)
        {
            work.PaintCallbacks++;
            work.PaintElapsedMilliseconds += ToMilliseconds(nonNegativeElapsedTicks);
            work.PaintAllocatedBytes += Math.Max(0, allocatedBytes);
        }
        else
        {
            work.ScrollCallbacks++;
            work.ScrollElapsedMilliseconds += ToMilliseconds(nonNegativeElapsedTicks);
            work.ScrollAllocatedBytes += Math.Max(0, allocatedBytes);
        }
    }

    private void AddScrollAllocationBreakdown(
        long frameId,
        long lazyLayoutBytes,
        long adornerBytes,
        long realizationBytes,
        long highlightingBytes)
    {
        if (frameId <= 0)
            return;
        if (!_frames.TryGetValue(frameId, out FrameWork? work))
        {
            work = new FrameWork();
            _frames.Add(frameId, work);
        }
        work.ScrollLazyLayoutAllocatedBytes += Math.Max(0, lazyLayoutBytes);
        work.ScrollAdornerAllocatedBytes += Math.Max(0, adornerBytes);
        work.ScrollRealizationAllocatedBytes += Math.Max(0, realizationBytes);
        work.ScrollHighlightingAllocatedBytes += Math.Max(0, highlightingBytes);
    }

    private static double ToMilliseconds(long stopwatchTicks)
        => stopwatchTicks * 1000.0 / Stopwatch.Frequency;

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public readonly record struct PipelineEvent(long Sequence, long Generation, long SourceUtf16Bytes);

public readonly record struct FirstViewportEvent(
    long Sequence,
    long Generation,
    long SourceUtf16Bytes,
    double ElapsedMilliseconds);

public readonly record struct FirstViewportStageProfileThreadMetrics(
    double? UiThreadCpuMilliseconds,
    int? ProcessGen0Collections,
    int? ProcessGen1Collections,
    int? ProcessGen2Collections);

public sealed record FirstViewportStageProfileCapture(
    long AfterSequence,
    long SourceUtf16Bytes,
    int ManagedThreadId,
    long CpuStartedTicks,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

public readonly record struct FirstViewportStageProfileObservation(
    long Sequence,
    long Generation,
    long SourceUtf16Bytes,
    FirstViewportStageProfileThreadMetrics Metrics);

public readonly record struct SupersessionEvent(long Sequence, long Generation);

public readonly record struct CancellationEvent(long Sequence, long Generation, double ElapsedMilliseconds);

public sealed record FrameWork
{
    public long UiThreadWorkElapsedTicks { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public long AllocatedBytes { get; set; }
    public int ScrollCallbacks { get; set; }
    public int PaintCallbacks { get; set; }
    public double ScrollElapsedMilliseconds { get; set; }
    public double PaintElapsedMilliseconds { get; set; }
    public long ScrollAllocatedBytes { get; set; }
    public long PaintAllocatedBytes { get; set; }
    public long ScrollLazyLayoutAllocatedBytes { get; set; }
    public long ScrollAdornerAllocatedBytes { get; set; }
    public long ScrollRealizationAllocatedBytes { get; set; }
    public long ScrollHighlightingAllocatedBytes { get; set; }
    public long PaintSchedulingAllocatedBytes { get; set; }
    public long PaintPlatformSessionAllocatedBytes { get; set; }
    public long SnapshotPaintAllocatedBytes { get; set; }
    public long InteractivePaintAllocatedBytes { get; set; }
    public long InlinePaintAllocatedBytes { get; set; }
    public long TablePaintAllocatedBytes { get; set; }
    public long CodePaintAllocatedBytes { get; set; }
    public long OtherPaintAllocatedBytes { get; set; }
    public long MaximumRealizedCodeActions { get; set; }
    public long MaximumOffscreenCodeActions { get; set; }
}
