using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
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
            context.CancellationToken.ThrowIfCancellationRequested();
            var box = TryBuildBlock(child, context);
            if (box is not null) stack.Add(box);
        }
    }

    /// <summary>Attempts to build a <see cref="BlockBox"/> for a Markdig block node.</summary>
    internal static BlockBox? TryBuildBlock(Block block, MarkdownLayoutContext context)
    {
        using var attrScope = context.PushMarkdownAttributes(block);
        BlockBox? box = null;

        // Check registry first so registered custom renderers are honoured.
        if (context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, context);
            if (custom is not null)
                box = custom;
        }

        box ??= block switch
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

        if (box is not null)
        {
            if (box.BlockIndex == 0)
                box.BlockIndex = context.NextBlockIndex();
            context.RegisterMarkdownAttributes(block, box.BlockIndex);
        }

        return box;
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
        var stack = new StackBox
        {
            FlowDirection = context.FlowDirection,
        };
        stack.BlockIndex = context.NextBlockIndex();
        PopulateChildren(stack, cb, context);
        return stack;
    }

    internal static void AddInlines(InlineContainerBox box, ContainerInline inlines, System.Func<Inline, bool>? skipFirstIf = null)
    {
        bool skippedFirst = skipFirstIf is null;
        foreach (var i in inlines)
        {
            box.Context.CancellationToken.ThrowIfCancellationRequested();
            if (!skippedFirst)
            {
                skippedFirst = true;
                if (skipFirstIf!(i)) continue;
            }
            int aliasStart = box.Context.StyleAliasCount;
            using var inlineAttrs = box.Context.PushMarkdownAttributes(i);
            var run = BuildInline(i, box.Context);
            if (run is not null)
            {
                run.SetStyleAliases(box.Context.CreateStyleAliasSnapshotFrom(aliasStart));
                box.Context.RegisterMarkdownAttributes(i, box.BlockIndex);
                box.Add(run);
            }
        }
    }

    private static InlineRun? BuildInline(Inline inline, MarkdownLayoutContext context) => inline switch
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
        LinkInline link => BuildLink(link, context),
        AbbreviationInline abbreviation => new AbbreviationRun(
            abbreviation.Abbreviation?.Label ?? string.Empty,
            abbreviation.Abbreviation?.Text.ToString() ?? string.Empty)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(abbreviation.Span.Start, abbreviation.Span.Length)
        },
        AutolinkInline al => new LinkRun(al.Url, al.Url)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(al.Span.Start, al.Span.Length)
        },
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
        if (emph.DelimiterChar == '~' && emph.DelimiterCount >= 2)
            return new StrikethroughRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '~')
            return new SubscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '^')
            return new SuperscriptRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '+')
            return new InsertedRun(sb.ToString()) { SourceSpan = span };
        if (emph.DelimiterChar == '=')
            return new MarkedRun(sb.ToString()) { SourceSpan = span };
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

    private static InlineRun BuildLink(LinkInline link, MarkdownLayoutContext context)
    {
        if (link.IsImage)
        {
            var alt = new StringBuilder();
            FlattenInlines(link, alt);
            return new InlineImageRun(context, alt.Length > 0 ? alt.ToString() : "image", link.Url ?? string.Empty, link.Title)
            {
                SourceSpan = new MarkdownRenderer.SourceSpan(link.Span.Start, link.Span.Length)
            };
        }

        var text = new StringBuilder();
        FlattenInlines(link, text);
        return new LinkRun(text.ToString(), link.Url ?? string.Empty, link.Title)
        {
            SourceSpan = new MarkdownRenderer.SourceSpan(link.Span.Start, link.Span.Length)
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
                case AbbreviationInline ab: sb.Append(ab.Abbreviation?.Label ?? string.Empty); break;
                case LineBreakInline: sb.Append('\n'); break;
                case ContainerInline c2: FlattenInlines(c2, sb); break;
            }
        }
    }
}
