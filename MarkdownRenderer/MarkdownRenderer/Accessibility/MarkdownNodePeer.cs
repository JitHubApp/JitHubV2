using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.Accessibility;

internal sealed partial class MarkdownNodePeer : FrameworkElementAutomationPeer, IGridProvider, ITableProvider, IGridItemProvider, ITableItemProvider
{
    private readonly MarkdownAutomationPeer _root;
    private readonly MarkdownSemanticNode _node;

    public MarkdownNodePeer(MarkdownRendererControl owner, MarkdownAutomationPeer root, MarkdownSemanticNode node) : base(owner)
    {
        _root = root;
        _node = node;
    }

    internal MarkdownSemanticNode Node => _node;

    protected override string GetClassNameCore() => _node.Role switch
    {
        MarkdownSemanticRole.List => "MarkdownList",
        MarkdownSemanticRole.ListItem => "MarkdownListItem",
        MarkdownSemanticRole.Table => "MarkdownTable",
        MarkdownSemanticRole.TableCell => "MarkdownTableCell",
        MarkdownSemanticRole.Image => "MarkdownImage",
        MarkdownSemanticRole.Embed => "MarkdownEmbed",
        MarkdownSemanticRole.Abbreviation => "MarkdownAbbreviation",
        _ => "MarkdownGroup",
    };

    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool IsKeyboardFocusableCore() => false;

    protected override AutomationControlType GetAutomationControlTypeCore() => _node.Role switch
    {
        MarkdownSemanticRole.List => AutomationControlType.List,
        MarkdownSemanticRole.ListItem => AutomationControlType.ListItem,
        MarkdownSemanticRole.Table => AutomationControlType.Table,
        MarkdownSemanticRole.TableCell => AutomationControlType.DataItem,
        MarkdownSemanticRole.Image => AutomationControlType.Image,
        MarkdownSemanticRole.Embed => AutomationControlType.Custom,
        MarkdownSemanticRole.Abbreviation => AutomationControlType.Text,
        _ => AutomationControlType.Group,
    };

    protected override string GetNameCore()
    {
        var text = _root.GetSemanticDocument().GetText(_node);
        if (!string.IsNullOrWhiteSpace(text)) return text;
        return _node.Role switch
        {
            MarkdownSemanticRole.Image => MarkdownLocalizedStrings.ImageName,
            MarkdownSemanticRole.Table => MarkdownLocalizedStrings.TableName,
            MarkdownSemanticRole.List => MarkdownLocalizedStrings.ListName,
            MarkdownSemanticRole.Embed => MarkdownLocalizedStrings.EmbeddedContentName,
            _ => string.Empty,
        };
    }

    protected override string GetHelpTextCore() => _node.HelpText ?? string.Empty;

    protected override IList<AutomationPeer> GetChildrenCore() => _root.GetChildPeersForSemanticNode(_node);

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        if (_node.Role == MarkdownSemanticRole.Table &&
            (patternIinterface == PatternInterface.Grid || patternIinterface == PatternInterface.Table))
        {
            return this;
        }

        if (_node.Role == MarkdownSemanticRole.TableCell &&
            (patternIinterface == PatternInterface.GridItem || patternIinterface == PatternInterface.TableItem))
        {
            return this;
        }

        return base.GetPatternCore(patternIinterface);
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        var rect = _node.Bounds;
        return rect.Width <= 0 || rect.Height <= 0
            ? base.GetBoundingRectangleCore()
            : _root.GetScreenRectForDocumentRect(rect);
    }

    int IGridProvider.ColumnCount => _node.ColumnCount;
    int IGridProvider.RowCount => _node.RowCount;

    IRawElementProviderSimple IGridProvider.GetItem(int row, int column)
    {
        foreach (var child in _node.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.Row == row &&
                child.Column == column &&
                _root.TryGetProviderForSemanticNode(child, out var provider))
            {
                return provider;
            }
        }

        return null!;
    }

    IRawElementProviderSimple[] ITableProvider.GetColumnHeaders()
    {
        var providers = new List<IRawElementProviderSimple>();
        foreach (var child in _node.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.IsHeader &&
                _root.TryGetProviderForSemanticNode(child, out var provider))
            {
                providers.Add(provider);
            }
        }

        return providers.ToArray();
    }

    IRawElementProviderSimple[] ITableProvider.GetRowHeaders() => System.Array.Empty<IRawElementProviderSimple>();

    RowOrColumnMajor ITableProvider.RowOrColumnMajor => RowOrColumnMajor.RowMajor;

    int IGridItemProvider.Column => _node.Column;
    int IGridItemProvider.ColumnSpan => _node.ColumnSpan;
    int IGridItemProvider.Row => _node.Row;
    int IGridItemProvider.RowSpan => _node.RowSpan;

    IRawElementProviderSimple IGridItemProvider.ContainingGrid
    {
        get
        {
            var table = FindAncestorTable(_node);
            return table is not null && _root.TryGetProviderForSemanticNode(table, out var provider)
                ? provider
                : null!;
        }
    }

    IRawElementProviderSimple[] ITableItemProvider.GetColumnHeaderItems()
    {
        var table = FindAncestorTable(_node);
        if (table is null) return System.Array.Empty<IRawElementProviderSimple>();

        var providers = new List<IRawElementProviderSimple>();
        foreach (var child in table.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.IsHeader &&
                child.Column == _node.Column &&
                _root.TryGetProviderForSemanticNode(child, out var provider))
            {
                providers.Add(provider);
            }
        }

        return providers.ToArray();
    }

    IRawElementProviderSimple[] ITableItemProvider.GetRowHeaderItems() => System.Array.Empty<IRawElementProviderSimple>();

    private static MarkdownSemanticNode? FindAncestorTable(MarkdownSemanticNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Role == MarkdownSemanticRole.Table) return current;
        }

        return null;
    }
}
