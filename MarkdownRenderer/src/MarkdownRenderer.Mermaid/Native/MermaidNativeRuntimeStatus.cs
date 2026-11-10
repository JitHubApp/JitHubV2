namespace MarkdownRenderer.Mermaid.Native;

public enum MermaidNativeRuntimeStatus
{
    Available,
    NotInstalled,
    IncompatibleArchitecture,
    MissingExport,
    IncompatibleAbi,
    LoadFailure,
}
