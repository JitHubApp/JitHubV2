using JitHub.Services;

namespace JitHub.WinUI.Tests.TestDoubles;

public sealed class RecordingTelemetryService : ITelemetryService
{
    private readonly object _gate = new();
    private readonly List<RecordedTelemetryEvent> _events = [];
    private readonly List<RecordedTelemetryTrace> _traces = [];

    public IReadOnlyList<RecordedTelemetryEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public IReadOnlyList<RecordedTelemetryTrace> Traces
    {
        get
        {
            lock (_gate)
            {
                return _traces.ToArray();
            }
        }
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
    {
        lock (_gate)
        {
            _events.Add(new RecordedTelemetryEvent(
                name,
                new Dictionary<string, string?>(properties ?? new Dictionary<string, string?>())));
        }
    }

    public void TrackMetric(
        string name,
        double value,
        IReadOnlyDictionary<string, string?>? properties = null)
    {
    }

    public IPerformanceTrace StartTrace(
        string name,
        IReadOnlyDictionary<string, string?>? properties = null) =>
        new RecordingPerformanceTrace(this, name, properties);

    private void RecordTrace(string name, IReadOnlyDictionary<string, string?> properties)
    {
        lock (_gate)
        {
            _traces.Add(new RecordedTelemetryTrace(
                name,
                new Dictionary<string, string?>(properties)));
        }
    }

    private sealed class RecordingPerformanceTrace : IPerformanceTrace
    {
        private readonly RecordingTelemetryService _owner;
        private readonly string _name;
        private readonly Dictionary<string, string?> _properties;
        private bool _disposed;

        public RecordingPerformanceTrace(
            RecordingTelemetryService owner,
            string name,
            IReadOnlyDictionary<string, string?>? properties)
        {
            _owner = owner;
            _name = name;
            _properties = new Dictionary<string, string?>(
                properties ?? new Dictionary<string, string?>());
        }

        public void SetProperty(string key, string? value)
        {
            if (!_disposed)
            {
                _properties[key] = value;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.RecordTrace(_name, _properties);
        }
    }
}

public sealed record RecordedTelemetryEvent(
    string Name,
    IReadOnlyDictionary<string, string?> Properties);

public sealed record RecordedTelemetryTrace(
    string Name,
    IReadOnlyDictionary<string, string?> Properties);
