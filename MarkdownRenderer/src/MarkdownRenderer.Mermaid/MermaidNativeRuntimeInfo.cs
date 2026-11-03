using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Mermaid;

/// <summary>Read-only availability information for the optional native Mermaid engine.</summary>
public sealed record MermaidNativeRuntimeInfo(
    MermaidNativeRuntimeStatus Status,
    MermaidNativeAbiVersion AbiVersion,
    string Message)
{
    public bool IsAvailable => Status == MermaidNativeRuntimeStatus.Available;
}
