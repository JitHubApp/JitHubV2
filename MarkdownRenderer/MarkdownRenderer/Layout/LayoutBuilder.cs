using System;
using System.Collections.Generic;
using System.Threading;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Footnotes;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

internal sealed class LayoutBuilder
{
    private const int MaxMonolithicTextLayoutLength = 32_768;

    private readonly MarkdownLayoutContext _context;
    private readonly IMarkdownEmbedFactory? _embedFactory;

    public LayoutBuilder(MarkdownLayoutContext context, IMarkdownEmbedFactory? embedFactory = null)
    {
        _context = context;
        _embedFactory = embedFactory;
    }

    public LayoutSnapshot Build(MarkdownDocument document, float availableWidth)
        => Build(document, availableWidth, CancellationToken.None);

    public LayoutSnapshot Build(MarkdownDocument document, float availableWidth, CancellationToken cancellationToken)
    {
        var blocks = BuildBlocks(document, cancellationToken);

        float y = 0;
        foreach (var b in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            b.ThrowIfCancellationRequested();
            float h = b.Measure(availableWidth);
            b.Arrange(0, y, availableWidth);
            y += h;
        }

        var (defs, refs) = _context.SnapshotFootnoteRegistry();
        var fragments = _context.SnapshotFragmentTargets();
        return new LayoutSnapshot(blocks, _context.SourceMap, availableWidth, y, defs, refs, fragments);
    }

    public LayoutSnapshot BuildLazy(
        MarkdownDocument document,
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
    {
        var blocks = BuildBlocks(document, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var (defs, refs) = _context.SnapshotFootnoteRegistry();
        var fragments = _context.SnapshotFragmentTargets();
        var snapshot = new LayoutSnapshot(blocks, _context.SourceMap, availableWidth, 0, defs, refs, fragments);
        snapshot.EnableLazyLayout(availableWidth, viewportTop, viewportHeight, overscan, cancellationToken);
        return snapshot;
    }

    private List<BlockBox> BuildBlocks(MarkdownDocument document, CancellationToken cancellationToken)
    {
        var blocks = new List<BlockBox>();
        foreach (var b in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var box = BuildBlock(b);
            if (box is not null) blocks.Add(box);
        }

        return blocks;
    }

    private BlockBox? BuildBlock(Block block)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        using var attrScope = _context.PushMarkdownAttributes(block);
        BlockBox? box = null;

        if (_embedFactory is { } ef)
        {
            _context.ThrowIfEmbedLayoutCallbackIsOnUiThread(nameof(IMarkdownEmbedFactory.CanCreate));
            if (!ef.CanCreate(block))
                goto SkipEmbedFactory;

            var eb = new EmbedBox(block, ef, _context);
            eb.BlockIndex = _context.NextBlockIndex();
            // Register the source span so Ctrl+C across an embed copies the
            // original markdown that produced it.
            if (block.Span.Length > 0)
            {
                var span = new MarkdownRenderer.SourceSpan(block.Span.Start, block.Span.Length);
                _context.SourceMap.Add(eb.BlockIndex, 0, 1, span);
            }
            box = eb;
        }

    SkipEmbedFactory:

        if (box is null && _context.Registry.TryGetRenderer(block.GetType(), out var renderer) && renderer is not null)
        {
            var custom = renderer.BuildBlock(block, _context);
            if (custom is not null)
            {
                if (custom.BlockIndex == 0) custom.BlockIndex = _context.NextBlockIndex();
                box = custom;
            }
        }

        box ??= block switch
        {
            HeadingBlock h => BuildHeading(h),
            ParagraphBlock u => BuildParagraphOrImage(u),
            FencedCodeBlock fc => BuildCodeBlock(fc, fc.Lines.ToString()),
            CodeBlock cb => BuildCodeBlock(cb, cb.Lines.ToString()),
            QuoteBlock qb => BuildQuote(qb),
            ListBlock lb => BuildList(lb),
            ThematicBreakBlock => MakeThematicBreak(),
            ContainerBlock cb => BuildGenericContainer(cb),
            _ => null
        };

        if (box is not null)
            _context.RegisterMarkdownAttributes(block, box.BlockIndex);

        return box;
    }

    private BlockBox BuildParagraphOrImage(ParagraphBlock u)
    {
        // If the paragraph contains only a single image link (optionally wrapped
        // in a single ContainerInline), promote to an ImageBox.
        var inline = u.Inline;
        if (inline is not null)
        {
            LinkInline? onlyImage = null;
            int count = 0;
            foreach (var node in inline)
            {
                count++;
                if (count > 1) { onlyImage = null; break; }
                if (node is LinkInline ln && ln.IsImage) onlyImage = ln;
                else { onlyImage = null; break; }
            }
            if (onlyImage is not null)
            {
                string url = onlyImage.Url ?? string.Empty;
                var altSb = new System.Text.StringBuilder();
                FlattenContainer(onlyImage, altSb);
                string alt = altSb.ToString();
                var img = new ImageBox(_context, url, alt);
                img.BlockIndex = _context.NextBlockIndex();
                // Register the source span so Ctrl+C copies the original ![alt](url).
                var span = new MarkdownRenderer.SourceSpan(u.Span.Start, u.Span.Length);
                _context.SourceMap.Add(img.BlockIndex, 0, 1, span);
                return img;
            }
        }
        return BuildParagraph(u);
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
        if (h.Span.Start >= 0 && h.Span.Length > 0)
            _context.SourceMap.AddSourceAffixesToBlock(box.BlockIndex, h.Span.Start, h.Span.Start + h.Span.Length);
        return box;
    }

    private InlineContainerBox BuildParagraph(ParagraphBlock u)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.Body);
        box.BlockIndex = _context.NextBlockIndex();
        AddInlines(box, u.Inline);
        return box;
    }

    private BlockBox BuildCodeBlock(LeafBlock block, string text)
    {
        text = CodeBlockMetadata.NormalizeCodeLineEndings(text);
        var metadata = CodeBlockMetadata.FromBlock(block, text);
        int lineCount = CountLogicalLines(text);
        bool showLineNumbers = metadata.ShowLineNumbers ?? _context.CodeBlockLineNumberMode switch
        {
            CodeBlockLineNumberMode.Always => true,
            CodeBlockLineNumberMode.Never => false,
            _ => lineCount > 1,
        };
        var codeBox = new CodeBlockBox(
            _context,
            metadata,
            text,
            _context.IsCodeBlockCopyEnabled,
            showLineNumbers)
        {
            BlockIndex = _context.NextBlockIndex(),
        };

        if (text.Length <= MaxMonolithicTextLayoutLength)
        {
            codeBox.AddChunk(BuildCodeBlockChunk(block, metadata, text, 0, text.Length));
            return codeBox;
        }

        int offset = 0;
        while (offset < text.Length)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(MaxMonolithicTextLayoutLength, text.Length - offset);
            if (offset + length < text.Length)
            {
                int newline = text.LastIndexOf('\n', offset + length - 1, length);
                if (newline > offset)
                    length = newline - offset + 1;
            }

            codeBox.AddChunk(BuildCodeBlockChunk(block, metadata, text.Substring(offset, length), offset, text.Length));
            offset += length;
        }

        return codeBox;
    }

    private InlineContainerBox BuildCodeBlockChunk(
        LeafBlock block,
        CodeBlockMetadata metadata,
        string text,
        int textOffset,
        int totalTextLength)
    {
        var box = new InlineContainerBox(_context, MarkdownElementKeys.CodeBlock)
        {
            CodeLanguage = metadata.Language,
            CodeBlockTextOffset = textOffset,
            CodeBlockTextLength = text.Length,
        };
        box.BlockIndex = _context.NextBlockIndex();
        // No ElementKey on the run: it inherits the container's CodeBlock style.
        // Setting ElementKey = CodeBlock would cause DrawDecorations to draw a
        // per-run background on top of the container-level background (double bg).
        var run = new TextRun(text)
        {
            SourceSpan = SliceSourceSpan(block, textOffset, text.Length, totalTextLength)
        };
        box.Add(run);
        return box;
    }

    private static int CountLogicalLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        int lines = 1;
        foreach (char ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        return text.EndsWith("\n", StringComparison.Ordinal) && lines > 1 ? lines - 1 : lines;
    }

    private static SourceSpan SliceSourceSpan(LeafBlock block, int textOffset, int textLength, int totalTextLength)
    {
        if (block.Span.Start < 0 || block.Span.Length <= 0 || totalTextLength <= 0)
            return SourceSpan.Empty;

        double scale = block.Span.Length / (double)totalTextLength;
        int start = block.Span.Start + (int)Math.Round(textOffset * scale);
        int end = block.Span.Start + (int)Math.Round((textOffset + textLength) * scale);
        return new SourceSpan(start, Math.Max(0, end - start));
    }

    private StackBox BuildQuote(QuoteBlock qb)
    {
        var style = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Quote,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        var stack = new StackBox
        {
            ContentPadding = style.Padding,
            AccentBar = style.AccentBar,
            Background = style.Background,
            BorderBrush = style.BorderBrush,
            BorderThickness = style.BorderThickness,
            CornerRadius = style.CornerRadius,
            Margin = style.Margin,
            FlowDirection = _context.FlowDirection,
        };
        stack.BlockIndex = _context.NextBlockIndex();
        using var quoteScoue = _context.PushStyleContext(MarkdownElementKeys.Quote);
        foreach (var child in qb)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var b = BuildBlock(child);
            if (b is not null) stack.Add(b);
        }
        return stack;
    }

    private StackBox BuildList(ListBlock list)
    {
        using var listScope = _context.PushListDepth();
        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
        };
        stack.BlockIndex = _context.NextBlockIndex();
        // Honour the ordered-list start number from the source (e.g. `5.`).
        // Markdig stores this as a string on ListBlock.OrderedStart.
        int index = 1;
        if (list.IsOrdered && !string.IsNullOrEmpty(list.OrderedStart)
            && int.TryParse(list.OrderedStart, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            index = parsed;
        }
        foreach (var item in list)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            if (item is not ListItemBlock ln) continue;

            BlockBox? itemBox = null;
            using (var itemAttrs = _context.PushMarkdownAttributes(ln))
            {
                if (_context.Registry.TryGetRenderer(typeof(ListItemBlock), out var itemRenderer) && itemRenderer is not null)
                    itemBox = itemRenderer.BuildBlock(ln, _context);

                itemBox ??= BuildDefaultListItem(ln, list.IsOrdered, index);

                if (itemBox.BlockIndex == 0) itemBox.BlockIndex = _context.NextBlockIndex();
                _context.RegisterMarkdownAttributes(ln, itemBox.BlockIndex);
                stack.Add(itemBox);
            }
            index++;
        }
        return stack;
    }

    private ListItemBox BuildDefaultListItem(ListItemBlock ln, bool isOrdered, int index)
    {
        var listStyle = _context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            _context.CreateStyleContextSnapshot(),
            _context.CreateStyleAliasSnapshot());
        float markerWidth = Math.Max(1f, listStyle.ListIndent + Math.Max(0, _context.ListDepth - 1) * listStyle.NestedListIndent);

        // Marker gutter — fixed width, right-aligned bullet/number.
        var marker = new InlineContainerBox(_context, MarkdownElementKeys.ListMarker);
        marker.BlockIndex = _context.NextBlockIndex();
        string markerText = isOrdered ? $"{index}." : "•";
        marker.Add(new TextRun(markerText)
        {
            ElementKey = MarkdownElementKeys.ListMarker,
            SourceSpan = new SourceSpan(ln.Span.Start, 0)
        });

        // Content area — all child blocks of the list item.
        var content = new StackBox
        {
            FlowDirection = _context.FlowDirection,
        };
        content.BlockIndex = _context.NextBlockIndex();
        foreach (var child in ln)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var cb = BuildBlock(child);
            if (cb is not null) content.Add(cb);
        }

        return new ListItemBox(marker, content, markerWidth)
        {
            FlowDirection = _context.FlowDirection,
        };
    }

    private StackBox BuildGenericContainer(ContainerBlock cb)
    {
        var stack = new StackBox
        {
            FlowDirection = _context.FlowDirection,
        };
        stack.BlockIndex = _context.NextBlockIndex();
        foreach (var child in cb)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            var b = BuildBlock(child);
            if (b is not null) stack.Add(b);
        }
        return stack;
    }

    private void AddInlines(InlineContainerBox box, ContainerInline? inline, int inheritedAliasStart = -1)
    {
        if (inline is null) return;
        foreach (var n in inline)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int aliasStart = _context.StyleAliasCount;
            using var inlineAttrs = _context.PushMarkdownAttributes(n);
            var run = BuildInline(n, box.BlockIndex);
            if (run is not null)
            {
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                run.StyleAliases = _context.CreateStyleAliasSnapshotFrom(effectiveAliasStart);
                _context.RegisterMarkdownAttributes(n, box.BlockIndex);
                box.Add(run);
            }
            else if (n is ContainerInline nested)
            {
                _context.RegisterMarkdownAttributes(n, box.BlockIndex);
                int effectiveAliasStart = inheritedAliasStart >= 0 ? inheritedAliasStart : aliasStart;
                AddInlines(box, nested, effectiveAliasStart);
            }
        }
    }

    private InlineRun? BuildInline(Inline inline, int parentBlockIndex = -1)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new TextRun(lit.Content.ToString())
                {
                    SourceSpan = new SourceSpan(lit.Span.Start, lit.Span.Length)
                };
            case CodeInline cn:
                return new CodeInlineRun(cn.Content)
                {
                    SourceSpan = new SourceSpan(cn.Span.Start, cn.Span.Length)
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
            case AbbreviationInline abbreviation:
                return new AbbreviationRun(
                    abbreviation.Abbreviation?.Label ?? string.Empty,
                    abbreviation.Abbreviation?.Text.ToString() ?? string.Empty)
                {
                    SourceSpan = new SourceSpan(abbreviation.Span.Start, abbreviation.Span.Length)
                };
            case FootnoteLink fl when !fl.IsBackLink:
            {
                // Render footnote forward-references as clickable superscript links.
                // URL uses the internal fragment scheme "#footnote-def-{order}" which
                // MarkdownRendererControl intercepts to scroll to the definition.
                // Use fl.Footnote.Order (the footnote's 1-based sequence number), NOT
                // fl.Index (which is a global sequential counter across all citations
                // of all footnotes and differs from Order when a footnote is cited
                // more than once).
                int order = fl.Footnote is { } footnote
                    ? _context.GetOrCreateFootnoteOrder(footnote, fl.Index)
                    : Math.Max(1, fl.Index);
                var run = new LinkRun(ToSuperscript(order), $"#footnote-def-{order}")
                {
                    SourceSpan = new SourceSpan(fl.Span.Start, fl.Span.Length),
                    IsSuperscript = true,
                };
                // Record the containing paragraph as the "ref" block so the
                // footnote definition's ↩ link can scroll back to the citation.
                if (parentBlockIndex >= 0) _context.RegisterFootnoteRef(order, parentBlockIndex);
                return run;
            }
        }
        return null;
    }

    private InlineRun BuildEmphasis(EmphasisInline emph)
    {
        var sb = new System.Text.StringBuilder();
        FlattenContainer(emph, sb);
        var span = new SourceSpan(emph.Span.Start, emph.Span.Length);
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

    private InlineRun BuildLink(LinkInline link)
    {
        if (link.IsImage)
        {
            var alt = new System.Text.StringBuilder();
            FlattenContainer(link, alt);
            string altText = alt.Length > 0 ? alt.ToString() : "image";
            return new InlineImageRun(_context, altText, link.Url ?? string.Empty, link.Title)
            {
                SourceSpan = new SourceSpan(link.Span.Start, link.Span.Length)
            };
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
                case CodeInline cn: sb.Append(cn.Content); break;
                case AbbreviationInline ab: sb.Append(ab.Abbreviation?.Label ?? string.Empty); break;
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
