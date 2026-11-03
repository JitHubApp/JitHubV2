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
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Walks a parsed Markdown tree and extension content, recording the style
/// roles each node can read. HTML blocks are delegated to
/// <see cref="SafeHtmlStyleRoleDemandWalker"/>; both walkers record into one
/// <see cref="MarkdownStyleRoleDemandAccumulator"/>.
/// </summary>
internal sealed class MarkdownStyleRoleDemandWalker
{
    private readonly MarkdownStyleRoleDemandAccumulator _demand;
    private readonly SafeHtmlStyleRoleDemandWalker _html;
    private readonly CancellationToken _cancellationToken;

    internal MarkdownStyleRoleDemandWalker(
        MarkdownStyleRoleDemandAccumulator demand,
        SafeHtmlStyleRoleDemandWalker html,
        CancellationToken cancellationToken)
    {
        _demand = demand;
        _html = html;
        _cancellationToken = cancellationToken;
    }

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
                _demand.MarkIncomplete(item);
            }

            switch (item.Kind)
            {
                case MarkdownContentKind.Link:
                    _demand.Add(MarkdownStyleRoleDemand.Link);
                    break;
                case MarkdownContentKind.List:
                case MarkdownContentKind.ListItem:
                    _demand.Add(MarkdownStyleRoleDemand.ListMarker);
                    break;
                case MarkdownContentKind.Table:
                case MarkdownContentKind.TableRow:
                case MarkdownContentKind.TableCell:
                    _demand.AddTableDemand();
                    break;
                case MarkdownContentKind.CodeBlock:
                    _demand.AddCodeBlockDemand();
                    break;
                case MarkdownContentKind.Image:
                    _demand.Add(MarkdownStyleRoleDemand.ImageCaption);
                    break;
                case MarkdownContentKind.VectorScene:
                    _demand.Add(item.AccessibilityRole == MarkdownAccessibilityRole.Math
                        ? MarkdownStyleRoleDemand.Math
                        : MarkdownStyleRoleDemand.Diagram);
                    _demand.Add(MarkdownStyleRoleDemand.Link);
                    break;
                case MarkdownContentKind.Custom:
                    // Feature-pack renderers may request additional roles.
                    _demand.MarkIncomplete(item);
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
                _demand.Add(MarkdownStyleRoleDemand.Math);
                break;
            case HeadingBlock heading:
                _demand.Add(heading.Level switch
                {
                    1 => MarkdownStyleRoleDemand.Heading1,
                    2 => MarkdownStyleRoleDemand.Heading2,
                    3 => MarkdownStyleRoleDemand.Heading3,
                    4 => MarkdownStyleRoleDemand.Heading4,
                    5 => MarkdownStyleRoleDemand.Heading5,
                    _ => MarkdownStyleRoleDemand.Heading6,
                });
                break;
            case CodeBlock:
                _demand.AddCodeBlockDemand();
                break;
            case QuoteBlock:
                // GitHub alert renderers recognize alert markers at layout
                // time, so every quote can demand any alert style.
                _demand.Add(MarkdownStyleRoleDemand.Quote |
                    MarkdownStyleRoleDemand.AlertNote |
                    MarkdownStyleRoleDemand.AlertTip |
                    MarkdownStyleRoleDemand.AlertImportant |
                    MarkdownStyleRoleDemand.AlertWarning |
                    MarkdownStyleRoleDemand.AlertCaution);
                break;
            case ListBlock or ListItemBlock:
                _demand.Add(MarkdownStyleRoleDemand.ListMarker);
                break;
            case ThematicBreakBlock:
                _demand.Add(MarkdownStyleRoleDemand.ThematicBreak);
                break;
            case Table or TableRow or TableCell:
                _demand.AddTableDemand();
                break;
            case FootnoteGroup or Footnote:
                _demand.Add(MarkdownStyleRoleDemand.ListMarker | MarkdownStyleRoleDemand.Link);
                break;
            case DefinitionList or DefinitionItem or DefinitionTerm:
                _demand.Add(MarkdownStyleRoleDemand.DefinitionTerm | MarkdownStyleRoleDemand.DefinitionDescription);
                break;
            case Figure:
                _demand.Add(MarkdownStyleRoleDemand.Figure | MarkdownStyleRoleDemand.FigureCaption);
                break;
            case FigureCaption:
                _demand.Add(MarkdownStyleRoleDemand.FigureCaption);
                break;
            case HtmlBlock htmlBlock:
                _html.VisitHtmlBlock(htmlBlock);
                break;
            case ParagraphBlock:
                break;
            case ContainerBlock:
                // Unknown containers may be handled by custom renderers.
                _demand.MarkIncomplete(block);
                break;
            case LeafBlock:
                // Unknown leaves may request arbitrary built-in styles.
                _demand.MarkIncomplete(block);
                break;
            default:
                _demand.MarkIncomplete(block);
                break;
        }
    }

    private void AddInlineDemand(Inline inline)
    {
        switch (inline)
        {
            case CodeInline:
                _demand.Add(MarkdownStyleRoleDemand.CodeInline);
                break;
            case EmphasisInline emphasis:
                _demand.Add(GetEmphasisDemand(emphasis));
                break;
            case LinkInline link:
                _demand.Add(MarkdownStyleRoleDemand.Link);
                if (link.IsImage)
                    _demand.Add(MarkdownStyleRoleDemand.ImageCaption);
                break;
            case AbbreviationInline:
                _demand.Add(MarkdownStyleRoleDemand.Abbreviation);
                break;
            case HtmlInline html:
                if (!SafeHtmlStyleRoleDemandWalker.IsRoleNeutralUnsupportedHtml(html))
                    _demand.MarkIncomplete(html);
                break;
            case LiteralInline or LineBreakInline or HtmlEntityInline:
                break;
            case ContainerInline:
                // Unknown inline containers may come from Markdown packs.
                _demand.MarkIncomplete(inline);
                break;
            default:
                _demand.MarkIncomplete(inline);
                break;
        }
    }

    private static MarkdownStyleRoleDemand GetEmphasisDemand(EmphasisInline emphasis)
    {
        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2)
            return MarkdownStyleRoleDemand.Strikethrough;
        if (emphasis.DelimiterChar == '~')
            return MarkdownStyleRoleDemand.Subscript;
        if (emphasis.DelimiterChar == '^')
            return MarkdownStyleRoleDemand.Superscript;
        if (emphasis.DelimiterChar == '+')
            return MarkdownStyleRoleDemand.Inserted;
        if (emphasis.DelimiterChar == '=')
            return MarkdownStyleRoleDemand.Marked;
        return emphasis.DelimiterCount >= 2
            ? MarkdownStyleRoleDemand.Strong
            : MarkdownStyleRoleDemand.Emphasis;
    }
}
