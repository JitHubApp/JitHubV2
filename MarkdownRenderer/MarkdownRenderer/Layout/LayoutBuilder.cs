using System;
using System.Collections.Generic;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

public sealed class LayoutBuilder
{
    private readonly MarkdownLayoutContext _context;

    public LayoutBuilder(MarkdownLayoutContext context)
    {
        _context = context;
    }

    public LayoutSnapshot Build(MarkdownDocument document, float availableWidth)
    {
        var blocks = new List<BlockBox>();
        foreach (var b in document)
        {
            var box = BuildBlock(b);
            if (box is not null) blocks.Add(box);
        }

        float y = 0;
        foreach (var b in blocks)
        {
            float h = b.Measure(availableWidth);
            b.Arrange(0, y, availableWidth);
            y += h;
        }

        return new LayoutSnapshot(blocks, _context.SourceMap, availableWidth, y);
    }

    private BlockBox? BuildBlock(Block block)
    {
        if (_context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, _context);
            if (custom is not null)
            {
                if (custom.BlockIndex == 0) custom.BlockIndex = _context.NextBlockIndex();
                return custom;
            }
        }

        return block switch
        {
            HeadingBlock h => BuildHeading(h),
            ParagraphBlock p => BuildParagraph(p),
            FencedCodeBlock fc => BuildCodeBlock(fc, fc.Lines.ToString()),
            CodeBlock cb => BuildCodeBlock(cb, cb.Lines.ToString()),
            QuoteBlock qb => BuildQuote(qb),
            ListBlock lb => BuildList(lb),
            ThematicBreakBlock => MakeThematicBreak(),
            ContainerBlock cb => BuildGenericContainer(cb),
            _ => null
        };
    }

    private BlockBox MakeThematicBreak()
    {
        var box = new ThematicBreakBox(_context);
        box.BlockIndex = _context.NextBlockIndex();
        return box;
    }

    private InlineContainerBox BuildHeading(HeadingBlock h)
    {
        string key = h.Level switch
        {
            1 => MarkdownElementKeys.Heading1,
            2 => MarkdownElementKeys.Heading2,
            3 => MarkdownElementKeys.Heading3,
            4 => MarkdownElementKeys.Heading4,
            5 => MarkdownElementKeys.Heading5,
            _ => MarkdownElementKeys.Heading6
        };
        var box = new InlineContainerBox(_context, key);
        box.BlockIndex = _context.NextBlockIndex();
        AddInlines(box, h.Inline);
        return box;
    }

    private InlineContainerBox BuildParagraph(ParagraphBlock p)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.Body);
        box.BlockIndex = _context.NextBlockIndex();
        AddInlines(box, p.Inline);
        return box;
    }

    private InlineContainerBox BuildCodeBlock(LeafBlock block, string text)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.CodeBlock);
        box.BlockIndex = _context.NextBlockIndex();
        var run = new TextRun(text)
        {
            ElementKey = MarkdownElementKeys.CodeBlock,
            SourceSpan = new SourceSpan(block.Span.Start, block.Span.Length)
        };
        box.Add(run);
        return box;
    }

    private StackBox BuildQuote(QuoteBlock qb)
    {
        var style = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Quote);
        var stack = new StackBox
        {
            ContentPadding = style.Padding,
            AccentBar = style.AccentBar,
            Margin = style.Margin,
        };
        stack.BlockIndex = _context.NextBlockIndex();
        foreach (var child in qb)
        {
            var b = BuildBlock(child);
            if (b is not null) stack.Add(b);
        }
        return stack;
    }

    private StackBox BuildList(ListBlock list)
    {
        var stack = new StackBox();
        stack.BlockIndex = _context.NextBlockIndex();
        int index = 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock li) continue;

            BlockBox? itemBox = null;
            if (_context.Registry.TryGetRenderer(typeof(ListItemBlock), out var itemRenderer) && itemRenderer is not null)
                itemBox = itemRenderer.BuildBlock(li, _context);

            itemBox ??= BuildDefaultListItem(li, list.IsOrdered, index);

            if (itemBox.BlockIndex == 0) itemBox.BlockIndex = _context.NextBlockIndex();
            stack.Add(itemBox);
            index++;
        }
        return stack;
    }

    private StackBox BuildDefaultListItem(ListItemBlock li, bool isOrdered, int index)
    {
        var itemBox = new StackBox
        {
            ContentPadding = new Microsoft.UI.Xaml.Thickness(20, 0, 0, 0),
        };
        itemBox.BlockIndex = _context.NextBlockIndex();

        var marker = new InlineContainerBox(_context, MarkdownElementKeys.ListMarker);
        marker.BlockIndex = _context.NextBlockIndex();
        string markerText = isOrdered ? $"{index}. " : "• ";
        marker.Add(new TextRun(markerText)
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(li.Span.Start, 0)
        });
        itemBox.Add(marker);

        foreach (var child in li)
        {
            var cb = BuildBlock(child);
            if (cb is not null) itemBox.Add(cb);
        }
        return itemBox;
    }

    private StackBox BuildGenericContainer(ContainerBlock cb)
    {
        var stack = new StackBox();
        stack.BlockIndex = _context.NextBlockIndex();
        foreach (var child in cb)
        {
            var b = BuildBlock(child);
            if (b is not null) stack.Add(b);
        }
        return stack;
    }

    private void AddInlines(InlineContainerBox box, ContainerInline? inline)
    {
        if (inline is null) return;
        foreach (var i in inline)
        {
            var run = BuildInline(i);
            if (run is not null) box.Add(run);
        }
    }

    private InlineRun? BuildInline(Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new TextRun(lit.Content.ToString())
                {
                    SourceSpan = new SourceSpan(lit.Span.Start, lit.Span.Length)
                };
            case CodeInline ci:
                return new CodeInlineRun(ci.Content)
                {
                    SourceSpan = new SourceSpan(ci.Span.Start, ci.Span.Length)
                };
            case EmphasisInline emph:
                return BuildEmphasis(emph);
            case LinkInline link:
                return BuildLink(link);
            case LineBreakInline:
                return new LineBreakRun { SourceSpan = new SourceSpan(inline.Span.Start, inline.Span.Length) };
            case AutolinkInline al:
                return new LinkRun(al.Url, al.Url) { SourceSpan = new SourceSpan(al.Span.Start, al.Span.Length) };
            case HtmlInline html:
                return new TextRun(html.Tag) { SourceSpan = new SourceSpan(html.Span.Start, html.Span.Length) };
            case ContainerInline ci2:
                {
                    var sb = new System.Text.StringBuilder();
                    FlattenContainer(ci2, sb);
                    return new TextRun(sb.ToString()) { SourceSpan = new SourceSpan(ci2.Span.Start, ci2.Span.Length) };
                }
        }
        return null;
    }

    private InlineRun BuildEmphasis(EmphasisInline emph)
    {
        var sb = new System.Text.StringBuilder();
        FlattenContainer(emph, sb);
        var span = new SourceSpan(emph.Span.Start, emph.Span.Length);
        return emph.DelimiterCount >= 2
            ? new StrongRun(sb.ToString()) { SourceSpan = span }
            : new EmphasisRun(sb.ToString()) { SourceSpan = span };
    }

    private InlineRun BuildLink(LinkInline link)
    {
        var sb = new System.Text.StringBuilder();
        FlattenContainer(link, sb);
        return new LinkRun(sb.ToString(), link.Url ?? string.Empty, link.Title)
        {
            SourceSpan = new SourceSpan(link.Span.Start, link.Span.Length)
        };
    }

    private static void FlattenContainer(ContainerInline container, System.Text.StringBuilder sb)
    {
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline ci: sb.Append(ci.Content); break;
                case LineBreakInline: sb.Append('\n'); break;
                case ContainerInline c2: FlattenContainer(c2, sb); break;
                default: break;
            }
        }
    }
}
