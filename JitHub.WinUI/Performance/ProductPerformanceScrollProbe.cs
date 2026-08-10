using System;
using System.Diagnostics;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Performance;

internal sealed class ProductPerformanceScrollProbe : IDisposable
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
        CompositionTarget.Rendering += CompositionTarget_Rendering;
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
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        if (!_renderPending)
        {
            return;
        }

        _renderPending = false;
        long renderedTimestamp = Stopwatch.GetTimestamp();
        AutomationProperties.SetItemStatus(
            _statusHost,
            new ProductPerformanceScrollStatus(
                ++_sequence,
                _startedTimestamp,
                renderedTimestamp).Format());
    }
}
