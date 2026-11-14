using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

internal sealed class MarkdownDeferredSceneResult(
    MarkdownContentFragment fragment,
    IReadOnlyList<MarkdownDiagnostic> diagnostics)
{
    internal MarkdownContentFragment Fragment { get; } = fragment;
    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; } = diagnostics;
}
