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
}

internal static partial class DocumentInventoryBuilder
{
    // GitHub's disclosure block type is internal to its feature pack; it is
    // identified by name so this audit does not widen that pack's surface.
    private const string GitHubDisclosureBlockTypeName = "GitHubDetailsBlock";

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
                        paragraph, paragraph.Inline, unrecognized);
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

        inventory.UnrecognizedInlineTypes = unrecognized.ToList();
        return inventory;
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
        SortedSet<string> unrecognized)
    {
        if (inline is null)
            return;

        var builder = new StringBuilder();
        AppendVisibleText(inline, builder, unrecognized);
        string text = NormalizeWhitespace(builder.ToString());
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
            });
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"\bsrc\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SrcAttribute();
}
