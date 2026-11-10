namespace MarkdownRenderer.Mermaid.NativeRuntime;

public enum MermaidNativeRuntimeStatus
{
    Available,
    NotInstalled,
    IncompatibleArchitecture,
    MissingExport,
    IncompatibleAbi,
    LoadFailure,
}
