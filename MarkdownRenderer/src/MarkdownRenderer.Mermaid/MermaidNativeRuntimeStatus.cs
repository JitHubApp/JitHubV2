using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
