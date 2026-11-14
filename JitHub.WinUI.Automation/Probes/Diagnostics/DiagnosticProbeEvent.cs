namespace JitHub.WinUI.Automation.Probes.Diagnostics;

internal sealed record DiagnosticProbeEvent(
    int LineIndex,
    string Name,
    IReadOnlyDictionary<string, string> Properties);
