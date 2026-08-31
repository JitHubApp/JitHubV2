using System;
using System.Linq;
using System.Threading.Tasks;
using JitHub.Services.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.Common;

public sealed class ListViewScrollAnchor
{
    private readonly ScrollViewer? _scrollViewer;
    private readonly ListViewBase? _listView;
    private readonly Func<object, string?>? _itemKeySelector;
    private readonly string? _itemKey;
    private readonly double _itemViewportOffset;
    private readonly double _horizontalOffset;
    private readonly double _verticalOffset;
    private readonly bool _wasAtBottom;
    private readonly PointerEventHandler? _pointerInteractionHandler;
    private readonly KeyEventHandler? _keyInteractionHandler;
    private bool _handlersAttached;
    private bool _userInteracted;
    private bool _restoreFailureReported;

    private ListViewScrollAnchor(ListViewBase listView, ScrollViewer? scrollViewer, Func<object, string?>? itemKeySelector)
    {
        _listView = listView;
        _scrollViewer = scrollViewer;
        _itemKeySelector = itemKeySelector;
        if (scrollViewer is null)
        {
            return;
        }

        _horizontalOffset = scrollViewer.HorizontalOffset;
        _verticalOffset = scrollViewer.VerticalOffset;
        _wasAtBottom = ListViewScrollAnchorPolicy.IsAtScrollableBottom(
            scrollViewer.ScrollableHeight,
            scrollViewer.VerticalOffset);

        _pointerInteractionHandler = OnUserPointerInteraction;
        _keyInteractionHandler = OnUserKeyInteraction;
        scrollViewer.AddHandler(UIElement.PointerPressedEvent, _pointerInteractionHandler, handledEventsToo: true);
        scrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, _pointerInteractionHandler, handledEventsToo: true);
        listView.AddHandler(UIElement.KeyDownEvent, _keyInteractionHandler, handledEventsToo: true);
        _handlersAttached = true;

        if (itemKeySelector is not null && TryFindFirstVisibleContainer(listView, scrollViewer, out object? item, out FrameworkElement? container))
        {
            _itemKey = itemKeySelector(item!);
            _itemViewportOffset = GetViewportOffset(container!, scrollViewer);
        }
    }

    public static ListViewScrollAnchor Capture(ListViewBase listView)
    {
        ArgumentNullException.ThrowIfNull(listView);
        listView.ApplyTemplate();
        return new ListViewScrollAnchor(listView, FindDescendant<ScrollViewer>(listView), itemKeySelector: null);
    }

    public static ListViewScrollAnchor Capture(ListViewBase listView, Func<object, string?> itemKeySelector)
    {
        ArgumentNullException.ThrowIfNull(listView);
        ArgumentNullException.ThrowIfNull(itemKeySelector);
        listView.ApplyTemplate();
        return new ListViewScrollAnchor(listView, FindDescendant<ScrollViewer>(listView), itemKeySelector);
    }

    public void Restore()
    {
        if (_scrollViewer is null || !ListViewScrollAnchorPolicy.ShouldRestore(_userInteracted))
        {
            return;
        }

        try
        {
            if (_wasAtBottom)
            {
                _scrollViewer.ChangeView(_horizontalOffset, _scrollViewer.ScrollableHeight, null, disableAnimation: true);
                return;
            }

            if (TryRestoreKeyedAnchor())
            {
                return;
            }

            double targetVerticalOffset = Math.Min(_verticalOffset, _scrollViewer.ScrollableHeight);
            _scrollViewer.ChangeView(_horizontalOffset, targetVerticalOffset, null, disableAnimation: true);
        }
        catch (Exception exception)
        {
            if (!_restoreFailureReported)
            {
                _restoreFailureReported = true;
                JitHub.WinUI.App.LogHandledException(exception, "ui-list-view-scroll-anchor");
            }
        }
    }

    public void RestoreAcrossLayoutPasses(DispatcherQueue dispatcherQueue)
    {
        if (_scrollViewer is null)
        {
            return;
        }

        QueueRestore(dispatcherQueue, 0, releaseHandlers: false);
        QueueRestore(dispatcherQueue, 40, releaseHandlers: false);
        QueueRestore(dispatcherQueue, 160, releaseHandlers: false);
        QueueRestore(dispatcherQueue, 420, releaseHandlers: false);
        QueueRestore(dispatcherQueue, 760, releaseHandlers: true);
    }

    public void RestoreAfterCollectionChange(DispatcherQueue dispatcherQueue)
    {
        if (_scrollViewer is null)
        {
            return;
        }

        QueueRestore(dispatcherQueue, 0, releaseHandlers: false);
        QueueRestore(dispatcherQueue, 50, releaseHandlers: true);
    }

    private bool TryRestoreKeyedAnchor()
    {
        if (_listView is null ||
            _itemKeySelector is null ||
            string.IsNullOrWhiteSpace(_itemKey) ||
            _scrollViewer is null)
        {
            return false;
        }

        object? anchorItem = KeyedViewportAnchorPolicy.FindByKey(
            _listView.Items.Cast<object>(),
            _itemKey,
            _itemKeySelector);
        if (anchorItem is null)
        {
            return false;
        }

        _listView.ScrollIntoView(anchorItem, ScrollIntoViewAlignment.Leading);
        _listView.UpdateLayout();
        if (_listView.ContainerFromItem(anchorItem) is not FrameworkElement container)
        {
            return false;
        }

        double currentViewportOffset = GetViewportOffset(container, _scrollViewer);
        double targetVerticalOffset = KeyedViewportAnchorPolicy.ResolveTargetVerticalOffset(
            _scrollViewer.VerticalOffset,
            currentViewportOffset,
            _itemViewportOffset,
            _scrollViewer.ScrollableHeight);
        _scrollViewer.ChangeView(_horizontalOffset, targetVerticalOffset, null, disableAnimation: true);
        return true;
    }

    private static bool TryFindFirstVisibleContainer(
        ListViewBase listView,
        ScrollViewer scrollViewer,
        out object? item,
        out FrameworkElement? container)
    {
        item = null;
        container = null;
        if (listView.ItemsPanelRoot is ItemsStackPanel stackPanel &&
            stackPanel.FirstVisibleIndex >= 0 &&
            stackPanel.FirstVisibleIndex < listView.Items.Count &&
            listView.ContainerFromIndex(stackPanel.FirstVisibleIndex) is FrameworkElement firstVisible)
        {
            item = listView.Items[stackPanel.FirstVisibleIndex];
            container = firstVisible;
            return true;
        }

        double bestOffset = double.PositiveInfinity;
        for (int index = 0; index < listView.Items.Count; index++)
        {
            if (listView.ContainerFromIndex(index) is not FrameworkElement candidate)
            {
                continue;
            }

            double offset = GetViewportOffset(candidate, scrollViewer);
            if (offset + candidate.ActualHeight <= 0 || offset >= scrollViewer.ViewportHeight)
            {
                continue;
            }

            if (offset < bestOffset)
            {
                bestOffset = offset;
                item = listView.Items[index];
                container = candidate;
            }
        }

        return item is not null && container is not null;
    }

    private static double GetViewportOffset(FrameworkElement element, ScrollViewer scrollViewer) =>
        element.TransformToVisual(scrollViewer).TransformPoint(new Windows.Foundation.Point()).Y;

    private void QueueRestore(DispatcherQueue dispatcherQueue, int delayMilliseconds, bool releaseHandlers)
    {
        if (delayMilliseconds <= 0)
        {
            _ = dispatcherQueue.TryEnqueue(() => RestoreAndOptionallyRelease(releaseHandlers));
            return;
        }

        UiTaskGuard.Observe(RestoreAfterDelayAsync(dispatcherQueue, delayMilliseconds, releaseHandlers), "ui-list-view-scroll-anchor");
    }

    private async Task RestoreAfterDelayAsync(
        DispatcherQueue dispatcherQueue,
        int delayMilliseconds,
        bool releaseHandlers)
    {
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        _ = dispatcherQueue.TryEnqueue(() => RestoreAndOptionallyRelease(releaseHandlers));
    }

    private void RestoreAndOptionallyRelease(bool releaseHandlers)
    {
        Restore();
        if (releaseHandlers)
        {
            DetachInteractionHandlers();
        }
    }

    private void OnUserPointerInteraction(object sender, PointerRoutedEventArgs e) => CancelPendingRestores();

    private void OnUserKeyInteraction(object sender, KeyRoutedEventArgs e) => CancelPendingRestores();

    private void CancelPendingRestores()
    {
        _userInteracted = true;
        DetachInteractionHandlers();
    }

    private void DetachInteractionHandlers()
    {
        if (!_handlersAttached ||
            _scrollViewer is null ||
            _listView is null ||
            _pointerInteractionHandler is null ||
            _keyInteractionHandler is null)
        {
            return;
        }

        _scrollViewer.RemoveHandler(UIElement.PointerPressedEvent, _pointerInteractionHandler);
        _scrollViewer.RemoveHandler(UIElement.PointerWheelChangedEvent, _pointerInteractionHandler);
        _listView.RemoveHandler(UIElement.KeyDownEvent, _keyInteractionHandler);
        _handlersAttached = false;
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

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
