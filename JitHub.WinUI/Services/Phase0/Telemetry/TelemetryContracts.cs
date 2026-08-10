using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface ITelemetryService
{
    void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null);

    void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null);

    IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null);
}

public interface IPerformanceTrace : IDisposable
{
    void SetProperty(string key, string? value);
}

public interface ILocalDiagnosticsStore : IAsyncDisposable
{
    long DroppedEventCount => 0;

    bool TryAppend(LocalDiagnosticEvent entry);

    Task AppendAsync(LocalDiagnosticEvent entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalDiagnosticEvent>> ReadAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);

    Task<long> GetSizeAsync(CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public sealed record LocalDiagnosticEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string Name,
    IReadOnlyDictionary<string, string> Properties,
    double? Value = null,
    long? DurationMs = null);
