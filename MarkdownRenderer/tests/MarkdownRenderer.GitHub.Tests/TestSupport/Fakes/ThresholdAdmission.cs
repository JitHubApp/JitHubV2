using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

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
    private readonly object _admissionDomain = new();
    private long _availableBytes = initialAvailableBytes;
    private ManualResetEventSlim? _heldDomainRead;
    private ManualResetEventSlim? _releaseDomainRead;
    private TaskCompletionSource? _domainReadStarted;

    /// <summary>
    /// The provider domain key. While <see cref="HoldNextDomainRead"/> is armed,
    /// the next read blocks until <see cref="ReleaseDomainRead"/>. The host
    /// coordinator reads it first when it registers a denial, so a held read
    /// pauses that registration after the denial is recorded.
    /// </summary>
    public object AdmissionDomain
    {
        get
        {
            ManualResetEventSlim? held = Interlocked.Exchange(ref _heldDomainRead, null);
            if (held is not null)
            {
                _domainReadStarted!.TrySetResult();
                if (!held.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The held admission domain read was never released.");
            }
            return _admissionDomain;
        }
    }

    public long CapacityVersion => 0;

    /// <summary>The domain key, read without consuming a held read.</summary>
    internal object DomainKey => _admissionDomain;

    /// <summary>Arms a one-shot hold on the next <see cref="AdmissionDomain"/> read.</summary>
    /// <returns>A task that completes when that read starts waiting.</returns>
    internal Task HoldNextDomainRead()
    {
        _domainReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseDomainRead = new ManualResetEventSlim();
        Volatile.Write(ref _heldDomainRead, _releaseDomainRead);
        return _domainReadStarted.Task;
    }

    /// <summary>Lets a held <see cref="AdmissionDomain"/> read continue.</summary>
    internal void ReleaseDomainRead() => _releaseDomainRead?.Set();

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
