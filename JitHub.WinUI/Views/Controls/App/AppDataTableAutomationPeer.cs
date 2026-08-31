using System;
using System.Collections.Generic;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

internal sealed partial class AppDataTableHeaderButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new AppDataTableHeaderButtonAutomationPeer(this);
}

internal sealed partial class AppDataTableHeaderButtonAutomationPeer : ButtonAutomationPeer
{
    public AppDataTableHeaderButtonAutomationPeer(AppDataTableHeaderButton owner) : base(owner)
    {
    }

    protected override string GetClassNameCore() => nameof(AppDataTableHeaderButton);

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.HeaderItem;
}

internal sealed partial class AppDataTableAutomationPeer : FrameworkElementAutomationPeer, IGridProvider, ITableProvider
{
    private readonly AppDataTable _owner;

    public AppDataTableAutomationPeer(AppDataTable owner) : base(owner)
    {
        _owner = owner;
    }

    protected override string GetClassNameCore() => nameof(AppDataTable);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Table;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Grid or PatternInterface.Table => this,
        _ => base.GetPatternCore(patternInterface),
    };

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        IList<AutomationPeer> baseChildren = base.GetChildrenCore() ?? Array.Empty<AutomationPeer>();
        List<AutomationPeer> children = new(_owner.ColumnCount + baseChildren.Count);
        for (int column = 0; column < _owner.ColumnCount; column++)
        {
            if (_owner.GetHeaderElement(column) is Button header &&
                (FromElement(header) ?? CreatePeerForElement(header)) is AutomationPeer peer)
            {
                children.Add(peer);
            }
        }

        foreach (AutomationPeer child in baseChildren)
        {
            if (!string.Equals(child.GetAutomationId(), "HeaderScroller", StringComparison.Ordinal))
            {
                children.Add(child);
            }
        }

        return children;
    }

    int IGridProvider.ColumnCount => _owner.ColumnCount;

    int IGridProvider.RowCount => _owner.RowCount;

    IRawElementProviderSimple IGridProvider.GetItem(int row, int column)
    {
        AppDataTableCell? cell = _owner.GetRealizedCell(row, column);
        AutomationPeer? peer = cell is null
            ? null
            : FrameworkElementAutomationPeer.FromElement(cell) ??
              FrameworkElementAutomationPeer.CreatePeerForElement(cell);
        return peer is null ? null! : ProviderFromPeer(peer);
    }

    public IRawElementProviderSimple[] GetColumnHeaders()
    {
        List<IRawElementProviderSimple> providers = new(_owner.ColumnCount);
        for (int column = 0; column < _owner.ColumnCount; column++)
        {
            if (_owner.GetHeaderElement(column) is not Button header)
            {
                continue;
            }

            AutomationPeer? peer = FromElement(header) ?? CreatePeerForElement(header);
            if (peer is not null)
            {
                providers.Add(ProviderFromPeer(peer));
            }
        }

        return providers.Count > 0 ? providers.ToArray() : Array.Empty<IRawElementProviderSimple>();
    }

    public IRawElementProviderSimple[] GetRowHeaders() => Array.Empty<IRawElementProviderSimple>();

    public RowOrColumnMajor RowOrColumnMajor => RowOrColumnMajor.RowMajor;
}

internal sealed partial class AppDataTableCell : Grid
{
    internal AppDataTableCell(
        AppDataTable table,
        AppDataTableRowModel row,
        int sourceColumnIndex,
        Border visual)
    {
        Table = table;
        Row = row;
        SourceColumnIndex = sourceColumnIndex;
        Visual = visual;
        Children.Add(visual);
    }

    internal AppDataTable Table { get; }

    internal AppDataTableRowModel Row { get; }

    internal int SourceColumnIndex { get; }

    internal Border Visual { get; }

    protected override AutomationPeer OnCreateAutomationPeer() => new AppDataTableCellAutomationPeer(this);
}

internal sealed partial class AppDataTableCellAutomationPeer : FrameworkElementAutomationPeer, IGridItemProvider, ITableItemProvider
{
    private readonly AppDataTableCell _cell;

    public AppDataTableCellAutomationPeer(AppDataTableCell owner) : base(owner)
    {
        _cell = owner;
    }

    protected override string GetClassNameCore() => nameof(AppDataTableCell);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override string GetNameCore()
    {
        int displayColumn = _cell.Table.GetDisplayColumn(_cell.SourceColumnIndex);
        string header = displayColumn >= 0
            ? _cell.Table.Columns[displayColumn].Header
            : string.Empty;
        return LF(
            "RepoCode/Csv/CellAutomationName",
            "{0}: {1}",
            header,
            _cell.Row.GetValue(_cell.SourceColumnIndex));
    }

    protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.GridItem or PatternInterface.TableItem => this,
        _ => base.GetPatternCore(patternInterface),
    };

    int IGridItemProvider.Column => _cell.Table.GetDisplayColumn(_cell.SourceColumnIndex);

    int IGridItemProvider.ColumnSpan => 1;

    int IGridItemProvider.Row => _cell.Table.GetDisplayRow(_cell.Row);

    int IGridItemProvider.RowSpan => 1;

    IRawElementProviderSimple IGridItemProvider.ContainingGrid => GetTableProvider();

    public IRawElementProviderSimple[] GetColumnHeaderItems()
    {
        int column = _cell.Table.GetDisplayColumn(_cell.SourceColumnIndex);
        if (_cell.Table.GetHeaderElement(column) is not Button header)
        {
            return [];
        }

        AutomationPeer? peer = FromElement(header) ?? CreatePeerForElement(header);
        return peer is null ? Array.Empty<IRawElementProviderSimple>() : [ProviderFromPeer(peer)];
    }

    public IRawElementProviderSimple[] GetRowHeaderItems() => Array.Empty<IRawElementProviderSimple>();

    private IRawElementProviderSimple GetTableProvider()
    {
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(_cell.Table) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(_cell.Table);
        return peer is null ? null! : ProviderFromPeer(peer);
    }

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);
}
