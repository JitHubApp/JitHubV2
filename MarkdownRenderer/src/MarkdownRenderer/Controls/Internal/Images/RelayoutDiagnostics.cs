using System.Diagnostics;
using MarkdownRenderer.Diagnostics.Internal;

namespace MarkdownRenderer.Controls.Internal.Images;

/// <summary>Debug-only diagnostics for image relayout and its recovery.</summary>
internal static class RelayoutDiagnostics
{
    [Conditional("DEBUG")]
    internal static void ReportFailure(string operation, object details) =>
        MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] image {operation} failed: {details}");
}
