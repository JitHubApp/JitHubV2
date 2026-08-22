using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class AppDataTable : UserControl
{
    private const double MinimumColumnWidth = 88;
    private const double MaximumColumnWidth = 480;
    private const double DefaultViewportWidth = 1200;
    private const int ColumnOverscan = 1;

    private readonly List<AppDataTableColumn> _columns = [];
    private readonly List<AppDataTableRowModel> _sourceRows = [];
    private ObservableCollection<AppDataTableRowModel> _displayRows = [];
    private ScrollViewer? _rowsScroller;
    private int? _sortSourceIndex;
    private bool _sortDescending;
    private int _selectedDisplayRow = -1;
    private int _selectedSourceColumn = -1;
    private bool _synchronizingScroll;
    private int _firstRealizedColumn;
    private int _lastRealizedColumn = -1;

    internal event EventHandler<AppDataTableLayoutChangedEventArgs>? LayoutChanged;

    internal event EventHandler? ActiveCellChanged;

    public AppDataTable()
    {
        InitializeComponent();
        ShowStatus(L(
            "RepoCode/Csv/Empty",
            "No rows to display."));
    }

    internal IReadOnlyList<AppDataTableColumn> Columns => _columns;

    internal int RowCount => _displayRows.Count;

    internal int ColumnCount => _columns.Count;

    internal int SelectedRow => _selectedDisplayRow;

    internal int SelectedColumn => _columns.FindIndex(column => column.SourceIndex == _selectedSourceColumn);

    internal int FirstRealizedColumn => _firstRealizedColumn;

    internal int LastRealizedColumn => _lastRealizedColumn;

    internal double TableWidth => _columns.Sum(column => column.Width);

    internal void SetDocument(CsvDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _columns.Clear();
        for (int index = 0; index < document.Headers.Count; index++)
        {
            string header = document.Headers[index];
            double width = Math.Clamp(96 + (header.Length * 7), index == 0 ? 160 : 112, 280);
            _columns.Add(new AppDataTableColumn(index, header, width));
        }

        _sourceRows.Clear();
        foreach (CsvRow row in document.Rows)
        {
            _sourceRows.Add(new AppDataTableRowModel(this, row));
        }

        _sortSourceIndex = null;
        _sortDescending = false;
        _selectedDisplayRow = _sourceRows.Count > 0 ? 0 : -1;
        _selectedSourceColumn = _columns.Count > 0 ? _columns[0].SourceIndex : -1;
        ApplySort();
        RebuildHeader();
        UpdateVisibleColumnWindow(force: true);

        StatusOverlay.Visibility = _sourceRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_sourceRows.Count == 0)
        {
            StatusText.Text = L(
                "RepoCode/Csv/HeaderOnly",
                "This file contains a header but no data rows.");
        }

        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ShowStatus(string message)
    {
        _columns.Clear();
        _sourceRows.Clear();
        _displayRows = [];
        RowsList.ItemsSource = null;
        HeaderGrid.Children.Clear();
        HeaderGrid.ColumnDefinitions.Clear();
        HeaderGrid.Width = 0;
        StatusText.Text = message;
        StatusOverlay.Visibility = Visibility.Visible;
        _selectedDisplayRow = -1;
        _selectedSourceColumn = -1;
        _firstRealizedColumn = 0;
        _lastRealizedColumn = -1;
        LayoutChanged?.Invoke(this, new AppDataTableLayoutChangedEventArgs(rebuildCells: true));
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool IsActiveCell(AppDataTableRowModel row, int sourceColumnIndex)
    {
        if (_selectedDisplayRow < 0 || _selectedDisplayRow >= _displayRows.Count)
        {
            return false;
        }

        return ReferenceEquals(_displayRows[_selectedDisplayRow], row) &&
            _selectedSourceColumn == sourceColumnIndex;
    }

    internal void SelectCell(AppDataTableRowModel row, int sourceColumnIndex)
    {
        int displayIndex = IndexOfReference(_displayRows, row);
        if (displayIndex < 0 || _columns.All(column => column.SourceIndex != sourceColumnIndex))
        {
            return;
        }

        _selectedDisplayRow = displayIndex;
        _selectedSourceColumn = sourceColumnIndex;
        EnsureColumnVisible(GetDisplayColumn(sourceColumnIndex));
        Focus(FocusState.Programmatic);
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    internal AppDataTableCell? GetRealizedCell(int row, int column)
    {
        if (row < 0 || row >= _displayRows.Count || column < 0 || column >= _columns.Count)
        {
            return null;
        }

        EnsureColumnVisible(column);
        RowsList.ScrollIntoView(_displayRows[row]);
        RowsList.UpdateLayout();
        if (RowsList.ContainerFromItem(_displayRows[row]) is not ListViewItem container)
        {
            return null;
        }

        AppDataTableRowPresenter? presenter = FindDescendant<AppDataTableRowPresenter>(container);
        return presenter?.GetCell(column) as AppDataTableCell;
    }

    internal int GetDisplayRow(AppDataTableRowModel row) => IndexOfReference(_displayRows, row);

    internal int GetDisplayColumn(int sourceColumnIndex) =>
        _columns.FindIndex(column => column.SourceIndex == sourceColumnIndex);

    internal FrameworkElement? GetHeaderElement(int column)
    {
        if (column < 0 || column >= HeaderGrid.Children.Count)
        {
            return null;
        }

        return FindDescendant<Button>(HeaderGrid.Children[column]);
    }

    internal double GetColumnLeft(int displayColumn)
    {
        double left = 0;
        for (int index = 0; index < displayColumn && index < _columns.Count; index++)
        {
            left += _columns[index].Width;
        }

        return left;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new AppDataTableAutomationPeer(this);

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        _rowsScroller = FindDescendant<ScrollViewer>(RowsList);
        if (_rowsScroller is not null)
        {
            _rowsScroller.ViewChanged -= RowsScroller_ViewChanged;
            _rowsScroller.ViewChanged += RowsScroller_ViewChanged;
        }

        UpdateVisibleColumnWindow(force: true);
    }

    private void Root_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_rowsScroller is not null)
        {
            _rowsScroller.ViewChanged -= RowsScroller_ViewChanged;
            _rowsScroller = null;
        }
    }

    private void RowsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not AppDataTableRowModel row || args.ItemContainer is null)
        {
            return;
        }

        AutomationProperties.SetAutomationId(args.ItemContainer, $"CsvPreviewDataTableRow_{row.SourceIndex}");
        AutomationProperties.SetName(args.ItemContainer, LF(
            "RepoCode/Csv/RowAutomationName",
            "Row {0}",
            GetDisplayRow(row) + 1));
    }

    private void RebuildHeader()
    {
        HeaderGrid.Children.Clear();
        HeaderGrid.ColumnDefinitions.Clear();

        for (int displayIndex = 0; displayIndex < _columns.Count; displayIndex++)
        {
            AppDataTableColumn column = _columns[displayIndex];
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });

            Border cell = new()
            {
                Style = (Style)Resources["AppDataTableHeaderCellStyle"],
                AllowDrop = true,
                Tag = column,
            };
            cell.DragOver += HeaderCell_DragOver;
            cell.Drop += HeaderCell_Drop;

            Grid content = new();
            Button sortButton = new()
            {
                Style = (Style)Resources["AppDataTableHeaderButtonStyle"],
                Tag = column,
                CanDrag = true,
            };
            sortButton.Click += HeaderButton_Click;
            sortButton.DragStarting += HeaderButton_DragStarting;
            AutomationProperties.SetAutomationId(sortButton, $"CsvPreviewDataTableSortColumn_{column.SourceIndex}");
            AutomationProperties.SetName(sortButton, LF(
                "RepoCode/Csv/SortColumnAutomationName",
                "Sort by {0}",
                column.Header));
            ToolTipService.SetToolTip(sortButton, LF(
                "RepoCode/Csv/SortColumnToolTip",
                "Sort by {0}",
                column.Header));

            Grid label = new() { ColumnSpacing = 8 };
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = new()
            {
                Text = column.Header,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            FontIcon indicator = new()
            {
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = _sortSourceIndex == column.SourceIndex ? Visibility.Visible : Visibility.Collapsed,
                Glyph = _sortDescending ? "\uE70E" : "\uE70D",
            };
            Grid.SetColumn(indicator, 1);
            label.Children.Add(title);
            label.Children.Add(indicator);
            sortButton.Content = label;
            content.Children.Add(sortButton);

            Thumb resizeThumb = new()
            {
                Style = (Style)Resources["AppDataTableResizeThumbStyle"],
                Tag = column,
            };
            resizeThumb.DragDelta += ResizeThumb_DragDelta;
            AutomationProperties.SetAutomationId(resizeThumb, $"CsvPreviewDataTableResizeColumn_{column.SourceIndex}");
            AutomationProperties.SetName(resizeThumb, LF(
                "RepoCode/Csv/ResizeColumnAutomationName",
                "Resize {0} column",
                column.Header));
            content.Children.Add(resizeThumb);
            cell.Child = content;

            Grid.SetColumn(cell, displayIndex);
            HeaderGrid.Children.Add(cell);
        }

        UpdateTableWidth();
    }

    private void UpdateTableWidth()
    {
        double width = _columns.Sum(column => column.Width);
        HeaderGrid.Width = width;
        LayoutChanged?.Invoke(this, new AppDataTableLayoutChangedEventArgs(rebuildCells: false));
        UpdateVisibleColumnWindow(force: false);
    }

    private void HeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppDataTableColumn column })
        {
            return;
        }

        if (_sortSourceIndex == column.SourceIndex)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortSourceIndex = column.SourceIndex;
            _sortDescending = false;
        }

        AppDataTableRowModel? activeRow = GetActiveRow();
        ApplySort();
        if (activeRow is not null)
        {
            _selectedDisplayRow = IndexOfReference(_displayRows, activeRow);
        }

        RebuildHeader();
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySort()
    {
        IEnumerable<AppDataTableRowModel> rows = _sourceRows;
        if (_sortSourceIndex is int sourceIndex)
        {
            IOrderedEnumerable<AppDataTableRowModel> ordered = _sortDescending
                ? rows.OrderByDescending(
                    row => row.GetValue(sourceIndex),
                    StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(
                    row => row.GetValue(sourceIndex),
                    StringComparer.CurrentCultureIgnoreCase);
            rows = ordered.ThenBy(row => row.SourceIndex);
        }

        _displayRows = new ObservableCollection<AppDataTableRowModel>(rows);
        RowsList.ItemsSource = _displayRows;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: AppDataTableColumn column })
        {
            return;
        }

        column.Width = Math.Clamp(column.Width + e.HorizontalChange, MinimumColumnWidth, MaximumColumnWidth);
        int displayIndex = _columns.IndexOf(column);
        if (displayIndex >= 0 && displayIndex < HeaderGrid.ColumnDefinitions.Count)
        {
            HeaderGrid.ColumnDefinitions[displayIndex].Width = new GridLength(column.Width);
        }

        UpdateTableWidth();
    }

    private void HeaderButton_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is Button { Tag: AppDataTableColumn column })
        {
            args.AllowedOperations = DataPackageOperation.Move;
            args.Data.SetText(column.SourceIndex.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void HeaderCell_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = L(
            "RepoCode/Csv/ReorderColumn",
            "Move column");
    }

    private async void HeaderCell_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: AppDataTableColumn destination } ||
            !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }

        string sourceText = await e.DataView.GetTextAsync();
        if (!int.TryParse(sourceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sourceIndex))
        {
            return;
        }

        int from = _columns.FindIndex(column => column.SourceIndex == sourceIndex);
        int to = _columns.IndexOf(destination);
        if (from < 0 || to < 0 || from == to)
        {
            return;
        }

        AppDataTableColumn moving = _columns[from];
        _columns.RemoveAt(from);
        _columns.Insert(to, moving);
        RebuildHeader();
        UpdateVisibleColumnWindow(force: true);
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_displayRows.Count == 0 || _columns.Count == 0)
        {
            return;
        }

        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control && e.Key == Windows.System.VirtualKey.C)
        {
            CopyActiveCell();
            e.Handled = true;
            return;
        }

        int displayColumn = _columns.FindIndex(column => column.SourceIndex == _selectedSourceColumn);
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
                displayColumn = Math.Max(0, displayColumn - 1);
                break;
            case Windows.System.VirtualKey.Right:
                displayColumn = Math.Min(_columns.Count - 1, displayColumn + 1);
                break;
            case Windows.System.VirtualKey.Up:
                _selectedDisplayRow = Math.Max(0, _selectedDisplayRow - 1);
                break;
            case Windows.System.VirtualKey.Down:
                _selectedDisplayRow = Math.Min(_displayRows.Count - 1, _selectedDisplayRow + 1);
                break;
            case Windows.System.VirtualKey.PageUp:
                _selectedDisplayRow = Math.Max(0, _selectedDisplayRow - 10);
                break;
            case Windows.System.VirtualKey.PageDown:
                _selectedDisplayRow = Math.Min(_displayRows.Count - 1, _selectedDisplayRow + 10);
                break;
            case Windows.System.VirtualKey.Home:
                if (control)
                {
                    _selectedDisplayRow = 0;
                }
                else
                {
                    displayColumn = 0;
                }
                break;
            case Windows.System.VirtualKey.End:
                if (control)
                {
                    _selectedDisplayRow = _displayRows.Count - 1;
                }
                else
                {
                    displayColumn = _columns.Count - 1;
                }
                break;
            default:
                return;
        }

        _selectedDisplayRow = Math.Max(0, _selectedDisplayRow);
        _selectedSourceColumn = _columns[Math.Max(0, displayColumn)].SourceIndex;
        EnsureColumnVisible(displayColumn);
        RowsList.ScrollIntoView(_displayRows[_selectedDisplayRow]);
        ActiveCellChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void CopyActiveCell()
    {
        AppDataTableRowModel? row = GetActiveRow();
        if (row is null || _selectedSourceColumn < 0)
        {
            return;
        }

        try
        {
            DataPackage package = new();
            package.SetText(row.GetValue(_selectedSourceColumn));
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception)
        {
            // Clipboard access can be denied by an isolated or closing app window.
        }
    }

    private AppDataTableRowModel? GetActiveRow()
    {
        return _selectedDisplayRow >= 0 && _selectedDisplayRow < _displayRows.Count
            ? _displayRows[_selectedDisplayRow]
            : null;
    }

    private void RowsScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_synchronizingScroll || _rowsScroller is null)
        {
            return;
        }

        _synchronizingScroll = true;
        HeaderScroller.ChangeView(_rowsScroller.HorizontalOffset, null, null, disableAnimation: true);
        _synchronizingScroll = false;
        UpdateVisibleColumnWindow(force: false);
    }

    private void HeaderScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_synchronizingScroll || _rowsScroller is null)
        {
            return;
        }

        _synchronizingScroll = true;
        _rowsScroller.ChangeView(HeaderScroller.HorizontalOffset, null, null, disableAnimation: true);
        _synchronizingScroll = false;
        UpdateVisibleColumnWindow(force: false, horizontalOffset: HeaderScroller.HorizontalOffset);
    }

    private void EnsureColumnVisible(int displayColumn)
    {
        if (_rowsScroller is null || displayColumn < 0 || displayColumn >= _columns.Count)
        {
            return;
        }

        double viewportWidth = _rowsScroller.ViewportWidth > 0
            ? _rowsScroller.ViewportWidth
            : Math.Max(RowsList.ActualWidth, DefaultViewportWidth);
        double columnLeft = GetColumnLeft(displayColumn);
        double columnRight = columnLeft + _columns[displayColumn].Width;
        double targetOffset = _rowsScroller.HorizontalOffset;
        if (columnLeft < targetOffset)
        {
            targetOffset = columnLeft;
        }
        else if (columnRight > targetOffset + viewportWidth)
        {
            targetOffset = Math.Max(0, columnRight - viewportWidth);
        }

        if (Math.Abs(targetOffset - _rowsScroller.HorizontalOffset) > 0.5)
        {
            _rowsScroller.ChangeView(targetOffset, null, null, disableAnimation: true);
            HeaderScroller.ChangeView(targetOffset, null, null, disableAnimation: true);
        }

        UpdateVisibleColumnWindow(force: true, targetOffset);
    }

    private void UpdateVisibleColumnWindow(bool force, double? horizontalOffset = null)
    {
        if (_columns.Count == 0)
        {
            _firstRealizedColumn = 0;
            _lastRealizedColumn = -1;
            return;
        }

        double viewportWidth = _rowsScroller?.ViewportWidth > 0
            ? _rowsScroller.ViewportWidth
            : RowsList.ActualWidth > 0
                ? RowsList.ActualWidth
                : DefaultViewportWidth;
        double visibleLeft = Math.Max(0, horizontalOffset ?? _rowsScroller?.HorizontalOffset ?? HeaderScroller.HorizontalOffset);
        double visibleRight = visibleLeft + viewportWidth;
        double edge = 0;
        int first = 0;
        int last = _columns.Count - 1;
        bool foundFirst = false;

        for (int index = 0; index < _columns.Count; index++)
        {
            double nextEdge = edge + _columns[index].Width;
            if (!foundFirst && nextEdge >= visibleLeft)
            {
                first = index;
                foundFirst = true;
            }

            if (edge > visibleRight)
            {
                last = Math.Max(first, index - 1);
                break;
            }

            edge = nextEdge;
        }

        if (!foundFirst)
        {
            first = _columns.Count - 1;
        }

        first = Math.Max(0, first - ColumnOverscan);
        last = Math.Min(_columns.Count - 1, last + ColumnOverscan);
        if (!force && first == _firstRealizedColumn && last == _lastRealizedColumn)
        {
            return;
        }

        _firstRealizedColumn = first;
        _lastRealizedColumn = last;
        LayoutChanged?.Invoke(this, new AppDataTableLayoutChangedEventArgs(rebuildCells: true));
    }

    private static int IndexOfReference(IReadOnlyList<AppDataTableRowModel> rows, AppDataTableRowModel target)
    {
        for (int index = 0; index < rows.Count; index++)
        {
            if (ReferenceEquals(rows[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}

internal sealed class AppDataTableColumn(int sourceIndex, string header, double width)
{
    public int SourceIndex { get; } = sourceIndex;

    public string Header { get; } = header;

    public double Width { get; set; } = width;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppDataTableRowModel
{
    internal AppDataTableRowModel(AppDataTable owner, CsvRow source)
    {
        Owner = owner;
        Source = source;
    }

    internal AppDataTable Owner { get; }

    internal CsvRow Source { get; }

    internal int SourceIndex => Source.SourceIndex;

    internal string GetValue(int sourceColumnIndex) =>
        sourceColumnIndex >= 0 && sourceColumnIndex < Source.Values.Count
            ? Source.Values[sourceColumnIndex]
            : string.Empty;
}

internal sealed class AppDataTableLayoutChangedEventArgs(bool rebuildCells) : EventArgs
{
    public bool RebuildCells { get; } = rebuildCells;
}
