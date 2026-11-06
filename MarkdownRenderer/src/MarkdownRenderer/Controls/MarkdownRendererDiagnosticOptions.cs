namespace MarkdownRenderer.Controls;

/// <summary>
/// Diagnostics one renderer is constructed with. They are fixed for the
/// renderer's lifetime, so they apply from its first build.
/// </summary>
/// <param name="CaptureLayoutStages">
/// True to enable the build-stage layout counters without attaching a
/// performance session. It does not change layout scheduling. The standalone
/// quick-profile harness uses it.
/// </param>
internal sealed record MarkdownRendererDiagnosticOptions(bool CaptureLayoutStages)
{
    /// <summary>No diagnostics: the configuration every public constructor uses.</summary>
    internal static MarkdownRendererDiagnosticOptions None { get; } = new(CaptureLayoutStages: false);
}
