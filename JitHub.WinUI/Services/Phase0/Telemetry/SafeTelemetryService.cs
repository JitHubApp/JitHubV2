using System;
using System.Collections.Generic;

namespace JitHub.Services;

/// <summary>
/// Keeps optional telemetry outside product failure boundaries, including when
/// tests or future integrations provide an arbitrary <see cref="ITelemetryService"/>.
/// </summary>
public sealed partial class SafeTelemetryService : ITelemetryService
{
    private readonly ITelemetryService _inner;

    private SafeTelemetryService(ITelemetryService inner)
    {
        _inner = inner;
    }

    public static ITelemetryService Wrap(ITelemetryService telemetryService)
    {
        ArgumentNullException.ThrowIfNull(telemetryService);
        return telemetryService is SafeTelemetryService
            ? telemetryService
            : new SafeTelemetryService(telemetryService);
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
    {
        try
        {
            _inner.TrackEvent(name, properties);
        }
        catch
        {
        }
    }

    public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
    {
        try
        {
            _inner.TrackMetric(name, value, properties);
        }
        catch
        {
        }
    }

    public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null)
    {
        try
        {
            IPerformanceTrace? trace = _inner.StartTrace(name, properties);
            return trace is null ? NoOpPerformanceTrace.Instance : new SafePerformanceTrace(trace);
        }
        catch
        {
            return NoOpPerformanceTrace.Instance;
        }
    }

    private sealed partial class SafePerformanceTrace : IPerformanceTrace
    {
        private IPerformanceTrace? _inner;

        public SafePerformanceTrace(IPerformanceTrace inner)
        {
            _inner = inner;
        }

        public void SetProperty(string key, string? value)
        {
            try
            {
                _inner?.SetProperty(key, value);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            IPerformanceTrace? trace = _inner;
            _inner = null;
            try
            {
                trace?.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed partial class NoOpPerformanceTrace : IPerformanceTrace
    {
        public static NoOpPerformanceTrace Instance { get; } = new();

        public void SetProperty(string key, string? value)
        {
        }

        public void Dispose()
        {
        }
    }
}
