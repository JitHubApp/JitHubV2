using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CommunityToolkit.WinUI.UI.Controls;

[TemplatePart(Name = "RootPanel", Type = typeof(TwoPaneView))]
[TemplatePart(Name = "DetailsPresenter", Type = typeof(ContentPresenter))]
[TemplatePart(Name = "MainList", Type = typeof(ListView))]
[TemplatePart(Name = "ListDetailsBackButton", Type = typeof(Button))]
[TemplatePart(Name = "HeaderContentPresenter", Type = typeof(ContentPresenter))]
public sealed partial class ListDetailsView : ItemsControl
{
    private INotifyCollectionChanged? _observedCollection;
    private bool _updatingSelection;
    private TwoPaneView? _twoPaneView;
    private Grid? _listPanel;
    private ContentPresenter? _detailsPresenter;
    private ContentPresenter? _listHeaderPresenter;
    private Grid? _listCommandBarHost;
    private Grid? _detailsCommandBarHost;
    private ListView? _mainList;
    private Button? _inlineBackButton;
    private IEnumerable? _lastItemsSource;

    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(
            nameof(SelectedIndex),
            typeof(int),
            typeof(ListDetailsView),
            new PropertyMetadata(-1, OnSelectedIndexChanged));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(object),
            typeof(ListDetailsView),
            new PropertyMetadata(null, OnSelectedItemChanged));

    public static readonly DependencyProperty DetailsTemplateProperty =
        DependencyProperty.Register(
            nameof(DetailsTemplate),
            typeof(DataTemplate),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DetailsContentTemplateSelectorProperty =
        DependencyProperty.Register(
            nameof(DetailsContentTemplateSelector),
            typeof(DataTemplateSelector),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ListPaneItemTemplateSelectorProperty =
        DependencyProperty.Register(
            nameof(ListPaneItemTemplateSelector),
            typeof(DataTemplateSelector),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DetailsPaneBackgroundProperty =
        DependencyProperty.Register(
            nameof(DetailsPaneBackground),
            typeof(Brush),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ListPaneBackgroundProperty =
        DependencyProperty.Register(
            nameof(ListPaneBackground),
            typeof(Brush),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ListHeaderProperty =
        DependencyProperty.Register(
            nameof(ListHeader),
            typeof(object),
            typeof(ListDetailsView),
            new PropertyMetadata(null, OnListHeaderChanged));

    public static readonly DependencyProperty ListHeaderTemplateProperty =
        DependencyProperty.Register(
            nameof(ListHeaderTemplate),
            typeof(DataTemplate),
            typeof(ListDetailsView),
            new PropertyMetadata(null, OnListHeaderChanged));

    public static readonly DependencyProperty ListPaneEmptyContentProperty =
        DependencyProperty.Register(
            nameof(ListPaneEmptyContent),
            typeof(object),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ListPaneEmptyContentTemplateProperty =
        DependencyProperty.Register(
            nameof(ListPaneEmptyContentTemplate),
            typeof(DataTemplate),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DetailsHeaderProperty =
        DependencyProperty.Register(
            nameof(DetailsHeader),
            typeof(object),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DetailsHeaderTemplateProperty =
        DependencyProperty.Register(
            nameof(DetailsHeaderTemplate),
            typeof(DataTemplate),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ListPaneWidthProperty =
        DependencyProperty.Register(
            nameof(ListPaneWidth),
            typeof(double),
            typeof(ListDetailsView),
            new PropertyMetadata(320d, OnListPaneWidthChanged));

    public static readonly DependencyProperty NoSelectionContentProperty =
        DependencyProperty.Register(
            nameof(NoSelectionContent),
            typeof(object),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty NoSelectionContentTemplateProperty =
        DependencyProperty.Register(
            nameof(NoSelectionContentTemplate),
            typeof(DataTemplate),
            typeof(ListDetailsView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ViewStateProperty =
        DependencyProperty.Register(
            nameof(ViewState),
            typeof(ListDetailsViewState),
            typeof(ListDetailsView),
            new PropertyMetadata(ListDetailsViewState.List));

    public static readonly DependencyProperty ListCommandBarProperty =
        DependencyProperty.Register(
            nameof(ListCommandBar),
            typeof(CommandBar),
            typeof(ListDetailsView),
            new PropertyMetadata(null, OnListCommandBarChanged));

    public static readonly DependencyProperty DetailsCommandBarProperty =
        DependencyProperty.Register(
            nameof(DetailsCommandBar),
            typeof(CommandBar),
            typeof(ListDetailsView),
            new PropertyMetadata(null, OnDetailsCommandBarChanged));

    public static readonly DependencyProperty CompactModeThresholdWidthProperty =
        DependencyProperty.Register(
            nameof(CompactModeThresholdWidth),
            typeof(double),
            typeof(ListDetailsView),
            new PropertyMetadata(640d, OnCompactModeThresholdWidthChanged));

    public static readonly DependencyProperty BackButtonBehaviorProperty =
        DependencyProperty.Register(
            nameof(BackButtonBehavior),
            typeof(BackButtonBehavior),
            typeof(ListDetailsView),
            new PropertyMetadata(BackButtonBehavior.System, OnBackButtonBehaviorChanged));

    public ListDetailsView()
    {
        DefaultStyleKey = typeof(ListDetailsView);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        RegisterPropertyChangedCallback(ItemsSourceProperty, OnItemsSourcePropertyChanged);
        _lastItemsSource = ItemsSource as IEnumerable;
        UpdateItemsSourceSubscription(null, _lastItemsSource);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public DataTemplate? DetailsTemplate
    {
        get => (DataTemplate?)GetValue(DetailsTemplateProperty);
        set => SetValue(DetailsTemplateProperty, value);
    }

    public DataTemplateSelector? DetailsContentTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(DetailsContentTemplateSelectorProperty);
        set => SetValue(DetailsContentTemplateSelectorProperty, value);
    }

    public DataTemplateSelector? ListPaneItemTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(ListPaneItemTemplateSelectorProperty);
        set => SetValue(ListPaneItemTemplateSelectorProperty, value);
    }

    public Brush? DetailsPaneBackground
    {
        get => (Brush?)GetValue(DetailsPaneBackgroundProperty);
        set => SetValue(DetailsPaneBackgroundProperty, value);
    }

    public Brush? ListPaneBackground
    {
        get => (Brush?)GetValue(ListPaneBackgroundProperty);
        set => SetValue(ListPaneBackgroundProperty, value);
    }

    public object? ListHeader
    {
        get => GetValue(ListHeaderProperty);
        set => SetValue(ListHeaderProperty, value);
    }

    public DataTemplate? ListHeaderTemplate
    {
        get => (DataTemplate?)GetValue(ListHeaderTemplateProperty);
        set => SetValue(ListHeaderTemplateProperty, value);
    }

    public object? ListPaneEmptyContent
    {
        get => GetValue(ListPaneEmptyContentProperty);
        set => SetValue(ListPaneEmptyContentProperty, value);
    }

    public DataTemplate? ListPaneEmptyContentTemplate
    {
        get => (DataTemplate?)GetValue(ListPaneEmptyContentTemplateProperty);
        set => SetValue(ListPaneEmptyContentTemplateProperty, value);
    }

    public object? DetailsHeader
    {
        get => GetValue(DetailsHeaderProperty);
        set => SetValue(DetailsHeaderProperty, value);
    }

    public DataTemplate? DetailsHeaderTemplate
    {
        get => (DataTemplate?)GetValue(DetailsHeaderTemplateProperty);
        set => SetValue(DetailsHeaderTemplateProperty, value);
    }

    public double ListPaneWidth
    {
        get => (double)GetValue(ListPaneWidthProperty);
        set => SetValue(ListPaneWidthProperty, value);
    }

    public object? NoSelectionContent
    {
        get => GetValue(NoSelectionContentProperty);
        set => SetValue(NoSelectionContentProperty, value);
    }

    public DataTemplate? NoSelectionContentTemplate
    {
        get => (DataTemplate?)GetValue(NoSelectionContentTemplateProperty);
        set => SetValue(NoSelectionContentTemplateProperty, value);
    }

    public ListDetailsViewState ViewState
    {
        get => (ListDetailsViewState)GetValue(ViewStateProperty);
        private set => SetValue(ViewStateProperty, value);
    }

    public CommandBar? ListCommandBar
    {
        get => (CommandBar?)GetValue(ListCommandBarProperty);
        set => SetValue(ListCommandBarProperty, value);
    }

    public CommandBar? DetailsCommandBar
    {
        get => (CommandBar?)GetValue(DetailsCommandBarProperty);
        set => SetValue(DetailsCommandBarProperty, value);
    }

    public double CompactModeThresholdWidth
    {
        get => (double)GetValue(CompactModeThresholdWidthProperty);
        set => SetValue(CompactModeThresholdWidthProperty, value);
    }

    public BackButtonBehavior BackButtonBehavior
    {
        get => (BackButtonBehavior)GetValue(BackButtonBehaviorProperty);
        set => SetValue(BackButtonBehaviorProperty, value);
    }

    public Func<object, object>? MapDetails { get; set; }

    public event SelectionChangedEventHandler? SelectionChanged;

    public event EventHandler<ListDetailsViewState>? ViewStateChanged;

    protected override void OnApplyTemplate()
    {
        if (_mainList is not null)
        {
            _mainList.SelectionChanged -= MainList_SelectionChanged;
        }

        if (_inlineBackButton is not null)
        {
            _inlineBackButton.Click -= InlineBackButton_Click;
        }

        base.OnApplyTemplate();

        _twoPaneView = GetTemplateChild("RootPanel") as TwoPaneView;
        _listPanel = GetTemplateChild("ListPanel") as Grid;
        _detailsPresenter = GetTemplateChild("DetailsPresenter") as ContentPresenter;
        _listHeaderPresenter = GetTemplateChild("HeaderContentPresenter") as ContentPresenter;
        _listCommandBarHost = GetTemplateChild("ListCommandBar") as Grid;
        _detailsCommandBarHost = GetTemplateChild("DetailsCommandBar") as Grid;
        _mainList = GetTemplateChild("MainList") as ListView;
        _inlineBackButton = GetTemplateChild("ListDetailsBackButton") as Button;

        if (_mainList is not null)
        {
            _mainList.SelectionChanged += MainList_SelectionChanged;
        }

        if (_inlineBackButton is not null)
        {
            _inlineBackButton.Click += InlineBackButton_Click;
        }

        ApplyListHeaderVisibility();
        ApplyListPaneWidth();
        ApplyCommandBar(_listCommandBarHost, ListCommandBar);
        ApplyCommandBar(_detailsCommandBarHost, DetailsCommandBar);
        SyncSelectionFromProperties();
        UpdateView(false);
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ListDetailsView control = (ListDetailsView)d;
        if (control._updatingSelection)
        {
            return;
        }

        object? oldItem = control.GetItemAt((int)e.OldValue);
        object? newItem = control.GetItemAt((int)e.NewValue);
        control._updatingSelection = true;
        try
        {
            if (!Equals(control.SelectedItem, newItem))
            {
                control.SetValue(SelectedItemProperty, newItem);
            }

            if (control._mainList is not null && control._mainList.SelectedIndex != (int)e.NewValue)
            {
                control._mainList.SelectedIndex = (int)e.NewValue;
            }
        }
        finally
        {
            control._updatingSelection = false;
        }

        control.OnSelectionUpdated(oldItem, newItem);
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ListDetailsView control = (ListDetailsView)d;
        if (control._updatingSelection)
        {
            return;
        }

        int newIndex = control.IndexOfItem(e.NewValue);
        control._updatingSelection = true;
        try
        {
            if (control.SelectedIndex != newIndex)
            {
                control.SetValue(SelectedIndexProperty, newIndex);
            }

            if (control._mainList is not null && !Equals(control._mainList.SelectedItem, e.NewValue))
            {
                control._mainList.SelectedItem = e.NewValue;
            }
        }
        finally
        {
            control._updatingSelection = false;
        }

        control.OnSelectionUpdated(e.OldValue, e.NewValue);
    }

    private static void OnListHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ListDetailsView)d).ApplyListHeaderVisibility();
    }

    private static void OnListPaneWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ListDetailsView)d).ApplyListPaneWidth();
    }

    private static void OnListCommandBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ListDetailsView control = (ListDetailsView)d;
        control.ApplyCommandBar(control._listCommandBarHost, e.NewValue as CommandBar);
    }

    private static void OnDetailsCommandBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ListDetailsView control = (ListDetailsView)d;
        control.ApplyCommandBar(control._detailsCommandBarHost, e.NewValue as CommandBar);
    }

    private static void OnCompactModeThresholdWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ListDetailsView)d).UpdateView(false);
    }

    private static void OnBackButtonBehaviorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ListDetailsView)d).UpdateBackButtonVisibility();
    }

    private void MainList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || _mainList is null)
        {
            return;
        }

        object? oldItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
        object? newItem = e.AddedItems.Count > 0 ? e.AddedItems[0] : null;

        _updatingSelection = true;
        try
        {
            SetValue(SelectedItemProperty, newItem);
            SetValue(SelectedIndexProperty, _mainList.SelectedIndex);
        }
        finally
        {
            _updatingSelection = false;
        }

        OnSelectionUpdated(oldItem, newItem);
    }

    private void InlineBackButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedItem = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateView(false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mainList is not null)
        {
            _mainList.SelectionChanged -= MainList_SelectionChanged;
        }

        if (_inlineBackButton is not null)
        {
            _inlineBackButton.Click -= InlineBackButton_Click;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateView(true);
    }

    private void OnItemsSourcePropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        IEnumerable? newItemsSource = ItemsSource as IEnumerable;
        UpdateItemsSourceSubscription(_lastItemsSource, newItemsSource);
        _lastItemsSource = newItemsSource;
        SyncSelectionFromProperties();
        UpdateView(false);
    }

    private void UpdateItemsSourceSubscription(IEnumerable? oldItemsSource, IEnumerable? newItemsSource)
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= ItemsSource_CollectionChanged;
            _observedCollection = null;
        }

        if (newItemsSource is INotifyCollectionChanged collection)
        {
            _observedCollection = collection;
            _observedCollection.CollectionChanged += ItemsSource_CollectionChanged;
        }
    }

    private void ItemsSource_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncSelectionFromProperties();
        UpdateView(false);
    }

    private void SyncSelectionFromProperties()
    {
        if (_updatingSelection)
        {
            return;
        }

        _updatingSelection = true;
        try
        {
            object? selectedItem = SelectedItem;
            int selectedIndex = selectedItem is not null ? IndexOfItem(selectedItem) : SelectedIndex;

            if (selectedItem is null && selectedIndex >= 0)
            {
                selectedItem = GetItemAt(selectedIndex);
            }
            else if (selectedItem is not null && selectedIndex < 0)
            {
                selectedItem = null;
                selectedIndex = -1;
            }

            SetValue(SelectedItemProperty, selectedItem);
            SetValue(SelectedIndexProperty, selectedIndex);

            if (_mainList is not null)
            {
                if (_mainList.SelectedIndex != selectedIndex)
                {
                    _mainList.SelectedIndex = selectedIndex;
                }

                if (!Equals(_mainList.SelectedItem, selectedItem))
                {
                    _mainList.SelectedItem = selectedItem;
                }
            }
        }
        finally
        {
            _updatingSelection = false;
        }
    }

    private void OnSelectionUpdated(object? oldSelection, object? newSelection)
    {
        if (Equals(oldSelection, newSelection))
        {
            UpdateView(false);
            return;
        }

        SelectionChanged?.Invoke(
            this,
            new SelectionChangedEventArgs(
                oldSelection is null ? [] : [oldSelection],
                newSelection is null ? [] : [newSelection]));

        UpdateView(true);
    }

    private void UpdateView(bool animate)
    {
        UpdateViewState();
        SetDetailsContent();
        SetVisualState(animate);
        UpdateBackButtonVisibility();
    }

    private void UpdateViewState()
    {
        ListDetailsViewState previousViewState = ViewState;
        if (_twoPaneView is null)
        {
            ViewState = ListDetailsViewState.Both;
        }
        else if (_twoPaneView.Mode == TwoPaneViewMode.SinglePane)
        {
            ViewState = SelectedItem is not null ? ListDetailsViewState.Details : ListDetailsViewState.List;
            _twoPaneView.PanePriority = SelectedItem is not null
                ? TwoPaneViewPriority.Pane2
                : TwoPaneViewPriority.Pane1;
        }
        else
        {
            ViewState = ListDetailsViewState.Both;
        }

        if (previousViewState != ViewState)
        {
            ViewStateChanged?.Invoke(this, ViewState);
        }
    }

    private void SetVisualState(bool animate)
    {
        string state = ViewState switch
        {
            ListDetailsViewState.Both when SelectedItem is null => "NoSelectionWide",
            ListDetailsViewState.Both => "HasSelectionWide",
            _ when SelectedItem is null => "NoSelectionNarrow",
            _ => "HasSelectionNarrow"
        };

        _ = VisualStateManager.GoToState(this, state, animate);
        _ = VisualStateManager.GoToState(this, Items.Count > 0 ? "HasItemsState" : "HasNoItemsState", animate);
    }

    private void SetDetailsContent()
    {
        if (_detailsPresenter is null)
        {
            return;
        }

        if (_detailsPresenter.ContentTemplateSelector is not null)
        {
            _detailsPresenter.ContentTemplate =
                _detailsPresenter.ContentTemplateSelector.SelectTemplate(SelectedItem, _detailsPresenter);
        }

        _detailsPresenter.Content = SelectedItem is null
            ? null
            : MapDetails is null
                ? SelectedItem
                : MapDetails(SelectedItem);
    }

    private void ApplyListHeaderVisibility()
    {
        if (_listHeaderPresenter is null)
        {
            return;
        }

        _listHeaderPresenter.Visibility =
            ListHeader is null && ListHeaderTemplate is null
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void ApplyListPaneWidth()
    {
        if (_listPanel is not null)
        {
            _listPanel.Width = ListPaneWidth;
        }
    }

    private void ApplyCommandBar(Grid? host, CommandBar? commandBar)
    {
        if (host is null)
        {
            return;
        }

        host.Children.Clear();
        if (commandBar is not null)
        {
            host.Children.Add(commandBar);
        }
    }

    private void UpdateBackButtonVisibility()
    {
        if (_inlineBackButton is null)
        {
            return;
        }

        _inlineBackButton.Visibility =
            ViewState == ListDetailsViewState.Details &&
            (BackButtonBehavior == BackButtonBehavior.Automatic || BackButtonBehavior == BackButtonBehavior.Inline)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private int IndexOfItem(object? item)
    {
        if (item is null)
        {
            return -1;
        }

        return Items.IndexOf(item);
    }

    private object? GetItemAt(int index)
    {
        return index >= 0 && index < Items.Count ? Items[index] : null;
    }
}
