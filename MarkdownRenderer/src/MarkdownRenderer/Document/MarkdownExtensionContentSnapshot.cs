using System;
using System.Collections.Generic;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Document;

// This is an implementation-only snapshot. A record would emit value
// equality, hashing, cloning, and formatting members that callers cannot
// use and that are never part of snapshot semantics.
internal sealed class MarkdownExtensionContentSnapshot(
    IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>> Blocks,
    IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>> Inlines,
    IReadOnlyDictionary<Block, MarkdownContentFragment> BlockNodes,
    IReadOnlyDictionary<Inline, MarkdownContentFragment> InlineNodes,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics)
{
    internal readonly IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>> Blocks = Blocks;

    internal readonly IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>> Inlines = Inlines;

    internal readonly IReadOnlyDictionary<Block, MarkdownContentFragment> BlockNodes = BlockNodes;

    internal readonly IReadOnlyDictionary<Inline, MarkdownContentFragment> InlineNodes = InlineNodes;

    internal readonly IReadOnlyList<MarkdownDiagnostic> Diagnostics = Diagnostics;

    internal static MarkdownExtensionContentSnapshot Empty { get; } = new(
        new Dictionary<SourceSpan, List<MarkdownContentFragment>>(),
        new Dictionary<SourceSpan, List<MarkdownContentFragment>>(),
        new Dictionary<Block, MarkdownContentFragment>(ReferenceEqualityComparer.Instance),
        new Dictionary<Inline, MarkdownContentFragment>(ReferenceEqualityComparer.Instance),
        Array.Empty<MarkdownDiagnostic>());
}
