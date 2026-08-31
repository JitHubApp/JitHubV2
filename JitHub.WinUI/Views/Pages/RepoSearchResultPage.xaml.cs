using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoSearchResultPage : Page
{
    private const string SearchSuggestionsScenario = "search-suggestions";
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _searchDebounce;
    private string _initialQuery = string.Empty;
    private bool _initialized;
    private ProductPerformanceScrollProbe? _performanceScrollProbe;

    public RepoSearchResultPage()
    {
        ViewModel = ((App)Application.Current).GetService<RepoSearchResultPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += RepoSearchResultPage_Loaded;
        Unloaded += RepoSearchResultPage_Unloaded;
    }

    public RepoSearchResultPageViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _initialQuery = e.Parameter as string ?? string.Empty;
        if (string.Equals(Program.CurrentLaunchOptions.Scenario, SearchSuggestionsScenario, StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.SetAutomationRepositories(CreateAutomationRepositories());
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelPendingWork();
        base.OnNavigatedFrom(e);
    }

    private void RepoSearchResultPage_Loaded(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            AttachPerformanceScrollProbe();
            ApplyResponsiveLayout(ActualWidth);
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            await ViewModel.InitializeAsync(_initialQuery, _lifetimeCancellation.Token);
            ProductPerformanceReadiness.CommitRoute("repo_search", ProductPerformanceReadiness.CountIdentity(ViewModel.Results.Count));
            UiTaskGuard.Observe(PrefetchLikelyRepositoriesAsync(), "ui-repo-search-result-page");
        }, "ui-repo-search-result-page");
    }

    private Task PrefetchLikelyRepositoriesAsync() =>
        Task.WhenAll(ViewModel.Results
            .Take(4)
            .Select(item => ((App)Application.Current)
                .GetService<ShellPageViewModel>()
                .PrefetchRepositoryCodeAsync(item.Repository, _lifetimeCancellation.Token)));

    private void RepoSearchResultPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
        _searchDebounce?.Cancel();
        ViewModel.CancelPendingWork();
    }

    private void AttachPerformanceScrollProbe()
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = ProductPerformanceReadiness.IsEnabled &&
            FindDescendant<ScrollViewer>(ResultsList) is ScrollViewer scrollViewer
                ? ProductPerformanceScrollProbe.TryStart(ResultsList, scrollViewer)
                : null;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is T nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double availableWidth)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(
            availableWidth,
            WorkspaceChromeContracts.RepositorySearch);
        WorkspaceChromeVisuals.ApplyRoot(SearchWorkspace, chrome);
        WorkspaceChromeVisuals.ApplyHeader(SearchHeaderGrid, chrome);

        SearchSortColumn.Width = chrome.StackCommandRows ? new GridLength(0) : new GridLength(180);
        WorkspaceChromeVisuals.ApplyPlacement(
            QueryTextBox,
            chrome,
            new WorkspaceElementPlacement(0, 0, 1, StretchHorizontally: true),
            new WorkspaceElementPlacement(0, 0, 1, StretchHorizontally: true));
        WorkspaceChromeVisuals.ApplyPlacement(
            FilterButton,
            chrome,
            new WorkspaceElementPlacement(0, 1, 1),
            new WorkspaceElementPlacement(0, 1, 1));
        WorkspaceChromeVisuals.ApplyPlacement(
            RepoSearchSortComboBox,
            chrome,
            new WorkspaceElementPlacement(0, 2, 1, StretchHorizontally: true),
            new WorkspaceElementPlacement(1, 0, 2, StretchHorizontally: true));
    }

    private void SearchCriteria_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            ScheduleSearch();
        }
    }

    private void SearchCriteria_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            ScheduleSearch(delayMilliseconds: 80);
        }
    }

    private void QueryTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        _searchDebounce?.Cancel();
        UiTaskGuard.Observe(ViewModel.ApplySearchAsync(_lifetimeCancellation.Token), "ui-repo-search-result-page");
        e.Handled = true;
    }

    private void ScheduleSearch(int delayMilliseconds = 280)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        CancellationTokenSource debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _searchDebounce = debounce;
        UiTaskGuard.Observe(DebounceSearchAsync(debounce, delayMilliseconds), "ui-repo-search-result-page");
    }

    private async Task DebounceSearchAsync(CancellationTokenSource debounce, int delayMilliseconds)
    {
        try
        {
            await Task.Delay(delayMilliseconds, debounce.Token);
            await ViewModel.ApplySearchAsync(debounce.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchDebounce, null, debounce), debounce))
            {
                debounce.Dispose();
            }
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
        }, "ui-repo-search-result-page");
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAllFilters();
        UiTaskGuard.Observe(ViewModel.ApplySearchAsync(_lifetimeCancellation.Token), "ui-repo-search-result-page");
        FilterButton.Flyout?.Hide();
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RepositorySearchFilterChip chip })
        {
            ViewModel.ClearFilter(chip.Id);
            UiTaskGuard.Observe(ViewModel.ApplySearchAsync(_lifetimeCancellation.Token), "ui-repo-search-result-page");
        }
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RepositorySearchResultItem item)
        {
            ProductPerformanceReadiness.BeginTraversal(
                "repo_search",
                $"RepoSearchResult_{item.Repository.Id}",
                "repo_code");
            ((App)Application.Current).GetService<ShellPageViewModel>().OpenRepository(item.Repository);
        }
    }

    private void ResultsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.PointerEntered -= ResultContainer_PointerEntered;
        if (args.InRecycleQueue)
        {
            return;
        }

        if (args.Item is not RepositorySearchResultItem item)
        {
            return;
        }

        AutomationProperties.SetAutomationId(args.ItemContainer, $"RepoSearchResult_{item.Repository.Id}");
        AutomationProperties.SetName(args.ItemContainer, item.FullName);
        args.ItemContainer.PointerEntered += ResultContainer_PointerEntered;
        if (args.ItemIndex >= ViewModel.Results.Count - 8 && ViewModel.CanLoadMore)
        {
            UiTaskGuard.Observe(ViewModel.LoadNextPageAsync(_lifetimeCancellation.Token), "ui-repo-search-result-page");
        }
    }

    private void ResultContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RepositorySearchResultItem item })
        {
            UiTaskGuard.Observe(((App)Application.Current).GetService<ShellPageViewModel>().PrefetchRepositoryCodeAsync(item.Repository, _lifetimeCancellation.Token), "ui-repo-search-result-page");
        }
    }

    private static GitHubRepository[] CreateAutomationRepositories() =>
    [
        CreateAutomationRepository(1001, "JitHubApp", "JitHubV2", "A native GitHub client for Windows.", "C#", 420),
        CreateAutomationRepository(1002, "microsoft", "WinUI-Gallery", "Windows App SDK controls and samples.", "C#", 9800),
        CreateAutomationRepository(1003, "microsoft", "WindowsAppSDK", "The Windows application development platform.", "C++", 5400),
        CreateAutomationRepository(1004, "microsoft", "terminal", "The modern Windows terminal.", "C++", 99000),
        CreateAutomationRepository(1005, "microsoft", "PowerToys", "Windows utilities for power users.", "C#", 121000)
    ];

    private static GitHubRepository CreateAutomationRepository(
        long id,
        string owner,
        string name,
        string description,
        string language,
        int stars) => new()
        {
            Id = id,
            Name = name,
            FullName = $"{owner}/{name}",
            Description = description,
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Language = language,
            StargazersCount = stars,
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-(id % 10)),
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = $"https://github.com/{owner}"
            }
        };
}
