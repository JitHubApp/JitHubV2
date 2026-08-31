using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace JitHub.Services;

public sealed partial class TelemetryService : ITelemetryService
{
    private readonly ILocalDiagnosticsStore _diagnosticsStore;
    private readonly IStoreTelemetrySink _storeTelemetrySink;
    private readonly ISettingService _settingService;

    public TelemetryService(
        ILocalDiagnosticsStore diagnosticsStore,
        IStoreTelemetrySink storeTelemetrySink,
        ISettingService settingService)
    {
        _diagnosticsStore = diagnosticsStore;
        _storeTelemetrySink = storeTelemetrySink;
        _settingService = settingService;
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
    {
        try
        {
            string eventName = TelemetrySanitizer.NormalizeEventName(name);
            IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(properties);
            TrackStoreEvent(eventName, sanitized);
            AppendDiagnostics(new LocalDiagnosticEvent(
                DateTimeOffset.UtcNow,
                "event",
                eventName,
                sanitized));
        }
        catch
        {
            // Telemetry is deliberately outside every product failure boundary.
        }
    }

    public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
    {
        try
        {
            Dictionary<string, string?> merged = new(properties ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase)
            {
                ["metric"] = name
            };
            IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(merged);
            TrackStoreEvent("telemetry.metric", sanitized);
            AppendDiagnostics(new LocalDiagnosticEvent(
                DateTimeOffset.UtcNow,
                "metric",
                "telemetry.metric",
                sanitized,
                value));
        }
        catch
        {
            // Metrics are best-effort and must never affect request admission or UI work.
        }
    }

    public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null)
    {
        try
        {
            return new PerformanceTrace(this, name, properties);
        }
        catch
        {
            return NoOpPerformanceTrace.Instance;
        }
    }

    private void AppendDiagnostics(LocalDiagnosticEvent entry)
    {
        try
        {
            if (!IsDiagnosticsEnabled())
            {
                return;
            }

            _ = _diagnosticsStore.TryAppend(entry);
        }
        catch
        {
            // Local diagnostics can be unavailable during shutdown or storage recovery.
        }
    }

    private void TrackStoreEvent(
        string eventName,
        IReadOnlyDictionary<string, string> sanitizedProperties)
    {
        try
        {
            if (IsStoreTelemetryEnabled())
            {
                _storeTelemetrySink.TrackEvent(eventName);
                string? projectedEvent = StoreTelemetryProjection.Create(eventName, sanitizedProperties);
                if (projectedEvent is not null)
                {
                    _storeTelemetrySink.TrackEvent(projectedEvent);
                }
            }
        }
        catch
        {
            // Store engagement APIs are optional and may disappear with package state.
        }
    }

    private bool IsDiagnosticsEnabled() =>
        !_settingService.Contains(SettingsKeys.DiagnosticsEnabled) ||
        _settingService.Get<bool>(SettingsKeys.DiagnosticsEnabled);

    private bool IsStoreTelemetryEnabled() =>
        _storeTelemetrySink.IsAvailable &&
        (!_settingService.Contains(SettingsKeys.StoreTelemetryEnabled) ||
            _settingService.Get<bool>(SettingsKeys.StoreTelemetryEnabled));

    private sealed partial class PerformanceTrace : IPerformanceTrace
    {
        private readonly TelemetryService _owner;
        private readonly string _name;
        private readonly Dictionary<string, string?> _properties;
        private readonly Stopwatch _stopwatch;
        private bool _disposed;

        public PerformanceTrace(
            TelemetryService owner,
            string name,
            IReadOnlyDictionary<string, string?>? properties)
        {
            _owner = owner;
            _name = name;
            _properties = new Dictionary<string, string?>(properties ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
            _stopwatch = Stopwatch.StartNew();
        }

        public void SetProperty(string key, string? value)
        {
            try
            {
                if (!_disposed)
                {
                    _properties[key] = value;
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopwatch.Stop();
                _properties["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(_stopwatch.Elapsed);
                IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(_properties);
                string eventName = TelemetrySanitizer.NormalizeEventName(_name);
                _owner.TrackStoreEvent(eventName, sanitized);
                _owner.AppendDiagnostics(new LocalDiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    "trace",
                    eventName,
                    sanitized,
                    DurationMs: _stopwatch.ElapsedMilliseconds));
            }
            catch
            {
                // Disposing a trace must remain safe during teardown and fault handling.
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
