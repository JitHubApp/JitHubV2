using System;
using System.Collections.Generic;
using System.Linq;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class AppDataTableRowPresenter : Grid
{
    private readonly Dictionary<int, AppDataTableCell> _cells = [];
    private AppDataTableRowModel? _model;
    private bool _isPointerOver;

    public AppDataTableRowPresenter()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Stretch;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
    }

    public AppDataTableRowModel? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(_model, value))
            {
                return;
            }

            DetachOwner();
            _model = value;
            AttachOwner();
            if (IsLoaded)
            {
                RebuildCells();
            }
        }
    }

    public Style? CellStyle { get; set; }

    public Style? HoverCellStyle { get; set; }

    public Style? SelectedCellStyle { get; set; }

    public Style? CellTextStyle { get; set; }

    public Style? HoverCellTextStyle { get; set; }

    public Style? SelectedCellTextStyle { get; set; }

    internal FrameworkElement? GetCell(int displayColumn) =>
        _cells.TryGetValue(displayColumn, out AppDataTableCell? cell) ? cell : null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOwner();
        RebuildCells();
        ApplyCellStates();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachOwner();

    private void AttachOwner()
    {
        if (_model is null || !IsLoaded)
        {
            return;
        }

        _model.Owner.LayoutChanged -= Owner_LayoutChanged;
        _model.Owner.LayoutChanged += Owner_LayoutChanged;
        _model.Owner.ActiveCellChanged -= Owner_ActiveCellChanged;
        _model.Owner.ActiveCellChanged += Owner_ActiveCellChanged;
    }

    private void DetachOwner()
    {
        if (_model is null)
        {
            return;
        }

        _model.Owner.LayoutChanged -= Owner_LayoutChanged;
        _model.Owner.ActiveCellChanged -= Owner_ActiveCellChanged;
    }

    private void Owner_LayoutChanged(object? sender, AppDataTableLayoutChangedEventArgs e)
    {
        if (e.RebuildCells)
        {
            RebuildCells();
        }
        else
        {
            ApplyColumnWidths();
        }
    }

    private void Owner_ActiveCellChanged(object? sender, EventArgs e) => ApplyCellStates();

    private void RebuildCells()
    {
        Children.Clear();
        _cells.Clear();
        if (_model is null)
        {
            Width = 0;
            return;
        }

        IReadOnlyList<AppDataTableColumn> columns = _model.Owner.Columns;
        int first = Math.Max(0, _model.Owner.FirstRealizedColumn);
        int last = Math.Min(columns.Count - 1, _model.Owner.LastRealizedColumn);
        for (int displayIndex = first; displayIndex <= last; displayIndex++)
        {
            AppDataTableColumn column = columns[displayIndex];
            string value = _model.GetValue(column.SourceIndex);
            TextBlock text = new()
            {
                Text = value,
                Style = RequireStyle(CellTextStyle, nameof(CellTextStyle)),
            };
            AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);

            Border visual = new()
            {
                Style = RequireStyle(CellStyle, nameof(CellStyle)),
                Child = text,
                Tag = column,
            };
            AppDataTableCell cell = new(_model.Owner, _model, column.SourceIndex, visual);
            cell.Width = column.Width;
            cell.HorizontalAlignment = HorizontalAlignment.Left;
            cell.Margin = new Thickness(_model.Owner.GetColumnLeft(displayIndex), 0, 0, 0);
            Children.Add(cell);
            _cells.Add(displayIndex, cell);
        }

        ApplyColumnWidths();
        ApplyCellStates();
        AutomationProperties.SetName(this, string.Join(
            ", ",
            columns.Select(column => $"{column.Header}: {_model.GetValue(column.SourceIndex)}")));
    }

    private void ApplyColumnWidths()
    {
        if (_model is null)
        {
            return;
        }

        IReadOnlyList<AppDataTableColumn> columns = _model.Owner.Columns;
        foreach ((int displayIndex, AppDataTableCell cell) in _cells)
        {
            if (displayIndex >= columns.Count)
            {
                RebuildCells();
                return;
            }

            cell.Width = columns[displayIndex].Width;
            cell.Margin = new Thickness(_model.Owner.GetColumnLeft(displayIndex), 0, 0, 0);
        }

        Width = _model.Owner.TableWidth;
    }

    private void ApplyCellStates()
    {
        if (_model is null)
        {
            return;
        }

        foreach ((int displayIndex, AppDataTableCell cell) in _cells)
        {
            AppDataTableColumn column = _model.Owner.Columns[displayIndex];
            Style? style = _model.Owner.IsActiveCell(_model, column.SourceIndex)
                ? SelectedCellStyle
                : _isPointerOver
                    ? HoverCellStyle
                    : CellStyle;
            Style? textStyle = _model.Owner.IsActiveCell(_model, column.SourceIndex)
                ? SelectedCellTextStyle
                : _isPointerOver
                    ? HoverCellTextStyle
                    : CellTextStyle;
            cell.Visual.Style = RequireStyle(style, "cell state style");
            if (cell.Visual.Child is TextBlock text)
            {
                text.Style = RequireStyle(textStyle, "cell text state style");
            }
        }
    }

    private static Style RequireStyle(Style? style, string name) =>
        style ?? throw new InvalidOperationException($"AppDataTable row presenter is missing {name}.");

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        ApplyCellStates();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        ApplyCellStates();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        double x = e.GetCurrentPoint(this).Position.X;
        double edge = 0;
        foreach (AppDataTableColumn column in _model.Owner.Columns)
        {
            edge += column.Width;
            if (x <= edge)
            {
                _model.Owner.SelectCell(_model, column.SourceIndex);
                e.Handled = true;
                return;
            }
        }
    }

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);
}
