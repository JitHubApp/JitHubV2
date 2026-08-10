using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Dialogs;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoManagePage : Page
{
    private bool _automationRepositoriesSeeded;
    private bool _synchronizingNativeSelection;
    private ListViewScrollAnchor? _pendingScrollAnchor;
    private readonly PointerEventHandler _repositoryRowPointerEnteredHandler;
    private readonly PointerEventHandler _repositoryRowPointerExitedHandler;
    private readonly PointerEventHandler _repositoryRowPointerPressedHandler;
    private readonly PointerEventHandler _repositoryRowPointerReleasedHandler;

    public RepoManagePageViewModel ViewModel { get; }

    public RepoManagePage()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _repositoryRowPointerEnteredHandler = RepositoryRowContainer_PointerEntered;
        _repositoryRowPointerExitedHandler = RepositoryRowContainer_PointerExited;
        _repositoryRowPointerPressedHandler = RepositoryRowContainer_PointerPressed;
        _repositoryRowPointerReleasedHandler = RepositoryRowContainer_PointerReleased;
        ViewModel = ((App)Application.Current).GetService<RepoManagePageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.ProjectionChanging += ViewModel_ProjectionChanging;
        ViewModel.ProjectionChanged += ViewModel_ProjectionChanged;
        ViewModel.SelectionStateChanged += ViewModel_SelectionStateChanged;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateWidthState(ActualWidth);
        if (IsRepositoryLibraryAutomationScenario() && !_automationRepositoriesSeeded)
        {
            _automationRepositoriesSeeded = true;
            ViewModel.SetAutomationRepositories(CreateAutomationRepositories());
        }

        await RunSafelyAsync(ViewModel.ActivateAsync);
        ProductPerformanceReadiness.CommitRoute(
            "repo_manage",
            ProductPerformanceReadiness.CountIdentity(ViewModel.Repositories.Count));
        _ = ViewModel.PrefetchLikelyRepositoriesAsync();
        SynchronizeNativeSelection();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateWidthState(e.NewSize.Width);

    private void Page_Unloaded(object sender, RoutedEventArgs e) => ViewModel.Deactivate();

    private void UpdateWidthState(double availableWidth)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(availableWidth);
        VisualStateManager.GoToState(
            this,
            chrome.StackCommandRows ? "Compact" : "Wide",
            useTransitions: false);
        WorkspaceRoot.Padding = chrome.Insets.ToThickness();
        RepositoryLibraryCount.Visibility = chrome.ShowOptionalHeaderContext
            ? Visibility.Visible
            : Visibility.Collapsed;
        NewRepositoryButtonText.Visibility = chrome.ShowActionLabels
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectionModeButtonText.Visibility = chrome.ShowActionLabels
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RepositoriesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RepositoryLibraryViewItem item)
        {
            return;
        }

        if (ViewModel.IsSelectionMode)
        {
            ViewModel.SetRepositorySelected(item, RepositoriesList.SelectedItems.Contains(item));
            return;
        }

        ProductPerformanceReadiness.BeginTraversal("repo_manage", item.AutomationId, "repo_code");
        ViewModel.ActivateRepository(item);
    }

    private void RepositoryRowContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetRepositoryRowBackground(sender as ListViewItem, "AppSurfaceSubtleBrush");
        if (sender is ListViewItem { DataContext: RepositoryLibraryViewItem item })
        {
            _ = ViewModel.PrefetchRepositoryAsync(item);
        }
    }

    private void RepositoryRowContainer_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        SetRepositoryRowBackground(sender as ListViewItem, "AppSurfaceBrush");

    private void RepositoryRowContainer_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        SetRepositoryRowBackground(sender as ListViewItem, "AppSurfaceSubtleBrush");

    private void RepositoryRowContainer_PointerExited(object sender, PointerRoutedEventArgs e) =>
        RestoreRepositoryRowBackground(sender as ListViewItem);

    private static void RestoreRepositoryRowBackground(ListViewItem? container)
    {
        Grid? row = FindRepositoryRowSurface(container);
        if (row is null)
        {
            return;
        }

        if (container?.DataContext is RepositoryLibraryViewItem { Selected: true })
        {
            SetRepositoryRowBackground(container, "AppCanvasRaisedBrush");
            return;
        }

        row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private static void SetRepositoryRowBackground(ListViewItem? container, string resourceKey)
    {
        if (FindRepositoryRowSurface(container) is Grid row &&
            Application.Current.Resources.TryGetValue(resourceKey, out object value) &&
            value is Brush brush)
        {
            row.Background = brush;
        }
    }

    private static Grid? FindRepositoryRowSurface(DependencyObject? root)
    {
        if (root is Grid { Name: "RepositoryRowRoot" } row)
        {
            return row;
        }

        if (root is null)
        {
            return null;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            Grid? match = FindRepositoryRowSurface(VisualTreeHelper.GetChild(root, index));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void RepositoriesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            DetachRepositoryRowPointerHandlers(container);
            container.ContextFlyout = null;
            return;
        }

        if (args.Item is not RepositoryLibraryViewItem item)
        {
            return;
        }

        AutomationProperties.SetAutomationId(container, item.AutomationId);
        AutomationProperties.SetName(container, item.AutomationName);
        AttachRepositoryRowPointerHandlers(container);
        container.ContextFlyout = CreateRepositoryContextFlyout(item);
    }

    private void AttachRepositoryRowPointerHandlers(ListViewItem container)
    {
        DetachRepositoryRowPointerHandlers(container);
        container.AddHandler(UIElement.PointerEnteredEvent, _repositoryRowPointerEnteredHandler, handledEventsToo: true);
        container.AddHandler(UIElement.PointerExitedEvent, _repositoryRowPointerExitedHandler, handledEventsToo: true);
        container.AddHandler(UIElement.PointerPressedEvent, _repositoryRowPointerPressedHandler, handledEventsToo: true);
        container.AddHandler(UIElement.PointerReleasedEvent, _repositoryRowPointerReleasedHandler, handledEventsToo: true);
        container.AddHandler(UIElement.PointerCanceledEvent, _repositoryRowPointerExitedHandler, handledEventsToo: true);
        container.AddHandler(UIElement.PointerCaptureLostEvent, _repositoryRowPointerExitedHandler, handledEventsToo: true);
    }

    private void DetachRepositoryRowPointerHandlers(ListViewItem container)
    {
        container.RemoveHandler(UIElement.PointerEnteredEvent, _repositoryRowPointerEnteredHandler);
        container.RemoveHandler(UIElement.PointerExitedEvent, _repositoryRowPointerExitedHandler);
        container.RemoveHandler(UIElement.PointerPressedEvent, _repositoryRowPointerPressedHandler);
        container.RemoveHandler(UIElement.PointerReleasedEvent, _repositoryRowPointerReleasedHandler);
        container.RemoveHandler(UIElement.PointerCanceledEvent, _repositoryRowPointerExitedHandler);
        container.RemoveHandler(UIElement.PointerCaptureLostEvent, _repositoryRowPointerExitedHandler);
    }

    private MenuFlyout CreateRepositoryContextFlyout(RepositoryLibraryViewItem item)
    {
        MenuFlyout flyout = new();
        flyout.Items.Add(CreateContextItem(
            ViewModel.OpenRepositoryMenuText,
            "RepositoryLibraryContextOpen",
            item,
            OpenRepositoryMenuItem_Click));
        flyout.Items.Add(CreateContextItem(
            ViewModel.OpenOwnerMenuText,
            "RepositoryLibraryContextOwner",
            item,
            OpenOwnerMenuItem_Click));
        flyout.Items.Add(CreateContextItem(
            ViewModel.CopyRepositoryLinkMenuText,
            "RepositoryLibraryContextCopy",
            item,
            CopyRepositoryLinkMenuItem_Click));
        if (item.CanDeleteRepository)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem delete = CreateContextItem(
                ViewModel.DeleteRepositoryMenuText,
                "RepositoryLibraryContextDelete",
                item,
                DeleteRepositoryMenuItem_Click);
            delete.Icon = new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE74D"
            };
            flyout.Items.Add(delete);
        }

        return flyout;
    }

    private static MenuFlyoutItem CreateContextItem(
        string text,
        string automationId,
        RepositoryLibraryViewItem item,
        RoutedEventHandler click)
    {
        MenuFlyoutItem menuItem = new()
        {
            Text = text,
            CommandParameter = item
        };
        AutomationProperties.SetAutomationId(menuItem, automationId);
        AutomationProperties.SetName(menuItem, text);
        menuItem.Click += click;
        return menuItem;
    }

    private void ViewModel_ProjectionChanging(object? sender, EventArgs e)
    {
        if (RepositoriesList.Items.Count > 0)
        {
            _pendingScrollAnchor = ListViewScrollAnchor.Capture(
                RepositoriesList,
                static item => item is RepositoryLibraryViewItem repository ? repository.Key : null);
        }
    }

    private void ViewModel_ProjectionChanged(object? sender, EventArgs e)
    {
        ListViewScrollAnchor? anchor = _pendingScrollAnchor;
        _pendingScrollAnchor = null;
        anchor?.RestoreAfterCollectionChange(DispatcherQueue);
        SynchronizeNativeSelection();
    }

    private void ViewModel_SelectionStateChanged(object? sender, EventArgs e) => SynchronizeNativeSelection();

    private void NewRepositoryButton_Click(object sender, RoutedEventArgs e) => ViewModel.OpenNewRepository();

    private void SelectionModeButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSelectionMode();
        SynchronizeNativeSelection();
        RepositoriesList.Focus(FocusState.Programmatic);
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelection();
        SynchronizeNativeSelection();
    }

    private void RepositoriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingNativeSelection || !ViewModel.IsSelectionMode)
        {
            return;
        }

        _synchronizingNativeSelection = true;
        try
        {
            foreach (RepositoryLibraryViewItem item in e.RemovedItems.OfType<RepositoryLibraryViewItem>())
            {
                ViewModel.SetRepositorySelected(item, selected: false);
            }

            foreach (RepositoryLibraryViewItem item in e.AddedItems.OfType<RepositoryLibraryViewItem>())
            {
                if (item.CanDeleteRepository)
                {
                    ViewModel.SetRepositorySelected(item, selected: true);
                }
                else
                {
                    RepositoriesList.SelectedItems.Remove(item);
                }
            }
        }
        finally
        {
            _synchronizingNativeSelection = false;
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e) =>
        await RunSafelyAsync(ViewModel.RetryAsync);

    private void RepositorySelectionCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: RepositoryLibraryViewItem item })
        {
            ViewModel.SetRepositorySelected(item, selected: true);
            SynchronizeNativeSelection();
        }
    }

    private void RepositorySelectionCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: RepositoryLibraryViewItem item })
        {
            ViewModel.SetRepositorySelected(item, selected: false);
            SynchronizeNativeSelection();
        }
    }

    private void SynchronizeNativeSelection()
    {
        if (RepositoriesList is null || _synchronizingNativeSelection)
        {
            return;
        }

        _synchronizingNativeSelection = true;
        try
        {
            ListViewSelectionMode desiredMode = ViewModel.IsSelectionMode
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.None;
            if (RepositoriesList.SelectionMode != desiredMode)
            {
                RepositoriesList.SelectionMode = desiredMode;
            }

            if (desiredMode == ListViewSelectionMode.None)
            {
                return;
            }

            HashSet<RepositoryLibraryViewItem> selected = ViewModel.Repositories
                .Where(static item => item.Selected)
                .ToHashSet();
            foreach (object selectedItem in RepositoriesList.SelectedItems.Cast<object>().ToArray())
            {
                if (selectedItem is RepositoryLibraryViewItem item && !selected.Contains(item))
                {
                    RepositoriesList.SelectedItems.Remove(item);
                    SetRealizedRepositorySelection(item, selected: false);
                }
            }

            foreach (RepositoryLibraryViewItem item in selected)
            {
                if (!RepositoriesList.SelectedItems.Contains(item))
                {
                    RepositoriesList.SelectedItems.Add(item);
                }

                SetRealizedRepositorySelection(item, selected: true);
            }
        }
        finally
        {
            _synchronizingNativeSelection = false;
        }
    }

    private void SetRealizedRepositorySelection(RepositoryLibraryViewItem item, bool selected)
    {
        if (RepositoriesList.ContainerFromItem(item) is ListViewItem container &&
            container.IsSelected != selected)
        {
            container.IsSelected = selected;
        }
    }

    private void OpenRepositoryMenuItem_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenRepository(GetCommandRepository(sender));

    private void OpenOwnerMenuItem_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenOwner(GetCommandRepository(sender));

    private void CopyRepositoryLinkMenuItem_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CopyRepositoryLink(GetCommandRepository(sender));

    private async void DeleteRepositoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RepositoryLibraryViewItem? item = GetCommandRepository(sender);
        if (item is null)
        {
            return;
        }

        await DeleteRepositoriesAsync([item]);
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<RepositoryLibraryViewItem> selected = ViewModel.GetSelectedRepositories();
        if (selected.Count > 0)
        {
            await DeleteRepositoriesAsync(selected);
        }
    }

    private async Task DeleteRepositoriesAsync(IReadOnlyList<RepositoryLibraryViewItem> repositories)
    {
        RepositoryDeletionResult? deletionResult = null;
        ContentDialog dialog = CreateDeleteConfirmation(repositories);
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepositoryDeleteConfirmationError");
        if (dialog.Content is TextBlock message)
        {
            dialog.Content = new StackPanel
            {
                Spacing = 12,
                Children = { message, errorText }
            };
        }

        ContentDialogResult dialogResult = await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                try
                {
                    if (!await ViewModel.EnsureDeletionScopeAsync())
                    {
                        return DialogMutationResult.Failure(
                            JitHub.WinUI.Helpers.LocalizedResourceText.GetString(
                                "RepoManage/DeletePermissionRequired",
                                "Repository deletion permission is required before JitHub can continue."));
                    }

                    deletionResult = await ViewModel.DeleteSelectedAsync(repositories);
                    return DialogMutationResult.Success();
                }
                catch (Exception ex)
                {
                    ViewModel.ShowUnexpectedError(ex);
                    return DialogMutationResult.Failure(ViewModel.StatusText);
                }
            },
            errorText);

        if (dialogResult == ContentDialogResult.Primary && deletionResult?.HasFailures == true)
        {
            await ShowDeleteFailuresAsync(deletionResult.Failures);
        }
    }

    private ContentDialog CreateDeleteConfirmation(
        IReadOnlyList<RepositoryLibraryViewItem> repositories)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.DeleteDialogTitle,
            Content = new TextBlock
            {
                Text = ViewModel.FormatDeleteDialogContent(repositories),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = ViewModel.DeleteDialogConfirmButtonText,
            CloseButtonText = ViewModel.DeleteDialogCloseButtonText,
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AppDestructiveButtonStyle"]
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepositoryDeleteConfirmation");
        AutomationProperties.SetName(dialog, ViewModel.DeleteDialogTitle);

        return dialog;
    }

    private async Task ShowDeleteFailuresAsync(IReadOnlyList<string> failures)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.DeleteFailureDialogTitle,
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, failures),
                    TextWrapping = TextWrapping.Wrap
                }
            },
            CloseButtonText = ViewModel.CloseButtonText,
            DefaultButton = ContentDialogButton.Close
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepositoryDeleteFailures");
        AutomationProperties.SetName(dialog, ViewModel.DeleteFailureDialogTitle);

        await AppContentDialogPresenter.ShowAsync(dialog, XamlRoot);
    }

    private static RepositoryLibraryViewItem? GetCommandRepository(object sender) =>
        sender is MenuFlyoutItem { CommandParameter: RepositoryLibraryViewItem item } ? item : null;

    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ViewModel.ShowUnexpectedError(ex);
        }
    }

    private static bool IsRepositoryLibraryAutomationScenario() =>
        AppDataPathPolicy.TryGetAutomationRoots(out _, out _) &&
        string.Equals(
            Program.CurrentLaunchOptions.Scenario,
            "repository-library",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GitHubRepository> CreateAutomationRepositories()
    {
        string[] languages = ["C#", "Rust", "TypeScript", "Python", "Kotlin"];
        List<GitHubRepository> repositories = new(135);
        for (int index = 0; index < 135; index++)
        {
            bool isFork = index % 7 == 0;
            bool isArchived = index % 13 == 0;
            bool isPrivate = index % 5 == 0;
            string name = index == 0 ? "JitHubV2" : $"repository-{index + 1:000}";
            repositories.Add(new GitHubRepository
            {
                Id = 900_000 + index,
                Name = name,
                FullName = $"JitHubApp/{name}",
                Description = index == 0
                    ? "A native Windows GitHub client built with WinUI."
                    : $"Automation repository {index + 1} for responsive library verification.",
                DefaultBranch = "main",
                HtmlUrl = $"https://github.com/JitHubApp/{name}",
                Private = isPrivate,
                Fork = isFork,
                Archived = isArchived,
                StargazersCount = 250 - index,
                Language = languages[index % languages.Length],
                Topics = index % 2 == 0 ? ["winui", "desktop"] : ["developer-tools"],
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-index),
                Visibility = isPrivate ? "private" : "public",
                Permissions = new GitHubRepositoryPermissions
                {
                    Admin = index % 4 == 0,
                    Maintain = index % 4 <= 1,
                    Push = index % 3 != 0,
                    Pull = true
                },
                Owner = new GitHubRepositoryOwner { Login = "JitHubApp" }
            });
        }

        return repositories;
    }
}
