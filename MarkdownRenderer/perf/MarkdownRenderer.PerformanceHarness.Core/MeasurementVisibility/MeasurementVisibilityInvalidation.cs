using MarkdownRenderer.PerformanceHarness.Core.Evidence;
using MarkdownRenderer.PerformanceHarness.Core.Reports;

namespace MarkdownRenderer.PerformanceHarness.Core.MeasurementVisibility;

/// <summary>
/// Latches the first visibility-policy violation for a benchmark leg. Once
/// the window leaves the qualified measurement environment, later recovery
/// cannot make the already-contaminated leg valid again.
/// </summary>
public sealed class MeasurementVisibilityInvalidation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _cancellationToken;
    private MeasurementVisibilitySample? _firstUnqualifiedSample;
    private string? _firstViolationReason;
    private bool _cancellationStarted;
    private int _cancellationInFlight;
    private bool _disposed;

    public MeasurementVisibilityInvalidation()
        => _cancellationToken = _cancellation.Token;

    public CancellationToken CancellationToken => _cancellationToken;
    public MeasurementVisibilitySample? FirstUnqualifiedSample
    {
        get { lock (_gate) return _firstUnqualifiedSample; }
    }
    public string? FirstViolationReason
    {
        get { lock (_gate) return _firstViolationReason; }
    }

    public bool TryInvalidate(
        MeasurementVisibilitySample sample,
        string? irreversibleViolationReason = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (MeasurementVisibilityEvidenceValidator.IsQualifiedSample(sample) &&
            string.IsNullOrWhiteSpace(irreversibleViolationReason))
        {
            return false;
        }

        bool requestCancellation = false;
        bool firstByElapsedTime = false;
        lock (_gate)
        {
            if (_disposed)
                return false;

            // Monitor callbacks append under their own lock, then signal
            // cancellation after releasing it. A timer callback and a WinEvent
            // callback can therefore reach this latch out of order; retain the
            // earliest sample from their shared monotonic clock.
            if (_firstUnqualifiedSample is null ||
                sample.ElapsedTicks < _firstUnqualifiedSample.ElapsedTicks)
            {
                _firstUnqualifiedSample = sample;
                _firstViolationReason = string.IsNullOrWhiteSpace(irreversibleViolationReason)
                    ? MeasurementVisibilityEvidenceValidator.DescribeUnqualifiedSample(sample)
                    : irreversibleViolationReason;
                firstByElapsedTime = true;
            }

            if (!_cancellationStarted)
            {
                _cancellationStarted = true;
                _cancellationInFlight++;
                requestCancellation = true;
            }
        }

        if (requestCancellation)
        {
            try
            {
                // Cancellation callbacks may resume harness continuations. Run
                // them after releasing both the monitor's evidence lock and our
                // own state lock.
                _cancellation.Cancel();
            }
            finally
            {
                bool disposeCancellationSource;
                lock (_gate)
                {
                    _cancellationInFlight--;
                    disposeCancellationSource = _disposed && _cancellationInFlight == 0;
                }

                if (disposeCancellationSource)
                    _cancellation.Dispose();
            }
        }

        return firstByElapsedTime;
    }

    public void Dispose()
    {
        bool disposeCancellationSource;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            // If Dispose is reached from a cancellation continuation, defer
            // CTS disposal until Cancel has returned rather than waiting for
            // the callback stack to unwind itself.
            disposeCancellationSource = _cancellationInFlight == 0;
        }

        if (disposeCancellationSource)
            _cancellation.Dispose();
    }
}
