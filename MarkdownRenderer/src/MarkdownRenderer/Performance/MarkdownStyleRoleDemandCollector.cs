using System;
using System.Collections.Generic;
using System.Threading;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownDocumentSnapshot = MarkdownRenderer.Document.MarkdownDocument;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Computes the built-in style roles a complete immutable document can use.
/// This lives in the opt-in Performance pack so lean Core/WinUI consumers keep
/// their original full-resolution path without carrying the collector.
/// </summary>
internal static class MarkdownStyleRoleDemandCollector
{
    internal static MarkdownStyleRoleDemandResult Collect(
        MarkdownDocumentSnapshot document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var demand = new MarkdownStyleRoleDemandAccumulator();
        var collector = new MarkdownStyleRoleDemandWalker(
            demand,
            new SafeHtmlStyleRoleDemandWalker(demand, cancellationToken),
            cancellationToken);
        if (document.ParsedDocument is { } parsedDocument)
        {
            foreach (Block block in parsedDocument)
                collector.VisitBlock(block);
        }

        foreach (MarkdownContentFragment fragment in document.ExtensionBlockNodeContent.Values)
            collector.VisitExtensionContent(fragment.Items);
        foreach (MarkdownContentFragment fragment in document.ExtensionInlineNodeContent.Values)
            collector.VisitExtensionContent(fragment.Items);

        return new MarkdownStyleRoleDemandResult(
            (ulong)demand.Demands,
            demand.IsComplete,
            demand.FirstIncompleteNodeType);
    }
}
