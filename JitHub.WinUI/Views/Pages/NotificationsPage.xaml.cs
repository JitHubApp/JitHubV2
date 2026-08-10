using System;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class NotificationsPage : Page
{
    private bool _initialized;
    private string? _pointerOpenedNotificationKey;

    public NotificationsPageViewModel ViewModel { get; }

    public NotificationsPage()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<NotificationsPageViewModel>();
        InitializeComponent();
        NotificationFilterSegmented.SelectedItem = UnreadFilterItem;
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pointerOpenedNotificationKey = null;
        ApplyResponsiveLayout(ActualWidth);
        if (_initialized)
        {
            CommitPerformanceReadiness();
            return;
        }

        _initialized = true;
        try
        {
            await ViewModel.InitializeAsync();
            CommitPerformanceReadiness();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize notifications: {ex}");
        }
    }

    private void CommitPerformanceReadiness() =>
        ProductPerformanceReadiness.CommitRoute(
            "notifications",
            ProductPerformanceReadiness.CountIdentity(ViewModel.Notifications.Count));

    private void NotificationsRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double availableWidth)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(
            availableWidth,
            WorkspaceChromeContracts.Notifications);
        WorkspaceChromeVisuals.ApplyRoot(NotificationsRoot, chrome);
        WorkspaceChromeVisuals.ApplyHeader(NotificationsHeaderGrid, chrome);
        WorkspaceChromeVisuals.ApplyOptionalContext(NotificationsResultScope, chrome);
        WorkspaceChromeVisuals.ApplyActionLabel(NotificationsMarkAllReadText, chrome);
        WorkspaceChromeVisuals.ApplyActionButton(
            NotificationsMarkAllReadButton,
            chrome,
            hasVisibleLabel: chrome.ShowActionLabels);

        WorkspaceChromeVisuals.ApplyPlacement(
            NotificationSearchHost,
            chrome,
            new WorkspaceElementPlacement(0, 0, 1, StretchHorizontally: true),
            new WorkspaceElementPlacement(0, 0, 2, StretchHorizontally: true));
        WorkspaceChromeVisuals.ApplyPlacement(
            NotificationFilterSegmented,
            chrome,
            new WorkspaceElementPlacement(0, 1, 1),
            new WorkspaceElementPlacement(1, 0, 2, StretchHorizontally: true));
        NotificationFilterSegmented.MinWidth = chrome.StackCommandRows ? 0 : 286;
    }

    private async void NotificationFilterSegmented_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        NotificationListFilter filter = ReferenceEquals(sender.SelectedItem, AllFilterItem)
            ? NotificationListFilter.All
            : ReferenceEquals(sender.SelectedItem, ParticipatingFilterItem)
                ? NotificationListFilter.Participating
                : NotificationListFilter.Unread;
        await ViewModel.ChangeFilterAsync(filter);
    }

    private void NotificationsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NotificationViewItem item)
        {
            if (string.Equals(_pointerOpenedNotificationKey, item.StableKey, StringComparison.Ordinal))
            {
                return;
            }

            OpenNotificationItem(item);
        }
    }

    private void NotificationsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem recycledContainer && args.InRecycleQueue)
        {
            recycledContainer.PointerEntered -= NotificationRow_PointerEntered;
            recycledContainer.PointerExited -= NotificationRow_PointerExited;
            recycledContainer.RemoveHandler(
                PointerPressedEvent,
                new PointerEventHandler(NotificationRow_PointerPressed));
            return;
        }

        if (args.ItemContainer is ListViewItem container && args.Item is NotificationViewItem item)
        {
            AutomationProperties.SetAutomationId(container, $"NotificationRow_{item.StableKey}");
            AutomationProperties.SetName(container, item.AutomationName);
            container.PointerEntered -= NotificationRow_PointerEntered;
            container.PointerEntered += NotificationRow_PointerEntered;
            container.PointerExited -= NotificationRow_PointerExited;
            container.PointerExited += NotificationRow_PointerExited;
            container.RemoveHandler(
                PointerPressedEvent,
                new PointerEventHandler(NotificationRow_PointerPressed));
            container.AddHandler(
                PointerPressedEvent,
                new PointerEventHandler(NotificationRow_PointerPressed),
                handledEventsToo: true);
        }

        if (ViewModel.HasMore && !ViewModel.IsLoadingMore && args.ItemIndex >= ViewModel.Notifications.Count - 10)
        {
            _ = ViewModel.LoadMoreCommand.ExecuteAsync(null);
        }
    }

    private void NotificationRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewItem { DataContext: NotificationViewItem item })
        {
            _ = ViewModel.PrefetchDestinationAsync(item);
        }
    }

    private void NotificationRow_PointerExited(object sender, PointerRoutedEventArgs e) =>
        ViewModel.CancelDestinationPrefetch();

    private void NotificationRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewItem { DataContext: NotificationViewItem item } container ||
            e.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            FindAncestorButton(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        // SendInput callers may enqueue down/up together. WinUI still raises this
        // PointerPressed event for the left edge, but the current point can already
        // reflect its matching release by the time the routed handler runs.
        PointerUpdateKind updateKind = e.GetCurrentPoint(container).Properties.PointerUpdateKind;
        if (updateKind is not PointerUpdateKind.LeftButtonPressed and not PointerUpdateKind.LeftButtonReleased)
        {
            return;
        }

        _pointerOpenedNotificationKey = item.StableKey;
        e.Handled = true;
        OpenNotificationItem(item);
    }

    private void OpenNotificationItem(NotificationViewItem item)
    {
        // The destination owns its authoritative post-frame load. Cancel a pending
        // incidental-hover prediction; a prediction that already crossed the dwell
        // threshold is intentionally allowed to finish in the Phase 0 cache.
        ViewModel.CancelDestinationPrefetch();
        string expectedDestinationRoute = item.Thread.Subject.Type switch
        {
            "Issue" => "repo_issues",
            "PullRequest" => "repo_pull_requests",
            "Commit" => "repo_commits",
            _ => "repo_code"
        };
        ProductPerformanceReadiness.BeginTraversal(
            "notifications",
            $"NotificationRow_{item.StableKey}",
            expectedDestinationRoute);
        item.OpenCommand?.Execute(null);
    }

    private static Button? FindAncestorButton(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Button button)
            {
                return button;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
