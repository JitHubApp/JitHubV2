namespace MarkdownRenderer.PerformanceHarness;

internal sealed record GraphicsAdapterMetadata(
    string AdapterIdentitySha256,
    string AdapterLuid,
    string DriverVersion);
