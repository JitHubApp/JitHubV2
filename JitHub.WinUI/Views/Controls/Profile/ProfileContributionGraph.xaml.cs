using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using JitHub.Services.Accessibility;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Controls.Profile;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfileContributionGraph : UserControl
{
    public static readonly DependencyProperty WeeksProperty = DependencyProperty.Register(
        nameof(Weeks),
        typeof(object),
        typeof(ProfileContributionGraph),
        new PropertyMetadata(null, OnWeeksChanged));

    private static string KeyboardHelpText => LocalizedResourceText.GetString(
        "Profile.ContributionGraph.KeyboardHelp",
        "Use the arrow keys to inspect contribution days. Home and End move to the first and last day.");
    private readonly Grid _calendarGrid = new();
    private readonly Dictionary<ProfileContributionCursor, ContributionCell> _cells = [];
    private static readonly Lazy<AccessibilitySettings?> AccessibilitySettingsInstance = new(
        TryCreateAccessibilitySettings,
        isThreadSafe: true);
    private bool _isHighContrastSubscribed;
    private ProfileContributionWeekViewItem[] _renderedWeeks = [];
    private ProfileContributionCursor _selectedCursor = new(-1, -1);
    private ToolTip? _openToolTip;
    private INotifyCollectionChanged? _weeksCollection;
    private bool _preserveUserSelection;

    public ProfileContributionGraph()
    {
        MinHeight = 84;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Content = _calendarGrid;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Control);
        AutomationProperties.SetHelpText(this, KeyboardHelpText);
        SetAccessibleName(LocalizedResourceText.GetString(
            "Profile.ContributionGraph.AccessibleName",
            "Contribution calendar"));
        SizeChanged += (_, _) => Render();
        KeyDown += OnKeyDown;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        Loaded += ProfileContributionGraph_Loaded;
        Unloaded += ProfileContributionGraph_Unloaded;
    }

    public object? Weeks
    {
        get => GetValue(WeeksProperty);
        set => SetValue(WeeksProperty, value);
    }

    private static void OnWeeksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProfileContributionGraph graph)
        {
            graph.DetachWeeksCollection();
            if (graph.IsLoaded)
            {
                graph.AttachWeeksCollection();
            }
            graph.Render();
        }
    }

    private void Render()
    {
        DateTimeOffset? selectedDate = _preserveUserSelection && TryGetSelectedCell(out ContributionCell selectedCell)
            ? selectedCell.Day.Date
            : null;

        CloseKeyboardToolTip();
        _calendarGrid.Children.Clear();
        _calendarGrid.RowDefinitions.Clear();
        _calendarGrid.ColumnDefinitions.Clear();
        _cells.Clear();

        _renderedWeeks = (Weeks as IEnumerable<ProfileContributionWeekViewItem>)
            ?.Where(static week => week.Days.Length > 0)
            .ToArray() ?? [];
        if (_renderedWeeks.Length == 0)
        {
            _selectedCursor = new ProfileContributionCursor(-1, -1);
            _preserveUserSelection = false;
            MinHeight = 84;
            Height = double.NaN;
            SetAccessibleName(LocalizedResourceText.GetString(
                "Profile.ContributionGraph.EmptyAccessibleName",
                "Contribution calendar. No contribution data available."));
            return;
        }

        double availableWidth = ActualWidth > 0 ? ActualWidth : 640;
        ProfileContributionLayoutMetrics layout = ProfileContributionGraphNavigation.CalculateLayout(
            availableWidth,
            _renderedWeeks.Length);
        double cellSize = layout.CellSize;
        double weekGap = layout.Gap;
        double dayGap = layout.Gap;
        double graphHeight = (cellSize * 7) + (dayGap * 6);
        MinHeight = graphHeight;
        Height = graphHeight;

        for (int row = 0; row < 7; row++)
        {
            _calendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellSize) });
        }

        for (int column = 0; column < _renderedWeeks.Length; column++)
        {
            _calendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellSize) });
        }

        _calendarGrid.ColumnSpacing = weekGap;
        _calendarGrid.RowSpacing = dayGap;
        for (int column = 0; column < _renderedWeeks.Length; column++)
        {
            ProfileContributionDayViewItem[] days = _renderedWeeks[column].Days.Take(7).ToArray();
            for (int row = 0; row < days.Length; row++)
            {
                ProfileContributionDayViewItem day = days[row];
                ProfileContributionCursor cursor = new(column, row);
                Border cell = new()
                {
                    Width = cellSize,
                    Height = cellSize,
                    CornerRadius = new CornerRadius(Math.Max(1.5, cellSize / 4)),
                    Background = CreateContributionBrush(day),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                ToolTip toolTip = new() { Content = day.ToolTipText };
                ToolTipService.SetToolTip(cell, toolTip);
                AutomationProperties.SetName(cell, day.ToolTipText);
                AutomationProperties.SetAccessibilityView(cell, AccessibilityView.Content);
                Grid.SetColumn(cell, column);
                Grid.SetRow(cell, row);
                _calendarGrid.Children.Add(cell);
                _cells[cursor] = new ContributionCell(day, cell, toolTip);
            }
        }

        ProfileContributionCursor restoredCursor = new(-1, -1);
        if (selectedDate is not null)
        {
            foreach ((ProfileContributionCursor cursor, ContributionCell cell) in _cells)
            {
                if (cell.Day.Date == selectedDate)
                {
                    restoredCursor = cursor;
                    break;
                }
            }
        }

        _selectedCursor = _cells.ContainsKey(restoredCursor)
            ? restoredCursor
            : ProfileContributionGraphNavigation.FindLast(GetWeekDayCounts());

        UpdateAccessibleSelection();
        UpdateSelectionVisual();
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        CloseAllToolTips();
        if (!_cells.ContainsKey(_selectedCursor))
        {
            _selectedCursor = ProfileContributionGraphNavigation.FindLast(GetWeekDayCounts());
        }

        UpdateAccessibleSelection();
        UpdateSelectionVisual();
        OpenKeyboardToolTip();
    }

    private void ProfileContributionGraph_Loaded(object sender, RoutedEventArgs e)
    {
        AttachWeeksCollection();
        AccessibilitySettings? accessibilitySettings = AccessibilitySettingsInstance.Value;
        if (accessibilitySettings is not null && !_isHighContrastSubscribed)
        {
            try
            {
                accessibilitySettings.HighContrastChanged += AccessibilitySettings_HighContrastChanged;
                _isHighContrastSubscribed = true;
            }
            catch (Exception)
            {
                _isHighContrastSubscribed = false;
            }
        }

        Render();
    }

    private void ProfileContributionGraph_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachWeeksCollection();
        AccessibilitySettings? accessibilitySettings = AccessibilitySettingsInstance.Value;
        if (accessibilitySettings is null || !_isHighContrastSubscribed)
        {
            return;
        }

        try
        {
            accessibilitySettings.HighContrastChanged -= AccessibilitySettings_HighContrastChanged;
        }
        catch (Exception)
        {
            // The system projection can already be unavailable during app shutdown.
        }
        finally
        {
            _isHighContrastSubscribed = false;
        }
    }

    private void AccessibilitySettings_HighContrastChanged(AccessibilitySettings sender, object args) =>
        DispatcherQueue.TryEnqueue(Render);

    private void AttachWeeksCollection()
    {
        if (_weeksCollection is not null || Weeks is not INotifyCollectionChanged collection)
        {
            return;
        }

        _weeksCollection = collection;
        collection.CollectionChanged += WeeksCollection_CollectionChanged;
    }

    private void DetachWeeksCollection()
    {
        if (_weeksCollection is null)
        {
            return;
        }

        _weeksCollection.CollectionChanged -= WeeksCollection_CollectionChanged;
        _weeksCollection = null;
    }

    private void WeeksCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            Render();
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(Render);
        }
    }

    private Brush CreateContributionBrush(ProfileContributionDayViewItem day)
    {
        string? highContrastBrushKey = HighContrastVisualPolicy.GetContributionCellBrushKey(
            IsHighContrastActive(),
            day.ContributionCount);
        if (highContrastBrushKey is not null)
        {
            return GetThemeBrush(highContrastBrushKey);
        }

        Windows.UI.Color fallback = GetThemeBrushColor(HighContrastVisualPolicy.CanvasBrushKey);
            return ProfileColorBrush.Create(day.ColorHex, fallback);
    }

    private bool IsHighContrastActive()
    {
        try
        {
            return AccessibilitySettingsInstance.Value?.HighContrast == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AccessibilitySettings? TryCreateAccessibilitySettings()
    {
        try
        {
            return new AccessibilitySettings();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Brush GetThemeBrush(string resourceKey) =>
        Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Brush brush
            ? brush
            : throw new InvalidOperationException($"Required theme brush '{resourceKey}' is unavailable.");

    private static Windows.UI.Color GetThemeBrushColor(string resourceKey) =>
        GetThemeBrush(resourceKey) is SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"Theme brush '{resourceKey}' must be a solid color brush.");

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        CloseAllToolTips();
        UpdateSelectionVisual();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        ProfileContributionNavigationDirection? direction = e.Key switch
        {
            VirtualKey.Left => ProfileContributionNavigationDirection.PreviousWeek,
            VirtualKey.Right => ProfileContributionNavigationDirection.NextWeek,
            VirtualKey.Up => ProfileContributionNavigationDirection.PreviousDay,
            VirtualKey.Down => ProfileContributionNavigationDirection.NextDay,
            VirtualKey.Home => ProfileContributionNavigationDirection.FirstDay,
            VirtualKey.End => ProfileContributionNavigationDirection.LastDay,
            _ => null
        };
        if (direction is null || _cells.Count == 0)
        {
            return;
        }

        ProfileContributionCursor next = ProfileContributionGraphNavigation.Move(
            _selectedCursor,
            GetWeekDayCounts(),
            direction.Value);
        if (_cells.ContainsKey(next))
        {
            _selectedCursor = next;
            _preserveUserSelection = true;
            UpdateAccessibleSelection();
            UpdateSelectionVisual();
            OpenKeyboardToolTip();
        }

        e.Handled = true;
    }

    private int[] GetWeekDayCounts() =>
        _renderedWeeks.Select(static week => Math.Min(7, week.Days.Length)).ToArray();

    private bool TryGetSelectedCell(out ContributionCell cell) =>
        _cells.TryGetValue(_selectedCursor, out cell!);

    private void UpdateAccessibleSelection()
    {
        string name = TryGetSelectedCell(out ContributionCell cell)
            ? LocalizedResourceText.Format(
                "Profile.ContributionGraph.DayAccessibleName",
                "Contribution calendar. {0}",
                cell.Day.ToolTipText)
            : LocalizedResourceText.GetString(
                "Profile.ContributionGraph.AccessibleName",
                "Contribution calendar");
        SetAccessibleName(name);
    }

    private void SetAccessibleName(string name)
    {
        string previousName = AutomationProperties.GetName(this);
        if (string.Equals(previousName, name, StringComparison.Ordinal))
        {
            return;
        }

        AutomationProperties.SetName(this, name);
        FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
            AutomationElementIdentifiers.NameProperty,
            previousName,
            name);
    }

    private void UpdateSelectionVisual()
    {
        foreach ((ProfileContributionCursor cursor, ContributionCell cell) in _cells)
        {
            bool isKeyboardSelection = FocusState != FocusState.Unfocused && cursor == _selectedCursor;
            cell.Element.BorderThickness = isKeyboardSelection ? new Thickness(1) : new Thickness(0);
            cell.Element.BorderBrush = isKeyboardSelection
                ? GetThemeBrush(HighContrastVisualPolicy.GetContributionFocusBrushKey(
                    IsHighContrastActive(),
                    cell.Day.ContributionCount))
                : null;
        }
    }

    private void OpenKeyboardToolTip()
    {
        CloseAllToolTips();
        if (FocusState == FocusState.Unfocused || !TryGetSelectedCell(out ContributionCell cell))
        {
            return;
        }

        _openToolTip = cell.ToolTip;
        _openToolTip.IsOpen = true;
    }

    private void CloseKeyboardToolTip()
    {
        if (_openToolTip is not null)
        {
            _openToolTip.IsOpen = false;
            _openToolTip = null;
        }
    }

    private void CloseAllToolTips()
    {
        foreach (ContributionCell cell in _cells.Values)
        {
            cell.ToolTip.IsOpen = false;
        }

        _openToolTip = null;
    }

    private sealed record ContributionCell(
        ProfileContributionDayViewItem Day,
        Border Element,
        ToolTip ToolTip);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ProfileContributionGraphAutomationPeer(this);

    private sealed partial class ProfileContributionGraphAutomationPeer(ProfileContributionGraph owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ProfileContributionGraph);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Calendar;

        protected override string GetHelpTextCore() => KeyboardHelpText;
    }
}
