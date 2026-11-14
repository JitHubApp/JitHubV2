using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer.Gfm.Internal.Compatibility;

/// <summary>
/// Enables Markdig pipe tables and turns every pipe that did not become part
/// of a table back into literal text.
/// </summary>
/// <remarks>
/// Markdig's pipe-table parser marks each <c>|</c> in a paragraph with a
/// <see cref="PipeTableDelimiterInline"/>. When the paragraph has no
/// <c>---|---</c> delimiter row it is not a table, but Markdig leaves those
/// delimiters in the syntax tree and relies on its own HTML renderer writing
/// <see cref="DelimiterInline.ToLiteral"/>. The native layout, the public
/// syntax nodes and nested safe-HTML fragments read the tree directly, so they
/// silently lost every such pipe ("Website | Learn" became "Website  Learn").
/// Replacing the leftovers with source-mapped literals once, as each document
/// is parsed, gives every consumer the text GitHub shows.
/// </remarks>
internal sealed class PipeTableDelimiterLiteralExtension : IMarkdownExtension
{
    /// <summary>Adds pipe tables together with the literal-pipe repair.</summary>
    internal static MarkdownPipelineBuilder UsePipeTables(
        MarkdownPipelineBuilder pipeline,
        PipeTableOptions? options = null)
    {
        pipeline.UsePipeTables(options);
        pipeline.Extensions.AddIfNotAlready<PipeTableDelimiterLiteralExtension>();
        return pipeline;
    }

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed -= ReplaceLeftoverDelimiters;
        pipeline.DocumentProcessed += ReplaceLeftoverDelimiters;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    internal static void ReplaceLeftoverDelimiters(MarkdownDocument document)
    {
        var blocks = new Stack<Block>();
        var inlines = new Stack<ContainerInline>();
        List<PipeTableDelimiterInline>? leftovers = null;
        blocks.Push(document);
        while (blocks.Count > 0)
        {
            switch (blocks.Pop())
            {
                case ContainerBlock container:
                    for (int index = container.Count - 1; index >= 0; index--)
                        blocks.Push(container[index]);
                    break;
                case LeafBlock { Inline: { } root }:
                    inlines.Push(root);
                    while (inlines.Count > 0)
                    {
                        for (Inline? child = inlines.Pop().FirstChild; child is not null; child = child.NextSibling)
                        {
                            if (child is PipeTableDelimiterInline delimiter)
                                (leftovers ??= []).Add(delimiter);
                            if (child is ContainerInline nested)
                                inlines.Push(nested);
                        }
                    }
                    break;
            }
        }

        if (leftovers is null)
            return;

        foreach (PipeTableDelimiterInline delimiter in leftovers)
            ReplaceWithLiteral(delimiter);
    }

    // Matches Markdig's DelimiterInline rendering: the delimiter's literal,
    // then any inlines it contains.
    private static void ReplaceWithLiteral(PipeTableDelimiterInline delimiter)
    {
        var literal = new LiteralInline(delimiter.ToLiteral())
        {
            Span = delimiter.Span,
            Line = delimiter.Line,
            Column = delimiter.Column,
            IsClosed = true,
        };
        delimiter.InsertBefore(literal);
        Inline insertionPoint = literal;
        while (delimiter.FirstChild is { } child)
        {
            child.Remove();
            insertionPoint.InsertAfter(child);
            insertionPoint = child;
        }

        delimiter.Remove();
    }
}
