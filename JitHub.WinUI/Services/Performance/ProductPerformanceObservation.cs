using System;
using System.Diagnostics;
using System.Globalization;

namespace JitHub.Services;

public readonly record struct ProductPerformanceHeartbeat(
    long Frame,
    long Dispatcher,
    long? InteractiveTimestamp = null)
{
    public static bool TryParse(string? value, out ProductPerformanceHeartbeat heartbeat)
    {
        heartbeat = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        long? frame = null;
        long? dispatcher = null;
        long? interactiveTimestamp = null;
        foreach (string part in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1 ||
                !long.TryParse(part[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                continue;
            }

            if (part[..separator].Equals("frame", StringComparison.Ordinal))
            {
                frame = parsed;
            }
            else if (part[..separator].Equals("dispatcher", StringComparison.Ordinal))
            {
                dispatcher = parsed;
            }
            else if (part[..separator].Equals("interactive_ticks", StringComparison.Ordinal) && parsed > 0)
            {
                interactiveTimestamp = parsed;
            }
        }

        if (frame is null || dispatcher is null)
        {
            return false;
        }

        heartbeat = new ProductPerformanceHeartbeat(frame.Value, dispatcher.Value, interactiveTimestamp);
        return true;
    }

    public string Format()
    {
        string interactive = InteractiveTimestamp is long timestamp
            ? $";interactive_ticks={timestamp.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        return FormattableString.Invariant($"frame={Frame};dispatcher={Dispatcher}{interactive}");
    }
}

public readonly record struct ProductPerformanceScrollStatus(
    long Sequence,
    long StartedTimestamp,
    long RenderedTimestamp)
{
    private const string Prefix = "scroll;";

    public static bool TryParse(string? value, out ProductPerformanceScrollStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        long sequence = 0;
        long startedTimestamp = 0;
        long renderedTimestamp = 0;
        foreach (string part in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1 ||
                !long.TryParse(part[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                continue;
            }

            switch (part[..separator])
            {
                case "sequence":
                    sequence = parsed;
                    break;
                case "started_ticks":
                    startedTimestamp = parsed;
                    break;
                case "rendered_ticks":
                    renderedTimestamp = parsed;
                    break;
            }
        }

        if (sequence < 0 || startedTimestamp <= 0 || renderedTimestamp < startedTimestamp)
        {
            return false;
        }

        status = new ProductPerformanceScrollStatus(sequence, startedTimestamp, renderedTimestamp);
        return true;
    }

    public string Format() => FormattableString.Invariant(
        $"{Prefix}sequence={Sequence};started_ticks={StartedTimestamp};rendered_ticks={RenderedTimestamp}");
}

public sealed record ProductPerformanceContentObservation(
    string Identity,
    int MeaningfulElementCount,
    bool IsVisible,
    bool IsBusy,
    ProductPerformanceHeartbeat Heartbeat,
    long? MeasurementStartedTimestamp = null,
    long? FirstRenderedTimestamp = null,
    long? SettledTimestamp = null)
{
    public bool HasRenderedContent =>
        IsVisible &&
        MeaningfulElementCount > 0 &&
        !string.IsNullOrWhiteSpace(Identity);

    public bool HasSettledContent => HasRenderedContent && !IsBusy;
}

public sealed class ProductPerformanceContentTransitionTracker
{
    private readonly long _startedTimestamp;
    private readonly string _previousIdentity;
    private readonly bool _requiresIdentityChange;
    private readonly int _requiredStableFrames;
    private string _lastIdentity = string.Empty;
    private long _lastFrame = -1;
    private int _stableFrames;
    private bool _hasSeenRenderedContent;

    public ProductPerformanceContentTransitionTracker(
        long startedTimestamp,
        string? previousIdentity = null,
        bool requiresIdentityChange = false,
        int requiredStableFrames = 3)
    {
        if (startedTimestamp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startedTimestamp));
        }

        if (requiredStableFrames < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredStableFrames));
        }

        _startedTimestamp = startedTimestamp;
        _previousIdentity = previousIdentity?.Trim() ?? string.Empty;
        _requiresIdentityChange = requiresIdentityChange;
        _requiredStableFrames = requiredStableFrames;
    }

    public TimeSpan? FirstDataContent { get; private set; }

    public TimeSpan? SettledDataContent { get; private set; }

    public int BlankingFrameCount { get; private set; }

    public bool IsSettled => SettledDataContent is not null;

    public void Observe(ProductPerformanceContentObservation observation, long observedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observedTimestamp < _startedTimestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(observedTimestamp));
        }

        bool isNewFrame = observation.Heartbeat.Frame > _lastFrame;
        if (!isNewFrame)
        {
            return;
        }

        _lastFrame = observation.Heartbeat.Frame;
        if (_hasSeenRenderedContent && !observation.HasRenderedContent)
        {
            BlankingFrameCount++;
        }

        if (!observation.HasRenderedContent)
        {
            _stableFrames = 0;
            return;
        }

        _hasSeenRenderedContent = true;
        long measurementStartedTimestamp = observation.MeasurementStartedTimestamp is long startedTimestamp
            ? startedTimestamp
            : _startedTimestamp;
        long firstContentTimestamp = observation.FirstRenderedTimestamp is long firstTimestamp
            ? Math.Max(measurementStartedTimestamp, firstTimestamp)
            : observedTimestamp;
        FirstDataContent ??= Stopwatch.GetElapsedTime(measurementStartedTimestamp, firstContentTimestamp);

        bool identityChanged = !_requiresIdentityChange ||
            !string.Equals(observation.Identity, _previousIdentity, StringComparison.Ordinal);
        if (identityChanged &&
            observation.SettledTimestamp is long settledTimestamp)
        {
            SettledDataContent ??= Stopwatch.GetElapsedTime(
                measurementStartedTimestamp,
                Math.Max(measurementStartedTimestamp, settledTimestamp));
            _stableFrames = _requiredStableFrames;
            _lastIdentity = observation.Identity;
            return;
        }

        if (!observation.HasSettledContent)
        {
            _stableFrames = 0;
            _lastIdentity = observation.Identity;
            return;
        }

        if (!identityChanged)
        {
            _stableFrames = 0;
            _lastIdentity = observation.Identity;
            return;
        }

        _stableFrames = string.Equals(observation.Identity, _lastIdentity, StringComparison.Ordinal)
            ? _stableFrames + 1
            : 1;
        _lastIdentity = observation.Identity;
        if (_stableFrames >= _requiredStableFrames)
        {
            SettledDataContent ??= Stopwatch.GetElapsedTime(_startedTimestamp, observedTimestamp);
        }
    }
}

public sealed class ProductPerformanceScrollTransitionTracker
{
    private const double MinimumOffsetDelta = 0.001;
    private readonly long _startedTimestamp;
    private readonly double _initialOffset;
    private readonly long _initialFrame;
    private TimeSpan? _appRenderedInterval;
    private TimeSpan? _observerRenderedInterval;
    private bool _offsetChangedAfterFrame;

    public ProductPerformanceScrollTransitionTracker(
        long startedTimestamp,
        double initialOffset,
        long initialFrame)
    {
        _startedTimestamp = startedTimestamp > 0
            ? startedTimestamp
            : throw new ArgumentOutOfRangeException(nameof(startedTimestamp));
        _initialOffset = initialOffset;
        _initialFrame = initialFrame;
    }

    public TimeSpan? Completed { get; private set; }

    public bool IsCompleted => Completed is not null;

    public void ObserveRenderedTimestamp(long renderedTimestamp)
    {
        if (Completed is not null || renderedTimestamp < _startedTimestamp)
        {
            return;
        }

        _appRenderedInterval = Stopwatch.GetElapsedTime(_startedTimestamp, renderedTimestamp);
        TryComplete();
    }

    public void ObserveRenderedInterval(long startedTimestamp, long renderedTimestamp)
    {
        if (Completed is not null || startedTimestamp <= 0 || renderedTimestamp < startedTimestamp)
        {
            return;
        }

        _appRenderedInterval = Stopwatch.GetElapsedTime(startedTimestamp, renderedTimestamp);
        TryComplete();
    }

    public void Observe(double offset, ProductPerformanceHeartbeat heartbeat, long observedTimestamp)
    {
        if (Completed is not null)
        {
            return;
        }

        if (Math.Abs(offset - _initialOffset) >= MinimumOffsetDelta && heartbeat.Frame > _initialFrame)
        {
            _offsetChangedAfterFrame = true;
            _observerRenderedInterval ??= Stopwatch.GetElapsedTime(_startedTimestamp, observedTimestamp);
        }

        TryComplete();
    }

    private void TryComplete()
    {
        if (!_offsetChangedAfterFrame)
        {
            return;
        }

        Completed = _appRenderedInterval ?? _observerRenderedInterval;
    }
}
