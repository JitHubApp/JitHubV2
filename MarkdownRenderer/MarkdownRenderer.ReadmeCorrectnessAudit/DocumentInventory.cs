using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;

/// <summary>
/// Per-document structural inventory taken from the parsed tree the native
/// view lays out. Phase 3 compares it with the live view's UIA tree, so it
/// records what the renderer was asked to show, independent of layout.
/// Unrecognized inline node types are listed rather than silently skipped.
/// </summary>
internal sealed class DocumentInventory
{
    public bool Available { get; set; }
    public List<InventoryTextBlock> TextBlocks { get; set; } = [];
    public List<InventoryTable> Tables { get; set; } = [];
    public List<InventoryTaskItem> TaskItems { get; set; } = [];
    public List<InventorySpan> Disclosures { get; set; } = [];
    public List<InventorySpan> HtmlBlocks { get; set; } = [];
    public List<InventoryHtmlImage> HtmlImages { get; set; } = [];
    public List<InventorySpan> InlineMath { get; set; } = [];
    public List<string> UnrecognizedInlineTypes { get; set; } = [];
}

internal sealed class InventorySpan
{
    public int SourceStart { get; set; }
    public int SourceLength { get; set; }
}

internal sealed class InventoryTextBlock
{
    public string Kind { get; set; } = "";
    public int SourceStart { get; set; }
    public int SourceLength { get; set; }
    public string Text { get; set; } = "";
    public string TextSha256 { get; set; } = "";
}

internal sealed class InventoryTable
{
    public int SourceStart { get; set; }
    public int SourceLength { get; set; }
    public int RowCount { get; set; }
    public int HeaderRowCount { get; set; }
    public int ColumnCount { get; set; }
}

internal sealed class InventoryTaskItem
{
    public int SourceStart { get; set; }
    public int SourceLength { get; set; }
    public bool Checked { get; set; }
}

internal sealed class InventoryHtmlImage
{
    public int SourceStart { get; set; }
    public int SourceLength { get; set; }
    public string Origin { get; set; } = "";
    public string? SrcSha256 { get; set; }
    public int? SrcUtf16Length { get; set; }
    // The decoded source is needed in memory for input acquisition only; the
    // case report keeps its hash and length, like the other projections.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Src { get; set; }
}

internal static partial class DocumentInventoryBuilder
{
    // GitHub's disclosure block type is internal to its feature pack; it is
    // identified by name so this audit does not widen that pack's surface.
    private const string GitHubDisclosureBlockTypeName = "GitHubDetailsBlock";

    // GitHub alerts (AlertRenderer): a blockquote whose first paragraph starts
    // with one of these tags shows a title instead of the tag, so the tag is
    // not visible paragraph text.
    private static readonly string[] GitHubAlertTags = ["[!NOTE]", "[!TIP]", "[!IMPORTANT]", "[!WARNING]", "[!CAUTION]"];

    internal static DocumentInventory Create(MarkdownRenderer.Document.MarkdownDocument document)
    {
        var inventory = new DocumentInventory();
        Markdig.Syntax.MarkdownDocument? parsed = document.ParsedDocument;
        if (parsed is null)
            return inventory;

        inventory.Available = true;
        string source = document.Source;
        var unrecognized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (MarkdownObject node in parsed.Descendants())
        {
            switch (node)
            {
                case Table table:
                    inventory.Tables.Add(CreateTable(table));
                    break;
                case HeadingBlock heading:
                    AddTextBlock(inventory, "heading", heading, heading.Inline, unrecognized);
                    break;
                case ParagraphBlock paragraph:
                    AddTextBlock(inventory, paragraph.Parent is TableCell ? "table-cell" : "paragraph",
                        paragraph, paragraph.Inline, unrecognized, stripAlertTag: IsFirstQuoteParagraph(paragraph));
                    break;
                case HtmlBlock html:
                    inventory.HtmlBlocks.Add(Span(html));
                    AddHtmlImages(inventory, source, html.Span, "html-block");
                    break;
                case HtmlInline htmlInline:
                    AddHtmlImages(inventory, source, htmlInline.Span, "html-inline");
                    break;
                case Markdig.Extensions.Mathematics.MathInline math:
                    inventory.InlineMath.Add(Span(math));
                    break;
                case TaskList task:
                    inventory.TaskItems.Add(new InventoryTaskItem
                    {
                        SourceStart = task.Span.Start,
                        SourceLength = task.Span.Length,
                        Checked = task.Checked,
                    });
                    break;
                case ContainerBlock container when container.GetType().Name == GitHubDisclosureBlockTypeName:
                    inventory.Disclosures.Add(Span(container));
                    break;
            }
        }

        foreach (ContainerBlock container in parsed.Descendants<ContainerBlock>().Prepend(parsed))
            AddHtmlDetailsRegions(inventory, container, source);

        inventory.UnrecognizedInlineTypes = unrecognized.ToList();
        return inventory;
    }

    /// <summary>
    /// Ranks 91 and 262: <c>&lt;div&gt;&lt;details&gt;</c> opens in one HTML block and
    /// closes in a later one, with Markdown blocks between. The view keeps that
    /// content collapsed, as GitHub does, so the region from the block that
    /// opens the disclosure to the block that closes it is a disclosure.
    /// </summary>
    private static void AddHtmlDetailsRegions(DocumentInventory inventory, ContainerBlock container, string source)
    {
        int depth = 0;
        int regionStart = -1;
        Block? last = null;
        foreach (Block block in container)
        {
            last = block;
            if (block is not HtmlBlock html ||
                html.Span.Start < 0 || html.Span.End >= source.Length || html.Span.End < html.Span.Start)
            {
                continue;
            }

            string text = source.Substring(html.Span.Start, html.Span.Length);
            int opens = DetailsOpenTag().Count(text);
            int closes = DetailsCloseTag().Count(text);
            int before = depth;
            depth = Math.Max(0, depth + opens - closes);
            if (before == 0 && depth > 0)
            {
                regionStart = html.Span.Start;
            }
            else if (before > 0 && depth == 0 && regionStart >= 0)
            {
                inventory.Disclosures.Add(new InventorySpan
                {
                    SourceStart = regionStart,
                    SourceLength = html.Span.End - regionStart + 1,
                });
                regionStart = -1;
            }
        }

        // An unclosed <details> collapses the rest of its container.
        if (depth > 0 && regionStart >= 0 && last is not null && last.Span.End >= regionStart)
        {
            inventory.Disclosures.Add(new InventorySpan
            {
                SourceStart = regionStart,
                SourceLength = last.Span.End - regionStart + 1,
            });
        }
    }

    [GeneratedRegex(@"<details\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailsOpenTag();

    [GeneratedRegex(@"</details\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailsCloseTag();

    private static bool IsFirstQuoteParagraph(ParagraphBlock paragraph)
    {
        if (paragraph.Parent is not QuoteBlock quote)
            return false;
        foreach (Block child in quote)
        {
            if (child is ParagraphBlock first)
                return ReferenceEquals(first, paragraph);
        }
        return false;
    }

    /// <summary>
    /// Mirrors AlertRenderer: the tag is matched case-insensitively at the
    /// start of the first paragraph's text and is replaced by the alert's
    /// title, so only the remaining body text is visible paragraph text.
    /// </summary>
    private static bool IsLineBreakTag(string? tag) =>
        tag is not null && System.Text.RegularExpressions.Regex.IsMatch(
            tag, @"^<br\s*/?>$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string StripGitHubAlertTag(string text)
    {
        string trimmed = text.TrimStart();
        foreach (string tag in GitHubAlertTags)
        {
            if (trimmed.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                return trimmed[tag.Length..].TrimStart();
        }
        return text;
    }

    private static InventorySpan Span(MarkdownObject node)
        => new() { SourceStart = node.Span.Start, SourceLength = node.Span.Length };

    private static InventoryTable CreateTable(Table table)
    {
        int rows = 0, headers = 0, columns = 0;
        foreach (Block child in table)
        {
            if (child is not TableRow row)
                continue;
            rows++;
            if (row.IsHeader)
                headers++;
            columns = Math.Max(columns, row.Count);
        }

        return new InventoryTable
        {
            SourceStart = table.Span.Start,
            SourceLength = table.Span.Length,
            RowCount = rows,
            HeaderRowCount = headers,
            ColumnCount = columns,
        };
    }

    private static void AddTextBlock(
        DocumentInventory inventory,
        string kind,
        LeafBlock block,
        ContainerInline? inline,
        SortedSet<string> unrecognized,
        bool stripAlertTag = false)
    {
        if (inline is null)
            return;

        var builder = new StringBuilder();
        AppendVisibleText(inline, builder, unrecognized);
        string text = NormalizeWhitespace(builder.ToString());
        if (stripAlertTag)
            text = StripGitHubAlertTag(text);
        if (text.Length == 0)
            return;

        inventory.TextBlocks.Add(new InventoryTextBlock
        {
            Kind = kind,
            SourceStart = block.Span.Start,
            SourceLength = block.Span.Length,
            Text = text,
            TextSha256 = Program.HashUtf8(text),
        });
    }

    /// <summary>
    /// Appends text a reader sees for an inline subtree. Image alt text and raw
    /// HTML tags are not visible body text; their content is inventoried
    /// separately as images and HTML.
    /// </summary>
    internal static void AppendVisibleText(Inline inline, StringBuilder builder, SortedSet<string> unrecognized)
    {
        switch (inline)
        {
            case LinkInline { IsImage: true }:
                return;
            case LiteralInline literal:
                builder.Append(literal.Content.AsSpan());
                return;
            case CodeInline code:
                builder.Append(code.Content);
                return;
            case HtmlEntityInline entity:
                builder.Append(entity.Transcoded.AsSpan());
                return;
            case AutolinkInline autolink:
                builder.Append(autolink.Url);
                return;
            case LineBreakInline:
                builder.Append(' ');
                return;
            // A <br> renders as a line break, so the words on either side are
            // separate visible words; other raw tags contribute no text.
            case HtmlInline html when IsLineBreakTag(html.Tag):
                builder.Append(' ');
                return;
            case HtmlInline:
            case TaskList:
            // Inline math renders as a vector scene and footnote references as
            // generated labels; neither is authored body text. Math is counted
            // separately so the oracle can require its scenes.
            case Markdig.Extensions.Mathematics.MathInline:
            case Markdig.Extensions.Footnotes.FootnoteLink:
                return;
            case ContainerInline container:
                foreach (Inline child in container)
                    AppendVisibleText(child, builder, unrecognized);
                return;
            default:
                unrecognized.Add(inline.GetType().FullName ?? inline.GetType().Name);
                return;
        }
    }

    internal static string NormalizeWhitespace(string value)
        => WhitespaceRun().Replace(value, " ").Trim();

    private static void AddHtmlImages(DocumentInventory inventory, string source, SourceSpan span, string origin)
    {
        if (span.Start < 0 || span.Length <= 0 || span.Start + span.Length > source.Length)
            return;

        string html = source.Substring(span.Start, span.Length);
        foreach (Match tag in ImageTag().Matches(html))
        {
            Match src = SrcAttribute().Match(tag.Value);
            string? value = null;
            if (src.Success)
            {
                string raw = src.Groups["dq"].Success ? src.Groups["dq"].Value
                    : src.Groups["sq"].Success ? src.Groups["sq"].Value
                    : src.Groups["bare"].Value;
                // SafeHtml decodes attribute entities exactly once (P2-003).
                value = WebUtility.HtmlDecode(raw);
            }

            inventory.HtmlImages.Add(new InventoryHtmlImage
            {
                SourceStart = span.Start + tag.Index,
                SourceLength = tag.Length,
                Origin = origin,
                SrcSha256 = value is null ? null : Program.HashUtf8(value),
                SrcUtf16Length = value?.Length,
                Src = value,
            });
        }

        // A <picture> renders one of its <source srcset> URLs instead of the
        // <img> fallback when the source matches the theme (light or dark
        // prefers-color-scheme) or names a supported image type. Demand every
        // single-URL candidate so the input holds the bytes for either theme;
        // only <picture> sources carry srcset (media elements use src).
        foreach (Match tag in PictureSourceTag().Matches(html))
        {
            Match srcset = SrcsetAttribute().Match(tag.Value);
            if (!srcset.Success)
                continue;
            string raw = srcset.Groups["dq"].Success ? srcset.Groups["dq"].Value
                : srcset.Groups["sq"].Success ? srcset.Groups["sq"].Value
                : srcset.Groups["bare"].Value;
            string decoded = WebUtility.HtmlDecode(raw);
            string[] parts = decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (decoded.Contains(',') || parts.Length != 1)
                continue;

            inventory.HtmlImages.Add(new InventoryHtmlImage
            {
                SourceStart = span.Start + tag.Index,
                SourceLength = tag.Length,
                Origin = origin + "-picture-source",
                SrcSha256 = Program.HashUtf8(parts[0]),
                SrcUtf16Length = parts[0].Length,
                Src = parts[0],
            });
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"<source\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PictureSourceTag();

    [GeneratedRegex(@"\bsrcset\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SrcsetAttribute();

    [GeneratedRegex(@"\bsrc\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SrcAttribute();
}
