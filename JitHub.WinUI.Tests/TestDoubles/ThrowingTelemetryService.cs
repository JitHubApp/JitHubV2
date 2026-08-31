using JitHub.Services;

namespace JitHub.WinUI.Tests.TestDoubles;

public sealed class ThrowingTelemetryService : ITelemetryService
{
    public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
        throw new InvalidOperationException("Injected telemetry event failure.");

    public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null) =>
        throw new InvalidOperationException("Injected telemetry metric failure.");

    public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
        new ThrowingTrace();

    private sealed class ThrowingTrace : IPerformanceTrace
    {
        public void SetProperty(string key, string? value) =>
            throw new InvalidOperationException("Injected telemetry trace failure.");

        public void Dispose() =>
            throw new InvalidOperationException("Injected telemetry trace disposal failure.");
    }
}
