using System;
using System.Collections.Generic;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Document;

/// <summary>
/// Converts parser nodes to the immutable public syntax contract. The maps are
/// exact-type maps: a newly introduced parser subtype is never silently treated
/// as one of its base types.
/// </summary>
internal static class MarkdownSyntaxAdapter
{
    private static readonly IReadOnlyDictionary<Type, string> BlockKinds =
        new Dictionary<Type, string>
        {
            [typeof(ParagraphBlock)] = MarkdownSyntaxKinds.Block.Paragraph,
            [typeof(HeadingBlock)] = MarkdownSyntaxKinds.Block.Heading,
            [typeof(FencedCodeBlock)] = MarkdownSyntaxKinds.Block.FencedCode,
            [typeof(CodeBlock)] = MarkdownSyntaxKinds.Block.IndentedCode,
            [typeof(QuoteBlock)] = MarkdownSyntaxKinds.Block.Quote,
            [typeof(ListBlock)] = MarkdownSyntaxKinds.Block.List,
            [typeof(ListItemBlock)] = MarkdownSyntaxKinds.Block.ListItem,
            [typeof(ThematicBreakBlock)] = MarkdownSyntaxKinds.Block.ThematicBreak,
            [typeof(HtmlBlock)] = MarkdownSyntaxKinds.Block.Html,
            [typeof(MathBlock)] = MarkdownSyntaxKinds.Block.Math,
        };

    private static readonly IReadOnlyDictionary<Type, string> InlineKinds =
        new Dictionary<Type, string>
        {
            [typeof(LiteralInline)] = MarkdownSyntaxKinds.Inline.Text,
            [typeof(CodeInline)] = MarkdownSyntaxKinds.Inline.Code,
            [typeof(EmphasisInline)] = MarkdownSyntaxKinds.Inline.Emphasis,
            [typeof(LinkInline)] = MarkdownSyntaxKinds.Inline.Link,
            [typeof(LineBreakInline)] = MarkdownSyntaxKinds.Inline.LineBreak,
            [typeof(AutolinkInline)] = MarkdownSyntaxKinds.Inline.AutoLink,
            [typeof(HtmlInline)] = MarkdownSyntaxKinds.Inline.Html,
            [typeof(HtmlEntityInline)] = MarkdownSyntaxKinds.Inline.Entity,
            [typeof(AbbreviationInline)] = MarkdownSyntaxKinds.Inline.Abbreviation,
            [typeof(MathInline)] = MarkdownSyntaxKinds.Inline.Math,
        };

    internal static bool TryGetBlockKind(Block block, out string kind)
        => BlockKinds.TryGetValue(block.GetType(), out kind!);

    internal static bool TryGetInlineKind(Inline inline, out string kind)
    {
        if (inline.GetType() == typeof(LinkInline) && ((LinkInline)inline).IsImage)
        {
            kind = MarkdownSyntaxKinds.Inline.Image;
            return true;
        }

        return InlineKinds.TryGetValue(inline.GetType(), out kind!);
    }

    internal static MarkdownSyntaxNode CreateBlockNode(Block block, string source)
    {
        if (!TryGetBlockKind(block, out string kind))
            throw new ArgumentException("The block does not have a stable syntax-kind mapping.", nameof(block));

        var attributes = new List<KeyValuePair<string, string>>();
        switch (block)
        {
            case HeadingBlock heading:
                attributes.Add(new("level", heading.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                break;
            case MathBlock mathBlock:
                attributes.Add(new("display", "true"));
                AddMathBlockRangeAttributes(mathBlock, source, attributes);
                break;
            case FencedCodeBlock fenced:
                attributes.Add(new("language", NormalizeLanguage(fenced.Info)));
                attributes.Add(new("info", fenced.Info?.Trim() ?? string.Empty));
                int contentOffset = fenced.Lines.Count > 0
                    ? Math.Max(0, fenced.Lines.Lines[0].Position - fenced.Span.Start)
                    : 0;
                attributes.Add(new(
                    "contentOffset",
                    contentOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                attributes.Add(new(
                    "contentLength",
                    fenced.Lines.ToString().Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                break;
            case ListBlock list:
                attributes.Add(new("ordered", list.IsOrdered ? "true" : "false"));
                if (list.IsOrdered && !string.IsNullOrWhiteSpace(list.OrderedStart))
                    attributes.Add(new("start", list.OrderedStart));
                break;
        }

        var children = new List<MarkdownSyntaxNode>();
        if (block is LeafBlock leaf && leaf.Inline is { } inlineContainer)
        {
            AddInlineChildren(inlineContainer, source, children);
        }
        else if (block is ContainerBlock container)
        {
            foreach (Block child in container)
            {
                if (TryGetBlockKind(child, out _))
                    children.Add(CreateBlockNode(child, source));
            }
        }

        return new MarkdownSyntaxNode(
            kind,
            ToSourceSpan(block.Span),
            GetBlockLiteral(block, source),
            attributes,
            children);
    }

    internal static MarkdownSyntaxNode CreateInlineNode(Inline inline, string source)
    {
        if (!TryGetInlineKind(inline, out string kind))
            throw new ArgumentException("The inline does not have a stable syntax-kind mapping.", nameof(inline));

        var attributes = new List<KeyValuePair<string, string>>();
        switch (inline)
        {
            case LinkInline link:
                attributes.Add(new("destination", link.Url ?? string.Empty));
                attributes.Add(new("title", link.Title ?? string.Empty));
                break;
            case EmphasisInline emphasis:
                attributes.Add(new("delimiter", emphasis.DelimiterChar.ToString()));
                attributes.Add(new("count", emphasis.DelimiterCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                break;
            case LineBreakInline lineBreak:
                attributes.Add(new("hard", lineBreak.IsHard ? "true" : "false"));
                break;
            case MathInline math:
                string mathLiteral = GetSourceSlice(source, math.Span);
                string mathContent = math.Content.ToString();
                int mathContentOffset = mathLiteral.IndexOf(
                    mathContent,
                    Math.Min(math.DelimiterCount, mathLiteral.Length),
                    StringComparison.Ordinal);
                if (mathContentOffset < 0)
                    mathContentOffset = Math.Min(math.DelimiterCount, mathLiteral.Length);
                attributes.Add(new("display", "false"));
                attributes.Add(new(
                    "delimiterCount",
                    math.DelimiterCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                attributes.Add(new(
                    "contentOffset",
                    mathContentOffset
                        .ToString(System.Globalization.CultureInfo.InvariantCulture)));
                attributes.Add(new(
                    "contentLength",
                    mathContent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                break;
        }

        var children = new List<MarkdownSyntaxNode>();
        if (inline is ContainerInline container)
            AddInlineChildren(container, source, children);

        return new MarkdownSyntaxNode(
            kind,
            ToSourceSpan(inline.Span),
            GetInlineLiteral(inline, source),
            attributes,
            children);
    }

    private static void AddInlineChildren(
        ContainerInline container,
        string source,
        List<MarkdownSyntaxNode> children)
    {
        foreach (Inline child in container)
        {
            if (TryGetInlineKind(child, out _))
                children.Add(CreateInlineNode(child, source));
        }
    }

    private static string? GetInlineLiteral(Inline inline, string source)
        => inline switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            AbbreviationInline abbreviation => abbreviation.Abbreviation?.Label ?? string.Empty,
            MathInline => GetSourceSlice(source, inline.Span),
            _ => GetSourceSlice(source, inline.Span),
        };

    private static string GetBlockLiteral(Block block, string source)
        => block switch
        {
            MathBlock => GetSourceSlice(source, block.Span),
            FencedCodeBlock fenced => fenced.Lines.ToString(),
            CodeBlock code => code.Lines.ToString(),
            _ => GetSourceSlice(source, block.Span),
        };

    private static void AddMathBlockRangeAttributes(
        MathBlock block,
        string source,
        List<KeyValuePair<string, string>> attributes)
    {
        string original = GetSourceSlice(source, block.Span);
        int openingLength = Math.Min(block.OpeningFencedCharCount, original.Length);
        int contentOffset = openingLength;
        // Markdown accepts LF, CRLF and bare CR (including WinUI TextBox.Text).
        // Locate boundaries in the original source so UTF-16 ranges and exact
        // fallback remain intact; normalizing the text would shift CRLF spans.
        int openingLineBreak = original.AsSpan(openingLength).IndexOfAny('\r', '\n');
        if (openingLineBreak >= 0)
        {
            openingLineBreak += openingLength;
            contentOffset = openingLineBreak + 1;
            if (original[openingLineBreak] == '\r' &&
                contentOffset < original.Length && original[contentOffset] == '\n')
                contentOffset++;
        }

        int contentEnd = original.Length;
        bool isClosed = block.ClosingFencedCharCount > 0;
        if (isClosed)
        {
            string closingFence = new('$', block.ClosingFencedCharCount);
            int closingOffset = original.LastIndexOf(closingFence, StringComparison.Ordinal);
            if (closingOffset >= contentOffset)
            {
                int closingLineBreak = original.AsSpan(0, closingOffset).LastIndexOfAny('\r', '\n');
                if (closingLineBreak >= contentOffset)
                {
                    contentEnd = closingLineBreak;
                    if (original[contentEnd] == '\n' &&
                        contentEnd > contentOffset && original[contentEnd - 1] == '\r')
                        contentEnd--;
                }
                else
                {
                    contentEnd = contentOffset;
                }
            }
        }

        attributes.Add(new("closed", isClosed ? "true" : "false"));
        attributes.Add(new(
            "contentOffset",
            contentOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        attributes.Add(new(
            "contentLength",
            Math.Max(0, contentEnd - contentOffset)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string NormalizeLanguage(string? info)
    {
        string value = info?.Trim() ?? string.Empty;
        int whitespace = value.IndexOfAny([' ', '\t', '\r', '\n']);
        return whitespace > 0 ? value[..whitespace] : value;
    }

    private static SourceSpan ToSourceSpan(Markdig.Syntax.SourceSpan span)
        => span.Start >= 0 && span.Length > 0
            ? new SourceSpan(span.Start, span.Length)
            : SourceSpan.Empty;

    private static string GetSourceSlice(string source, Markdig.Syntax.SourceSpan span)
    {
        if (span.Start < 0 || span.Start >= source.Length || span.Length <= 0)
            return string.Empty;

        int length = Math.Min(span.Length, source.Length - span.Start);
        return source.Substring(span.Start, length);
    }
}
