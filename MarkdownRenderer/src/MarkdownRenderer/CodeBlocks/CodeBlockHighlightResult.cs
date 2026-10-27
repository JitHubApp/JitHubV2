using System;
using System.Collections.Generic;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Syntax-highlighting spans returned by a code-block highlighter.
/// </summary>
public sealed class CodeBlockHighlightResult
{
    /// <summary>An empty highlighting result.</summary>
    public static CodeBlockHighlightResult Empty { get; } = new(Array.Empty<CodeBlockHighlightSpan>());

    /// <summary>Initializes a new result.</summary>
    public CodeBlockHighlightResult(IReadOnlyList<CodeBlockHighlightSpan>? spans)
    {
        Spans = spans ?? Array.Empty<CodeBlockHighlightSpan>();
    }

    /// <summary>Foreground-color spans in absolute code-text coordinates.</summary>
    public IReadOnlyList<CodeBlockHighlightSpan> Spans { get; }
}
