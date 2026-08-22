using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JitHub.Services;
using JitHub.WinUI.Performance;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace JitHub.WinUI.Views.Controls.Commit;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CommitDiffViewer : UserControl
{
    public static readonly DependencyProperty RowProjectionProperty = DependencyProperty.Register(
        nameof(RowProjection),
        typeof(CommitDiffRowProjection),
        typeof(CommitDiffViewer),
        new PropertyMetadata(CommitDiffRowProjection.Empty, OnRowProjectionChanged));

    public static readonly DependencyProperty SelectedSearchMatchIndexProperty = DependencyProperty.Register(
        nameof(SelectedSearchMatchIndex),
        typeof(int),
        typeof(CommitDiffViewer),
        new PropertyMetadata(-1, OnSelectedSearchMatchIndexChanged));

    private readonly Dictionary<string, FrameworkElement> _realizedRows = new(StringComparer.Ordinal);
    private readonly CommitDiffSelectionState _selection = new();
    private ProductPerformanceScrollProbe? _performanceScrollProbe;
    private bool _isSelecting;

    public event EventHandler<CommitDiffActionCompletedEventArgs>? ActionCompleted;

    public CommitDiffViewer()
    {
        InitializeComponent();
        IsTabStop = true;
        KeyDown += CommitDiffViewer_KeyDown;
        KeyboardAccelerator copyAccelerator = new()
        {
            Key = VirtualKey.C,
            Modifiers = VirtualKeyModifiers.Control
        };
        copyAccelerator.Invoked += CopyKeyboardAccelerator_Invoked;
        KeyboardAccelerators.Add(copyAccelerator);
        KeyboardAccelerator scrollViewerCopyAccelerator = new()
        {
            Key = VirtualKey.C,
            Modifiers = VirtualKeyModifiers.Control
        };
        scrollViewerCopyAccelerator.Invoked += CopyKeyboardAccelerator_Invoked;
        DiffRowsScrollViewer.IsTabStop = true;
        DiffRowsScrollViewer.KeyDown += CommitDiffViewer_KeyDown;
        DiffRowsScrollViewer.KeyboardAccelerators.Add(scrollViewerCopyAccelerator);
        DiffRowsScrollViewer.AddHandler(PointerPressedEvent, new PointerEventHandler(DiffRowsScrollViewer_PointerPressed), true);
        DiffRowsScrollViewer.AddHandler(PointerMovedEvent, new PointerEventHandler(DiffRowsScrollViewer_PointerMoved), true);
        DiffRowsScrollViewer.AddHandler(PointerReleasedEvent, new PointerEventHandler(DiffRowsScrollViewer_PointerReleased), true);
        DiffRowsScrollViewer.AddHandler(PointerCanceledEvent, new PointerEventHandler(DiffRowsScrollViewer_PointerReleased), true);
        DiffRowsRepeater.ElementPrepared += DiffRowsRepeater_ElementPrepared;
        DiffRowsRepeater.ElementClearing += DiffRowsRepeater_ElementClearing;
        Loaded += CommitDiffViewer_Loaded;
        Unloaded += CommitDiffViewer_Unloaded;
    }

    private void CommitDiffViewer_Loaded(object sender, RoutedEventArgs e)
    {
        _performanceScrollProbe ??= ProductPerformanceScrollProbe.TryStart(this, DiffRowsScrollViewer);
    }

    private void CommitDiffViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
    }

    public CommitDiffRowProjection RowProjection
    {
        get => (CommitDiffRowProjection)GetValue(RowProjectionProperty);
        set => SetValue(RowProjectionProperty, value);
    }

    public int SelectedSearchMatchIndex
    {
        get => (int)GetValue(SelectedSearchMatchIndexProperty);
        set => SetValue(SelectedSearchMatchIndexProperty, value);
    }

    private static void OnRowProjectionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CommitDiffViewer viewer)
        {
            viewer._selection.Clear();
            viewer.Bindings.Update();
            viewer.ApplyHighlightsToRealizedRows();
            viewer.ScrollToSelectedMatch();
        }
    }

    private static void OnSelectedSearchMatchIndexChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is CommitDiffViewer viewer)
        {
            viewer.ScrollToSelectedMatch();
        }
    }

    private void DiffRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root ||
            !TryResolveRealizedRow(root, out CommitDiffRow row))
        {
            return;
        }

        root.Tag = row;
        _realizedRows[row.Key] = root;
        ApplyHighlights(root, row);
    }

    private void DiffRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement root &&
            TryGetRow(root, out CommitDiffRow row) &&
            _realizedRows.TryGetValue(row.Key, out FrameworkElement? existing) &&
            ReferenceEquals(existing, root))
        {
            _realizedRows.Remove(row.Key);
        }

        if (sender is FrameworkElement unloadedRoot)
        {
            unloadedRoot.Tag = null;
        }
    }

    private void DiffRowsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement root)
        {
            return;
        }

        IReadOnlyList<CommitDiffRow> rows = (RowProjection ?? CommitDiffRowProjection.Empty).Rows;
        if (args.Index < 0 || args.Index >= rows.Count)
        {
            root.Tag = null;
            return;
        }

        CommitDiffRow row = rows[args.Index];
        root.Tag = row;
        _realizedRows[row.Key] = root;
        if (root.IsLoaded)
        {
            ApplyHighlights(root, row);
        }
    }

    private void DiffRowsRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement root)
        {
            return;
        }

        if (TryGetRow(root, out CommitDiffRow row) &&
            _realizedRows.TryGetValue(row.Key, out FrameworkElement? existing) &&
            ReferenceEquals(existing, root))
        {
            _realizedRows.Remove(row.Key);
        }

        root.Tag = null;
    }

    private void DiffRowsScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isSelecting ||
            !IsSelectionStartPointer(e) ||
            IsSelectionIgnoredPointerSource(e.OriginalSource as DependencyObject) ||
            IsPointerInVerticalScrollBarHitZone(e) ||
            !TryCreateSelectionHitFromPointer(e, out CommitDiffSelectionHit hit))
        {
            return;
        }

        Focus(FocusState.Pointer);
        DiffRowsScrollViewer.Focus(FocusState.Pointer);
        _isSelecting = true;
        _selection.Begin(hit.RowIndex, hit.CharIndex);
        ApplyHighlightsToRealizedRows();
        DiffRowsScrollViewer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DiffRowsScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting ||
            !e.GetCurrentPoint(DiffRowsScrollViewer).Properties.IsLeftButtonPressed ||
            IsSelectionIgnoredPointerSource(e.OriginalSource as DependencyObject) ||
            !TryCreateSelectionHitFromPointer(e, out CommitDiffSelectionHit hit))
        {
            return;
        }

        _selection.Update(hit.RowIndex, hit.CharIndex);
        ApplyHighlightsToRealizedRows();
        e.Handled = true;
    }

    private void DiffRowsScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        if (TryCreateSelectionHitFromPointer(e, out CommitDiffSelectionHit hit))
        {
            _selection.Update(hit.RowIndex, hit.CharIndex);
            ApplyHighlightsToRealizedRows();
        }

        _isSelecting = false;
        DiffRowsScrollViewer.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private bool IsPointerInVerticalScrollBarHitZone(PointerRoutedEventArgs e)
    {
        Point point = e.GetCurrentPoint(DiffRowsScrollViewer).Position;
        double width = DiffRowsScrollViewer.ActualWidth;
        return DiffRowsScrollViewer.ScrollableHeight > 0 &&
            width > 0 &&
            point.X >= width - 24;
    }

    private static bool IsSelectionStartPointer(PointerRoutedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint(null).Properties;
        return properties.IsLeftButtonPressed &&
            !properties.IsRightButtonPressed &&
            !properties.IsMiddleButtonPressed;
    }

    private static bool IsSelectionIgnoredPointerSource(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is ScrollBar ||
                current is Thumb ||
                current is RepeatButton ||
                current is ButtonBase)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void CommitDiffViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.C &&
            IsKeyDown(VirtualKey.Control) &&
            TryCopySelection())
        {
            e.Handled = true;
        }
    }

    private void CopyKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TryCopySelection())
        {
            args.Handled = true;
        }
    }

    private void ScrollToSelectedMatch()
    {
        CommitDiffRowProjection projection = RowProjection ?? CommitDiffRowProjection.Empty;
        int selectedIndex = SelectedSearchMatchIndex;
        if (selectedIndex < 0 || selectedIndex >= projection.Matches.Count)
        {
            return;
        }

        int rowIndex = projection.Matches[selectedIndex].RowIndex;
        if (rowIndex < 0 || rowIndex >= projection.Rows.Count)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            UIElement? element = DiffRowsRepeater.GetOrCreateElement(rowIndex);
            element?.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0.2,
                VerticalOffset = -12
            });
        });
    }

    private bool TryCreateSelectionHitFromPointer(PointerRoutedEventArgs e, out CommitDiffSelectionHit hit)
    {
        if (!_isSelecting)
        {
            FrameworkElement? originalSource = FindFrameworkElement(e.OriginalSource as DependencyObject);
            CommitDiffRow? originalRow = FindRowFromElement(originalSource);
            if (originalRow is not null &&
                _realizedRows.TryGetValue(originalRow.Key, out FrameworkElement? originalRoot) &&
                TryCreateSelectionHit(originalRow, originalRoot, e, out hit))
            {
                return true;
            }
        }

        Point scrollPoint = e.GetCurrentPoint(DiffRowsScrollViewer).Position;
        foreach ((string rowKey, FrameworkElement root) in _realizedRows.ToArray())
        {
            if (!(RowProjection ?? CommitDiffRowProjection.Empty).TryGetRow(rowKey, out CommitDiffRow row))
            {
                continue;
            }

            Point rowOrigin = root.TransformToVisual(DiffRowsScrollViewer).TransformPoint(new Point(0, 0));
            double rowBottom = rowOrigin.Y + root.ActualHeight;
            if (scrollPoint.Y >= rowOrigin.Y && scrollPoint.Y <= rowBottom)
            {
                if (TryCreateSelectionHit(row, root, e, out hit))
                {
                    return true;
                }

                break;
            }
        }

        Point repeaterPoint = e.GetCurrentPoint(DiffRowsRepeater).Position;
        Point hostPoint = DiffRowsRepeater.TransformToVisual(null).TransformPoint(repeaterPoint);
        foreach (UIElement element in VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, DiffRowsRepeater).OfType<UIElement>())
        {
            FrameworkElement? source = FindFrameworkElement(element);
            CommitDiffRow? row = FindRowFromElement(source);
            if (row is null ||
                !_realizedRows.TryGetValue(row.Key, out FrameworkElement? root))
            {
                continue;
            }

            if (TryCreateSelectionHit(row, root, e, out hit))
            {
                return true;
            }
        }

        hit = default;
        return false;
    }

    private bool TryCreateSelectionHit(
        CommitDiffRow row,
        FrameworkElement root,
        PointerRoutedEventArgs e,
        out CommitDiffSelectionHit hit)
    {
        hit = default;
        int rowIndex = FindRowIndex(row);
        if (rowIndex < 0)
        {
            return false;
        }

        TextBlock? textBlock = FindSelectableTextBlock(root, row);
        if (textBlock is null)
        {
            return false;
        }

        string text = GetSelectableText(row);
        int charIndex = EstimateCharacterIndex(textBlock, text, e.GetCurrentPoint(textBlock).Position);
        hit = new CommitDiffSelectionHit(rowIndex, charIndex);
        return true;
    }

    private void ApplyHighlightsToRealizedRows()
    {
        foreach ((string rowKey, FrameworkElement root) in _realizedRows.ToArray())
        {
            if (!(RowProjection ?? CommitDiffRowProjection.Empty).TryGetRow(rowKey, out CommitDiffRow row))
            {
                _realizedRows.Remove(rowKey);
                continue;
            }

            ApplyHighlights(root, row);
        }
    }

    private void ApplyHighlights(FrameworkElement root, CommitDiffRow row)
    {
        TextBlock? textBlock = FindSelectableTextBlock(root, row);
        if (textBlock is null)
        {
            return;
        }

        textBlock.TextHighlighters.Clear();
        if (row.SearchMatches.Count > 0)
        {
            AddHighlighter(
                textBlock,
                row.SearchMatches,
                GetThemeBrush("AppWarmAccentBrush"),
                GetThemeBrush("AppWarmAccentForegroundBrush"));
        }

        int rowIndex = FindRowIndex(row);
        string text = GetSelectableText(row);
        if (rowIndex >= 0 &&
            _selection.TryGetSelectionRangeForRow(rowIndex, text.Length, out int start, out int length))
        {
            AddHighlighter(
                textBlock,
                [new CommitDiffSearchMatch(0, row.Key, rowIndex, start, length)],
                GetThemeBrush("AppAccentBrush"),
                GetThemeBrush("AppAccentForegroundBrush"));
        }
    }

    private static void AddHighlighter(
        TextBlock textBlock,
        IReadOnlyList<CommitDiffSearchMatch> matches,
        Brush background,
        Brush foreground)
    {
        if (matches.Count == 0)
        {
            return;
        }

        TextHighlighter highlighter = new()
        {
            Background = background,
            Foreground = foreground
        };

        int textLength = textBlock.Text?.Length ?? 0;
        foreach (CommitDiffSearchMatch match in matches)
        {
            int start = Math.Clamp(match.StartIndex, 0, textLength);
            int length = Math.Clamp(match.Length, 0, textLength - start);
            if (length > 0)
            {
                highlighter.Ranges.Add(new TextRange(start, length));
            }
        }

        if (highlighter.Ranges.Count > 0)
        {
            textBlock.TextHighlighters.Add(highlighter);
        }
    }

    private bool TryCopySelection()
    {
        if (!_selection.HasSelection)
        {
            return false;
        }

        CommitDiffRowProjection projection = RowProjection ?? CommitDiffRowProjection.Empty;
        if (!_selection.TryGetNormalizedRange(out int startRow, out int startChar, out int endRow, out int endChar) ||
            projection.Rows.Count == 0)
        {
            return false;
        }

        startRow = Math.Clamp(startRow, 0, projection.Rows.Count - 1);
        endRow = Math.Clamp(endRow, 0, projection.Rows.Count - 1);
        StringBuilder builder = new();
        for (int rowIndex = startRow; rowIndex <= endRow; rowIndex++)
        {
            CommitDiffRow row = projection.Rows[rowIndex];
            string text = GetSelectableText(row);
            int start = rowIndex == startRow ? Math.Clamp(startChar, 0, text.Length) : 0;
            int end = rowIndex == endRow ? Math.Clamp(endChar, 0, text.Length) : text.Length;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            if (end > start)
            {
                builder.Append(text.AsSpan(start, end - start));
            }

            if (rowIndex < endRow)
            {
                builder.AppendLine();
            }
        }

        if (builder.Length == 0)
        {
            return false;
        }

        CompleteAction(
            TelemetryTaxonomy.Actions.CopyDiff,
            CopyText(builder.ToString()));
        return true;
    }

    private void CopyFilePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement source &&
            FindRowFromElement(source) is CommitDiffRow row)
        {
            CompleteAction(
                TelemetryTaxonomy.Actions.CopyPath,
                CopyText(row.FileName));
        }
    }

    private void CopyDiffRowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryCopySelection())
        {
            return;
        }

        if (sender is FrameworkElement source &&
            FindRowFromElement(source) is CommitDiffRow row)
        {
            CompleteAction(
                TelemetryTaxonomy.Actions.CopyDiff,
                CopyText(GetSelectableText(row)));
        }
    }

    private static string GetSelectableText(CommitDiffRow row) =>
        row.Kind == CommitDiffRowKind.FileHeader ? row.HeaderText : row.Text;

    private int FindRowIndex(CommitDiffRow row)
        => (RowProjection ?? CommitDiffRowProjection.Empty).TryGetRowIndex(row.Key, out int index) ? index : -1;

    private bool TryResolveRealizedRow(FrameworkElement root, out CommitDiffRow row)
    {
        if (TryGetRow(root, out row))
        {
            return true;
        }

        int rowIndex = DiffRowsRepeater.GetElementIndex(root);
        IReadOnlyList<CommitDiffRow> rows = (RowProjection ?? CommitDiffRowProjection.Empty).Rows;
        if (rowIndex >= 0 && rowIndex < rows.Count)
        {
            row = rows[rowIndex];
            return true;
        }

        row = default!;
        return false;
    }

    private static int EstimateCharacterIndex(TextBlock textBlock, string text, Point point)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        Thickness padding = textBlock.Padding;
        double contentWidth = Math.Max(1, textBlock.ActualWidth - padding.Left - padding.Right);
        double charWidth = Math.Max(1, textBlock.FontSize * 0.62);
        double lineHeight = Math.Max(1, textBlock.FontSize * 1.45);
        int charsPerLine = Math.Max(1, (int)Math.Floor(contentWidth / charWidth));
        int visualLine = Math.Max(0, (int)Math.Floor((point.Y - padding.Top) / lineHeight));
        int visualColumn = Math.Max(0, (int)Math.Round((point.X - padding.Left) / charWidth));
        int index = (visualLine * charsPerLine) + visualColumn;
        return Math.Clamp(index, 0, text.Length);
    }

    private static TextBlock? FindSelectableTextBlock(FrameworkElement root, CommitDiffRow row)
    {
        string name = row.Kind switch
        {
            CommitDiffRowKind.FileHeader => "FileHeaderTextBlock",
            CommitDiffRowKind.HunkHeader => "HunkTextBlock",
            CommitDiffRowKind.DiffLine => "DiffLineTextBlock",
            CommitDiffRowKind.UnavailableDiff => "UnavailableTextBlock",
            CommitDiffRowKind.SearchNoResults => "SearchNoResultsTextBlock",
            _ => string.Empty
        };

        return string.IsNullOrEmpty(name)
            ? null
            : root.FindName(name) as TextBlock ?? FindNamedDescendant<TextBlock>(root, name);
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T element &&
                string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            T? nested = FindNamedDescendant<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static FrameworkElement? FindFrameworkElement(DependencyObject? element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement frameworkElement)
            {
                return frameworkElement;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static CommitDiffRow? FindRowFromElement(FrameworkElement? element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement frameworkElement &&
                TryGetRow(frameworkElement, out CommitDiffRow row))
            {
                return row;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool TryGetRow(FrameworkElement element, out CommitDiffRow row)
    {
        if (element.Tag is CommitDiffRow tagRow)
        {
            row = tagRow;
            return true;
        }

        if (element.DataContext is CommitDiffRow dataContextRow)
        {
            row = dataContextRow;
            return true;
        }

        row = default!;
        return false;
    }

    private static Brush GetThemeBrush(string resourceKey) =>
        Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Brush brush
            ? brush
            : throw new InvalidOperationException($"Required theme brush '{resourceKey}' is unavailable.");

    private static bool IsKeyDown(VirtualKey key)
    {
        CoreVirtualKeyStates state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static bool CopyText(string text) => PlatformHelper.CopyString(text);

    private void CompleteAction(string action, bool succeeded) =>
        ActionCompleted?.Invoke(
            this,
            new CommitDiffActionCompletedEventArgs(
                action,
                succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error));
}

public sealed class CommitDiffActionCompletedEventArgs(string action, string result) : EventArgs
{
    public string Action { get; } = action;

    public string Result { get; } = result;
}

internal readonly record struct CommitDiffSelectionHit(int RowIndex, int CharIndex);

internal sealed class CommitDiffSelectionState
{
    private int _anchorRowIndex = -1;
    private int _anchorCharIndex = -1;
    private int _activeRowIndex = -1;
    private int _activeCharIndex = -1;

    public bool HasSelection =>
        _anchorRowIndex >= 0 &&
        _activeRowIndex >= 0 &&
        (_anchorRowIndex != _activeRowIndex || _anchorCharIndex != _activeCharIndex);

    public void Begin(int rowIndex, int charIndex)
    {
        _anchorRowIndex = rowIndex;
        _anchorCharIndex = charIndex;
        _activeRowIndex = rowIndex;
        _activeCharIndex = charIndex;
    }

    public void Update(int rowIndex, int charIndex)
    {
        _activeRowIndex = rowIndex;
        _activeCharIndex = charIndex;
    }

    public void Clear()
    {
        _anchorRowIndex = -1;
        _anchorCharIndex = -1;
        _activeRowIndex = -1;
        _activeCharIndex = -1;
    }

    public bool TryGetSelectionRangeForRow(int rowIndex, int rowTextLength, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (!HasSelection ||
            !TryGetNormalizedRange(out int startRow, out int startChar, out int endRow, out int endChar) ||
            rowIndex < startRow ||
            rowIndex > endRow)
        {
            return false;
        }

        int rowStart = rowIndex == startRow ? Math.Clamp(startChar, 0, rowTextLength) : 0;
        int rowEnd = rowIndex == endRow ? Math.Clamp(endChar, 0, rowTextLength) : rowTextLength;
        if (rowEnd < rowStart)
        {
            (rowStart, rowEnd) = (rowEnd, rowStart);
        }

        start = rowStart;
        length = rowEnd - rowStart;
        return length > 0;
    }

    public bool TryGetNormalizedRange(out int startRow, out int startChar, out int endRow, out int endChar)
    {
        startRow = startChar = endRow = endChar = -1;
        if (!HasSelection)
        {
            return false;
        }

        bool anchorFirst =
            _anchorRowIndex < _activeRowIndex ||
            (_anchorRowIndex == _activeRowIndex && _anchorCharIndex <= _activeCharIndex);

        if (anchorFirst)
        {
            startRow = _anchorRowIndex;
            startChar = _anchorCharIndex;
            endRow = _activeRowIndex;
            endChar = _activeCharIndex;
        }
        else
        {
            startRow = _activeRowIndex;
            startChar = _activeCharIndex;
            endRow = _anchorRowIndex;
            endChar = _anchorCharIndex;
        }

        return true;
    }
}
