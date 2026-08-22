using System;
using JitHub.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Performance;

internal sealed partial class ProductPerformanceVisualProbe : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly DispatcherQueueTimer _dispatcherTimer;
    private long _frame;
    private long _dispatcher;
    private bool _disposed;

    private ProductPerformanceVisualProbe(FrameworkElement root)
    {
        _root = root;
        _dispatcherTimer = root.DispatcherQueue.CreateTimer();
        _dispatcherTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dispatcherTimer.IsRepeating = true;
        _dispatcherTimer.Tick += DispatcherTimer_Tick;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        Publish();
        _dispatcherTimer.Start();
    }

    public static ProductPerformanceVisualProbe? TryStart(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        bool requested = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("JITHUB_PERFORMANCE_FIXTURE"));
        return requested && AppDataPathPolicy.TryGetAutomationRoots(out _, out _)
            ? new ProductPerformanceVisualProbe(root)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatcherTimer.Stop();
        _dispatcherTimer.Tick -= DispatcherTimer_Tick;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
    }

    private void DispatcherTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _dispatcher++;
        Publish();
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        _frame++;
    }

    private void Publish() =>
        AutomationProperties.SetItemStatus(
            _root,
            new ProductPerformanceHeartbeat(
                _frame,
                _dispatcher,
                ProductPerformanceReadiness.ApplicationInteractiveTimestamp).Format());
}
