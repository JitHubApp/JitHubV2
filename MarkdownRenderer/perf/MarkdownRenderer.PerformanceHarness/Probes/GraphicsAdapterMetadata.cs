namespace MarkdownRenderer.PerformanceHarness.Probes;

internal sealed record GraphicsAdapterMetadata(
    string AdapterIdentitySha256,
    string AdapterLuid,
    string DriverVersion);
