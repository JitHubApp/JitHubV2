using System.Diagnostics;

namespace MarkdownRenderer.Diagnostics;

internal static class MarkdownDiagnostics
{
    [Conditional("DEBUG")]
    public static void WriteLine(string message)
    {
        if (ShakeLogger.IsEnabled)
            Debug.WriteLine(message);
    }
}
