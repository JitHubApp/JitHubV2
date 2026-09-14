using System;
using System.Collections.Generic;
using System.IO;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer;

/// <summary>
/// Narrows current Markdig behavior to the grammar and reference serialization
/// pinned by the 0.29-gfm profile. This remains internal so parser internals do
/// not leak into the stable public contract.
/// </summary>
internal static class Gfm029Compatibility
{
    private const char MaskedTableSeparator = '\uE000';

    private static readonly string[] DisallowedRawHtmlTags =
    [
        "title",
        "textarea",
        "style",
        "xmp",
        "iframe",
        "noembed",
        "noframes",
        "script",
        "plaintext",
    ];

    internal static void NormalizeSyntaxTree(
        Markdig.Syntax.MarkdownDocument document,
        string source,
        bool enableAutoLinks,
        PreparedSource prepared)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.HasChanges)
        {
            RemapBlock(document, prepared);
            RestoreMaskedLiterals(document);
        }
        VisitBlocks(document, source, enableAutoLinks, insideTable: false);
    }

    internal static PreparedSource PrepareSource(string source, bool enableTables)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!enableTables || source.Length == 0)
            return PreparedSource.Unchanged(source);

        List<LineInfo> lines = GetLines(source);
        var insertAt = new HashSet<int>();
        var maskAt = new HashSet<int>();
        for (int lineIndex = 0; lineIndex + 1 < lines.Count; lineIndex++)
        {
            LineInfo header = lines[lineIndex];
            LineInfo separator = lines[lineIndex + 1];
            ReadOnlySpan<char> headerText = source.AsSpan(header.Start, header.Length);
            ReadOnlySpan<char> separatorText = source.AsSpan(separator.Start, separator.Length);
            int headerColumns = CountTableCells(headerText);
            if (headerColumns <= 0 ||
                !TryParseSeparatorRow(separatorText, out int separatorColumns, out int firstDash))
            {
                continue;
            }

            if (headerColumns != separatorColumns)
            {
                maskAt.Add(separator.Start + firstDash);
                lineIndex++;
                continue;
            }

            int bodyIndex = lineIndex + 2;
            for (; bodyIndex < lines.Count; bodyIndex++)
            {
                LineInfo body = lines[bodyIndex];
                ReadOnlySpan<char> bodyText = source.AsSpan(body.Start, body.Length);
                if (bodyText.Trim().IsEmpty || StartsBlockBoundary(bodyText))
                    break;
                if (CountUnescapedPipes(bodyText) == 0)
                    insertAt.Add(body.Start + body.Length);
            }

            lineIndex = Math.Max(lineIndex + 1, bodyIndex - 1);
        }

        if (insertAt.Count == 0 && maskAt.Count == 0)
            return PreparedSource.Unchanged(source);

        var transformed = new System.Text.StringBuilder(source.Length + insertAt.Count);
        var transformedInsertions = new List<int>(insertAt.Count);
        for (int index = 0; index <= source.Length; index++)
        {
            if (insertAt.Contains(index))
            {
                transformedInsertions.Add(transformed.Length);
                transformed.Append('|');
            }
            if (index < source.Length)
                transformed.Append(maskAt.Contains(index) ? MaskedTableSeparator : source[index]);
        }

        return new PreparedSource(
            transformed.ToString(),
            transformedInsertions.ToArray(),
            hasMaskedSeparators: maskAt.Count > 0);
    }

    internal static string RenderReferenceHtml(
        Markdig.Syntax.MarkdownDocument document,
        MarkdownPipeline pipeline,
        bool applyTagFilter)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<ListRenderer>(new GfmReferenceListRenderer());
        renderer.ObjectRenderers.Replace<HtmlTableRenderer>(new GfmReferenceTableRenderer());
        renderer.Render(document);
        string html = writer.ToString();
        return applyTagFilter ? FilterDisallowedRawHtml(html) : html;
    }

    private static List<LineInfo> GetLines(string source)
    {
        var lines = new List<LineInfo>();
        int start = 0;
        while (start < source.Length)
        {
            int end = start;
            while (end < source.Length && source[end] is not '\r' and not '\n')
                end++;
            lines.Add(new LineInfo(start, end - start));
            if (end < source.Length && source[end] == '\r' &&
                end + 1 < source.Length && source[end + 1] == '\n')
            {
                end++;
            }
            start = end + 1;
        }

        if (source.Length == 0 || source[^1] is '\r' or '\n')
            lines.Add(new LineInfo(source.Length, 0));
        return lines;
    }

    private static int CountTableCells(ReadOnlySpan<char> line)
    {
        int first = 0;
        while (first < line.Length && char.IsWhiteSpace(line[first]))
            first++;
        int last = line.Length - 1;
        while (last >= first && char.IsWhiteSpace(line[last]))
            last--;
        if (last < first)
            return 0;

        int pipes = CountUnescapedPipes(line);
        if (pipes == 0)
            return 0;
        bool leading = line[first] == '|' && !IsEscaped(line, first);
        bool trailing = line[last] == '|' && !IsEscaped(line, last);
        return pipes + 1 - (leading ? 1 : 0) - (trailing ? 1 : 0);
    }

    private static bool TryParseSeparatorRow(
        ReadOnlySpan<char> line,
        out int columns,
        out int firstDash)
    {
        columns = CountTableCells(line);
        firstDash = line.IndexOf('-');
        if (columns <= 0 || firstDash < 0)
            return false;

        int segmentStart = 0;
        int parsedColumns = 0;
        for (int index = 0; index <= line.Length; index++)
        {
            bool atDelimiter = index < line.Length &&
                line[index] == '|' &&
                !IsEscaped(line, index);
            if (!atDelimiter && index != line.Length)
                continue;

            ReadOnlySpan<char> segment = line[segmentStart..index].Trim();
            bool boundaryEmpty = segment.IsEmpty &&
                (segmentStart == 0 || index == line.Length);
            if (!boundaryEmpty)
            {
                if (!IsSeparatorCell(segment))
                    return false;
                parsedColumns++;
            }
            segmentStart = index + 1;
        }

        return parsedColumns == columns;
    }

    private static bool IsSeparatorCell(ReadOnlySpan<char> cell)
    {
        if (!cell.IsEmpty && cell[0] == ':')
            cell = cell[1..];
        if (!cell.IsEmpty && cell[^1] == ':')
            cell = cell[..^1];
        if (cell.IsEmpty)
            return false;
        foreach (char value in cell)
        {
            if (value != '-')
                return false;
        }
        return true;
    }

    private static int CountUnescapedPipes(ReadOnlySpan<char> line)
    {
        int count = 0;
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == '|' && !IsEscaped(line, index))
                count++;
        }
        return count;
    }

    private static bool IsEscaped(ReadOnlySpan<char> line, int index)
    {
        int slashes = 0;
        for (int cursor = index - 1; cursor >= 0 && line[cursor] == '\\'; cursor--)
            slashes++;
        return (slashes & 1) != 0;
    }

    private static bool StartsBlockBoundary(ReadOnlySpan<char> line)
    {
        int indentation = 0;
        while (indentation < line.Length && line[indentation] == ' ')
            indentation++;
        if (indentation >= 4)
            return true;
        ReadOnlySpan<char> text = line[indentation..];
        if (text.IsEmpty || text[0] is '>' or '<')
            return true;
        if (text.StartsWith("```", StringComparison.Ordinal) ||
            text.StartsWith("~~~", StringComparison.Ordinal))
        {
            return true;
        }
        if (text[0] == '#')
        {
            int hashes = 1;
            while (hashes < text.Length && text[hashes] == '#')
                hashes++;
            if (hashes <= 6 && (hashes == text.Length || char.IsWhiteSpace(text[hashes])))
                return true;
        }
        if (text.Length >= 2 && text[0] is '-' or '+' or '*' && char.IsWhiteSpace(text[1]))
            return true;

        int digitCount = 0;
        while (digitCount < text.Length && digitCount < 9 && char.IsAsciiDigit(text[digitCount]))
            digitCount++;
        if (digitCount > 0 && digitCount + 1 < text.Length &&
            text[digitCount] is '.' or ')' && char.IsWhiteSpace(text[digitCount + 1]))
        {
            return true;
        }

        char? thematic = null;
        int thematicCount = 0;
        bool thematicOnly = true;
        foreach (char value in text)
        {
            if (char.IsWhiteSpace(value))
                continue;
            if (value is not '-' and not '_' and not '*')
            {
                thematicOnly = false;
                break;
            }
            thematic ??= value;
            if (thematic != value)
            {
                thematicOnly = false;
                break;
            }
            thematicCount++;
        }
        if (thematicOnly && thematicCount >= 3)
            return true;

        return text[0] == '[' && text.IndexOf("]:", StringComparison.Ordinal) > 0;
    }

    private static void RemapBlock(Block block, PreparedSource prepared)
    {
        block.Span = prepared.MapSpan(block.Span);
        if (block is LeafBlock { Inline: { } inline })
            RemapInline(inline, prepared);
        if (block is ContainerBlock container)
        {
            foreach (Block child in container)
                RemapBlock(child, prepared);
        }
    }

    private static void RemapInline(Inline inline, PreparedSource prepared)
    {
        inline.Span = prepared.MapSpan(inline.Span);
        if (inline is LinkInline link && !link.UrlSpan.IsEmpty)
            link.UrlSpan = prepared.MapSpan(link.UrlSpan);
        if (inline is ContainerInline container)
        {
            for (Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
                RemapInline(child, prepared);
        }
    }

    private static void RestoreMaskedLiterals(ContainerBlock container)
    {
        foreach (Block block in container)
        {
            if (block is LeafBlock { Inline: { } inline })
                RestoreMaskedLiterals(inline);
            if (block is ContainerBlock nested)
                RestoreMaskedLiterals(nested);
        }
    }

    private static void RestoreMaskedLiterals(ContainerInline container)
    {
        for (Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is LiteralInline literal &&
                literal.Content.AsSpan().IndexOf(MaskedTableSeparator) >= 0)
            {
                literal.Content = new StringSlice(
                    literal.Content.ToString().Replace(MaskedTableSeparator, '-'));
            }
            if (child is ContainerInline nested)
                RestoreMaskedLiterals(nested);
        }
    }

    private static void VisitBlocks(
        ContainerBlock container,
        string source,
        bool enableAutoLinks,
        bool insideTable)
    {
        foreach (Block block in container)
        {
            bool childInsideTable = insideTable || block is Table;
            if (block is LeafBlock { Inline: { } inline })
                VisitInlines(inline, source, enableAutoLinks, childInsideTable);
            if (block is ContainerBlock nested)
                VisitBlocks(nested, source, enableAutoLinks, childInsideTable);
        }
    }

    private static void VisitInlines(
        ContainerInline container,
        string source,
        bool enableAutoLinks,
        bool insideTable)
    {
        Inline? child = container.FirstChild;
        while (child is not null)
        {
            Inline? next = child.NextSibling;
            if (child is ContainerInline nested)
            {
                VisitInlines(
                    nested,
                    source,
                    enableAutoLinks && child is not LinkInline,
                    insideTable);
                if (container is EmphasisInline outer &&
                    nested is EmphasisInline inner &&
                    outer.DelimiterChar == inner.DelimiterChar &&
                    outer.DelimiterCount == 2 &&
                    inner.DelimiterCount == 2)
                {
                    // CommonMark 0.30 made same-delimiter strong nesting
                    // recursive. 0.29-gfm treats the inner delimiters as part
                    // of the already-open strong span. Flatten only that exact
                    // case; same-delimiter nested emphasis remains meaningful.
                    nested.MoveChildrenAfter(nested);
                    nested.Remove();
                }
            }
            else if (insideTable && child is CodeInline code && code.Content.Contains("\\|", StringComparison.Ordinal))
            {
                // GFM resolves an escaped table pipe before parsing cell
                // inlines, including inside code spans.
                code.Content = code.Content.Replace("\\|", "|", StringComparison.Ordinal);
            }

            child = next;
        }

        if (enableAutoLinks)
        {
            RepairUrlSuffixes(container, source);
            LinkEmailLiterals(container, source);
        }
    }

    private static void RepairUrlSuffixes(ContainerInline container, string source)
    {
        for (Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is not LinkInline { IsAutoLink: true, IsImage: false } link ||
                link.Url is null ||
                link.FirstChild is not LiteralInline label ||
                link.NextSibling is not LiteralInline suffixLiteral ||
                suffixLiteral.Span.Start != link.Span.End + 1)
            {
                continue;
            }

            int suffixStart = suffixLiteral.Span.Start;
            int suffixEnd = suffixLiteral.Span.End + 1;
            if (suffixStart < 0 || suffixEnd > source.Length || suffixStart >= suffixEnd)
                continue;

            string literalText = suffixLiteral.Content.ToString();
            if (literalText.Length != suffixEnd - suffixStart ||
                !source.AsSpan(suffixStart, literalText.Length).SequenceEqual(literalText.AsSpan()) ||
                source[suffixStart] != ')')
            {
                continue;
            }

            int tokenEnd = suffixStart;
            while (tokenEnd < suffixEnd &&
                   !char.IsWhiteSpace(source[tokenEnd]) &&
                   source[tokenEnd] is not '<' and not '>')
            {
                tokenEnd++;
            }
            while (tokenEnd > suffixStart && source[tokenEnd - 1] is '.' or ',' or ';' or ':' or '!' or '?')
                tokenEnd--;

            bool hasNonPunctuationTail = false;
            for (int index = suffixStart + 1; index < tokenEnd; index++)
            {
                if (char.IsLetterOrDigit(source[index]))
                {
                    hasNonPunctuationTail = true;
                    break;
                }
            }
            if (!hasNonPunctuationTail)
                continue;

            string suffix = source.Substring(suffixStart, tokenEnd - suffixStart);
            link.Url += suffix;
            link.Span = new Markdig.Syntax.SourceSpan(link.Span.Start, tokenEnd - 1);
            link.UrlSpan = link.Span;
            label.Content = new StringSlice(source, label.Span.Start, tokenEnd - 1);
            label.Span = new Markdig.Syntax.SourceSpan(label.Span.Start, tokenEnd - 1);

            if (tokenEnd > suffixLiteral.Span.End)
            {
                suffixLiteral.Remove();
            }
            else
            {
                int consumed = tokenEnd - suffixStart;
                suffixLiteral.Content = new StringSlice(source, tokenEnd, suffixLiteral.Span.End);
                suffixLiteral.Span = new Markdig.Syntax.SourceSpan(tokenEnd, suffixLiteral.Span.End);
                suffixLiteral.Column += consumed;
            }
        }
    }

    private static void LinkEmailLiterals(ContainerInline container, string source)
    {
        Inline? child = container.FirstChild;
        while (child is not null)
        {
            Inline? next = child.NextSibling;
            if (child is LiteralInline literal)
                ReplaceEmailLiteral(literal, source);
            child = next;
        }
    }

    private static void ReplaceEmailLiteral(LiteralInline literal, string source)
    {
        string content = literal.Content.ToString();
        int sourceStart = literal.Span.Start;
        if (sourceStart < 0 ||
            literal.Span.End < sourceStart ||
            literal.Span.End >= source.Length ||
            content.Length != literal.Span.End - sourceStart + 1 ||
            !source.AsSpan(sourceStart, content.Length).SequenceEqual(content.AsSpan()) ||
            content.IndexOf('@') < 0)
        {
            return;
        }

        List<(int Start, int End)> ranges = FindEmailRanges(content);
        ranges.RemoveAll(range =>
        {
            int following = sourceStart + range.End;
            return following < source.Length && source[following] is '-' or '_';
        });
        if (ranges.Count == 0)
            return;

        var replacements = new List<Inline>(ranges.Count * 2 + 1);
        int copiedThrough = 0;
        foreach ((int start, int end) in ranges)
        {
            if (start > copiedThrough)
            {
                replacements.Add(CreateLiteral(
                    source,
                    sourceStart + copiedThrough,
                    sourceStart + start - 1,
                    literal.Line,
                    literal.Column + copiedThrough));
            }

            int absoluteStart = sourceStart + start;
            int absoluteEnd = sourceStart + end - 1;
            string email = content.Substring(start, end - start);
            var span = new Markdig.Syntax.SourceSpan(absoluteStart, absoluteEnd);
            var link = new LinkInline
            {
                Url = "mailto:" + email,
                IsAutoLink = true,
                IsClosed = true,
                Span = span,
                UrlSpan = span,
                Line = literal.Line,
                Column = literal.Column + start,
            };
            link.AppendChild(CreateLiteral(
                source,
                absoluteStart,
                absoluteEnd,
                literal.Line,
                literal.Column + start));
            replacements.Add(link);
            copiedThrough = end;
        }

        if (copiedThrough < content.Length)
        {
            replacements.Add(CreateLiteral(
                source,
                sourceStart + copiedThrough,
                literal.Span.End,
                literal.Line,
                literal.Column + copiedThrough));
        }

        Inline first = replacements[0];
        literal.ReplaceBy(first);
        Inline previous = first;
        for (int index = 1; index < replacements.Count; index++)
        {
            previous.InsertAfter(replacements[index]);
            previous = replacements[index];
        }
    }

    private static List<(int Start, int End)> FindEmailRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int at = text.IndexOf('@', searchFrom);
            if (at < 0)
                break;

            int start = at;
            while (start > 0 && IsEmailLocalCharacter(text[start - 1]))
                start--;
            int end = at + 1;
            while (end < text.Length && IsEmailDomainCharacter(text[end]))
                end++;
            while (end > at + 1 && text[end - 1] == '.')
                end--;

            ReadOnlySpan<char> local = text.AsSpan(start, at - start);
            ReadOnlySpan<char> domain = text.AsSpan(at + 1, end - at - 1);
            if (!local.IsEmpty && IsValidEmailDomain(domain))
            {
                ranges.Add((start, end));
                searchFrom = end;
            }
            else
            {
                searchFrom = at + 1;
            }
        }

        return ranges;
    }

    private static bool IsEmailLocalCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) ||
           ".!#$%&'*+/=?^_`{|}~-".Contains(value, StringComparison.Ordinal);

    private static bool IsEmailDomainCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_';

    private static bool IsValidEmailDomain(ReadOnlySpan<char> domain)
    {
        int labelStart = 0;
        int labelCount = 0;
        for (int index = 0; index <= domain.Length; index++)
        {
            if (index < domain.Length && domain[index] != '.')
                continue;

            ReadOnlySpan<char> label = domain[labelStart..index];
            if (label.IsEmpty ||
                !char.IsAsciiLetterOrDigit(label[0]) ||
                !char.IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }
            foreach (char value in label)
            {
                if (!char.IsAsciiLetterOrDigit(value) && value != '-')
                    return false;
            }

            labelCount++;
            labelStart = index + 1;
        }

        return labelCount >= 2;
    }

    private static LiteralInline CreateLiteral(
        string source,
        int start,
        int end,
        int line,
        int column)
        => new(new StringSlice(source, start, end))
        {
            Span = new Markdig.Syntax.SourceSpan(start, end),
            Line = line,
            Column = column,
            IsClosed = true,
        };

    internal sealed class PreparedSource
    {
        private readonly int[] _insertions;

        internal PreparedSource(
            string text,
            int[] insertions,
            bool hasMaskedSeparators)
        {
            Text = text;
            _insertions = insertions;
            HasChanges = insertions.Length > 0 || hasMaskedSeparators;
        }

        internal string Text { get; }

        internal bool HasChanges { get; }

        internal static PreparedSource Unchanged(string source)
            => new(source, Array.Empty<int>(), hasMaskedSeparators: false);

        internal Markdig.Syntax.SourceSpan MapSpan(Markdig.Syntax.SourceSpan span)
        {
            if (_insertions.Length == 0 || span.Start < 0 || span.End < 0)
                return span;

            int start = span.Start - CountLessThan(span.Start);
            int end = span.End - CountLessThanOrEqual(span.End);
            if (end < start)
                end = start;
            return new Markdig.Syntax.SourceSpan(start, end);
        }

        private int CountLessThan(int value)
        {
            int low = 0;
            int high = _insertions.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (_insertions[middle] < value)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private int CountLessThanOrEqual(int value)
        {
            int low = 0;
            int high = _insertions.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (_insertions[middle] <= value)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
    }

    private readonly record struct LineInfo(int Start, int Length);

    private static string FilterDisallowedRawHtml(string html)
    {
        int firstTag = html.IndexOf('<');
        if (firstTag < 0)
            return html;

        var output = new System.Text.StringBuilder(html.Length + 16);
        int copiedThrough = 0;
        for (int index = firstTag; index < html.Length; index++)
        {
            if (html[index] != '<' || !StartsDisallowedTag(html, index + 1))
                continue;

            output.Append(html, copiedThrough, index - copiedThrough);
            output.Append("&lt;");
            copiedThrough = index + 1;
        }

        if (copiedThrough == 0)
            return html;
        output.Append(html, copiedThrough, html.Length - copiedThrough);
        return output.ToString();
    }

    private static bool StartsDisallowedTag(string html, int offset)
    {
        if (offset < html.Length && html[offset] == '/')
            offset++;

        foreach (string tag in DisallowedRawHtmlTags)
        {
            if (offset <= html.Length - tag.Length &&
                html.AsSpan(offset, tag.Length).Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                int end = offset + tag.Length;
                return end == html.Length ||
                    char.IsWhiteSpace(html[end]) ||
                    html[end] is '>' or '/';
            }
        }

        return false;
    }

    // Adapted from Markdig's BSD-2-Clause ListRenderer. The only behavioral
    // change is the cmark-gfm reference newline before block-level list-item
    // children; tight inline paragraphs intentionally remain on the same line.
    private sealed class GfmReferenceListRenderer : HtmlObjectRenderer<ListBlock>
    {
        protected override void Write(HtmlRenderer renderer, ListBlock listBlock)
        {
            renderer.EnsureLine();
            if (renderer.EnableHtmlForBlock)
            {
                if (listBlock.IsOrdered)
                {
                    renderer.Write("<ol");
                    if (listBlock.BulletType != '1')
                    {
                        renderer.Write(" type=\"");
                        renderer.Write(listBlock.BulletType);
                        renderer.Write('"');
                    }
                    if (listBlock.OrderedStart is not null && listBlock.OrderedStart != "1")
                    {
                        renderer.Write(" start=\"");
                        renderer.Write(listBlock.OrderedStart);
                        renderer.Write('"');
                    }
                    renderer.WriteAttributes(listBlock);
                    renderer.WriteLine('>');
                }
                else
                {
                    renderer.Write("<ul");
                    renderer.WriteAttributes(listBlock);
                    renderer.WriteLine('>');
                }
            }

            foreach (Block item in listBlock)
            {
                var listItem = (ListItemBlock)item;
                bool previousImplicit = renderer.ImplicitParagraph;
                renderer.ImplicitParagraph = !listBlock.IsLoose;
                renderer.EnsureLine();
                if (renderer.EnableHtmlForBlock)
                {
                    renderer.Write("<li");
                    renderer.WriteAttributes(listItem);
                    bool staysOnOpeningLine =
                        listItem.Count == 0 ||
                        (!listBlock.IsLoose && listItem[0] is ParagraphBlock);
                    if (staysOnOpeningLine)
                        renderer.Write('>');
                    else
                        renderer.WriteLine('>');
                }

                renderer.WriteChildren(listItem);
                if (renderer.EnableHtmlForBlock)
                    renderer.WriteLine("</li>");
                renderer.EnsureLine();
                renderer.ImplicitParagraph = previousImplicit;
            }

            if (renderer.EnableHtmlForBlock)
                renderer.WriteLine(listBlock.IsOrdered ? "</ol>" : "</ul>");
            renderer.EnsureLine();
        }
    }

    // Adapted from Markdig's BSD-2-Clause HtmlTableRenderer. cmark-gfm 0.29
    // serializes column alignment with the historical align attribute.
    private sealed class GfmReferenceTableRenderer : HtmlObjectRenderer<Table>
    {
        protected override void Write(HtmlRenderer renderer, Table table)
        {
            if (!renderer.EnableHtmlForBlock)
            {
                bool previousImplicit = renderer.ImplicitParagraph;
                renderer.ImplicitParagraph = true;
                foreach (Block rowObject in table)
                {
                    var row = (TableRow)rowObject;
                    foreach (Block cellObject in row)
                    {
                        renderer.Write((TableCell)cellObject);
                        renderer.Write(' ');
                    }
                }
                renderer.ImplicitParagraph = previousImplicit;
                return;
            }

            renderer.EnsureLine();
            renderer.Write("<table").WriteAttributes(table).WriteLine('>');
            bool hasBody = false;
            bool hasHeader = false;
            bool headerOpen = false;
            foreach (Block rowObject in table)
            {
                var row = (TableRow)rowObject;
                if (row.IsHeader)
                {
                    if (!hasHeader)
                    {
                        renderer.WriteLine("<thead>");
                        headerOpen = true;
                    }
                    hasHeader = true;
                }
                else if (!hasBody)
                {
                    if (headerOpen)
                    {
                        renderer.WriteLine("</thead>");
                        headerOpen = false;
                    }
                    renderer.WriteLine("<tbody>");
                    hasBody = true;
                }

                renderer.Write("<tr").WriteAttributes(row).WriteLine('>');
                for (int index = 0; index < row.Count; index++)
                {
                    var cell = (TableCell)row[index];
                    renderer.EnsureLine();
                    renderer.Write(row.IsHeader ? "<th" : "<td");
                    if (cell.ColumnSpan != 1)
                        renderer.Write($" colspan=\"{cell.ColumnSpan}\"");
                    if (cell.RowSpan != 1)
                        renderer.Write($" rowspan=\"{cell.RowSpan}\"");
                    if (table.ColumnDefinitions.Count > 0)
                    {
                        int columnIndex = cell.ColumnIndex < 0 ||
                            cell.ColumnIndex >= table.ColumnDefinitions.Count
                                ? index
                                : cell.ColumnIndex;
                        columnIndex = Math.Min(columnIndex, table.ColumnDefinitions.Count - 1);
                        TableColumnAlign? alignment = table.ColumnDefinitions[columnIndex].Alignment;
                        if (alignment.HasValue)
                        {
                            string value = alignment.Value switch
                            {
                                TableColumnAlign.Left => "left",
                                TableColumnAlign.Center => "center",
                                TableColumnAlign.Right => "right",
                                _ => string.Empty,
                            };
                            if (value.Length > 0)
                                renderer.Write($" align=\"{value}\"");
                        }
                    }
                    renderer.WriteAttributes(cell);
                    renderer.Write('>');

                    bool previousImplicit = renderer.ImplicitParagraph;
                    if (cell.Count == 1)
                        renderer.ImplicitParagraph = true;
                    renderer.Write(cell);
                    renderer.ImplicitParagraph = previousImplicit;
                    renderer.WriteLine(row.IsHeader ? "</th>" : "</td>");
                }
                renderer.WriteLine("</tr>");
            }

            if (hasBody)
                renderer.WriteLine("</tbody>");
            else if (headerOpen)
                renderer.WriteLine("</thead>");
            renderer.WriteLine("</table>");
        }
    }
}
