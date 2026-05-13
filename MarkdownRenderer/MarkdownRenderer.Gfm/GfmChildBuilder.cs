using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm;

/// <summary>
/// Lightweight block/inline builder for GFM extension renderers. Handles common
/// Markdig block types without requiring access to the internal LayoutBuilder.
/// </summary>
internal static class GfmChildBuilder
{
    /// <summary>Builds child blocks from <paramref name="container"/> and adds them to <paramref name="stack"/>.</summary>
    internal static void PopulateChildren(StackBox stack, ContainerBlock container, MarkdownLayoutContext context)
    {
        foreach (var child in container)
        {
            var box = TryBuildBlock(child, context);
            if (box is not null) stack.Add(box);
        }
    }

    /// <summary>Attempts to build a <see cref="BlockBox"/> for a Markdig block node.</summary>
    internal static BlockBox? TryBuildBlock(Block block, MarkdownLayoutContext context)
    {
        // Check registry first so registered custom renderers are honoured.
        if (context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, context);
            if (custom is not null) return custom;
        }

        return block switch
        {
            ParagraphBlock p => BuildLeaf(p, context, MarkdownElementKeys.Body),
            HeadingBlock h => BuildLeaf(h, context, h.Level switch
            {
                1 => MarkdownElementKeys.Heading1,
                2 => MarkdownElementKeys.Heading2,
                3 => MarkdownElementKeys.Heading3,
                4 => MarkdownElementKeys.Heading4,
                5 => MarkdownElementKeys.Heading5,
                _ => MarkdownElementKeys.Heading6
            }),
            ContainerBlock cb => BuildContainer(cb, context),
            _ => null
        };
    }

    private static InlineContainerBox BuildLeaf(LeafBlock leaf, MarkdownLayoutContext context, string elementKey)
    {
        var box = new InlineContainerBox(context, elementKey);
        box.BlockIndex = context.NextBlockIndex();
        if (leaf.Inline is not null)
            AddInlines(box, leaf.Inline);
        return box;
    }

    private static StackBox BuildContainer(ContainerBlock cb, MarkdownLayoutContext context)
    {
        var stack = new StackBox();
        PopulateChildren(stack, cb, context);
        return stack;
    }

    internal static void AddInlines(InlineContainerBox box, ContainerInline inlines, System.Func<Inline, bool>? skipFirstIf = null)
    {
        bool skippedFirst = skipFirstIf is null;
        foreach (var i in inlines)
        {
            if (!skippedFirst)
            {
                skippedFirst = true;
                if (skipFirstIf!(i)) continue;
            }
            var run = BuildInline(i);
            if (run is not null) box.Add(run);
        }
    }

    private static InlineRun? BuildInline(Inline inline) => inline switch
    {
        LiteralInline lit => new TextRun(lit.Content.ToString())
        {
            // No ElementKey = inherit container's style (fixes headings inside GFM blocks).
            SourceSpan = new MarkdownRenderer.SourceSpan(lit.Span.Start, lit.Span.Length)
        },
        CodeInline ci => new CodeInlineRun(ci.Content)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(ci.Span.Start, ci.Span.Length)
        },
        EmphasisInline emph => BuildEmphasis(emph),
        LineBreakInline => new LineBreakRun
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(inline.Span.Start, inline.Span.Length)
        },
        ContainerInline ci2 => FlattenAsTextRun(ci2),
        _ => null
    };

    private static InlineRun BuildEmphasis(EmphasisInline emph)
    {
        var sb = new StringBuilder();
        FlattenInlines(emph, sb);
        var span = new MarkdownRenderer.SourceSpan(emph.Span.Start, emph.Span.Length);
        if (emph.DelimiterChar == '~')
            return new StrikethroughRun(sb.ToString()) { SourceSpan = span };
        return emph.DelimiterCount >= 2
            ? new StrongRun(sb.ToString()) { SourceSpan = span }
            : new EmphasisRun(sb.ToString()) { SourceSpan = span };
    }

    private static TextRun FlattenAsTextRun(ContainerInline ci)
    {
        var sb = new StringBuilder();
        FlattenInlines(ci, sb);
        return new TextRun(sb.ToString())
        {
            // No ElementKey = inherit container's style.
            SourceSpan = new MarkdownRenderer.SourceSpan(ci.Span.Start, ci.Span.Length)
        };
    }

    internal static void FlattenInlines(ContainerInline container, StringBuilder sb)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline ci: sb.Append(ci.Content); break;
                case LineBreakInline: sb.Append('\n'); break;
                case ContainerInline c2: FlattenInlines(c2, sb); break;
            }
        }
    }
}
