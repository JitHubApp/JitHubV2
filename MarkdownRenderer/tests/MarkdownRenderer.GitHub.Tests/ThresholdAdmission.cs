using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// An SVG admission double whose capacity waits complete once the bytes
/// set by the test reach their required threshold. It records every wait
/// call and fails if asked to open a document.
/// </summary>
internal sealed class ThresholdAdmission(long initialAvailableBytes = 0) : IMarkdownSvgAdmission
{
    private readonly object _sync = new();
    private readonly List<(long RequiredBytes, MarkdownSvgRenderPriority Priority,
        TaskCompletionSource Signal)> _waits = [];
    private long _availableBytes = initialAvailableBytes;

    public object AdmissionDomain { get; } = new();
    public long CapacityVersion => 0;

    internal (long RequiredBytes, MarkdownSvgRenderPriority Priority)[] WaitCalls
    {
        get
        {
            lock (_sync)
                return _waits.Select(wait => (wait.RequiredBytes, wait.Priority)).ToArray();
        }
    }

    public Task WaitForCapacityAsync(
        long requiredFreeBytes,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waits.Add((requiredFreeBytes, priority, signal));
            if (_availableBytes >= requiredFreeBytes)
                return Task.CompletedTask;
            return signal.Task.WaitAsync(cancellationToken);
        }
    }

    internal void SetAvailableBytes(long bytes)
    {
        TaskCompletionSource[] ready;
        lock (_sync)
        {
            _availableBytes = bytes;
            ready = _waits
                .Where(wait => wait.RequiredBytes <= bytes)
                .Select(wait => wait.Signal)
                .ToArray();
        }

        foreach (TaskCompletionSource signal in ready)
            signal.TrySetResult();
    }

    public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
        MarkdownSvgOpenRequest request,
        MarkdownSvgRenderDemand demand,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Pure admission tests must not open a document.");
}
