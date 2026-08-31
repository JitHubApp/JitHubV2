using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using JitHub.Services;
using JitHub.Services.Accessibility;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

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
    private readonly CanvasControl _calendarCanvas = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly Dictionary<ProfileContributionCursor, ContributionCell> _cells = [];
    private readonly Dictionary<string, Color> _contributionColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _cellToolTip = new()
    {
        IsHitTestVisible = false,
        Placement = PlacementMode.Top
    };
    private readonly bool _toolTipsEnabled;
    private AppThemeSettingsMonitor? _themeSettings;
    private bool _isHighContrastSubscribed;
    private bool _isPaletteSubscribed;
    private ProfileContributionWeekViewItem[] _renderedWeeks = [];
    private ProfileContributionCursor _selectedCursor = new(-1, -1);
    private INotifyCollectionChanged? _weeksCollection;
    private bool _preserveUserSelection;
    private double _cellSize;
    private double _cellGap;
    private int _renderQueued;

    public ProfileContributionGraph()
    {
        _toolTipsEnabled = !Program.CurrentLaunchOptions.WebsiteShowcase;
        MinHeight = 84;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _calendarCanvas.Draw += CalendarCanvas_Draw;
        if (_toolTipsEnabled)
        {
            _calendarCanvas.PointerMoved += CalendarCanvas_PointerMoved;
            _calendarCanvas.PointerExited += CalendarCanvas_PointerExited;
            ToolTipService.SetToolTip(_calendarCanvas, _cellToolTip);
        }
        _calendarCanvas.PointerPressed += CalendarCanvas_PointerPressed;
        AutomationProperties.SetAccessibilityView(_calendarCanvas, AccessibilityView.Raw);
        Content = _calendarCanvas;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Control);
        AutomationProperties.SetHelpText(this, KeyboardHelpText);
        SetAccessibleName(LocalizedResourceText.GetString(
            "Profile.ContributionGraph.AccessibleName",
            "Contribution calendar"));
        SizeChanged += (_, _) => RequestRender();
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
            graph.RequestRender();
        }
    }

    private void RequestRender()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (IsLoaded)
                {
                    Render();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _renderQueued, 0);
            }
        }))
        {
            Interlocked.Exchange(ref _renderQueued, 0);
        }
    }

    private void Render()
    {
        DateTimeOffset? selectedDate = _preserveUserSelection && TryGetSelectedCell(out ContributionCell selectedCell)
            ? selectedCell.Day.Date
            : null;

        CloseKeyboardToolTip();
        _cells.Clear();
        _contributionColors.Clear();

        _renderedWeeks = (Weeks as IEnumerable<ProfileContributionWeekViewItem>)
            ?.Where(static week => week.Days.Length > 0)
            .ToArray() ?? [];
        if (_renderedWeeks.Length == 0)
        {
            _selectedCursor = new ProfileContributionCursor(-1, -1);
            _preserveUserSelection = false;
            MinHeight = 84;
            Height = double.NaN;
            _cellSize = 0;
            _cellGap = 0;
            _calendarCanvas.Invalidate();
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
        _cellSize = cellSize;
        _cellGap = layout.Gap;
        MinHeight = graphHeight;
        Height = graphHeight;

        for (int column = 0; column < _renderedWeeks.Length; column++)
        {
            ProfileContributionDayViewItem[] days = _renderedWeeks[column].Days;
            int dayCount = Math.Min(7, days.Length);
            for (int row = 0; row < dayCount; row++)
            {
                ProfileContributionDayViewItem day = days[row];
                ProfileContributionCursor cursor = new(column, row);
                Rect bounds = new(
                    column * (cellSize + weekGap),
                    row * (cellSize + dayGap),
                    cellSize,
                    cellSize);
                _cells[cursor] = new ContributionCell(day, bounds, CreateContributionColor(day));
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

    private void CalendarCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float radius = (float)Math.Max(1.5, _cellSize / 4);
        foreach ((ProfileContributionCursor cursor, ContributionCell cell) in _cells)
        {
            args.DrawingSession.FillRoundedRectangle(cell.Bounds, radius, radius, cell.Color);
            if (FocusState == FocusState.Unfocused || cursor != _selectedCursor)
            {
                continue;
            }

            Color focusColor = GetThemeBrushColor(
                HighContrastVisualPolicy.GetContributionFocusBrushKey(
                    IsHighContrastActive(),
                    cell.Day.ContributionCount));
            args.DrawingSession.DrawRoundedRectangle(cell.Bounds, radius, radius, focusColor, 1);
        }
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
        _themeSettings ??= ThemeSettingsHelper.TryGetFor(this);
        if (_themeSettings is not null && !_isHighContrastSubscribed)
        {
            try
            {
                _themeSettings.Changed += ThemeSettings_Changed;
                _isHighContrastSubscribed = true;
            }
            catch (Exception)
            {
                _isHighContrastSubscribed = false;
            }
        }
        if (!_isPaletteSubscribed)
        {
            ThemePaletteRuntime.PaletteChanged += ThemePaletteRuntime_PaletteChanged;
            _isPaletteSubscribed = true;
        }

        RequestRender();
    }

    private void ProfileContributionGraph_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachWeeksCollection();
        if (_isPaletteSubscribed)
        {
            ThemePaletteRuntime.PaletteChanged -= ThemePaletteRuntime_PaletteChanged;
            _isPaletteSubscribed = false;
        }
        if (_themeSettings is null || !_isHighContrastSubscribed)
        {
            _themeSettings = null;
            return;
        }

        try
        {
            _themeSettings.Changed -= ThemeSettings_Changed;
        }
        catch (Exception)
        {
            // The system projection can already be unavailable during app shutdown.
        }
        finally
        {
            _isHighContrastSubscribed = false;
            _themeSettings = null;
        }
    }

    private void ThemeSettings_Changed(object? sender, EventArgs args) => RequestRender();

    private void ThemePaletteRuntime_PaletteChanged(
        object? sender,
        ThemePaletteChangedEventArgs args) =>
        RequestRender();

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
        RequestRender();
    }

    private Color CreateContributionColor(ProfileContributionDayViewItem day)
    {
        string? highContrastBrushKey = HighContrastVisualPolicy.GetContributionCellBrushKey(
            IsHighContrastActive(),
            day.ContributionCount);
        if (highContrastBrushKey is not null)
        {
            return GetThemeBrushColor(highContrastBrushKey);
        }

        Color fallback = GetThemeBrushColor(HighContrastVisualPolicy.CanvasBrushKey);
        if (!_contributionColors.TryGetValue(day.ColorHex, out Color color))
        {
            color = ProfileColorBrush.CreateColor(day.ColorHex, fallback);
            _contributionColors[day.ColorHex] = color;
        }

        return color;
    }

    private bool IsHighContrastActive() =>
        ThemeSettingsHelper.IsHighContrastActive(_themeSettings);

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
        _calendarCanvas.Invalidate();
    }

    private void OpenKeyboardToolTip()
    {
        CloseAllToolTips();
        if (!_toolTipsEnabled ||
            FocusState == FocusState.Unfocused ||
            !TryGetSelectedCell(out ContributionCell cell))
        {
            return;
        }

        ConfigureCellToolTip(cell);
        _cellToolTip.IsOpen = true;
    }

    private void CloseKeyboardToolTip()
    {
        _cellToolTip.IsOpen = false;
    }

    private void CloseAllToolTips()
    {
        CloseKeyboardToolTip();
    }

    private void CalendarCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TryGetCellAt(e.GetCurrentPoint(_calendarCanvas).Position, out _, out ContributionCell cell))
        {
            ConfigureCellToolTip(cell);
        }
        else
        {
            _cellToolTip.IsOpen = false;
        }
    }

    private void CalendarCanvas_PointerExited(object sender, PointerRoutedEventArgs e) =>
        _cellToolTip.IsOpen = false;

    private void CalendarCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!TryGetCellAt(
                e.GetCurrentPoint(_calendarCanvas).Position,
                out ProfileContributionCursor cursor,
                out ContributionCell cell))
        {
            return;
        }

        _selectedCursor = cursor;
        _preserveUserSelection = true;
        Focus(FocusState.Pointer);
        UpdateAccessibleSelection();
        UpdateSelectionVisual();
        ConfigureCellToolTip(cell);
        e.Handled = true;
    }

    private bool TryGetCellAt(
        Point point,
        out ProfileContributionCursor cursor,
        out ContributionCell cell)
    {
        cursor = new ProfileContributionCursor(-1, -1);
        cell = null!;
        double pitch = _cellSize + _cellGap;
        if (_cellSize <= 0 || pitch <= 0 || point.X < 0 || point.Y < 0)
        {
            return false;
        }

        int week = (int)Math.Floor(point.X / pitch);
        int day = (int)Math.Floor(point.Y / pitch);
        double cellX = point.X - (week * pitch);
        double cellY = point.Y - (day * pitch);
        if (cellX > _cellSize || cellY > _cellSize)
        {
            return false;
        }

        cursor = new ProfileContributionCursor(week, day);
        return _cells.TryGetValue(cursor, out cell!);
    }

    private void ConfigureCellToolTip(ContributionCell cell)
    {
        if (!_toolTipsEnabled)
        {
            return;
        }

        _cellToolTip.Content = cell.Day.ToolTipText;
        _cellToolTip.PlacementTarget = _calendarCanvas;
        _cellToolTip.PlacementRect = cell.Bounds;
    }

    private sealed record ContributionCell(
        ProfileContributionDayViewItem Day,
        Rect Bounds,
        Color Color);

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
