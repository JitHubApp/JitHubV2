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
        // No ElementKey on the run — it inherits the container's CodeBlock style.
        // Setting ElementKey = CodeBlock would cause DrawDecorations to draw a
        // per-run background on top of the container-level background (double bg).
        var run = new TextRun(text)
        {
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

    private ListItemBox BuildDefaultListItem(ListItemBlock li, bool isOrdered, int index)
    {
        // Marker gutter — fixed width, right-aligned bullet/number.
        var marker = new InlineContainerBox(_context, MarkdownElementKeys.ListMarker);
        marker.BlockIndex = _context.NextBlockIndex();
        string markerText = isOrdered ? $"{index}." : "•";
        marker.Add(new TextRun(markerText)
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(li.Span.Start, 0)
        });

        // Content area — all child blocks of the list item.
        var content = new StackBox();
        content.BlockIndex = _context.NextBlockIndex();
        foreach (var child in li)
        {
            var cb = BuildBlock(child);
            if (cb is not null) content.Add(cb);
        }

        // markerWidth: enough room for "99." in 14px body font (~20px), plus small gap.
        return new ListItemBox(marker, content, markerWidth: 22f);
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
            case Markdig.Extensions.Footnotes.FootnoteLink fl when !fl.IsBackLink:
                // Render footnote forward-references as superscript numbers.
                return new TextRun(ToSuperscript(fl.Index))
                {
                    SourceSpan = new SourceSpan(fl.Span.Start, fl.Span.Length)
                };
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
        // Strikethrough uses '~' delimiter (~~text~~); bold uses '*' or '_' with count ≥ 2.
        if (emph.DelimiterChar == '~')
            return new StrikethroughRun(sb.ToString()) { SourceSpan = span };
        return emph.DelimiterCount >= 2
            ? new StrongRun(sb.ToString()) { SourceSpan = span }
            : new EmphasisRun(sb.ToString()) { SourceSpan = span };
    }

    private InlineRun BuildLink(LinkInline link)
    {
        if (link.IsImage)
        {
            // Images are not yet natively rendered; show alt text as plain text.
            var alt = new System.Text.StringBuilder();
            FlattenContainer(link, alt);
            string altText = alt.Length > 0 ? alt.ToString() : "image";
            return new TextRun(altText) { SourceSpan = new SourceSpan(link.Span.Start, link.Span.Length) };
        }
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

    private static string ToSuperscript(int n)
    {
        const string digits = "\u2070\u00B9\u00B2\u00B3\u2074\u2075\u2076\u2077\u2078\u2079";
        var sb = new System.Text.StringBuilder();
        foreach (char c in n.ToString())
            sb.Append(c >= '0' && c <= '9' ? digits[c - '0'] : c);
        return sb.ToString();
    }
}
