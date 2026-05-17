using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Behaviors;

public static class IncrementalLoadingBehavior
{
    private const int MaxAttachAttempts = 20;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(IncrementalLoadingBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(object),
            typeof(IncrementalLoadingBehavior),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty PageSizeProperty =
        DependencyProperty.RegisterAttached(
            "PageSize",
            typeof(int),
            typeof(IncrementalLoadingBehavior),
            new PropertyMetadata(20));

    public static readonly DependencyProperty ThresholdProperty =
        DependencyProperty.RegisterAttached(
            "Threshold",
            typeof(double),
            typeof(IncrementalLoadingBehavior),
            new PropertyMetadata(1200d));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(IncrementalLoadingState),
            typeof(IncrementalLoadingBehavior),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject obj)
        => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value)
        => obj.SetValue(IsEnabledProperty, value);

    public static object? GetSource(DependencyObject obj)
        => obj.GetValue(SourceProperty);

    public static void SetSource(DependencyObject obj, object? value)
        => obj.SetValue(SourceProperty, value);

    public static int GetPageSize(DependencyObject obj)
        => (int)obj.GetValue(PageSizeProperty);

    public static void SetPageSize(DependencyObject obj, int value)
        => obj.SetValue(PageSizeProperty, value);

    public static double GetThreshold(DependencyObject obj)
        => (double)obj.GetValue(ThresholdProperty);

    public static void SetThreshold(DependencyObject obj, double value)
        => obj.SetValue(ThresholdProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (e.NewValue is true)
        {
            EnsureState(element);
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
                Attach(element);
        }
        else
        {
            Detach(element);
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
        }
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || !GetIsEnabled(element))
            return;

        var state = EnsureState(element);
        state.UpdateCollection(e.NewValue as INotifyCollectionChanged, () => QueueLoadIfNeeded(element, state));
        if (element.IsLoaded)
            Attach(element);

        QueueLoadIfNeeded(element, state);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
        => Attach((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e)
        => Detach((FrameworkElement)sender);

    private static void Attach(FrameworkElement element, int attempt = 0)
    {
        var state = EnsureState(element);
        if (element is Control control)
            control.ApplyTemplate();

        var listHosts = new List<ListViewBase>();
        if (element is ListViewBase listViewBase)
            listHosts.Add(listViewBase);
        listHosts.AddRange(FindDescendants<ListViewBase>(element));

        if (listHosts.Count > 0)
        {
            foreach (ListViewBase listHost in listHosts)
            {
                foreach (ScrollViewer descendant in FindDescendants<ScrollViewer>(listHost))
                    state.AddScrollViewer(descendant, OnViewChanged);
            }
        }
        else if (ShouldWaitForListHost(element) && attempt < MaxAttachAttempts)
        {
            _ = element.DispatcherQueue.TryEnqueue(() => Attach(element, attempt + 1));
            return;
        }
        else
        {
            if (element is ScrollViewer scrollViewer)
                state.AddScrollViewer(scrollViewer, OnViewChanged);

            foreach (ScrollViewer descendant in FindDescendants<ScrollViewer>(element))
                state.AddScrollViewer(descendant, OnViewChanged);
        }

        state.UpdateCollection(ResolveSource(element) as INotifyCollectionChanged, () => QueueLoadIfNeeded(element, state));

        if (state.ScrollViewerCount == 0 && attempt < MaxAttachAttempts)
        {
            _ = element.DispatcherQueue.TryEnqueue(() => Attach(element, attempt + 1));
            return;
        }

        QueueLoadIfNeeded(element, state);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not IncrementalLoadingState state)
            return;

        state.Dispose(OnViewChanged);
        element.ClearValue(StateProperty);
    }

    private static async void QueueLoadIfNeeded(FrameworkElement element, IncrementalLoadingState state)
    {
        if (!GetIsEnabled(element) ||
            !ReferenceEquals(element.GetValue(StateProperty), state) ||
            state.IsLoading)
            return;

        if (ResolveSource(element) is not ISupportIncrementalLoading source || !source.HasMoreItems)
            return;

        if (IsSourceLoading(source))
        {
            ScheduleAfterCurrentSourceLoad(element, state);
            return;
        }

        if (source is System.Collections.ICollection { Count: 0 })
            return;

        if (!state.ShouldLoad(GetThreshold(element)))
            return;

        state.IsLoading = true;
        try
        {
            uint pageSize = (uint)Math.Max(1, GetPageSize(element));
            await source.LoadMoreItemsAsync(pageSize);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Incremental load failed: {ex}");
        }
        finally
        {
            state.IsLoading = false;
        }

        if (source.HasMoreItems && state.ShouldLoad(GetThreshold(element)))
            _ = element.DispatcherQueue.TryEnqueue(() => QueueLoadIfNeeded(element, state));
    }

    private static async void ScheduleAfterCurrentSourceLoad(FrameworkElement element, IncrementalLoadingState state)
    {
        if (state.IsWaitingForSource)
            return;

        state.IsWaitingForSource = true;
        try
        {
            await Task.Delay(120);
        }
        finally
        {
            state.IsWaitingForSource = false;
        }

        QueueLoadIfNeeded(element, state);
    }

    private static void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        FrameworkElement? owner = FindOwner(scrollViewer);
        if (owner?.GetValue(StateProperty) is IncrementalLoadingState state)
            QueueLoadIfNeeded(owner, state);
    }

    private static object? ResolveSource(FrameworkElement element)
    {
        object? explicitSource = GetSource(element);
        if (explicitSource is not null)
            return explicitSource;

        return element switch
        {
            ItemsControl itemsControl => itemsControl.ItemsSource,
            _ => element.GetType().GetProperty("ItemsSource")?.GetValue(element)
        };
    }

    private static bool IsSourceLoading(ISupportIncrementalLoading source)
        => source.GetType().GetProperty("IsLoading")?.GetValue(source) is true;

    private static bool ShouldWaitForListHost(FrameworkElement element)
        => element is ItemsControl and not ListViewBase && ResolveSource(element) is not null;

    private static FrameworkElement? FindOwner(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.GetValue(StateProperty) is IncrementalLoadingState)
                return element;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static IncrementalLoadingState EnsureState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is IncrementalLoadingState state)
            return state;

        state = new IncrementalLoadingState();
        element.SetValue(StateProperty, state);
        return state;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (T descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class IncrementalLoadingState
    {
        private readonly List<ScrollViewer> _scrollViewers = [];
        private INotifyCollectionChanged? _collection;
        private Action? _collectionChangedCallback;

        public bool IsLoading { get; set; }

        public bool IsWaitingForSource { get; set; }

        public int ScrollViewerCount => _scrollViewers.Count;

        public void AddScrollViewer(ScrollViewer scrollViewer, EventHandler<ScrollViewerViewChangedEventArgs> handler)
        {
            if (_scrollViewers.Contains(scrollViewer))
                return;

            _scrollViewers.Add(scrollViewer);
            scrollViewer.ViewChanged += handler;
        }

        public void UpdateCollection(INotifyCollectionChanged? collection, Action collectionChangedCallback)
        {
            if (ReferenceEquals(_collection, collection))
                return;

            if (_collection is not null && _collectionChangedCallback is not null)
                _collection.CollectionChanged -= OnCollectionChanged;

            _collection = collection;
            _collectionChangedCallback = collectionChangedCallback;

            if (_collection is not null)
                _collection.CollectionChanged += OnCollectionChanged;
        }

        public bool ShouldLoad(double threshold)
        {
            if (_scrollViewers.Count == 0)
                return false;

            foreach (ScrollViewer scrollViewer in _scrollViewers)
            {
                if (scrollViewer.Visibility != Visibility.Visible ||
                    (scrollViewer.ViewportHeight <= 0 && scrollViewer.ActualHeight <= 0))
                {
                    continue;
                }

                double viewportHeight = scrollViewer.ViewportHeight > 0
                    ? scrollViewer.ViewportHeight
                    : scrollViewer.ActualHeight;
                double prefetchDistance = Math.Max(threshold, viewportHeight * 1.25d);
                double remaining = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
                if (scrollViewer.ExtentHeight <= viewportHeight + prefetchDistance || remaining <= prefetchDistance)
                    return true;
            }

            return false;
        }

        public void Dispose(EventHandler<ScrollViewerViewChangedEventArgs> handler)
        {
            foreach (ScrollViewer scrollViewer in _scrollViewers)
                scrollViewer.ViewChanged -= handler;

            _scrollViewers.Clear();

            if (_collection is not null)
                _collection.CollectionChanged -= OnCollectionChanged;

            _collection = null;
            _collectionChangedCallback = null;
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => _collectionChangedCallback?.Invoke();
    }
}
