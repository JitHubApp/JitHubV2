using System;
using System.Diagnostics;
using JitHub.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Performance;

internal sealed partial class ProductPerformanceScrollProbe : IDisposable
{
    private readonly FrameworkElement _statusHost;
    private readonly ScrollViewer _scrollViewer;
    private long _sequence;
    private long _startedTimestamp;
    private bool _renderPending;
    private bool _disposed;

    private ProductPerformanceScrollProbe(FrameworkElement statusHost, ScrollViewer scrollViewer)
    {
        _statusHost = statusHost;
        _scrollViewer = scrollViewer;
        _scrollViewer.ViewChanging += ScrollViewer_ViewChanging;
        long initializedTimestamp = Stopwatch.GetTimestamp();
        AutomationProperties.SetItemStatus(
            _statusHost,
            new ProductPerformanceScrollStatus(0, initializedTimestamp, initializedTimestamp).Format());
    }

    public static ProductPerformanceScrollProbe? TryStart(
        FrameworkElement statusHost,
        ScrollViewer scrollViewer)
    {
        ArgumentNullException.ThrowIfNull(statusHost);
        ArgumentNullException.ThrowIfNull(scrollViewer);
        return ProductPerformanceReadiness.IsEnabled
            ? new ProductPerformanceScrollProbe(statusHost, scrollViewer)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scrollViewer.ViewChanging -= ScrollViewer_ViewChanging;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
    }

    private void ScrollViewer_ViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (_renderPending)
        {
            return;
        }

        _startedTimestamp = Stopwatch.GetTimestamp();
        _renderPending = true;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        if (!_renderPending)
        {
            return;
        }

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        long startedTimestamp = _startedTimestamp;
        _ = _statusHost.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                _renderPending = false;
                if (_disposed)
                {
                    return;
                }

                long renderedTimestamp = Stopwatch.GetTimestamp();
                AutomationProperties.SetItemStatus(
                    _statusHost,
                    new ProductPerformanceScrollStatus(
                        ++_sequence,
                        startedTimestamp,
                        renderedTimestamp).Format());
            });
    }
}
