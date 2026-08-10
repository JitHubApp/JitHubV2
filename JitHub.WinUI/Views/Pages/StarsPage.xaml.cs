using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class StarsPage : Page
{
    private static readonly string[] CategoryColors = ["#74BEA7", "#5B9BD5", "#A77BD8", "#E06C75", "#D19A66", "#E5C07B"];
    private const double RepositoryDragThreshold = 6;
    private bool _initialized;
    private bool _isCompact;
    private bool _responsiveModeInitialized;
    private bool _categoryDrawerRequestedOpen = true;
    private CategoryDrawerTransition _categoryDrawerTransition;
    private bool _loadingMore;
    private StarUndoState? _undoState;
    private bool _synchronizingSelection;
    private bool _isRepositoryPointerDown;
    private bool _isRepositoryDragActive;
    private Point _repositoryDragStart;
    private FrameworkElement? _repositoryDragSource;
    private StarNavigationItem? _repositoryDragTarget;
    private IReadOnlyList<StarRepositoryViewItem> _draggedRepositories = [];

    public StarLibraryPageViewModel ViewModel { get; }

    public StarsPage()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<StarLibraryPageViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            try
            {
                await ViewModel.InitializeAsync();
                _ = ViewModel.PrefetchLikelyRepositoriesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize Stars: {ex}");
                ShowStatus(L("Stars/Status/OpenFailed", "The Stars library could not be opened."), InfoBarSeverity.Error, canUndo: false);
            }
        }

        ProductPerformanceReadiness.CommitRoute(
            "stars",
            ProductPerformanceReadiness.CountIdentity(ViewModel.Repositories.Count));

        _ = DispatcherQueue.TryEnqueue(RestoreWorkspaceState);
    }

    private void RestoreWorkspaceState()
    {
        if (ViewModel.SelectedRepositoryIds.Count > 0)
        {
            HashSet<long> selectedRepositoryIds = ViewModel.SelectedRepositoryIds.ToHashSet();
            RepositoriesList.SelectionMode = ListViewSelectionMode.Multiple;
            SetRepositorySelectionModeVisuals(isVisible: true);
            foreach (StarRepositoryViewItem item in ViewModel.Repositories.Where(item => selectedRepositoryIds.Contains(item.Repository.Id)))
            {
                RepositoriesList.SelectedItems.Add(item);
            }
        }

        if (ViewModel.ListScrollOffset > 0 && FindDescendant<ScrollViewer>(RepositoriesList) is ScrollViewer scrollViewer)
        {
            scrollViewer.ChangeView(null, ViewModel.ListScrollOffset, null, disableAnimation: true);
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(
            e.NewSize.Width,
            WorkspaceChromeContracts.Stars);
        WorkspaceChromeVisuals.ApplyRoot(StarsContentRoot, chrome);
        WorkspaceChromeVisuals.ApplyHeader(StarsHeaderGrid, chrome);
        bool compact = chrome.Mode != WorkspaceChromeMode.Wide;
        bool modeChanged = !_responsiveModeInitialized || compact != _isCompact;
        _responsiveModeInitialized = true;
        _isCompact = compact;

        if (modeChanged)
        {
            _categoryDrawerRequestedOpen = !compact;
            _categoryDrawerTransition = CategoryDrawerTransition.None;
        }

        CategorySplitView.DisplayMode = compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        if (compact)
        {
            ApplyRequestedCategoryDrawerState();
        }
        else
        {
            CategorySplitView.IsPaneOpen = true;
        }
        OpenCategoriesButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CloseCategoriesButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceChromeVisuals.ApplyOptionalContext(StarsResultCount, chrome);
        WorkspaceChromeVisuals.ApplyActionLabel(StarsFilterButtonText, chrome);
        WorkspaceChromeVisuals.ApplyActionButton(
            StarsFilterButton,
            chrome,
            hasVisibleLabel: chrome.ShowActionLabels);

        WorkspaceChromeVisuals.ApplyPlacement(
            StarsSearchHost,
            chrome,
            new WorkspaceElementPlacement(0, 0, 1, StretchHorizontally: true),
            new WorkspaceElementPlacement(0, 0, 4, StretchHorizontally: true));
        WorkspaceChromeVisuals.ApplyPlacement(
            StarsSortComboBox,
            chrome,
            new WorkspaceElementPlacement(0, 1, 1, StretchHorizontally: true),
            new WorkspaceElementPlacement(1, 0, 2, StretchHorizontally: true));
        StarsSortComboBox.MinWidth = compact ? 0 : 158;
        WorkspaceChromeVisuals.ApplyPlacement(
            StarsFilterButton,
            chrome,
            new WorkspaceElementPlacement(0, 2, 1),
            new WorkspaceElementPlacement(1, 2, 1));
        WorkspaceChromeVisuals.ApplyPlacement(
            SelectionModeButton,
            chrome,
            new WorkspaceElementPlacement(0, 3, 1),
            new WorkspaceElementPlacement(1, 3, 1));
    }

    private void OpenCategoriesButton_Click(object sender, RoutedEventArgs e)
    {
        _categoryDrawerRequestedOpen = true;
        ApplyRequestedCategoryDrawerState();
    }

    private void CloseCategoriesButton_Click(object sender, RoutedEventArgs e)
    {
        _categoryDrawerRequestedOpen = false;
        ApplyRequestedCategoryDrawerState();
    }

    private void CategoryNavigationList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container || args.Item is not StarNavigationItem item)
        {
            return;
        }

        AutomationProperties.SetName(container, item.Title);
        AutomationProperties.SetAutomationId(container, item.AutomationId);
    }

    private void CategorySplitView_PaneClosed(SplitView sender, object args)
    {
        if (!_isCompact)
        {
            return;
        }

        _categoryDrawerTransition = CategoryDrawerTransition.None;
        if (!_categoryDrawerRequestedOpen)
        {
            OpenCategoriesButton.Focus(FocusState.Programmatic);
        }

        ApplyRequestedCategoryDrawerState();
    }

    private void CategorySplitView_PaneOpened(SplitView sender, object args)
    {
        if (!_isCompact)
        {
            return;
        }

        _categoryDrawerTransition = CategoryDrawerTransition.None;
        ApplyRequestedCategoryDrawerState();
    }

    private void CategorySplitView_PaneClosing(SplitView sender, SplitViewPaneClosingEventArgs args)
    {
        if (!_isCompact || _categoryDrawerTransition != CategoryDrawerTransition.None)
        {
            return;
        }

        // A close not initiated by ApplyRequestedCategoryDrawerState is the
        // SplitView light-dismiss gesture.
        _categoryDrawerRequestedOpen = false;
        _categoryDrawerTransition = CategoryDrawerTransition.Closing;
    }

    private void ApplyRequestedCategoryDrawerState()
    {
        if (!_isCompact || _categoryDrawerTransition != CategoryDrawerTransition.None)
        {
            return;
        }

        if (_categoryDrawerRequestedOpen == CategorySplitView.IsPaneOpen)
        {
            return;
        }

        _categoryDrawerTransition = _categoryDrawerRequestedOpen
            ? CategoryDrawerTransition.Opening
            : CategoryDrawerTransition.Closing;
        CategorySplitView.IsPaneOpen = _categoryDrawerRequestedOpen;
    }

    private enum CategoryDrawerTransition
    {
        None,
        Opening,
        Closing
    }

    private void RepositoriesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (RepositoriesList.SelectionMode == ListViewSelectionMode.None)
        {
            if (e.ClickedItem is StarRepositoryViewItem item)
            {
                ProductPerformanceReadiness.BeginTraversal("stars", item.AutomationId, "repo_code");
                ViewModel.OpenRepository(item);
            }
        }
    }

    private async void RepositoriesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem container && args.Item is StarRepositoryViewItem item)
        {
            AutomationProperties.SetAutomationId(container, item.AutomationId);
            AutomationProperties.SetName(container, item.AutomationName);
            item.IsSelectionModeVisible = RepositoriesList.SelectionMode == ListViewSelectionMode.Multiple;
            item.IsSelected = RepositoriesList.SelectedItems.Contains(item);
        }

        if (_loadingMore || !ViewModel.HasMore || args.ItemIndex < ViewModel.Repositories.Count - 12)
        {
            return;
        }

        _loadingMore = true;
        try
        {
            await ViewModel.LoadMoreAsync();
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void SelectionModeButton_Click(object sender, RoutedEventArgs e)
    {
        bool enableSelection = RepositoriesList.SelectionMode == ListViewSelectionMode.None;
        RepositoriesList.SelectionMode = enableSelection
            ? ListViewSelectionMode.Multiple
            : ListViewSelectionMode.None;
        SetRepositorySelectionModeVisuals(enableSelection);
        if (enableSelection)
        {
            RepositoriesList.Focus(FocusState.Programmatic);
        }
        else
        {
            ViewModel.SetSelection([]);
        }
    }

    private void RepositoriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection)
        {
            return;
        }

        IReadOnlyList<StarRepositoryViewItem> selected = GetSelectedRepositories();
        HashSet<StarRepositoryViewItem> selectedSet = selected.ToHashSet();
        _synchronizingSelection = true;
        try
        {
            foreach (StarRepositoryViewItem item in ViewModel.Repositories)
            {
                item.IsSelected = selectedSet.Contains(item);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }

        ViewModel.SetSelection(selected);
    }

    private void RepositorySelectionCheckBox_Checked(object sender, RoutedEventArgs e) =>
        SynchronizeSelectionFromCheckBox(sender as CheckBox, isSelected: true);

    private void RepositorySelectionCheckBox_Unchecked(object sender, RoutedEventArgs e) =>
        SynchronizeSelectionFromCheckBox(sender as CheckBox, isSelected: false);

    private void SynchronizeSelectionFromCheckBox(CheckBox? checkBox, bool isSelected)
    {
        if (_synchronizingSelection ||
            RepositoriesList.SelectionMode != ListViewSelectionMode.Multiple ||
            checkBox?.DataContext is not StarRepositoryViewItem item)
        {
            return;
        }

        _synchronizingSelection = true;
        try
        {
            if (isSelected && !RepositoriesList.SelectedItems.Contains(item))
            {
                RepositoriesList.SelectedItems.Add(item);
            }
            else if (!isSelected && RepositoriesList.SelectedItems.Contains(item))
            {
                RepositoriesList.SelectedItems.Remove(item);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }

        ViewModel.SetSelection(GetSelectedRepositories());
    }

    private void SetRepositorySelectionModeVisuals(bool isVisible)
    {
        HashSet<StarRepositoryViewItem> selected = isVisible
            ? GetSelectedRepositories().ToHashSet()
            : [];
        _synchronizingSelection = true;
        try
        {
            foreach (StarRepositoryViewItem item in ViewModel.Repositories)
            {
                item.IsSelectionModeVisible = isVisible;
                item.IsSelected = isVisible && selected.Contains(item);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void CancelSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        RepositoriesList.SelectionMode = ListViewSelectionMode.None;
        SetRepositorySelectionModeVisuals(isVisible: false);
        ViewModel.SetSelection([]);
    }

    private async void BulkUnstarButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StarRepositoryViewItem> selected = GetSelectedRepositories();
        if (selected.Count == 0)
        {
            return;
        }

        ContentDialog dialog = CreateDialog(
            L("Stars/Dialogs/BulkUnstar/Title", "Unstar repositories?"),
            LF("Stars/Dialogs/BulkUnstar/BodyFormat", "This will remove {0:N0} repositories from your GitHub stars and from every local category.", selected.Count),
            L("Common/Unstar", "Unstar"),
            L("Common/Cancel", "Cancel"));
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AppDestructiveButtonStyle"];
        dialog.DefaultButton = ContentDialogButton.Close;
        TextBlock errorText = AttachInlineError(dialog, "StarsBulkUnstarDialogError");
        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                try
                {
                    await ViewModel.UnstarManyAsync(selected);
                    CancelSelectionButton_Click(sender, e);
                    ShowStatus(LF("Stars/Status/BulkUnstarredFormat", "Unstarred {0:N0} repositories.", selected.Count), InfoBarSeverity.Success, canUndo: false);
                    return DialogMutationResult.Success();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Bulk unstar failed: {ex}");
                    return DialogMutationResult.Failure(L("Stars/Status/BulkUnstarFailed", "The selected repositories could not be unstarred."));
                }
            },
            errorText);
    }

    private async void BulkCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StarRepositoryViewItem> selected = GetSelectedRepositories();
        StarCategoryViewItem? category = await ChooseCategoryAsync(L("Stars/Dialogs/ChooseCategory/BulkTitle", "Add selected repositories"));
        if (category is null || selected.Count == 0)
        {
            return;
        }

        await ViewModel.AddToCategoryAsync(category.Id, selected);
        ShowStatus(LF("Stars/Status/BulkAddedToCategoryFormat", "Added {0:N0} repositories to {1}.", selected.Count, category.Name), InfoBarSeverity.Success, canUndo: false);
    }

    private void RepositoryDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StarRepositoryViewItem draggedItem } source ||
            !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
        {
            return;
        }

        IReadOnlyList<StarRepositoryViewItem> selectedItems = GetSelectedRepositories();
        _draggedRepositories = selectedItems.Any(
            selected => selected.Repository.Id == draggedItem.Repository.Id)
                ? selectedItems
                : [draggedItem];
        _repositoryDragSource = source;
        _repositoryDragStart = e.GetCurrentPoint(RootGrid).Position;
        _isRepositoryPointerDown = source.CapturePointer(e.Pointer);
        _isRepositoryDragActive = false;
        e.Handled = _isRepositoryPointerDown;
    }

    private void RepositoryDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isRepositoryPointerDown || _repositoryDragSource is null)
        {
            return;
        }

        Point position = e.GetCurrentPoint(RootGrid).Position;
        if (!_isRepositoryDragActive)
        {
            double deltaX = position.X - _repositoryDragStart.X;
            double deltaY = position.Y - _repositoryDragStart.Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) < RepositoryDragThreshold * RepositoryDragThreshold)
            {
                return;
            }

            _isRepositoryDragActive = true;
            AutomationProperties.SetItemStatus(RepositoriesList, "drag-started");
        }

        SetRepositoryDragTarget(FindCategoryAt(position));
        e.Handled = true;
    }

    private async void RepositoryDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isRepositoryPointerDown)
        {
            return;
        }

        Point position = e.GetCurrentPoint(RootGrid).Position;
        if (_isRepositoryDragActive)
        {
            SetRepositoryDragTarget(FindCategoryAt(position));
        }

        StarNavigationItem? target = _repositoryDragTarget;
        IReadOnlyList<StarRepositoryViewItem> repositories = _draggedRepositories;
        bool shouldAssign = _isRepositoryDragActive && target?.Category is not null && repositories.Count > 0;
        ResetRepositoryDrag();
        e.Handled = true;
        if (shouldAssign)
        {
            AutomationProperties.SetItemStatus(RepositoriesList, "drop-received");
            await AssignDraggedRepositoriesAsync(target!, repositories);
        }
    }

    private void RepositoryDragHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isRepositoryPointerDown)
        {
            ResetRepositoryDrag();
            e.Handled = true;
        }
    }

    private void RepositoryDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isRepositoryPointerDown)
        {
            ResetRepositoryDrag();
        }
    }

    private StarNavigationItem? FindCategoryAt(Point position)
    {
        foreach (StarNavigationItem item in ViewModel.NavigationItems.Where(static item => item.Category is not null))
        {
            if (CategoryNavigationList.ContainerFromItem(item) is not FrameworkElement container)
            {
                continue;
            }

            Rect bounds = container.TransformToVisual(RootGrid).TransformBounds(
                new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Contains(position))
            {
                return item;
            }
        }

        return null;
    }

    private void SetRepositoryDragTarget(StarNavigationItem? target)
    {
        if (ReferenceEquals(_repositoryDragTarget, target))
        {
            return;
        }

        foreach (StarNavigationItem item in ViewModel.NavigationItems)
        {
            item.IsDropTarget = ReferenceEquals(item, target);
        }

        _repositoryDragTarget = target;
        AutomationProperties.SetItemStatus(
            RepositoriesList,
            target is null ? "drag-started" : "drag-over-category");
    }

    private void ResetRepositoryDrag()
    {
        FrameworkElement? source = _repositoryDragSource;
        _isRepositoryPointerDown = false;
        _isRepositoryDragActive = false;
        SetRepositoryDragTarget(null);
        _repositoryDragSource = null;
        _draggedRepositories = [];
        source?.ReleasePointerCaptures();
    }

    private async Task AssignDraggedRepositoriesAsync(
        StarNavigationItem target,
        IReadOnlyList<StarRepositoryViewItem> repositories)
    {
        try
        {
            await ViewModel.AddToCategoryAsync(target.Category!.Id, repositories);
            AutomationProperties.SetItemStatus(RepositoriesList, "drop-completed");
            ShowStatus(LF("Stars/Status/AddedToCategoryFormat", "Added to {0}.", target.Category.Name), InfoBarSeverity.Success, canUndo: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Assign Stars category failed: {ex}");
            AutomationProperties.SetItemStatus(RepositoriesList, "drop-failed");
            ShowStatus(L("Stars/Status/AddToCategoryFailed", "The repositories could not be added to the category."), InfoBarSeverity.Error, canUndo: false);
        }
    }

    private async void NewCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        StarCategory? createdCategory = null;
        bool saved = await EditCategoryDialogAsync(
            L("Stars/Dialogs/Category/CreateTitle", "New category"),
            existing: null,
            async (name, color) => createdCategory = await ViewModel.CreateCategoryAsync(name, color),
            L("Stars/Status/CategoryCreateFailed", "The category could not be created."));
        if (!saved || createdCategory is null)
        {
            return;
        }

        ViewModel.SelectCategory(createdCategory.Id);
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                CategoryNavigationList.UpdateLayout();
                if (ViewModel.SelectedNavigationItem is not null)
                {
                    CategoryNavigationList.ScrollIntoView(ViewModel.SelectedNavigationItem);
                }
            });
        ShowStatus(
            LF("Stars/Status/CategoryCreatedFormat", "Created {0}.", createdCategory.Name),
            InfoBarSeverity.Success,
            canUndo: false);
    }

    private void CategoryMenuButton_Click(object sender, RoutedEventArgs e)
    {
        StarCategoryViewItem? category = ViewModel.SelectedNavigationItem?.Category;
        if (category is null || sender is not FrameworkElement anchor)
        {
            return;
        }

        MenuFlyout menu = new();
        Func<Task>? deferredDialogAction = null;
        MenuFlyoutItem rename = new() { Text = L("Stars/CategoryActions/Rename", "Rename"), Icon = new FontIcon { Glyph = "\uE70F" } };
        AutomationProperties.SetAutomationId(rename, "StarsCategoryActionRename");
        AutomationProperties.SetName(rename, L("Stars/CategoryActions/RenameAutomationName", "Rename category"));
        rename.Click += (_, _) => deferredDialogAction = () => RenameCategoryAsync(category);
        MenuFlyoutItem moveUp = new() { Text = L("Stars/CategoryActions/MoveUp", "Move up"), Icon = new FontIcon { Glyph = "\uE74A" }, IsEnabled = category.Position > 0 };
        AutomationProperties.SetAutomationId(moveUp, "StarsCategoryActionMoveUp");
        AutomationProperties.SetName(moveUp, L("Stars/CategoryActions/MoveUpAutomationName", "Move category up"));
        moveUp.Click += async (_, _) => await ViewModel.MoveCategoryAsync(category, -1);
        MenuFlyoutItem moveDown = new() { Text = L("Stars/CategoryActions/MoveDown", "Move down"), Icon = new FontIcon { Glyph = "\uE74B" }, IsEnabled = category.Position < ViewModel.CustomCategories.Count - 1 };
        AutomationProperties.SetAutomationId(moveDown, "StarsCategoryActionMoveDown");
        AutomationProperties.SetName(moveDown, L("Stars/CategoryActions/MoveDownAutomationName", "Move category down"));
        moveDown.Click += async (_, _) => await ViewModel.MoveCategoryAsync(category, 1);
        MenuFlyoutItem delete = new() { Text = L("Stars/CategoryActions/Delete", "Delete category"), Icon = new FontIcon { Glyph = "\uE74D" } };
        AutomationProperties.SetAutomationId(delete, "StarsCategoryActionDelete");
        AutomationProperties.SetName(delete, L("Stars/CategoryActions/DeleteAutomationName", "Delete category"));
        delete.Click += (_, _) => deferredDialogAction = () => DeleteCategoryAsync(category);
        menu.Items.Add(rename);
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(delete);
        menu.Closed += async (_, _) =>
        {
            if (deferredDialogAction is not null)
            {
                await deferredDialogAction();
            }
        };
        menu.ShowAt(anchor);
    }

    private async Task RenameCategoryAsync(StarCategoryViewItem category)
    {
        string? savedName = null;
        bool saved = await EditCategoryDialogAsync(
            L("Stars/Dialogs/Category/EditTitle", "Edit category"),
            category,
            async (name, color) =>
            {
                await ViewModel.UpdateCategoryAsync(category, name, color);
                savedName = name;
            },
            L("Stars/Status/CategoryRenameFailed", "The category could not be renamed."));
        if (!saved || string.IsNullOrWhiteSpace(savedName))
        {
            return;
        }

        ShowStatus(
            LF("Stars/Status/CategoryRenamedFormat", "Renamed category to {0}.", savedName),
            InfoBarSeverity.Success,
            canUndo: false);
    }

    private async Task DeleteCategoryAsync(StarCategoryViewItem category)
    {
        ContentDialog dialog = CreateDialog(
            L("Stars/Dialogs/Category/DeleteTitle", "Delete category?"),
            LF("Stars/Dialogs/Category/DeleteBodyFormat", "Delete {0}? Repositories stay starred and remain in the library.", category.Name),
            L("Common/Delete", "Delete"),
            L("Common/Cancel", "Cancel"));
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AppDestructiveButtonStyle"];
        dialog.DefaultButton = ContentDialogButton.Close;
        TextBlock errorText = AttachInlineError(dialog, "StarsDeleteCategoryDialogError");
        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                try
                {
                    await ViewModel.DeleteCategoryAsync(category);
                    ShowStatus(LF("Stars/Status/CategoryDeletedFormat", "Deleted {0}.", category.Name), InfoBarSeverity.Success, canUndo: false);
                    return DialogMutationResult.Success();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Delete Stars category failed: {ex}");
                    return DialogMutationResult.Failure(L("Stars/Status/CategoryDeleteFailed", "The category could not be deleted."));
                }
            },
            errorText);
    }

    private async Task<bool> EditCategoryDialogAsync(
        string title,
        StarCategoryViewItem? existing,
        Func<string, string, Task> mutateAsync,
        string mutationFailureMessage)
    {
        TextBox name = new()
        {
            Header = L("Stars/Dialogs/Category/NameHeader", "Name"),
            Text = existing?.Name ?? string.Empty,
            PlaceholderText = L("Stars/Dialogs/Category/NamePlaceholder", "For example, Windows tooling"),
            MaxLength = 80
        };
        AutomationProperties.SetAutomationId(name, "StarsCategoryNameBox");
        AutomationProperties.SetName(name, L("Stars/Dialogs/Category/NameAutomationName", "Category name"));
        ComboBox color = new()
        {
            Header = L("Stars/Dialogs/Category/ColorHeader", "Color"),
            ItemsSource = CategoryColors,
            SelectedItem = existing?.Color is { Length: > 0 } current && CategoryColors.Contains(current, StringComparer.OrdinalIgnoreCase)
                ? CategoryColors.First(value => string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
                : CategoryColors[0]
        };
        AutomationProperties.SetAutomationId(color, "StarsCategoryColorPicker");
        AutomationProperties.SetName(color, L("Stars/Dialogs/Category/ColorAutomationName", "Category color"));
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(name);
        content.Children.Add(color);
        ContentDialog dialog = CreateDialog(
            title,
            content,
            existing is null ? L("Common/Create", "Create") : L("Common/Save", "Save"),
            L("Common/Cancel", "Cancel"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("StarsCategoryDialogError");
        content.Children.Add(errorText);
        AutomationProperties.SetAutomationId(dialog, existing is null ? "StarsCreateCategoryDialog" : "StarsEditCategoryDialog");
        AutomationProperties.SetName(dialog, title);
        return await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text))
                {
                    name.Focus(FocusState.Programmatic);
                    return DialogMutationResult.Failure(
                        L("Stars/Dialogs/Category/NameRequired", "Enter a category name."));
                }

                string normalizedName = name.Text.Trim();
                string normalizedColor = color.SelectedItem as string ?? CategoryColors[0];
                try
                {
                    await mutateAsync(normalizedName, normalizedColor);
                    return DialogMutationResult.Success();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Stars category mutation failed: {ex}");
                    return DialogMutationResult.Failure(mutationFailureMessage);
                }
            },
            errorText) == ContentDialogResult.Primary;
    }

    private async Task<StarCategoryViewItem?> ChooseCategoryAsync(string title)
    {
        if (ViewModel.CustomCategories.Count == 0)
        {
            ContentDialog empty = CreateDialog(
                L("Stars/Dialogs/ChooseCategory/EmptyTitle", "No categories yet"),
                L("Stars/Dialogs/ChooseCategory/EmptyBody", "Create a category first, then assign repositories to it."),
                L("Common/OK", "OK"),
                string.Empty);
            await AppContentDialogPresenter.ShowAsync(empty, XamlRoot);
            return null;
        }

        ListView list = new()
        {
            ItemsSource = ViewModel.CustomCategories,
            DisplayMemberPath = nameof(StarCategoryViewItem.Name),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 360
        };
        AutomationProperties.SetAutomationId(list, "StarsCategoryPickerList");
        AutomationProperties.SetName(list, L("Stars/Dialogs/ChooseCategory/ListAutomationName", "Stars categories"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("StarsCategoryPickerDialogError");
        ContentDialog dialog = CreateDialog(
            title,
            new StackPanel { Spacing = 8, Children = { list, errorText } },
            L("Common/Add", "Add"),
            L("Common/Cancel", "Cancel"));
        return await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            () => Task.FromResult(list.SelectedItem is null
                ? DialogMutationResult.Failure(L("Stars/Dialogs/ChooseCategory/SelectionRequired", "Select a category."))
                : DialogMutationResult.Success()),
            errorText) == ContentDialogResult.Primary
            ? list.SelectedItem as StarCategoryViewItem
            : null;
    }

    private void RepositoryRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row)
        {
            SetRepositoryRowHoverState(row, isVisible: true);
            if (row.DataContext is StarRepositoryViewItem item)
            {
                _ = ViewModel.PrefetchRepositoryAsync(item);
            }
        }
    }

    private void RepositoryRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row)
        {
            SetRepositoryRowHoverState(row, isVisible: false);
        }
    }

    private static void SetRepositoryRowHoverState(FrameworkElement row, bool isVisible)
    {
        if (row.FindName("RepositoryRowMetadata") is UIElement metadata)
        {
            metadata.Opacity = isVisible ? 0 : 1;
        }

        if (row.FindName("RepositoryHoverActions") is StackPanel actions)
        {
            actions.Opacity = isVisible ? 1 : 0;
            actions.IsHitTestVisible = isVisible;
        }
    }

    private async void RepositoryHoverUnstarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StarRepositoryViewItem item })
        {
            await UnstarOneAsync(item);
        }
    }

    private void RepositoryHoverMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StarRepositoryViewItem item } button)
        {
            ShowRepositoryContextMenu(
                button,
                item,
                new Windows.Foundation.Point(button.ActualWidth, button.ActualHeight));
        }
    }

    private void RepositoriesList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        StarRepositoryViewItem? item = FindDataContext<StarRepositoryViewItem>(e.OriginalSource as DependencyObject);
        if (item is null || RepositoriesList.ContainerFromItem(item) is not FrameworkElement anchor)
        {
            return;
        }

        ShowRepositoryContextMenu(anchor, item, e.GetPosition(anchor));
        e.Handled = true;
    }

    private void ShowRepositoryContextMenu(FrameworkElement anchor, StarRepositoryViewItem item, Windows.Foundation.Point position)
    {
        MenuFlyout menu = new();
        menu.Items.Add(CreateMenuItem(L("Stars/RepositoryActions/Open", "Open repository"), "\uE8A7", () => ViewModel.OpenRepository(item)));
        menu.Items.Add(CreateMenuItem(L("Stars/RepositoryActions/OpenOwner", "Open owner profile"), "\uE77B", () => ViewModel.OpenOwner(item)));
        menu.Items.Add(CreateMenuItem(L("Stars/RepositoryActions/AddToCategory", "Add to category"), "\uE8EC", async () =>
        {
            StarCategoryViewItem? category = await ChooseCategoryAsync(L("Stars/RepositoryActions/AddToCategory", "Add to category"));
            if (category is not null)
            {
                await ViewModel.AddToCategoryAsync(category.Id, [item]);
            }
        }));
        if (ViewModel.SelectedNavigationItem?.Category is not null)
        {
            menu.Items.Add(CreateMenuItem(L("Stars/RepositoryActions/RemoveFromCategory", "Remove from current category"), "\uE711", async () => await ViewModel.RemoveFromCurrentCategoryAsync([item])));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem(L("Stars/RepositoryActions/CopyLink", "Copy repository link"), "\uE8C8", () => ViewModel.CopyRepositoryLink(item)));
        menu.Items.Add(CreateMenuItem(L("Common/Unstar", "Unstar"), "\uE735", async () => await UnstarOneAsync(item)));
        menu.ShowAt(anchor, position);
    }

    private async Task UnstarOneAsync(StarRepositoryViewItem item)
    {
        try
        {
            _undoState = await ViewModel.UnstarAsync(item);
            ShowStatus(LF("Stars/Status/UnstarredRepositoryFormat", "Unstarred {0}.", item.FullName), InfoBarSeverity.Success, canUndo: _undoState is not null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unstar failed: {ex}");
            ShowStatus(L("Stars/Status/UnstarFailed", "The repository could not be unstarred."), InfoBarSeverity.Error, canUndo: false);
        }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        StarUndoState? undo = _undoState;
        _undoState = null;
        try
        {
            await ViewModel.UndoUnstarAsync(undo);
            ShowStatus(L("Stars/Status/UndoSucceeded", "The star was restored."), InfoBarSeverity.Success, canUndo: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Undo unstar failed: {ex}");
            ShowStatus(L("Stars/Status/UndoFailed", "The star could not be restored."), InfoBarSeverity.Error, canUndo: false);
        }
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e) => ViewModel.ClearFilters();

    private IReadOnlyList<StarRepositoryViewItem> GetSelectedRepositories() =>
        RepositoriesList.SelectedItems.OfType<StarRepositoryViewItem>().ToArray();

    private void ShowStatus(string message, InfoBarSeverity severity, bool canUndo)
    {
        ActionInfoBar.Message = message;
        ActionInfoBar.Severity = severity;
        UndoButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        ActionInfoBar.IsOpen = true;
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);

    private ContentDialog CreateDialog(string title, string message, string primary, string close) =>
        CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, primary, close);

    private ContentDialog CreateDialog(string title, object content, string primary, string close)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "StarsDialog_" + new string(title.Where(char.IsLetterOrDigit).ToArray()));
        AutomationProperties.SetName(dialog, title);
        return dialog;
    }

    private static TextBlock AttachInlineError(ContentDialog dialog, string automationId)
    {
        TextBlock error = AppContentDialogPresenter.CreateInlineErrorPresenter(automationId);
        object? existingContent = dialog.Content;
        StackPanel panel = new() { Spacing = 12 };
        if (existingContent is UIElement element)
        {
            panel.Children.Add(element);
        }
        else if (existingContent is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = existingContent.ToString(),
                TextWrapping = TextWrapping.Wrap
            });
        }

        panel.Children.Add(error);
        dialog.Content = panel;
        return error;
    }

    private static MenuFlyoutItem CreateMenuItem(string text, string glyph, Action action)
    {
        MenuFlyoutItem item = new() { Text = text, Icon = new FontIcon { Glyph = glyph } };
        AutomationProperties.SetAutomationId(item, "StarsContext" + new string(text.Where(char.IsLetterOrDigit).ToArray()));
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuFlyoutItem CreateMenuItem(string text, string glyph, Func<Task> action)
    {
        MenuFlyoutItem item = new() { Text = text, Icon = new FontIcon { Glyph = glyph } };
        AutomationProperties.SetAutomationId(item, "StarsContext" + new string(text.Where(char.IsLetterOrDigit).ToArray()));
        AutomationProperties.SetName(item, text);
        item.Click += async (_, _) => await action();
        return item;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is T descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? source) where T : class
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is T value)
            {
                return value;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
