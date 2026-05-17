using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.System;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class ShellPage : Page
{
    private const double SearchSuggestionsTopOffset = 8;
    private const string SearchSuggestionsScenario = "search-suggestions";
    private CancellationTokenSource? _notificationLifetime;
    private bool _suppressSearchSuggestionsUntilTextChanges;
    private bool _clearingInitialSearchFocus;
    private bool _searchFocusRequestedByUser;
    private bool _updatingSearchSelectionFromKeyboard;
    public ShellViewModel ViewModel { get; } = new();

    public ShellPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ShellRoot.AddHandler(KeyDownEvent, new KeyEventHandler(ShellRoot_KeyDown), true);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.LoadApplication(new RelayCommand(OpenModal), new RelayCommand(CloseModal));
        ViewModel.InitializeDesktopIntegration(((App)Application.Current).CurrentMainWindow);
        var notificationService = ((App)Application.Current).GetService<INotificationService>();
        notificationService.Register(new RelayCommand<string?>(PushNotification));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        MainWindow mainWindow = ((App)Application.Current).CurrentMainWindow;
        mainWindow.SearchShortcutRequested -= MainWindow_SearchShortcutRequested;
        mainWindow.SearchShortcutRequested += MainWindow_SearchShortcutRequested;
        mainWindow.SetPageTitleBar(TitleBarHost);
        QueueTitleBarPassthroughUpdate(mainWindow);

        ConnectedAnimation? animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("AppLogoAnimation");
        animation?.TryStart(AppLogoShellPage);

        bool useAutomationSearchResults = string.Equals(e.Parameter as string, SearchSuggestionsScenario, StringComparison.OrdinalIgnoreCase);
        if (useAutomationSearchResults)
        {
            ViewModel.SearchResults = CreateAutomationSearchResults();
        }
        else
        {
            ViewModel.RegisterSearchDebounce(SearchTextBox);
        }

        if (Program.CurrentLaunchOptions.IsRepositoryPageOverride)
        {
            _ = OpenLaunchRepositoryPageAsync();
        }

        _ = ViewModel.OnNavigatedTo();
    }

    private async Task OpenLaunchRepositoryPageAsync()
    {
        await Task.Delay(350);
        _ = DispatcherQueue.TryEnqueue(() =>
            ViewModel.OpenRepositoryPage(
                Program.CurrentLaunchOptions.RepositoryFullName,
                Program.CurrentLaunchOptions.Page,
                Program.CurrentLaunchOptions.Branch));
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        MainWindow mainWindow = ((App)Application.Current).CurrentMainWindow;
        mainWindow.SearchShortcutRequested -= MainWindow_SearchShortcutRequested;
        mainWindow.ClearTitleBarPassthroughRegions();
        ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("AppLogoLogoutAnimation", AppLogoShellPage);
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void OpenModal()
    {
        Modal.Visibility = Visibility.Visible;
        SearchTextBox.IsEnabled = false;
        SearchSubmitButton.IsEnabled = false;
        HideSearchSuggestions();
    }

    private void CloseModal()
    {
        Modal.Visibility = Visibility.Collapsed;
        SearchTextBox.IsEnabled = true;
        SearchSubmitButton.IsEnabled = true;
    }

    private void PushNotification(string? message)
    {
        _notificationLifetime?.Cancel();
        _notificationLifetime?.Dispose();

        CancellationTokenSource lifetime = new();
        _notificationLifetime = lifetime;

        NotificationBar.Message = message ?? string.Empty;
        NotificationBar.IsOpen = true;

        _ = CloseNotificationAsync(lifetime.Token);
    }

    private void TabView_AddTabButtonClick(TabView sender, object args)
    {
        ViewModel.OnAddTab(sender, args);
    }

    private void TabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        ViewModel.OnTabClose(sender, args);
    }

    private void TabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.OnTabSelectionChanged(sender, e);
    }

    private void Page_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 768)
        {
            VisualStateManager.GoToState(this, "WideLayout", false);
        }
        else
        {
            VisualStateManager.GoToState(this, "NarrowLayout", false);
        }
        QueueTitleBarPassthroughUpdate(((App)Application.Current).CurrentMainWindow);

        if (SearchSuggestionsHost.Visibility == Visibility.Visible)
        {
            UpdateSearchSuggestionsLayout();
        }
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _ = ClearInitialSearchFocusAsync();
    }

    private async Task ClearInitialSearchFocusAsync()
    {
        _clearingInitialSearchFocus = true;

        foreach (int delay in new[] { 0, 50, 150, 350, 750, 1250, 2000, 3500 })
        {
            if (delay > 0)
            {
                await Task.Delay(delay);
            }
            else
            {
                await Task.Yield();
            }

            ClearInitialSearchFocusIfNeeded();
        }

        _clearingInitialSearchFocus = false;
    }

    private void ClearInitialSearchFocusIfNeeded()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_searchFocusRequestedByUser)
            {
                MoveFocusToSearchSink();
            }

            SetSearchFocusVisual(IsFocusWithinSearchBox());
        });
    }

    private async Task CloseNotificationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        NotificationBar.IsOpen = false;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.SearchResults))
        {
            _ = DispatcherQueue.TryEnqueue(UpdateSearchSuggestionsState);
        }
    }

    private void SearchTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSearchTextAlignment();
        UpdateSearchSuggestionsState();
        QueueTitleBarPassthroughUpdate(((App)Application.Current).CurrentMainWindow);
    }

    private void SearchTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_clearingInitialSearchFocus && !_searchFocusRequestedByUser)
        {
            _ = DispatcherQueue.TryEnqueue(DismissSearchFocus);
            return;
        }

        SetSearchFocusVisual(true);
        UpdateSearchTextAlignment();
        UpdateSearchSuggestionsState();
    }

    private void SearchControl_LostFocus(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(() => SetSearchFocusVisual(IsFocusWithinSearchBox()));
    }

    private void SearchTextBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(SearchTextBox).Properties.IsRightButtonPressed)
        {
            return;
        }

        _searchFocusRequestedByUser = true;

        if (SearchSuggestionsHost.Visibility == Visibility.Visible)
        {
            HideSearchSuggestions();
        }
    }

    private void SearchTextBox_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        HideSearchSuggestions();
    }

    private void SearchTextContextFlyout_Opening(object sender, object e)
    {
        bool hasSelection = SearchTextBox.SelectionLength > 0;
        bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        bool canEdit = !SearchTextBox.IsReadOnly;

        SearchUndoMenuItem.IsEnabled = SearchTextBox.CanUndo;
        SearchRedoMenuItem.IsEnabled = SearchTextBox.CanRedo;
        SearchCutMenuItem.IsEnabled = canEdit && hasSelection;
        SearchCopyMenuItem.IsEnabled = hasSelection;
        SearchPasteMenuItem.IsEnabled = canEdit && Clipboard.GetContent().Contains(StandardDataFormats.Text);
        SearchSelectAllMenuItem.IsEnabled = hasText;
    }

    private void SearchUndoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SearchTextBox.CanUndo)
        {
            SearchTextBox.Undo();
        }
    }

    private void SearchRedoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SearchTextBox.CanRedo)
        {
            SearchTextBox.Redo();
        }
    }

    private void SearchCutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.CutSelectionToClipboard();
    }

    private void SearchCopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.CopySelectionToClipboard();
    }

    private void SearchPasteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.PasteFromClipboard();
    }

    private void SearchSelectAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.SelectAll();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _suppressSearchSuggestionsUntilTextChanges = false;
        SearchSuggestionsList.SelectedItem = null;
        UpdateSearchTextAlignment();
        UpdateSearchSuggestionsState();
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab)
        {
            SearchSuggestionsList.SelectedItem = null;
            HideSearchSuggestions();
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            MoveSearchSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Up)
        {
            MoveSearchSelection(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            SubmitSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            DismissSearchFocus();
            e.Handled = true;
        }
    }

    private void SearchSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        SubmitSearch();
    }

    private void SearchSubmitButton_GotFocus(object sender, RoutedEventArgs e)
    {
        SetSearchFocusVisual(true);
        SearchSuggestionsList.SelectedItem = null;
        HideSearchSuggestions();
    }

    private void SearchControl_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        DismissSearchFocus();
        e.Handled = true;
    }

    private void ShellMenuButton_GotFocus(object sender, RoutedEventArgs e)
    {
        SetSearchFocusVisual(false);
        HideSearchSuggestions();
    }

    private void ShellRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.K && IsControlKeyDown())
        {
            FocusSearchBox();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && IsFocusWithinSearchBox())
        {
            DismissSearchFocus();
            e.Handled = true;
        }
    }

    private void SearchKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        FocusSearchBox();
        args.Handled = true;
    }

    private void MainWindow_SearchShortcutRequested(object? sender, EventArgs e)
    {
        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        if (!SearchTextBox.IsEnabled)
        {
            return;
        }

        _searchFocusRequestedByUser = true;
        SearchTextBox.Focus(FocusState.Keyboard);
        SearchTextBox.SelectAll();
        UpdateSearchTextAlignment();
        UpdateSearchSuggestionsState();
    }

    private static bool IsControlKeyDown()
    {
        CoreVirtualKeyStates controlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (controlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private void SearchSuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GitHubRepository repo)
        {
            OpenRepository(repo);
        }
    }

    private void SearchSuggestionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSearchSelectionFromKeyboard || SearchSuggestionsHost.Visibility != Visibility.Visible)
        {
            return;
        }

        if (SearchSuggestionsList.SelectedItem is GitHubRepository repo)
        {
            OpenRepository(repo);
        }
    }

    private void SearchSuggestionsList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = TryOpenSearchSuggestionFromSource(e.OriginalSource);
    }

    private void SearchSuggestionsList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = TryOpenSearchSuggestionFromSource(e.OriginalSource);
    }

    private void SearchSuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitHubRepository repo })
        {
            OpenRepository(repo);
        }
    }

    private bool TryOpenSearchSuggestionFromSource(object source)
    {
        if (SearchSuggestionsHost.Visibility != Visibility.Visible || source is not DependencyObject dependencyObject)
        {
            return false;
        }

        GitHubRepository? repo = FindSearchSuggestionRepository(dependencyObject);
        if (repo is null)
        {
            return false;
        }

        OpenRepository(repo);
        return true;
    }

    private static GitHubRepository? FindSearchSuggestionRepository(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: GitHubRepository repo })
            {
                return repo;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void UpdateSearchTextAlignment()
    {
        SearchTextBox.TextAlignment = SearchTextBox.FlowDirection == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;
    }

    private void UpdateSearchSuggestionsState()
    {
        if (_suppressSearchSuggestionsUntilTextChanges)
        {
            HideSearchSuggestions();
            return;
        }

        bool hasResults = ViewModel.SearchResults.Count > 0;
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchTextBox.Text);
        bool shouldOpen = hasQuery && hasResults && SearchTextBox.IsEnabled;

        if (!shouldOpen)
        {
            if (SearchSuggestionsHost.Visibility == Visibility.Visible)
            {
                HideSearchSuggestions();
            }
            return;
        }

        UpdateSearchSuggestionsLayout();
        if (SearchSuggestionsHost.Visibility != Visibility.Visible)
        {
            SearchSuggestionsHost.Visibility = Visibility.Visible;
        }
    }

    private void UpdateSearchSuggestionsLayout()
    {
        if (SearchBoxContainer.ActualWidth <= 0)
        {
            return;
        }

        Point containerOrigin = SearchBoxContainer.TransformToVisual(ShellRoot).TransformPoint(new Point(0, 0));
        Point containerPoint = SearchBoxContainer.TransformToVisual(ShellRoot)
            .TransformPoint(new Point(0, SearchBoxContainer.ActualHeight + SearchSuggestionsTopOffset));

        SearchSuggestionsHost.Width = SearchBoxContainer.ActualWidth;
        SearchSuggestionsHost.MaxWidth = SearchBoxContainer.ActualWidth;
        SearchSuggestionsHost.MaxHeight = 360;
        Canvas.SetLeft(SearchSuggestionsHost, containerOrigin.X);
        Canvas.SetTop(SearchSuggestionsHost, containerPoint.Y);
    }

    private void QueueTitleBarPassthroughUpdate(MainWindow mainWindow)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            mainWindow.SetTitleBarPassthroughRegions(
                SearchBoxContainer,
                ShellMenuButton);
        });
    }

    private void MoveSearchSelection(int step)
    {
        if (ViewModel.SearchResults.Count == 0)
        {
            return;
        }

        if (SearchSuggestionsHost.Visibility != Visibility.Visible)
        {
            UpdateSearchSuggestionsState();
        }

        int count = SearchSuggestionsList.Items.Count;
        if (count == 0)
        {
            return;
        }

        int index = SearchSuggestionsList.SelectedIndex;
        index = index < 0
            ? (step > 0 ? 0 : count - 1)
            : Math.Clamp(index + step, 0, count - 1);

        try
        {
            _updatingSearchSelectionFromKeyboard = true;
            SearchSuggestionsList.SelectedIndex = index;
        }
        finally
        {
            _updatingSearchSelectionFromKeyboard = false;
        }

        if (SearchSuggestionsList.SelectedItem is object item)
        {
            SearchSuggestionsList.ScrollIntoView(item);
        }
    }

    private void SubmitSearch()
    {
        if (SearchSuggestionsList.SelectedItem is GitHubRepository repo)
        {
            OpenRepository(repo);
            return;
        }

        _suppressSearchSuggestionsUntilTextChanges = true;
        ViewModel.OpenSearchQuery(SearchTextBox.Text);
        HideSearchSuggestions();
    }

    private void OpenRepository(GitHubRepository repo)
    {
        _suppressSearchSuggestionsUntilTextChanges = true;
        HideSearchSuggestions();
        ViewModel.OpenRepository(repo);
    }

    private void HideSearchSuggestions()
    {
        SearchSuggestionsHost.Visibility = Visibility.Collapsed;
        SearchSuggestionsList.SelectedItem = null;
    }

    private void DismissSearchFocus()
    {
        _searchFocusRequestedByUser = false;
        HideSearchSuggestions();
        MoveFocusToSearchSink();
        SetSearchFocusVisual(IsFocusWithinSearchBox());
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            MoveFocusToSearchSink();
            SetSearchFocusVisual(IsFocusWithinSearchBox());
        });
    }

    private void MoveFocusToSearchSink()
    {
        _ = SearchFocusSink.Focus(FocusState.Programmatic);
    }

    private void SetSearchFocusVisual(bool focused)
    {
        VisualStateManager.GoToState(this, focused ? "SearchFocused" : "SearchUnfocused", true);
    }

    private bool IsFocusWithinSearchBox()
    {
        DependencyObject? focusedElement = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        return IsWithin(focusedElement, SearchBoxContainer);
    }

    private void Page_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (SearchSuggestionsHost.Visibility != Visibility.Visible)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            HideSearchSuggestions();
            return;
        }

        if (IsWithin(source, SearchBoxContainer) || IsWithin(source, SearchSuggestionsHost))
        {
            return;
        }

        HideSearchSuggestions();
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject? ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static List<GitHubRepository> CreateAutomationSearchResults() =>
    new()
    {
        CreateAutomationRepository(1, "flutter", "flutter", "Flutter makes it easy and fast to build beautiful apps."),
        CreateAutomationRepository(2, "flutter", "plugins", "Plugins for Flutter maintained by the Flutter team."),
        CreateAutomationRepository(3, "iampawan", "FlutterExampleApps", "Example Flutter apps for UI and architecture testing."),
        CreateAutomationRepository(4, "Solido", "awesome-flutter", "A curated list of Flutter resources."),
        CreateAutomationRepository(5, "wger-project", "flutter", "Flutter client for wger."),
        CreateAutomationRepository(6, "kaina404", "FlutterDouBan", "DouBan client written in Flutter."),
        CreateAutomationRepository(7, "toly1994328", "FlutterUnit", "Flutter samples and widgets."),
        CreateAutomationRepository(8, "flutter", "engine", "The Flutter engine.")
    };

    private static GitHubRepository CreateAutomationRepository(long id, string owner, string name, string description) =>
        new()
        {
            Id = id,
            Name = name,
            FullName = $"{owner}/{name}",
            Description = description,
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = $"https://github.com/{owner}"
            }
        };
}
