namespace MarkdownRenderer.Mermaid.NativeRuntime;

/// <summary>Read-only availability information for the optional native Mermaid engine.</summary>
public sealed record MermaidNativeRuntimeInfo(
    MermaidNativeRuntimeStatus Status,
    MermaidNativeAbiVersion AbiVersion,
    string Message)
{
    public bool IsAvailable => Status == MermaidNativeRuntimeStatus.Available;
}
