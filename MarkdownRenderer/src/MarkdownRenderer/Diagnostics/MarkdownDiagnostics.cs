using System.Diagnostics;

namespace MarkdownRenderer.Diagnostics;

internal static class MarkdownDiagnostics
{
    [Conditional("DEBUG")]
    public static void WriteLine(string message) => Debug.WriteLine(message);
}
