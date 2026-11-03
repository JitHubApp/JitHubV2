using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Performance;

internal sealed class MarkdownDeferredSceneResult(
    MarkdownContentFragment fragment,
    IReadOnlyList<MarkdownDiagnostic> diagnostics)
{
    internal MarkdownContentFragment Fragment { get; } = fragment;
    internal IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; } = diagnostics;
}
