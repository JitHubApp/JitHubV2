namespace MarkdownRenderer.Mermaid;

public enum MermaidNativeRuntimeStatus
{
    Available,
    NotInstalled,
    IncompatibleArchitecture,
    MissingExport,
    IncompatibleAbi,
    LoadFailure,
}
