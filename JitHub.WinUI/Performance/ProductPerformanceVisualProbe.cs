using System;
using System.Diagnostics;
using System.Threading;
using JitHub.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Performance;

internal sealed partial class ProductPerformanceVisualProbe : IDisposable
{
    private static readonly long TraversalSuppressionTimeoutTicks = Stopwatch.Frequency * 5;
    private readonly FrameworkElement _root;
    private readonly DispatcherQueueTimer _dispatcherTimer;
    private long _frame;
    private long _dispatcher;
    private long _heartbeatSuppressedUntil;
    private bool _renderingSubscribed;
    private bool _disposed;

    private ProductPerformanceVisualProbe(FrameworkElement root)
    {
        _root = root;
        _dispatcherTimer = root.DispatcherQueue.CreateTimer();
        _dispatcherTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dispatcherTimer.IsRepeating = true;
        _dispatcherTimer.Tick += DispatcherTimer_Tick;
        StartRenderingObservation();
        ProductPerformanceReadiness.TraversalMeasurementArmed += TraversalMeasurement_Armed;
        ProductPerformanceReadiness.TraversalMeasurementCompleted += TraversalMeasurement_Completed;
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
        StopRenderingObservation();
        ProductPerformanceReadiness.TraversalMeasurementArmed -= TraversalMeasurement_Armed;
        ProductPerformanceReadiness.TraversalMeasurementCompleted -= TraversalMeasurement_Completed;
    }

    private void DispatcherTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || IsHeartbeatSuppressed())
        {
            return;
        }

        // The timer callback is already a dispatcher turn. Publishing here keeps
        // the stall probe to one scheduling boundary; traversal suppression keeps
        // this UIA mutation out of the interaction and render being measured.
        _dispatcher++;
        Publish();
    }

    private void TraversalMeasurement_Armed(object? sender, EventArgs e)
    {
        Volatile.Write(
            ref _heartbeatSuppressedUntil,
            Stopwatch.GetTimestamp() + TraversalSuppressionTimeoutTicks);
        RunOnDispatcher(StopRenderingObservation);
    }

    private void TraversalMeasurement_Completed(object? sender, EventArgs e)
    {
        Volatile.Write(ref _heartbeatSuppressedUntil, 0);
        RunOnDispatcher(StartRenderingObservation);
    }

    private bool IsHeartbeatSuppressed()
    {
        long suppressedUntil = Volatile.Read(ref _heartbeatSuppressedUntil);
        if (suppressedUntil <= 0)
        {
            return false;
        }

        if (Stopwatch.GetTimestamp() < suppressedUntil)
        {
            return true;
        }

        if (Interlocked.CompareExchange(ref _heartbeatSuppressedUntil, 0, suppressedUntil) == suppressedUntil)
        {
            StartRenderingObservation();
        }

        return false;
    }

    private void StartRenderingObservation()
    {
        if (_disposed || _renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _renderingSubscribed = true;
    }

    private void StopRenderingObservation()
    {
        if (!_renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _renderingSubscribed = false;
    }

    private void RunOnDispatcher(Action action)
    {
        if (_root.DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _ = _root.DispatcherQueue.TryEnqueue(() => action());
        }
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
