using System;
using System.Collections.Generic;
using System.Threading;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownDocumentSnapshot = MarkdownRenderer.Document.MarkdownDocument;

namespace MarkdownRenderer.Performance;

internal sealed class MarkdownStyleRoleDemandResult(
    ulong mask,
    bool isComplete,
    string? firstIncompleteNodeType)
{
    internal ulong Mask { get; } = mask;
    internal bool IsComplete { get; } = isComplete;
    internal string? FirstIncompleteNodeType { get; } = firstIncompleteNodeType;
}

/// <summary>
/// Computes the built-in style roles a complete immutable document can use.
/// This lives in the opt-in Performance pack so lean Core/WinUI consumers keep
/// their original full-resolution path without carrying the collector.
/// </summary>
internal static class MarkdownStyleRoleDemandCollector
{
    // Bit positions intentionally match ThemeResolver.BuiltInElementKeys.
    [Flags]
    private enum StyleRoleDemand : ulong
    {
        None = 0,
        Heading1 = 1UL << 0,
        Heading2 = 1UL << 1,
        Heading3 = 1UL << 2,
        Heading4 = 1UL << 3,
        Heading5 = 1UL << 4,
        Heading6 = 1UL << 5,
        Body = 1UL << 6,
        CodeInline = 1UL << 7,
        CodeBlock = 1UL << 8,
        CodeBlockHeader = 1UL << 9,
        CodeBlockLanguage = 1UL << 10,
        CodeBlockGutter = 1UL << 11,
        CodeBlockLineNumber = 1UL << 12,
        Quote = 1UL << 13,
        Link = 1UL << 14,
        Strong = 1UL << 15,
        Emphasis = 1UL << 16,
        Strikethrough = 1UL << 17,
        Subscript = 1UL << 18,
        Superscript = 1UL << 19,
        Inserted = 1UL << 20,
        Marked = 1UL << 21,
        Abbreviation = 1UL << 22,
        ListMarker = 1UL << 23,
        ThematicBreak = 1UL << 24,
        ImageCaption = 1UL << 25,
        Figure = 1UL << 26,
        FigureCaption = 1UL << 27,
        Diagram = 1UL << 28,
        Math = 1UL << 29,
        DefinitionTerm = 1UL << 30,
        DefinitionDescription = 1UL << 31,
        Table = 1UL << 32,
        TableHeader = 1UL << 33,
        TableCell = 1UL << 34,
        AlertNote = 1UL << 35,
        AlertTip = 1UL << 36,
        AlertImportant = 1UL << 37,
        AlertWarning = 1UL << 38,
        AlertCaution = 1UL << 39,
    }

    internal static MarkdownStyleRoleDemandResult Collect(
        MarkdownDocumentSnapshot document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var collector = new Collector(cancellationToken);
        if (document.ParsedDocument is { } parsedDocument)
        {
            foreach (Block block in parsedDocument)
                collector.VisitBlock(block);
        }

        foreach (MarkdownContentFragment fragment in document.ExtensionBlockNodeContent.Values)
            collector.VisitExtensionContent(fragment.Items);
        foreach (MarkdownContentFragment fragment in document.ExtensionInlineNodeContent.Values)
            collector.VisitExtensionContent(fragment.Items);

        return new MarkdownStyleRoleDemandResult(
            (ulong)collector.Demands,
            collector.IsComplete,
            collector.FirstIncompleteNodeType);
    }

    private sealed class Collector
    {
        private readonly CancellationToken _cancellationToken;

        internal Collector(CancellationToken cancellationToken)
            => _cancellationToken = cancellationToken;

        internal StyleRoleDemand Demands { get; private set; } = StyleRoleDemand.Body;
        internal bool IsComplete { get; private set; } = true;
        internal string? FirstIncompleteNodeType { get; private set; }

        internal void VisitBlock(Block block)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            AddBlockDemand(block);
            if (block is LeafBlock leaf)
                VisitInlines(leaf.Inline);
            if (block is ContainerBlock container)
            {
                foreach (Block child in container)
                    VisitBlock(child);
            }
        }

        internal void VisitInlines(ContainerInline? container)
        {
            if (container is null)
                return;

            foreach (Inline inline in container)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                AddInlineDemand(inline);
                if (inline is ContainerInline nested)
                    VisitInlines(nested);
            }
        }

        internal void VisitExtensionContent(IReadOnlyList<MarkdownContent> content)
        {
            foreach (MarkdownContent item in content)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!item.StyleRole.IsEmpty)
                {
                    // Declarative extensions can name built-in or custom
                    // roles, so retain full resource resolution for them.
                    MarkIncomplete(item);
                }

                switch (item.Kind)
                {
                    case MarkdownContentKind.Link:
                        Add(StyleRoleDemand.Link);
                        break;
                    case MarkdownContentKind.List:
                    case MarkdownContentKind.ListItem:
                        Add(StyleRoleDemand.ListMarker);
                        break;
                    case MarkdownContentKind.Table:
                    case MarkdownContentKind.TableRow:
                    case MarkdownContentKind.TableCell:
                        AddTableDemand();
                        break;
                    case MarkdownContentKind.CodeBlock:
                        AddCodeBlockDemand();
                        break;
                    case MarkdownContentKind.Image:
                        Add(StyleRoleDemand.ImageCaption);
                        break;
                    case MarkdownContentKind.VectorScene:
                        Add(item.AccessibilityRole == MarkdownAccessibilityRole.Math
                            ? StyleRoleDemand.Math
                            : StyleRoleDemand.Diagram);
                        Add(StyleRoleDemand.Link);
                        break;
                    case MarkdownContentKind.Custom:
                        // Feature-pack renderers may request additional roles.
                        MarkIncomplete(item);
                        break;
                }

                if (item.Children.Count > 0)
                    VisitExtensionContent(item.Children);
            }
        }

        private void AddBlockDemand(Block block)
        {
            switch (block)
            {
                // MathBlock derives from Markdig's code-block hierarchy.
                case MathBlock:
                    Add(StyleRoleDemand.Math);
                    break;
                case HeadingBlock heading:
                    Add(heading.Level switch
                    {
                        1 => StyleRoleDemand.Heading1,
                        2 => StyleRoleDemand.Heading2,
                        3 => StyleRoleDemand.Heading3,
                        4 => StyleRoleDemand.Heading4,
                        5 => StyleRoleDemand.Heading5,
                        _ => StyleRoleDemand.Heading6,
                    });
                    break;
                case CodeBlock:
                    AddCodeBlockDemand();
                    break;
                case QuoteBlock:
                    // GitHub alert renderers recognize alert markers at layout
                    // time, so every quote can demand any alert style.
                    Add(StyleRoleDemand.Quote |
                        StyleRoleDemand.AlertNote |
                        StyleRoleDemand.AlertTip |
                        StyleRoleDemand.AlertImportant |
                        StyleRoleDemand.AlertWarning |
                        StyleRoleDemand.AlertCaution);
                    break;
                case ListBlock or ListItemBlock:
                    Add(StyleRoleDemand.ListMarker);
                    break;
                case ThematicBreakBlock:
                    Add(StyleRoleDemand.ThematicBreak);
                    break;
                case Table or TableRow or TableCell:
                    AddTableDemand();
                    break;
                case FootnoteGroup or Footnote:
                    Add(StyleRoleDemand.ListMarker | StyleRoleDemand.Link);
                    break;
                case DefinitionList or DefinitionItem or DefinitionTerm:
                    Add(StyleRoleDemand.DefinitionTerm | StyleRoleDemand.DefinitionDescription);
                    break;
                case Figure:
                    Add(StyleRoleDemand.Figure | StyleRoleDemand.FigureCaption);
                    break;
                case FigureCaption:
                    Add(StyleRoleDemand.FigureCaption);
                    break;
                case HtmlBlock:
                    MarkIncomplete(block);
                    break;
                case ParagraphBlock:
                    break;
                case ContainerBlock:
                    // Unknown containers may be handled by custom renderers.
                    MarkIncomplete(block);
                    break;
                case LeafBlock:
                    // Unknown leaves may request arbitrary built-in styles.
                    MarkIncomplete(block);
                    break;
                default:
                    MarkIncomplete(block);
                    break;
            }
        }

        private void AddInlineDemand(Inline inline)
        {
            switch (inline)
            {
                case CodeInline:
                    Add(StyleRoleDemand.CodeInline);
                    break;
                case EmphasisInline emphasis:
                    Add(GetEmphasisDemand(emphasis));
                    break;
                case LinkInline link:
                    Add(StyleRoleDemand.Link);
                    if (link.IsImage)
                        Add(StyleRoleDemand.ImageCaption);
                    break;
                case AbbreviationInline:
                    Add(StyleRoleDemand.Abbreviation);
                    break;
                case HtmlInline html:
                    if (!IsRoleNeutralUnsupportedHtml(html))
                        MarkIncomplete(html);
                    break;
                case LiteralInline or LineBreakInline or HtmlEntityInline:
                    break;
                case ContainerInline:
                    // Unknown inline containers may come from Markdown packs.
                    MarkIncomplete(inline);
                    break;
                default:
                    MarkIncomplete(inline);
                    break;
            }
        }

        private static bool IsRoleNeutralUnsupportedHtml(HtmlInline html)
        {
            // An unparsable tag remains conservative: a future policy could
            // interpret it differently. Parsed comments, declarations, raw
            // suppressed elements, and unsupported custom elements are either
            // literal text or omitted and never acquire a style role.
            if (!SafeHtmlParser.TryParseSingleTag(html.Tag, out SafeHtmlTag tag))
                return false;
            return tag.Kind is SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration ||
                SafeHtmlParser.IsSuppressedElement(tag.Name) ||
                !SafeHtmlInlineState.IsSupportedElement(tag.Name);
        }

        private void AddCodeBlockDemand()
            => Add(StyleRoleDemand.CodeBlock |
                StyleRoleDemand.CodeBlockHeader |
                StyleRoleDemand.CodeBlockLanguage |
                StyleRoleDemand.CodeBlockGutter |
                StyleRoleDemand.CodeBlockLineNumber);

        private void AddTableDemand()
            => Add(StyleRoleDemand.Table | StyleRoleDemand.TableHeader | StyleRoleDemand.TableCell);

        private void Add(StyleRoleDemand demand) => Demands |= demand;

        private void MarkIncomplete(object source)
        {
            if (!IsComplete)
                return;

            IsComplete = false;
            FirstIncompleteNodeType = source.GetType().FullName;
        }

        private static StyleRoleDemand GetEmphasisDemand(EmphasisInline emphasis)
        {
            if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
                return StyleRoleDemand.Strikethrough;
            if (emphasis.DelimiterChar == '~')
                return StyleRoleDemand.Subscript;
            if (emphasis.DelimiterChar == '^')
                return StyleRoleDemand.Superscript;
            if (emphasis.DelimiterChar == '+')
                return StyleRoleDemand.Inserted;
            if (emphasis.DelimiterChar == '=')
                return StyleRoleDemand.Marked;
            return emphasis.DelimiterCount >= 2
                ? StyleRoleDemand.Strong
                : StyleRoleDemand.Emphasis;
        }
    }
}
